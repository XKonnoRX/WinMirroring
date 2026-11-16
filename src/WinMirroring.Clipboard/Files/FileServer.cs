using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard.Files;

/// <summary>Answers chunk requests for files this device offered. Keeps a few handles open for sequential reads.</summary>
internal sealed class FileServer : IDisposable
{
    private const int MaxOpenHandles = 4;
    private static readonly TimeSpan HandleIdle = TimeSpan.FromSeconds(30);

    private sealed class OpenFile(SafeFileHandle handle)
    {
        public SafeFileHandle Handle { get; } = handle;

        public long LastUsed { get; set; } = Environment.TickCount64;
    }

    private readonly LocalOfferStore _store;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenFile> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(PeerSession Session, uint Id), BundleSender> _bundles = new();

    public FileServer(LocalOfferStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task HandleRequestAsync(PeerSession session, byte[] request)
    {
        if (!FileCodec.TryDecodeRequest(request, out var id, out var offerId, out var index, out var offset, out var length))
        {
            return;
        }

        byte[] response;
        if (index == 0 && _store.TryGetBlob(offerId, out var blob))
        {
            var start = (int)Math.Min(offset, blob.Length);
            var count = Math.Min(Math.Min(length, FileCodec.MaxChunk), blob.Length - start);
            response = FileCodec.EncodeResponse(id, FileChunkStatus.Ok, blob.AsSpan(start, count));
        }
        else if (!_store.TryGetFile(offerId, index, out var path))
        {
            response = FileCodec.EncodeResponse(id, FileChunkStatus.NotFound, []);
        }
        else
        {
            try
            {
                var buffer = new byte[Math.Min(length, FileCodec.MaxChunk)];
                var handle = Acquire(path);
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(total), offset + total).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                response = FileCodec.EncodeResponse(id, FileChunkStatus.Ok, buffer.AsSpan(0, total));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _logger.LogInformation("Cannot read {Path} for {Peer}: {Message}", path, session.PeerName, ex.Message);
                Release(path);
                response = FileCodec.EncodeResponse(id, FileChunkStatus.IoError, []);
            }
        }

        try
        {
            await session.SendAsync(ChannelId.File, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("File response to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    /// <summary>A bundle request, acknowledgement or cancellation (see <see cref="BundleCodec"/>).</summary>
    public void HandleBundleMessage(PeerSession session, ReadOnlySpan<byte> message)
    {
        if (!BundleCodec.TryReadId(message, out var id))
        {
            return;
        }

        switch (message[0])
        {
            case BundleCodec.RequestMessage when BundleCodec.TryDecodeRequest(message, out _, out var offerId, out var first, out var end, out var window, out var flags):
                if (!_store.TryGetOffer(offerId, out var entries, out var paths))
                {
                    _ = SendQuietlyAsync(session, BundleCodec.EncodeStatus(BundleCodec.StartMessage, id, FileChunkStatus.NotFound));
                    return;
                }

                var sender = new BundleSender(session, id, entries, paths, first, end, window, (flags & BundleCodec.FlagBrotli) != 0, _logger);
                if (_bundles.TryAdd((session, id), sender))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await sender.RunAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            _bundles.TryRemove((session, id), out _);
                        }
                    });
                }

                return;

            case BundleCodec.AckMessage when _bundles.TryGetValue((session, id), out var acked):
                acked.OnAck(BundleCodec.ReadAck(message));
                return;

            case BundleCodec.CancelMessage when _bundles.TryGetValue((session, id), out var cancelled):
                cancelled.Cancel();
                return;
        }
    }

    /// <summary>Stops whatever is still being sent over a session that has closed.</summary>
    public void OnSessionClosed(PeerSession session)
    {
        foreach (var key in _bundles.Keys.Where(k => ReferenceEquals(k.Session, session)).ToList())
        {
            if (_bundles.TryRemove(key, out var sender))
            {
                sender.Cancel();
            }
        }
    }

    private async Task SendQuietlyAsync(PeerSession session, byte[] message)
    {
        try
        {
            await session.SendAsync(ChannelId.File, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("File message to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    private SafeFileHandle Acquire(string path)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var stale in _open.Where(p => now - p.Value.LastUsed > HandleIdle.TotalMilliseconds).Select(p => p.Key).ToList())
            {
                _open[stale].Handle.Dispose();
                _open.Remove(stale);
            }

            if (_open.TryGetValue(path, out var existing))
            {
                existing.LastUsed = now;
                return existing.Handle;
            }

            if (_open.Count >= MaxOpenHandles)
            {
                var oldest = _open.MinBy(p => p.Value.LastUsed).Key;
                _open[oldest].Handle.Dispose();
                _open.Remove(oldest);
            }

            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            _open[path] = new OpenFile(handle);
            return handle;
        }
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            if (_open.Remove(path, out var file))
            {
                file.Handle.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var sender in _bundles.Values)
        {
            sender.Cancel();
        }

        lock (_gate)
        {
            foreach (var file in _open.Values)
            {
                file.Handle.Dispose();
            }

            _open.Clear();
        }
    }
}
