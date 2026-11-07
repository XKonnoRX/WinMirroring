using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace WinMirroring.Core.Media;

public delegate void MediaPacketHandler(IPEndPoint from, ReadOnlySpan<byte> packet);

/// <summary>
/// One UDP socket per node for real-time media (audio now, screen later). It binds the transport port number on
/// UDP; packets are authenticated and decrypted per stream by <see cref="MediaCipher"/>, so the socket itself
/// accepts anything and leaves filtering to the stream owners.
/// </summary>
public sealed class MediaSocket : IAsyncDisposable
{
    private const int SioUdpConnReset = -1744830452;

    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public MediaSocket(int port, ILogger logger)
    {
        _logger = logger;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1 << 20,
            SendBufferSize = 1 << 20,
        };

        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            try
            {
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Raised on the receive loop; handlers must be quick and must not keep the span.</summary>
    public event MediaPacketHandler? PacketReceived;

    public void Start()
    {
        _loop ??= Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _logger.LogInformation("Media listening on UDP {Port}", Port);
    }

    /// <summary>Fire-and-forget datagram; UDP sends do not block in practice.</summary>
    public void Send(IPEndPoint to, ReadOnlySpan<byte> datagram)
    {
        try
        {
            _socket.SendTo(datagram, SocketFlags.None, to);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug("Media send to {Target} failed: {Error}", to, ex.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Media receive failed");
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            if (result.RemoteEndPoint is IPEndPoint from)
            {
                try
                {
                    PacketReceived?.Invoke(from, buffer.AsSpan(0, result.ReceivedBytes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Media packet handler failed");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Close();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _cts.Dispose();
    }
}
