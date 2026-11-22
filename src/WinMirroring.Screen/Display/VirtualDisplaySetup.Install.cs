using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinMirroring.Screen.Display;

/// <summary>
/// Putting the driver itself in place. The winget package "VirtualDrivers.Virtual-Display-Driver" is not the driver:
/// it is the project's control app (VDD Control), unpacked as a portable package, which carries the signed driver
/// and devcon.exe with it and installs the driver only when someone presses its button. winget reported success and
/// nothing was installed, so the app now does the second half itself: it finds that package, puts the driver's
/// settings file where the driver and this app look for it, and creates the root device from the driver's INF with
/// devcon — in an elevated copy of the app, one Windows prompt.
/// </summary>
public static partial class VirtualDisplaySetup
{
    public const string InstallArgument = "--vdd-install";

    private const string PackagePrefix = "VirtualDrivers.Virtual-Display-Driver_";
    private const string HardwareId = @"Root\MttVDD";

    /// <summary>The folder winget unpacked the package into (per user or per machine), or null when it is not there.</summary>
    public static string? FindPackage()
    {
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
        ];

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var candidate in Directory.EnumerateDirectories(root, PackagePrefix + "*"))
                {
                    if (DriverFiles(candidate) is not null)
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>The INF for this machine's processor, and devcon; null when the package lacks either.</summary>
    private static (string Inf, string Devcon)? DriverFiles(string package)
    {
        // The package's "x86" folder is the x64 driver (its INF is NTamd64); ARM64 has its own.
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x86";
        var inf = Path.Combine(package, "SignedDrivers", architecture, "VDD", "MttVDD.inf");
        var devcon = Path.Combine(package, "Dependencies", "devcon.exe");
        return File.Exists(inf) && File.Exists(devcon) ? (inf, devcon) : null;
    }

    /// <summary>
    /// Asks for administrator rights and installs (or updates) the driver from <paramref name="package"/>. Returns the
    /// elevated copy's exit code (0: done), -1 when the prompt was declined, and what devcon said in <paramref name="report"/>.
    /// </summary>
    public static async Task<(int Code, string Report)> InstallElevatedAsync(string package)
    {
        var report = Path.Combine(Path.GetTempPath(), $"WinMirroring-vdd-install-{Environment.ProcessId}.log");
        try
        {
            File.Delete(report);
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{InstallArgument} \"{package}\" \"{report}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return (-1, string.Empty);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            var text = File.Exists(report) ? await File.ReadAllTextAsync(report).ConfigureAwait(false) : string.Empty;
            return (process.ExitCode, text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return (-1, ex.Message); // declined at the prompt, or it took too long
        }
        finally
        {
            try
            {
                File.Delete(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Entry point of the elevated copy. Exit codes: 0 done (or done and Windows wants a restart), 10 the package is
    /// incomplete, 11 the settings file could not be put in place, 20 devcon failed.
    /// </summary>
    public static int Install(string package, string? reportPath)
    {
        var report = new StringBuilder();
        try
        {
            if (DriverFiles(package) is not var (inf, devcon))
            {
                report.AppendLine($"No driver for this processor in {package}");
                return 10;
            }

            try
            {
                PrepareSettings(Path.GetDirectoryName(inf)!, report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                report.AppendLine($"Settings: {ex.Message}");
                return 11;
            }

            // A device made before (by VDD Control, or an earlier try) is given the driver again rather than a twin.
            var (_, found) = Devcon(devcon, $"find {HardwareId}", report);
            // "No matching devices found." or "1 matching device(s) found." — the device itself is listed by its
            // instance (ROOT\DISPLAY\0000), not by the hardware id that was asked for.
            var exists = !found.Contains("No matching devices", StringComparison.OrdinalIgnoreCase)
                && found.Contains("matching device", StringComparison.OrdinalIgnoreCase);
            var (code, _) = Devcon(devcon, $"{(exists ? "update" : "install")} \"{inf}\" {HardwareId}", report);

            // devcon: 0 done, 1 done but a restart is needed, anything else failed.
            return code is 0 or 1 ? 0 : 20;
        }
        finally
        {
            if (reportPath is not null)
            {
                try
                {
                    File.WriteAllText(reportPath, report.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// The driver reads its modes from vdd_settings.xml in the folder named in the registry (C:\VirtualDisplayDriver
    /// when nothing is named), and that is also the file this app adds resolutions to: it is put there once, from the
    /// copy that comes with the driver, and an existing one is left alone.
    /// </summary>
    private static void PrepareSettings(string driverFolder, StringBuilder report)
    {
        string? configured = null;
        using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver"))
        {
            configured = key.GetValue("VDDPATH") as string;
            if (string.IsNullOrEmpty(configured))
            {
                configured = DefaultDirectory;
                key.SetValue("VDDPATH", configured);
            }
        }

        Directory.CreateDirectory(configured);
        var settings = Path.Combine(configured, "vdd_settings.xml");
        var shipped = Path.Combine(driverFolder, "vdd_settings.xml");
        if (!File.Exists(settings) && File.Exists(shipped))
        {
            File.Copy(shipped, settings);
            report.AppendLine($"Settings copied to {settings}");
        }
    }

    private static (int Code, string Output) Devcon(string devcon, string arguments, StringBuilder report)
    {
        var start = new ProcessStartInfo(devcon, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(devcon)!,
        };

        using var process = Process.Start(start);
        if (process is null)
        {
            report.AppendLine($"devcon {arguments}: did not start");
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill();
            report.AppendLine($"devcon {arguments}: timed out");
            return (-1, output);
        }

        report.AppendLine($"devcon {arguments} -> {process.ExitCode}: {output.Trim()}");
        return (process.ExitCode, output);
    }
}
