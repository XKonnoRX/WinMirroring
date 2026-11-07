using System.Globalization;

namespace WinMirroring.Core.Discovery;

/// <summary>"host" or "host:discoveryPort" as typed by the user for peers that broadcast cannot reach.</summary>
public static class ManualAddress
{
    public static bool TryParse(string? text, int defaultDiscoveryPort, out string host, out int discoveryPort)
    {
        host = string.Empty;
        discoveryPort = defaultDiscoveryPort;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var colon = value.LastIndexOf(':');
        if (colon > 0 && colon < value.Length - 1
            && int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= ushort.MaxValue)
        {
            host = value[..colon];
            discoveryPort = port;
        }
        else
        {
            host = value;
        }

        return host.Length > 0 && !host.Contains(' ');
    }
}
