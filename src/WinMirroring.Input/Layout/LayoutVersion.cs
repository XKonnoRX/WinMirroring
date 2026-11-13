namespace WinMirroring.Input.Layout;

/// <summary>Ordering of layout versions between devices whose clocks need not agree.</summary>
public static class LayoutVersion
{
    /// <summary>
    /// The version for a change made now: the clock, but above everything this device has seen, so the change is
    /// newer than the layout it replaces even when this device's clock is behind the one that made that layout.
    /// </summary>
    public static long Next(long current, long nowUnixMilliseconds) => Math.Max(nowUnixMilliseconds, current + 1);

    /// <summary>
    /// Whether (version, origin) is newer than the current one. Equal versions from two devices that changed the
    /// layout at the same moment are ordered by the device id, so every device picks the same winner. An older
    /// device sends no origin; it then counts as the smallest.
    /// </summary>
    public static int Compare(long version, string? origin, long currentVersion, string? currentOrigin)
    {
        var byVersion = version.CompareTo(currentVersion);
        return byVersion != 0 ? byVersion : string.CompareOrdinal(origin ?? string.Empty, currentOrigin ?? string.Empty);
    }
}
