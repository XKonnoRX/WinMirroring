using System.Collections.Concurrent;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard.Files;

public readonly record struct FileChunk(FileChunkStatus Status, byte[] Data);

/// <summary>Reads byte ranges of files another device offered.</summary>
public interface IRemoteFileSource
{
    Task<FileChunk> ReadAsync(long offerId, int index, long offset, int length, CancellationToken ct);
}

/// <summary>Request/response over one session's File channel.</summary>
internal sealed class FileFetcher : IRemoteFileSource
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly PeerSession _session;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FileChunk>> _pending = new();
    private int _nextId;
    private volatile bool _closed;

    public FileFetcher(PeerSession session)
    {
        _session = session;
    }

    public async Task<FileChunk> ReadAsync(long offerId, int index, long offset, int length, CancellationToken ct)
    {
        if (_closed)
        {
            throw new IOException($"The connection to {_session.PeerName} is closed.");
        }

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var waiter = new TaskCompletionSource<FileChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await _session.SendAsync(ChannelId.File, FileCodec.EncodeRequest(id, offerId, index, offset, length), ct).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(ResponseTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException($"{_session.PeerName} did not answer a file request in time.");
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"The connection to {_session.PeerName} is closed.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void OnResponse(ReadOnlySpan<byte> message)
    {
        if (FileCodec.TryDecodeResponse(message, out var id, out var status) && _pending.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(new FileChunk(status, message[FileCodec.ResponseHeaderSize..].ToArray()));
        }
    }

    public void Close()
    {
        _closed = true;
        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                waiter.TrySetException(new IOException($"The connection to {_session.PeerName} closed."));
            }
        }
    }
}
