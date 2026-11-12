namespace WinMirroring.Audio;

/// <summary>Why this device's sound goes to another device.</summary>
public enum AudioRouteReason
{
    /// <summary>The user here chose that device ("always on …").</summary>
    Pinned,

    /// <summary>The user there asked for this device's sound.</summary>
    Asked,

    /// <summary>That device controls this one with its mouse and keyboard.</summary>
    Controls,

    /// <summary>That device shows one of this device's screens.</summary>
    Watches,
}

/// <summary>Where this device's sound should go, and why.</summary>
public readonly record struct AudioRoute(string PeerId, AudioRouteReason Reason)
{
    /// <summary>Not chosen by a user: it may give way to the other device (see <see cref="AudioRouting.ShouldYield"/>).</summary>
    public bool Automatic => Reason is AudioRouteReason.Controls or AudioRouteReason.Watches;
}

/// <summary>Everything the routing decision depends on, as plain values (so it can be tested without audio).</summary>
internal sealed record AudioRouteInputs
{
    public bool SendEnabled { get; init; } = true;

    public string? Pinned { get; init; }

    public bool FollowInput { get; init; } = true;

    public bool FollowViewer { get; init; } = true;

    /// <summary>Devices that asked for this device's sound.</summary>
    public IReadOnlyCollection<string> Listeners { get; init; } = [];

    /// <summary>Devices that said not to send it to them.</summary>
    public IReadOnlyCollection<string> RefusedBy { get; init; } = [];

    /// <summary>The device controlling this one, if any.</summary>
    public string? Focus { get; init; }

    /// <summary>Devices showing one of this device's screens, oldest first.</summary>
    public IReadOnlyList<string> Viewers { get; init; } = [];

    public Func<string, bool> IsReady { get; init; } = _ => true;

    /// <summary>This device gave way to that device (it sends its own sound here now): no automatic sound back.</summary>
    public Func<string, bool> IsYielded { get; init; } = _ => false;

    /// <summary>That device sends its sound here because a user chose it: do not send this one back on our own.</summary>
    public Func<string, bool> SendsHereByChoice { get; init; } = _ => false;
}

/// <summary>
/// The rules for where sound goes. What a user chose comes first (pinned here, then asked for there); after that the
/// sound follows whoever controls this device, then whoever watches its screen. Two devices never send to each other
/// on their own: the newer automatic stream wins and the older one gives way, so each device's speakers are muted
/// only while its sound really plays elsewhere and nothing plays here.
/// </summary>
internal static class AudioRouting
{
    /// <summary>Starts closer together than this count as simultaneous; the device id then decides.</summary>
    public static readonly TimeSpan SimultaneousWindow = TimeSpan.FromSeconds(1.5);

    public static AudioRoute? Choose(AudioRouteInputs inputs)
    {
        if (!inputs.SendEnabled)
        {
            return null;
        }

        bool Allowed(string peer) => inputs.IsReady(peer) && !inputs.RefusedBy.Contains(peer);
        bool AutoAllowed(string peer) => Allowed(peer) && !inputs.IsYielded(peer) && !inputs.SendsHereByChoice(peer);

        if (!string.IsNullOrEmpty(inputs.Pinned) && Allowed(inputs.Pinned))
        {
            return new AudioRoute(inputs.Pinned, AudioRouteReason.Pinned);
        }

        foreach (var listener in inputs.Listeners)
        {
            if (Allowed(listener))
            {
                return new AudioRoute(listener, AudioRouteReason.Asked);
            }
        }

        if (inputs.FollowInput && inputs.Focus is { Length: > 0 } focus && AutoAllowed(focus))
        {
            return new AudioRoute(focus, AudioRouteReason.Controls);
        }

        if (inputs.FollowViewer)
        {
            foreach (var viewer in inputs.Viewers)
            {
                if (AutoAllowed(viewer))
                {
                    return new AudioRoute(viewer, AudioRouteReason.Watches);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// This device and the device it sends to both send their sound to each other. Decides whether this device stops
    /// (gives way): never when a user chose this route; always when a user chose theirs; between two automatic
    /// streams the newer one wins, and for two that started together the device with the greater id gives way. Both
    /// sides run the same rule with the ages swapped, so exactly one of them gives way.
    /// </summary>
    public static bool ShouldYield(bool oursAutomatic, bool theirsAutomatic, TimeSpan oursRunningFor, TimeSpan theirsRunningFor, string ourId, string theirId)
    {
        if (!oursAutomatic)
        {
            return false;
        }

        if (!theirsAutomatic)
        {
            return true;
        }

        var oursOlderBy = oursRunningFor - theirsRunningFor;
        if (oursOlderBy >= SimultaneousWindow)
        {
            return true;
        }

        if (oursOlderBy <= -SimultaneousWindow)
        {
            return false;
        }

        return string.CompareOrdinal(ourId, theirId) > 0;
    }
}
