using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Peers;

public sealed class HandshakeException(string message) : Exception(message);

public sealed record HandshakeResult(
    string PeerId,
    string PeerName,
    string PeerFingerprint,
    X509Certificate2 PeerCertificate,
    int PeerTransportPort,
    int PeerDiscoveryPort,
    bool PairedOnBothSides);

/// <summary>
/// Application-level authentication right after TLS. The client learned the server's identity from the TLS
/// certificate; here it proves its own by signing a server challenge with its certificate key.
/// </summary>
public static class PeerHandshake
{
    private static readonly byte[] SignatureDomain = Encoding.ASCII.GetBytes("WinMirroring-auth-v1");

    public static async Task<HandshakeResult> RunAsInitiatorAsync(
        PeerConnection connection,
        DeviceIdentity identity,
        int transportPort,
        int discoveryPort,
        Func<string, string, bool> isPaired,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProtocolConstants.HandshakeTimeout);
        var token = timeout.Token;

        var serverCertificate = connection.RemoteCertificate
            ?? throw new HandshakeException("The server presented no certificate.");
        var serverFingerprint = CertificateFingerprint.Compute(serverCertificate);

        await SendAsync(connection, new HelloMessage(
            identity.Id, identity.Name, ProtocolConstants.Version,
            Convert.ToBase64String(identity.Certificate.RawData), null, transportPort, discoveryPort), token).ConfigureAwait(false);

        var serverHello = await ReceiveAsync<HelloMessage>(connection, token).ConfigureAwait(false);
        if (serverHello.ProtocolVersion != ProtocolConstants.Version)
        {
            throw new HandshakeException($"Protocol version {serverHello.ProtocolVersion} is not supported.");
        }

        if (serverHello.Challenge is null)
        {
            throw new HandshakeException("The server sent no challenge.");
        }

        if (!CertificateFingerprint.AreEqual(CertificateFingerprint.Compute(Convert.FromBase64String(serverHello.Certificate)), serverFingerprint))
        {
            throw new HandshakeException("The server's hello certificate does not match its TLS certificate.");
        }

        var challenge = Convert.FromBase64String(serverHello.Challenge);
        var signature = identity.Sign(BuildSignedData(challenge, serverFingerprint, identity.Fingerprint));
        var pairedLocally = isPaired(serverFingerprint, serverHello.DeviceId);

        await SendAsync(connection, new AuthMessage(Convert.ToBase64String(signature), serverFingerprint, pairedLocally), token).ConfigureAwait(false);

        var result = await ReceiveAsync<AuthResultMessage>(connection, token).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new HandshakeException(result.Reason ?? "The server rejected the handshake.");
        }

        return new HandshakeResult(
            serverHello.DeviceId,
            serverHello.DeviceName,
            serverFingerprint,
            serverCertificate,
            serverHello.TransportPort,
            serverHello.DiscoveryPort,
            result.Paired && pairedLocally);
    }

    public static async Task<HandshakeResult> RunAsResponderAsync(
        PeerConnection connection,
        DeviceIdentity identity,
        int transportPort,
        int discoveryPort,
        Func<string, string, bool> isPaired,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProtocolConstants.HandshakeTimeout);
        var token = timeout.Token;

        var clientHello = await ReceiveAsync<HelloMessage>(connection, token).ConfigureAwait(false);
        if (clientHello.ProtocolVersion != ProtocolConstants.Version)
        {
            await SendAsync(connection, new AuthResultMessage(false, false, "unsupported-version"), token).ConfigureAwait(false);
            throw new HandshakeException($"Protocol version {clientHello.ProtocolVersion} is not supported.");
        }

        X509Certificate2 clientCertificate;
        try
        {
            clientCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(clientHello.Certificate));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            await SendAsync(connection, new AuthResultMessage(false, false, "bad-certificate"), token).ConfigureAwait(false);
            throw new HandshakeException("The client sent an unreadable certificate.");
        }

        var clientFingerprint = CertificateFingerprint.Compute(clientCertificate);
        var challenge = PairingCrypto.GenerateNonce();

        await SendAsync(connection, new HelloMessage(
            identity.Id, identity.Name, ProtocolConstants.Version,
            Convert.ToBase64String(identity.Certificate.RawData), Convert.ToBase64String(challenge), transportPort, discoveryPort), token).ConfigureAwait(false);

        var auth = await ReceiveAsync<AuthMessage>(connection, token).ConfigureAwait(false);
        if (!CertificateFingerprint.AreEqual(auth.ServerFingerprint, identity.Fingerprint))
        {
            await SendAsync(connection, new AuthResultMessage(false, false, "server-fingerprint-mismatch"), token).ConfigureAwait(false);
            throw new HandshakeException("The client saw a different server certificate than ours.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(auth.Signature);
        }
        catch (FormatException)
        {
            signature = [];
        }

        if (!DeviceIdentity.Verify(clientCertificate, BuildSignedData(challenge, identity.Fingerprint, clientFingerprint), signature))
        {
            await SendAsync(connection, new AuthResultMessage(false, false, "bad-signature"), token).ConfigureAwait(false);
            throw new HandshakeException("The client failed to prove possession of its certificate key.");
        }

        var pairedLocally = isPaired(clientFingerprint, clientHello.DeviceId);
        var pairedOnBothSides = pairedLocally && auth.Paired;
        await SendAsync(connection, new AuthResultMessage(true, pairedOnBothSides, null), token).ConfigureAwait(false);

        return new HandshakeResult(
            clientHello.DeviceId,
            clientHello.DeviceName,
            clientFingerprint,
            clientCertificate,
            clientHello.TransportPort,
            clientHello.DiscoveryPort,
            pairedOnBothSides);
    }

    private static byte[] BuildSignedData(byte[] challenge, string serverFingerprint, string clientFingerprint)
    {
        var server = Encoding.ASCII.GetBytes(serverFingerprint.ToUpperInvariant());
        var client = Encoding.ASCII.GetBytes(clientFingerprint.ToUpperInvariant());
        var data = new byte[SignatureDomain.Length + challenge.Length + server.Length + client.Length];
        var offset = 0;
        foreach (var part in new[] { SignatureDomain, challenge, server, client })
        {
            part.CopyTo(data, offset);
            offset += part.Length;
        }

        return data;
    }

    private static ValueTask SendAsync(PeerConnection connection, ControlMessage message, CancellationToken ct) =>
        connection.SendAsync(ChannelId.Control, ControlMessageCodec.Encode(message), ct);

    private static async Task<T> ReceiveAsync<T>(PeerConnection connection, CancellationToken ct) where T : ControlMessage
    {
        var frame = await connection.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new HandshakeException("The peer closed the connection during the handshake.");

        if (frame.Channel != ChannelId.Control)
        {
            throw new HandshakeException($"Unexpected {frame.Channel} frame during the handshake.");
        }

        var message = ControlMessageCodec.TryDecode(frame.Payload);
        return message switch
        {
            T expected => expected,
            AuthResultMessage { Ok: false } rejected => throw new HandshakeException(rejected.Reason ?? "rejected"),
            null => throw new HandshakeException("Unreadable handshake message."),
            _ => throw new HandshakeException($"Expected {typeof(T).Name}, got {message.GetType().Name}."),
        };
    }
}
