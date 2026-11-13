using System.Text.Json.Serialization;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Messages;

/// <summary>Messages on the control channel. JSON with a "t" discriminator; new types append to the list.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(AuthResultMessage), "auth-result")]
[JsonDerivedType(typeof(PairRequestMessage), "pair-request")]
[JsonDerivedType(typeof(PairResultMessage), "pair-result")]
[JsonDerivedType(typeof(PairCancelMessage), "pair-cancel")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(LayoutMessage), "layout")]
[JsonDerivedType(typeof(AudioStartMessage), "audio-start")]
[JsonDerivedType(typeof(AudioStopMessage), "audio-stop")]
[JsonDerivedType(typeof(ScreenRequestMessage), "screen-request")]
[JsonDerivedType(typeof(ScreenStreamMessage), "screen-stream")]
[JsonDerivedType(typeof(ScreenStopMessage), "screen-stop")]
[JsonDerivedType(typeof(ScreenCodecFallbackMessage), "screen-fallback")]
[JsonDerivedType(typeof(LogRequestMessage), "log-request")]
[JsonDerivedType(typeof(LogResponseMessage), "log-response")]
[JsonDerivedType(typeof(AudioRouteRequestMessage), "audio-route")]
[JsonDerivedType(typeof(InputReleaseRequestMessage), "input-release")]
[JsonDerivedType(typeof(InputBlockedMessage), "input-blocked")]
[JsonDerivedType(typeof(DisplayInviteMessage), "display-invite")]
[JsonDerivedType(typeof(DisplayExtendRequestMessage), "display-extend")]
[JsonDerivedType(typeof(DisplayExtendResultMessage), "display-result")]
[JsonDerivedType(typeof(KeyboardLayoutMessage), "kbd-layout")]
[JsonDerivedType(typeof(DeviceCapabilitiesMessage), "caps")]
[JsonDerivedType(typeof(InputMonitorsMessage), "input-monitors")]
[JsonDerivedType(typeof(DeviceInfoMessage), "device-info")]
[JsonDerivedType(typeof(PeerVouchMessage), "vouch")]
[JsonDerivedType(typeof(VouchRelayMessage), "vouch-relay")]
public abstract record ControlMessage;

/// <summary>
/// What this device can offer: a virtual display driver (so it can extend its desktop), its monitors (so the other
/// side can pick one to view) and the screen codecs it can use (a source sends H.264 only to a viewer that can decode
/// it; otherwise JPEG tiles). Sent when a session is ready; a message that is not itself a reply is answered with the
/// receiver's own, because messages can arrive before the receiver has subscribed and be lost. Sent again, as a
/// reply, when the device's monitors change.
/// </summary>
public sealed record DeviceCapabilitiesMessage(
    bool HasVirtualDisplay,
    bool IsReply = false,
    List<RemoteMonitor>? Monitors = null,
    bool CanEncodeH264 = false,
    bool CanDecodeH264 = false) : ControlMessage;

/// <summary>
/// The app version this device runs ("1.0.13"), so devices can show it and offer to bring an older one up to date
/// from a newer one. Sent when a session is ready and answered (<see cref="IsReply"/>) like the capabilities.
/// </summary>
public sealed record DeviceInfoMessage(string AppVersion, bool IsReply = false) : ControlMessage;

/// <summary>
/// A device the sender is paired with, offered to a peer it is also paired with so the two can recognise each
/// other. The certificate fingerprint is the whole point: it is what the receiver pins, and it is only worth
/// anything because it arrives from a device the receiver has already confirmed by hand.
/// </summary>
public sealed record VouchedPeer(
    string Id,
    string Name,
    string Fingerprint,
    int TransportPort,
    int DiscoveryPort,
    List<string>? Addresses = null);

/// <summary>Everyone the sender is paired with, so that pairings made one by one can close into a mesh.</summary>
public sealed record PeerVouchMessage(List<VouchedPeer> Peers, bool IsReply = false) : ControlMessage;

/// <summary>
/// "Pass this on." Sent to the device that gave the vouch: the sender has just trusted <see cref="ToPeerId"/> on
/// its word and asks it to tell that device, so trust ends up on both sides from one confirmation. The hop in the
/// middle only forwards the record of the device it is talking to, and only to a device it is itself paired with;
/// the far end recognises the message by <see cref="ToPeerId"/> being its own.
/// </summary>
public sealed record VouchRelayMessage(string ToPeerId, VouchedPeer Peer) : ControlMessage;

/// <summary>
/// One monitor of a device. <see cref="Key"/> is its display device name, which names it exactly; the index is
/// only a position and is kept for devices that do not send a key.
/// </summary>
public sealed record RemoteMonitor(int Index, string Label, bool Primary, string Key = "");

/// <summary>First message in each direction. The server adds a challenge the client must sign.</summary>
public sealed record HelloMessage(
    string DeviceId,
    string DeviceName,
    int ProtocolVersion,
    string Certificate,
    string? Challenge,
    int TransportPort,
    int DiscoveryPort) : ControlMessage;

/// <summary>Client's signature over the server challenge plus both fingerprints, and whether it already pins the server.</summary>
public sealed record AuthMessage(string Signature, string ServerFingerprint, bool Paired) : ControlMessage;

public sealed record AuthResultMessage(bool Ok, bool Paired, string? Reason) : ControlMessage;

/// <summary>Initiator → responder: please ask your user to accept pairing with me.</summary>
public sealed record PairRequestMessage : ControlMessage;

/// <summary>Responder → initiator: the user accepted (both sides now pin each other) or not.</summary>
public sealed record PairResultMessage(bool Ok, string? Reason) : ControlMessage;

public sealed record PairCancelMessage(string? Reason) : ControlMessage;

public sealed record PingMessage(long Timestamp) : ControlMessage;

public sealed record PongMessage(long Timestamp) : ControlMessage;

public sealed record ByeMessage(string? Reason) : ControlMessage;

/// <summary>
/// The device grid, broadcast whenever it changes and passed on by every device that adopts it, so it also reaches
/// devices the author is not connected to. The newest (<see cref="Version"/>, then <see cref="Origin"/>) wins
/// everywhere; a device that gets an older one answers with its own.
/// </summary>
public sealed record LayoutMessage(long Version, List<LayoutCell> Cells, string? Origin = null) : ControlMessage;

/// <summary>
/// The sender's monitors, for placing them one by one in the layout. Sent when a session starts (the other side
/// answers with <see cref="IsReply"/> set) and again, as a reply, when the sender's monitors change. A device that
/// sends it also understands crossings onto a given monitor; an older one never sends it.
/// </summary>
public sealed record InputMonitorsMessage(List<LayoutMonitor> Monitors, bool IsReply = false) : ControlMessage;

/// <param name="Key">Stable name of the monitor on its device (its display device name).</param>
/// <param name="Label">How to show it, e.g. "Монитор 2 · 1920×1080 · справа".</param>
/// <param name="Width">Its size in pixels, so the layout editor can draw it with its own proportions (0: unknown).</param>
/// <param name="Left">Where it sits on its device's desktop, so the editor can show them arranged as they really are.</param>
public sealed record LayoutMonitor(
    string Key,
    string Label,
    bool Primary,
    int Width = 0,
    int Height = 0,
    int Left = 0,
    int Top = 0);

/// <summary>
/// Sender → receiver: an audio stream begins. Packets arrive over UDP at the receiver's transport port, sealed with
/// <see cref="Key"/> (base64, AES-256-GCM). Codec "opus" or "pcm16".
/// </summary>
/// <param name="Automatic">
/// The sound goes there on its own (it follows input or a screen view), not because a user chose it. When two
/// devices would send to each other on their own, the newer one wins and the other gives way. Sent again with the
/// same stream and key when only the reason changes.
/// </param>
public sealed record AudioStartMessage(uint StreamId, string Key, string Codec, int SampleRate, int Channels, int FrameMs, bool Automatic = false) : ControlMessage;

public sealed record AudioStopMessage(uint StreamId) : ControlMessage;

/// <summary>
/// The receiver's wish about the sender's sound: <see cref="Listen"/> "send it here"; <see cref="Refuse"/> "do not
/// send it here" (not even automatically, until asked again); neither: back to automatic.
/// </summary>
public sealed record AudioRouteRequestMessage(bool Listen, bool Refuse = false) : ControlMessage;

/// <summary>
/// Viewer → source: stream this monitor as <see cref="StreamId"/> (again with a different one to switch). The
/// monitor is named by <see cref="MonitorKey"/> where it is known, because an index is only a position in a list
/// and the two sides can order or filter theirs differently; the index remains for older devices.
/// </summary>
public sealed record ScreenRequestMessage(uint StreamId, int MonitorIndex, string? MonitorKey = null) : ControlMessage;

/// <summary>
/// Source → viewer: a stream starts or changes. Kind is "monitor", "window" or "display". <see cref="Codec"/>
/// is the encoding the source will use ("jpeg" changed tiles, or "h264" full-frame video); it defaults to "jpeg"
/// so a message from an older peer that omits the field is read as the tile path.
/// </summary>
public sealed record ScreenStreamMessage(
    uint StreamId,
    string Kind,
    string Title,
    int MonitorIndex,
    int MonitorCount,
    bool ControlAllowed,
    bool Minimized = false,
    int ScalePercent = 100,
    string Codec = "jpeg",
    string MonitorKey = "") : ControlMessage;

/// <summary>Either side: the stream ends.</summary>
public sealed record ScreenStopMessage(uint StreamId, string? Reason) : ControlMessage;

/// <summary>Viewer → source: H.264 gives no picture here (no decoder, or it stays silent); send JPEG tiles instead.</summary>
public sealed record ScreenCodecFallbackMessage(uint StreamId, string Reason) : ControlMessage;

/// <summary>Controlled → controller: take your input back (the user pressed "return the cursor" here).</summary>
public sealed record InputReleaseRequestMessage : ControlMessage;

/// <summary>Controlled → controller: an elevated window in front (<see cref="Window"/>) swallows the injected input.</summary>
public sealed record InputBlockedMessage(string Window) : ControlMessage;

/// <summary>Host → device: "become my second monitor"; the device answers with <see cref="DisplayExtendRequestMessage"/>.</summary>
public sealed record DisplayInviteMessage : ControlMessage;

/// <summary>Device → host: extend your desktop onto me; my screen is <see cref="Width"/>×<see cref="Height"/> physical pixels.</summary>
public sealed record DisplayExtendRequestMessage(int Width, int Height, int ScalePercent) : ControlMessage;

/// <summary>Host → device: the extension did not start (the stream message follows when it did).</summary>
public sealed record DisplayExtendResultMessage(bool Ok, string? Error) : ControlMessage;

/// <summary>
/// Keyboard layout (low 32 bits of an HKL: language in the low word). Controller → controlled device and viewer →
/// source: "type with this layout"; controlled device → controller: "the user switched to this layout here".
/// </summary>
public sealed record KeyboardLayoutMessage(int Layout) : ControlMessage;

/// <summary>Paired device asks for the tail of this device's application log (troubleshooting).</summary>
public sealed record LogRequestMessage(uint RequestId) : ControlMessage;

/// <summary>Tail of the log; <see cref="Text"/> is empty with an <see cref="Error"/> when it could not be read.</summary>
public sealed record LogResponseMessage(uint RequestId, string FileName, string Text, string? Error) : ControlMessage;
