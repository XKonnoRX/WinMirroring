using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WinMirroring.Core.Network;

/// <summary>
/// Where the devices this one is paired with are on a Tailscale network, if they are on one.
///
/// Devices find each other by broadcast, which stops at the edge of the local network. Away from home the way to
/// the other device is a VPN, and until now that meant typing its address in by hand. Where Tailscale is the VPN
/// it already knows every machine on the network by name, and its own command says so: this reads that list and
/// picks out the machines whose name is that of a device this one is paired with.
///
/// Only those. A Tailscale network is often shared — a company's, with its servers on it — and sending discovery
/// probes to every machine on it would be knocking on doors that are nobody's business here. A probe goes only to
/// a machine that carries the name of a device already paired, and what answers still has to prove it is that
/// device with its certificate before anything is said to it.
/// </summary>
public static class TailscalePeers
{
    /// <summary>One machine of the Tailscale network.</summary>
    public sealed record Machine(string HostName, IPAddress Address, bool Online);

    /// <summary>How long one reading of the list is used for. Asking is a process started and waited for.</summary>
    private static readonly TimeSpan KeptFor = TimeSpan.FromSeconds(30);

    /// <summary>...and how long an answer is waited for before giving up on this reading.</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(3);

    private static readonly object Gate = new();
    private static IReadOnlyList<Machine> _machines = [];
    private static long _readTicks = long.MinValue / 2;
    private static int _reading;

    /// <summary>
    /// The addresses, on the Tailscale network, of the machines called any of <paramref name="names"/> that are
    /// online there. Never waits: what it returns is the last reading, and a new one is started in the background
    /// when that is old. Empty where there is no Tailscale, which is most places.
    /// </summary>
    public static IReadOnlyList<IPAddress> AddressesOf(IEnumerable<string> names)
    {
        RefreshIfStale();
        IReadOnlyList<Machine> machines;
        lock (Gate)
        {
            machines = _machines;
        }

        return Pick(machines, names);
    }

    /// <summary>The choice itself: online machines whose name is one of those asked for, whatever the case.</summary>
    public static IReadOnlyList<IPAddress> Pick(IReadOnlyList<Machine> machines, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        return wanted.Count == 0
            ? []
            : machines.Where(m => m.Online && wanted.Contains(m.HostName)).Select(m => m.Address).Distinct().ToList();
    }

    /// <summary>
    /// What <c>tailscale status --json</c> says, as machines: the other machines of the network, each with its
    /// first IPv4 address. Anything that is not that — no network, a different shape — is no machines.
    /// </summary>
    public static IReadOnlyList<Machine> Parse(string json)
    {
        var machines = new List<Machine>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            {
                return machines;
            }

            foreach (var peer in peers.EnumerateObject())
            {
                var value = peer.Value;
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("HostName", out var host) || host.ValueKind != JsonValueKind.String
                    || !value.TryGetProperty("TailscaleIPs", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var online = value.TryGetProperty("Online", out var flag) && flag.ValueKind == JsonValueKind.True;
                foreach (var text in addresses.EnumerateArray())
                {
                    if (text.ValueKind == JsonValueKind.String
                        && IPAddress.TryParse(text.GetString(), out var address)
                        && address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        machines.Add(new Machine(host.GetString() ?? string.Empty, address, online));
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return machines;
    }

    private static void RefreshIfStale()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _readTicks) < KeptFor.TotalMilliseconds
            || Interlocked.Exchange(ref _reading, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var machines = await ReadAsync().ConfigureAwait(false);
                lock (Gate)
                {
                    _machines = machines;
                }
            }
            finally
            {
                Volatile.Write(ref _readTicks, Environment.TickCount64);
                Volatile.Write(ref _reading, 0);
            }
        });
    }

    private static async Task<IReadOnlyList<Machine>> ReadAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "status --json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return [];
            }

            using var timeout = new CancellationTokenSource(AnswerWithin);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? Parse(output) : [];
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                return [];
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }
}
