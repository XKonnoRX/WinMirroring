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
