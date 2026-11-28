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

    /// <summary>
    /// HEVC where this device has a hardware encoder for it and the viewer decodes it on its graphics card; H.264
    /// (or tiles) otherwise, exactly as <see cref="Auto"/>. The same picture in fewer bits — worth it where the
    /// link is what limits the picture.
    /// </summary>
    Hevc,
}

public sealed class ScreenSettings
{
    /// <summary>Paired devices may view this device's monitors and windows.</summary>
    public bool AllowViewing { get; set; } = true;

    /// <summary>Viewers may also control this device with pointer and keyboard.</summary>
    public bool AllowControl { get; set; } = true;

    /// <summary>
    /// Frames a second to aim for. Sixty rather than thirty: at thirty a frame stands for thirty-three
    /// milliseconds of the other device's screen, so every hesitation anywhere in the chain is at least that long
    /// and reads as a stutter. At sixty each frame is worth half as much, the same hesitation costs half as much
    /// picture, and the rate the link can actually carry is decided by the send window and the bitrate rather than
    /// by this number. It costs nothing when nothing is moving, because a still screen produces no frames.
    /// </summary>
    public int MaxFps { get; set; } = 60;

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

    /// <summary>
    /// Viewing another device: a tap on a text box there brings this device's touch keyboard up, and a tap elsewhere
    /// takes it down again (only a keyboard brought up that way). Off by default — a keyboard jumping up at every tap
    /// turned out to be more in the way than the button it saves (see <see cref="KeyboardButton"/>). A new name
    /// rather than the old AutoKeyboard, so the "on" that 1.0.62 saved does not carry over.
    /// </summary>
    public bool AutoKeyboardOnTap { get; set; }

    /// <summary>A round button over the picture of another device that opens and closes the touch keyboard.</summary>
    public bool KeyboardButton { get; set; } = true;

    /// <summary>Where that button stands over the picture, as shares of the free room (0..1 across and down).</summary>
    public double KeyboardButtonX { get; set; } = 0.97;

    public double KeyboardButtonY { get; set; } = 0.62;

    /// <summary>
    /// Viewing another device: pictures decoded by the graphics card are drawn from it directly instead of being
    /// read back into memory and uploaded again. On unless switched off — it is the faster path wherever it works,
    /// and a view goes back to the old one by itself where it does not. The switch is for the case that cannot
    /// detect itself: a driver that draws such a picture wrongly without saying so.
    /// </summary>
    public bool DirectPresent { get; set; } = true;

    /// <summary>
    /// The version of the program in which drawing from the graphics card was found to bring WPF's drawing thread
    /// down on this device, or empty. Written by the program when that happens; while it names the version that is
    /// running, pictures go through memory as if <see cref="DirectPresent"/> were off. A newer version tries again.
    /// </summary>
    public string DirectPresentBrokeIn { get; set; } = string.Empty;

    /// <summary>
    /// Viewing a device with several monitors, on a touch screen: a grip at the left and at the right edge of the
    /// picture, each only where there is a monitor on that side. Pulled inwards it brings that monitor in; touched,
    /// it goes there. On by default.
    /// </summary>
    public bool PageGrips { get; set; } = true;

    /// <summary>How far down the edge the grips stand, as a share of the room they have. They are moved by pulling one along the edge.</summary>
    public double PageGripsY { get; set; } = 0.5;

    /// <summary>
    /// Viewing a device with several monitors: three fingers swiped sideways go to the monitor on that side (and so
    /// are not passed on to the other device where there is one). Off unless asked for. It was the way to do this
    /// before the grips and was on then, under the name SwipeMonitors; it turned out awkward to do, and Windows 11
    /// keeps three fingers for itself unless told otherwise — so it has a new name, under which nobody has it on
    /// who has not chosen to.
    /// </summary>
    public bool ThreeFingerSwipe { get; set; }

    /// <summary>
    /// Viewing another device: a line over the picture with how the stream is doing — frames, the delay as the
    /// source measures it, the path, the bitrate, what has been lost. Off by default; it is for when something
    /// feels wrong and one wants to see what.
    /// </summary>
    public bool ShowStats { get; set; }

    /// <summary>
    /// Viewing another device: a microphone button beside the keyboard button that takes this device's voice
    /// typing and types what it hears on the other device.
    /// </summary>
    public bool DictationButton { get; set; } = true;

    /// <summary>
    /// Showing this device's screen: once it has stood still for a moment, send it once more as a single sharp
    /// picture (full colour for every pixel, which video does not keep), for the viewer to lay over the video
    /// until something moves. A few hundred kilobytes per pause; on unless switched off.
    /// </summary>
    public bool SharpStill { get; set; } = true;
}

/// <summary>
/// Letting a paired device run commands here — a console on this machine, in the place of logging into it.
///
/// Off until it is turned on, deliberately. A paired device can already move this one's mouse and type on its
/// keyboard, so this grants nothing it did not have; what it changes is how easy that power is to use, and the
/// first console each device opens is still asked about by whoever is sitting here.
/// </summary>
public sealed class ShellSettings
{
    public bool AllowCommands { get; set; }

    /// <summary>
    /// Also let a paired device ask for a console with administrator rights. A setting of its own, and off until
    /// it is turned on, because unlike the one above this is not a power the asking device already had: an
    /// ordinary console can do what the person at this machine can do, an administrator one can do anything to
    /// the machine. Every such console is confirmed here, each time, and Windows asks as well — the helper that
    /// runs it cannot start without its prompt.
    /// </summary>
    public bool AllowElevatedCommands { get; set; }
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

    /// <summary>
    /// Let another device send its microphone here, to be played into a virtual cable so that programs on this
    /// device can use it as a microphone. Nothing is ever played into the speakers by this.
    /// </summary>
    public bool AcceptMicrophone { get; set; } = true;

    /// <summary>
    /// The output another device's microphone is played into: its id or its name. Empty: the first virtual cable
    /// found (VB-CABLE, Steam's streaming microphone and the like), and nothing where there is none.
    /// </summary>
    public string MicrophoneOutput { get; set; } = string.Empty;
}
