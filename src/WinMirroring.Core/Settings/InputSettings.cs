namespace WinMirroring.Core.Settings;

/// <summary>
/// Where a device sits on the shared grid. Neighbouring cells share an edge the cursor can cross.
/// </summary>
/// <param name="Monitor">
/// One monitor of a device with several (its key, see <c>LayoutMonitor.Key</c>): crossings start and end on that
/// monitor's edges. Null: the device as a whole (all its monitors not placed on their own).
/// </param>
public sealed record LayoutCell(string DeviceId, int Column, int Row, string? Monitor = null);

public sealed class InputSettings
{
    /// <summary>Install the hooks and take part in input sharing at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far (in raw mouse counts) the cursor must be pushed against an edge before it crosses.</summary>
    public int EdgeResistance { get; set; } = 8;

    /// <summary>Re-time incoming mouse motion with an adaptive jitter buffer (applies on the controlled device).</summary>
    public bool SmoothingEnabled { get; set; } = true;

    /// <summary>Upper bound for the jitter buffer; the buffer adapts below it to the measured jitter.</summary>
    public int MaxSmoothingDelayMs { get; set; } = 30;

    /// <summary>The device that gets the keyboard switches to the controller's layout, and layout switches made there come back.</summary>
    public bool SyncKeyboardLayout { get; set; } = true;

    /// <summary>
    /// Route injection through a separate elevated helper so input from other devices reaches administrator windows,
    /// while the app itself stays at normal integrity. Off by default: it is a deliberate grant of elevated input.
    /// </summary>
    public bool ElevatedInjector { get; set; }

    /// <summary>
    /// Version of the layout: the time of the last change in Unix milliseconds, but always above every version seen
    /// before, so a change wins even on a device whose clock is behind. Peers adopt the newest layout they see.
    /// </summary>
    public long LayoutVersion { get; set; }

    /// <summary>The device that made the current layout; breaks a tie between two equal versions the same way everywhere.</summary>
    public string LayoutOrigin { get; set; } = string.Empty;

    public List<LayoutCell> Layout { get; set; } = [];
}
