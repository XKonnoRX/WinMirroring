namespace WinMirroring.Core.Settings;

/// <summary>When this device should spend less to do the same job.</summary>
public enum PowerSaving
{
    /// <summary>Never: everything runs as often and as fast as it is set to.</summary>
    Never,

    /// <summary>While this device is running on its own battery, which is what this is for.</summary>
    OnBattery,

    /// <summary>Always, whatever it is plugged into.</summary>
    Always,
}

/// <summary>
/// Running on a battery, where the cost of a thing is not what it takes but how often it takes it.
///
/// Everything here is a trade: fewer frames, a slower beacon, a longer pulse between two devices that are talking.
/// What is free was not left for a mode — it was simply done, because doing nothing should cost nothing whatever
/// the device is plugged into.
/// </summary>
public sealed class PowerSettings
{
    /// <summary>
    /// On a battery by default, which on a machine that has none never comes on at all. A tablet is the device
    /// this matters for and the one least likely to be asked about it.
    /// </summary>
    public PowerSaving Saving { get; set; } = PowerSaving.OnBattery;

    /// <summary>
    /// Frames a second while saving: what this device sends, and what it asks for when it is the one watching.
    /// Half of sixty is half the capturing, half the encoding, half the sending and half the decoding, and on a
    /// screen being read rather than played it is hard to see.
    /// </summary>
    public int Fps { get; set; } = 30;
}
