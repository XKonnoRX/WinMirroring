using System.Diagnostics;

namespace WinMirroring.Core.Hosting;

/// <summary>Inbound Windows Firewall rules for the node. Adding them needs elevation, so it goes through a UAC prompt.</summary>
public static class FirewallRules
{
    public const string TcpRuleName = "WinMirroring (TCP in)";
    public const string UdpRuleName = "WinMirroring (UDP in)";

    public static async Task<bool> ExistAsync(CancellationToken ct = default)
    {
        var output = await RunNetshAsync($"advfirewall firewall show rule name=\"{TcpRuleName}\"", ct).ConfigureAwait(false);
        return output is not null && !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds (or replaces) the rules for <paramref name="executablePath"/> on private and domain networks.</summary>
    public static async Task<bool> AddAsync(string executablePath, int tcpPort, int udpPort, CancellationToken ct = default)
    {
        var script = string.Join(" & ",
            $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\"",
            $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\"",
            $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={tcpPort} program=\"{executablePath}\" profile=private,domain",
            $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={udpPort} program=\"{executablePath}\" profile=private,domain");

        var startInfo = new ProcessStartInfo("cmd.exe", $"/c {script}")
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was declined.
            return false;
        }
    }

    private static async Task<string?> RunNetshAsync(string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("netsh.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
