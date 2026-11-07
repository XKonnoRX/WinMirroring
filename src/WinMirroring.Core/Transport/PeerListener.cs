using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace WinMirroring.Core.Transport;

/// <summary>Accepts TCP connections on each selected interface address and hands them to the session manager.</summary>
public sealed class PeerListener : IAsyncDisposable
{
    private readonly IReadOnlyList<IPAddress> _bindAddresses;
    private readonly int _port;
    private readonly Func<TcpClient, Task> _onAccepted;
    private readonly Func<IPAddress, bool> _isSourceAllowed;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];

    public PeerListener(
        IReadOnlyList<IPAddress> bindAddresses,
        int port,
        Func<TcpClient, Task> onAccepted,
        Func<IPAddress, bool> isSourceAllowed,
        ILogger logger)
    {
        _bindAddresses = bindAddresses;
        _port = port;
        _onAccepted = onAccepted;
        _isSourceAllowed = isSourceAllowed;
        _logger = logger;
    }

    public IReadOnlyList<IPEndPoint> BoundEndPoints =>
        _listeners.Select(l => (IPEndPoint)l.LocalEndpoint).ToList();

    public void Start()
    {
        foreach (var address in _bindAddresses)
        {
            var listener = new TcpListener(address, _port);
            try
            {
                listener.Start(backlog: 16);
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Cannot listen on {Address}:{Port}", address, _port);
                continue;
            }

            _listeners.Add(listener);
            _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(listener, _cts.Token)));
        }

        _logger.LogInformation("Transport listening on {EndPoints}", string.Join(", ", BoundEndPoints));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed on {EndPoint}", listener.LocalEndpoint);
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var remote = (IPEndPoint?)client.Client.RemoteEndPoint;
            if (remote is null || !_isSourceAllowed(remote.Address))
            {
                _logger.LogDebug("Rejected connection from {Remote} outside selected subnets", remote);
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _onAccepted(client).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Inbound connection from {Remote} failed", remote);
                }
            }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        foreach (var loop in _acceptLoops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _cts.Dispose();
    }
}
