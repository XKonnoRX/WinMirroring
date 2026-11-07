using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace WinMirroring.Screen.Display;

/// <summary>
/// One-time changes to the Virtual Display Driver that need administrator rights: adding a resolution to its
/// settings file and restarting the device so it picks the resolution up. Run in an elevated copy of the app
/// (<see cref="Argument"/>), started through a UAC prompt by <see cref="AddModeElevatedAsync"/>.
/// </summary>
public static class VirtualDisplaySetup
{
    public const string Argument = "--vdd-add-mode";

    private const string DefaultDirectory = @"C:\VirtualDisplayDriver";
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static string SettingsPath
    {
        get
        {
            var directory = DefaultDirectory;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
                if (key?.GetValue("VDDPATH") is string configured && configured.Length > 0)
                {
                    directory = configured;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }

            return Path.Combine(directory, "vdd_settings.xml");
        }
    }

    /// <summary>Asks for administrator rights and adds the mode; true when it was added and the device restarted.</summary>
    public static async Task<bool> AddModeElevatedAsync(int width, int height)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{Argument} {width}x{height}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return false; // declined at the UAC prompt, or it took too long
        }
    }

    /// <summary>Entry point of the elevated copy: "WIDTHxHEIGHT". Returns the process exit code.</summary>
    public static int Run(string argument)
    {
        var parts = argument.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width is < 640 or > 8192 || height is < 480 or > 8192)
        {
            return 2;
        }

        try
        {
            AddResolution(SettingsPath, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return 3;
        }

        return RestartDevice() ? 0 : 4;
    }

    /// <summary>Adds a 60 Hz resolution to the driver's settings file unless it is listed already.</summary>
    public static bool AddResolution(string path, int width, int height)
    {
        var document = File.Exists(path) ? XDocument.Load(path) : new XDocument(new XElement("vdd_settings"));
        var root = document.Root ?? throw new System.Xml.XmlException("empty settings file");
        var resolutions = root.Descendants("resolutions").FirstOrDefault();
        if (resolutions is null)
        {
            resolutions = new XElement("resolutions");
            root.Add(resolutions);
        }

        var exists = resolutions.Elements("resolution").Any(r =>
            (int?)r.Element("width") == width && (int?)r.Element("height") == height);
        if (exists)
        {
            return false;
        }

        resolutions.Add(new XElement("resolution",
            new XElement("width", width),
            new XElement("height", height),
            new XElement("refresh_rate", 60)));

        var temporary = path + ".tmp";
        document.Save(temporary);
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    /// <summary>Restarts the driver's device (like disable and enable in Device Manager) so it re-reads its settings.</summary>
    public static bool RestartDevice()
    {
        var classGuid = DisplayClass;
        var set = SetupDiGetClassDevsW(ref classGuid, null, 0, DigcfPresent);
        if (set == -1)
        {
            return false;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!HardwareIds(set, ref data).Any(id => id.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var parameters = new SP_PROPCHANGE_PARAMS
                {
                    cbSize = 8,
                    InstallFunction = DifPropertyChange,
                    StateChange = DicsPropChange,
                    Scope = DicsFlagConfigSpecific,
                    HwProfile = 0,
                };
                return SetupDiSetClassInstallParamsW(set, ref data, ref parameters, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())
                    && SetupDiCallClassInstaller(DifPropertyChange, set, ref data);
            }

            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static IEnumerable<string> HardwareIds(nint set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buffer, buffer.Length, out var required) || required <= 2)
        {
            return [];
        }

        return System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private const uint DigcfPresent = 0x2;
    private const uint SpdrpHardwareId = 0x1;
    private const uint DifPropertyChange = 0x12;
    private const uint DicsPropChange = 0x3;
    private const uint DicsFlagConfigSpecific = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public uint cbSize;
        public uint InstallFunction;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SP_DEVINFO_DATA data, uint property, out uint type, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParamsW(nint set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS parameters, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint function, nint set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
