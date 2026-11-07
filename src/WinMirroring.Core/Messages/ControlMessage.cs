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
[JsonDerivedType(typeof(LogRequestMessage), "log-request")]
[JsonDerivedType(typeof(LogResponseMessage), "log-response")]
[JsonDerivedType(typeof(AudioRouteRequestMessage), "audio-route")]
public abstract record ControlMessage;

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

/// <summary>The device grid, broadcast whenever it changes; the newest version wins everywhere.</summary>
public sealed record LayoutMessage(long Version, List<LayoutCell> Cells) : ControlMessage;

/// <summary>
/// Sender → receiver: an audio stream begins. Packets arrive over UDP at the receiver's transport port, sealed with
/// <see cref="Key"/> (base64, AES-256-GCM). Codec "opus" or "pcm16".
/// </summary>
public sealed record AudioStartMessage(uint StreamId, string Key, string Codec, int SampleRate, int Channels, int FrameMs) : ControlMessage;

public sealed record AudioStopMessage(uint StreamId) : ControlMessage;

/// <summary>"Send me your sound" (or stop): the sender wants to hear the receiver's sound until it says otherwise.</summary>
public sealed record AudioRouteRequestMessage(bool Listen) : ControlMessage;

/// <summary>Viewer → source: stream monitor <see cref="MonitorIndex"/> as <see cref="StreamId"/> (again with a new index to switch).</summary>
public sealed record ScreenRequestMessage(uint StreamId, int MonitorIndex) : ControlMessage;

/// <summary>Source → viewer: a stream starts or changes. Kind is "monitor" or "window".</summary>
public sealed record ScreenStreamMessage(uint StreamId, string Kind, string Title, int MonitorIndex, int MonitorCount, bool ControlAllowed) : ControlMessage;

/// <summary>Either side: the stream ends.</summary>
public sealed record ScreenStopMessage(uint StreamId, string? Reason) : ControlMessage;

/// <summary>Paired device asks for the tail of this device's application log (troubleshooting).</summary>
public sealed record LogRequestMessage(uint RequestId) : ControlMessage;

/// <summary>Tail of the log; <see cref="Text"/> is empty with an <see cref="Error"/> when it could not be read.</summary>
public sealed record LogResponseMessage(uint RequestId, string FileName, string Text, string? Error) : ControlMessage;
