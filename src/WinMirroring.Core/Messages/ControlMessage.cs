using System.Text.Json.Serialization;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Messages;

/// <summary>Messages on the control channel. JSON with a "t" discriminator; new types append to the list.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(AuthResultMessage), "auth-result")]
[JsonDerivedType(typeof(PairRequestMessage), "pair-request")]
[JsonDerivedType(typeof(PairChallengeMessage), "pair-challenge")]
[JsonDerivedType(typeof(PairProofMessage), "pair-proof")]
[JsonDerivedType(typeof(PairResultMessage), "pair-result")]
[JsonDerivedType(typeof(PairCancelMessage), "pair-cancel")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(LayoutMessage), "layout")]
[JsonDerivedType(typeof(AudioStartMessage), "audio-start")]
[JsonDerivedType(typeof(AudioStopMessage), "audio-stop")]
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

public sealed record PairRequestMessage : ControlMessage;

public sealed record PairChallengeMessage(string Nonce) : ControlMessage;

public sealed record PairProofMessage(string Proof) : ControlMessage;

public sealed record PairResultMessage(bool Ok, string? Proof, string? Reason) : ControlMessage;

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
