using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Transport;

/// <summary>
/// A TLS-protected TCP link to one peer. The server presents the device certificate; the client authenticates
/// afterwards inside the tunnel (see PeerHandshake), so no client certificate is needed at the TLS layer.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private PeerConnection(TcpClient client, SslStream stream, bool isInitiator, X509Certificate2? remoteCertificate)
    {
        _client = client;
        _stream = stream;
        IsInitiator = isInitiator;
        RemoteCertificate = remoteCertificate;
        RemoteEndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        LocalEndPoint = (IPEndPoint)client.Client.LocalEndPoint!;
    }

    /// <summary>True on the side that opened the TCP connection.</summary>
    public bool IsInitiator { get; }

    /// <summary>The server's TLS certificate, available on the initiator side only.</summary>
    public X509Certificate2? RemoteCertificate { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public IPEndPoint LocalEndPoint { get; }

    public SslProtocols NegotiatedProtocol => _stream.SslProtocol;

    public static async Task<PeerConnection> ConnectAsync(
        IPEndPoint endPoint,
        Func<X509Certificate2, bool> acceptServerCertificate,
        CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProtocolConstants.ConnectTimeout);
            await client.ConnectAsync(endPoint, timeout.Token).ConfigureAwait(false);

            X509Certificate2? captured = null;
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = ProtocolConstants.TlsTargetHost,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    captured = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return acceptServerCertificate(captured);
                },
            };

            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(ProtocolConstants.HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(options, handshakeTimeout.Token).ConfigureAwait(false);

            return new PeerConnection(client, ssl, isInitiator: true, captured);
        }
        catch
        {
            ssl?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public static async Task<PeerConnection> AcceptAsync(TcpClient client, X509Certificate2 serverCertificate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.NoDelay = true;
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProtocolConstants.HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(options, timeout.Token).ConfigureAwait(false);

            return new PeerConnection(client, ssl, isInitiator: false, remoteCertificate: null);
        }
        catch
        {
            ssl.Dispose();
            client.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(ChannelId channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, channel, payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask<Frame?> ReceiveAsync(CancellationToken ct) => FrameCodec.ReadAsync(_stream, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        _client.Dispose();
        _writeLock.Dispose();
    }
}
