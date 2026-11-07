namespace WinMirroring.Core.Settings;

/// <summary>Where a device sits on the shared grid. Neighbouring cells share an edge the cursor can cross.</summary>
public sealed record LayoutCell(string DeviceId, int Column, int Row);

public sealed class InputSettings
{
    /// <summary>Install the hooks and take part in input sharing at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far (in raw mouse counts) the cursor must be pushed against an edge before it crosses.</summary>
    public int EdgeResistance { get; set; } = 8;

    /// <summary>Unix milliseconds of the last layout change; peers adopt the newest layout they see.</summary>
    public long LayoutVersion { get; set; }

    public List<LayoutCell> Layout { get; set; } = [];
}
