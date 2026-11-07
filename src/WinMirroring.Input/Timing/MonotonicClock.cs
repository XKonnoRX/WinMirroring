using System.Diagnostics;

namespace WinMirroring.Input.Timing;

/// <summary>Microseconds from the high-resolution performance counter. Only differences are meaningful.</summary>
public static class MonotonicClock
{
    private static readonly double MicrosecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowMicroseconds() => (long)(Stopwatch.GetTimestamp() * MicrosecondsPerTick);

    /// <summary>The wire form: the low 32 bits, wrapping every ~71 minutes.</summary>
    public static uint NowWire() => unchecked((uint)NowMicroseconds());
}
