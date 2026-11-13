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

/// <summary>How a shared screen is encoded on the wire.</summary>
public enum VideoCodec
{
    /// <summary>Hardware H.264 when both devices support it, otherwise JPEG tiles.</summary>
    Auto,

    /// <summary>Always the changed-tile JPEG path.</summary>
    Jpeg,

    /// <summary>Hardware H.264 when both devices support it (same as <see cref="Auto"/>; forces the preference).</summary>
    H264,
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

    /// <summary>
    /// How the screen is encoded. <see cref="VideoCodec.Auto"/> uses hardware H.264 when both devices can, and
    /// falls back to JPEG tiles otherwise; H.264 is a full-frame stream, JPEG sends only the changed tiles.
    /// </summary>
    public VideoCodec VideoCodec { get; set; } = VideoCodec.Auto;

    /// <summary>Target H.264 bitrate in kbit/s for a full-screen stream (scaled down for smaller crops).</summary>
    public int VideoBitrateKbps { get; set; } = 12_000;

    /// <summary>
    /// Close a view that has been left minimised this long, in minutes (0: never). A view nobody looks at still
    /// costs the other device a capture and a stream, and opening it again is one press.
    /// </summary>
    public int CloseUnusedViewMinutes { get; set; } = 5;

    /// <summary>Dragging a window by its title bar across a screen edge sends it to that device.</summary>
    public bool WindowDragEnabled { get; set; } = true;

    /// <summary>
    /// While a window is shown as a copy on another device, park the real window on the virtual monitor (needs the
    /// Virtual Display Driver) so the same window is not visible on this device. It comes back when the other device
    /// stops showing it. Without the driver the window stays where it is.
    /// </summary>
    public bool HideSentWindow { get; set; } = true;
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
