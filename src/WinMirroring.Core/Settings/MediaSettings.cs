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

public sealed class ScreenSettings
{
    /// <summary>Paired devices may view this device's monitors and windows.</summary>
    public bool AllowViewing { get; set; } = true;

    /// <summary>Viewers may also control this device with pointer and keyboard.</summary>
    public bool AllowControl { get; set; } = true;

    public int MaxFps { get; set; } = 30;

    /// <summary>JPEG quality of streamed tiles, 30–95.</summary>
    public int Quality { get; set; } = 70;

    /// <summary>Dragging a window by its title bar across a screen edge sends it to that device.</summary>
    public bool WindowDragEnabled { get; set; } = true;

    /// <summary>
    /// Keep this device's desktop extended onto this peer as a second monitor the whole time it is connected
    /// (empty: none). Transferring a window to that peer then moves the real window onto the virtual monitor.
    /// </summary>
    public string SecondMonitorPeerId { get; set; } = string.Empty;
}

public sealed class AudioSettings
{
    /// <summary>Let this device's sound be sent to another device.</summary>
    public bool SendEnabled { get; set; } = true;

    /// <summary>Send sound to whichever device currently controls this one with its mouse and keyboard.</summary>
    public bool FollowInput { get; set; } = true;

    /// <summary>Always send sound to this device while it is connected (empty: none).</summary>
    public string PinnedTargetId { get; set; } = string.Empty;

    /// <summary>Send sound to a device that is watching one of this device's monitors.</summary>
    public bool FollowScreenViewer { get; set; } = true;

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
