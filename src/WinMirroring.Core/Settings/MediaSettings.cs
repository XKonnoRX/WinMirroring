namespace WinMirroring.Core.Settings;

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Offer copied files and folders to other devices (pasted on demand).</summary>
    public bool ShareFiles { get; set; } = true;

    /// <summary>Text and images above this size (uncompressed) are not sent.</summary>
    public int MaxInlineMegabytes { get; set; } = 64;

    /// <summary>File sets up to this size are also offered as ordinary files (CF_HDROP), downloaded when pasted.</summary>
    public int MaxDirectPasteMegabytes { get; set; } = 64;

    /// <summary>Drag files across a screen edge to another device.</summary>
    public bool DragDropEnabled { get; set; } = true;
}

public sealed class AudioSettings
{
    /// <summary>Let this device's sound be sent to another device.</summary>
    public bool SendEnabled { get; set; } = true;

    /// <summary>Send sound to whichever device currently controls this one with its mouse and keyboard.</summary>
    public bool FollowInput { get; set; } = true;

    /// <summary>Always send sound to this device while it is connected (empty: none).</summary>
    public string PinnedTargetId { get; set; } = string.Empty;

    /// <summary>Mute the local speakers while the sound plays elsewhere. Capture is not affected by mute.</summary>
    public bool MuteLocalWhileSending { get; set; } = true;

    /// <summary>Play sound that other devices send here.</summary>
    public bool ReceiveEnabled { get; set; } = true;

    public int BitrateKbps { get; set; } = 160;

    /// <summary>Playback jitter-buffer target.</summary>
    public int BufferMs { get; set; } = 40;

    /// <summary>Set while this app holds the speakers muted, so a crash can be repaired on the next start.</summary>
    public bool MutedByApp { get; set; }
}
