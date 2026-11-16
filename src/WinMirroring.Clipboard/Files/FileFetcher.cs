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

    /// <summary>
    /// Files <paramref name="first"/> to <paramref name="end"/> (exclusive) of an offer, sent one after another in a
    /// single stream. Null when the other device does not send bundles (an older version): read the files chunk by
    /// chunk then.
    /// </summary>
    Task<IBundleReader?> OpenBundleAsync(long offerId, int first, int end, CancellationToken ct) =>
        Task.FromResult<IBundleReader?>(null);
}

/// <summary>Request/response over one session's File channel.</summary>
internal sealed class FileFetcher : IRemoteFileSource
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A device that sends bundles answers a request at once; one that does not, never.</summary>
    private static readonly TimeSpan BundleStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How far ahead of what is read here the other device may send.</summary>
    internal const int BundleWindow = 16 * 1024 * 1024;

    private readonly ConcurrentDictionary<uint, BundleReader> _bundles = new();
    private volatile bool _noBundles;

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

    public async Task<IBundleReader?> OpenBundleAsync(long offerId, int first, int end, CancellationToken ct)
    {
        if (_noBundles || _closed)
        {
            return null;
        }

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var reader = new BundleReader(this, id, BundleWindow);
        _bundles[id] = reader;
        try
        {
            await _session.SendAsync(
                ChannelId.File,
                BundleCodec.EncodeRequest(id, offerId, first, end, BundleWindow, BundleCodec.FlagBrotli),
                ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _bundles.TryRemove(id, out _);
            throw new IOException($"The connection to {_session.PeerName} is closed.", ex);
        }

        switch (await reader.WaitStartAsync(BundleStartTimeout, ct).ConfigureAwait(false))
        {
            case FileChunkStatus.Ok:
                return reader;

            case null:
                // No answer: a version from before bundles, which drops the request without a word. Not asked again
                // over this connection.
                _noBundles = true;
                await reader.DisposeAsync().ConfigureAwait(false);
                return null;

            default:
                await reader.DisposeAsync().ConfigureAwait(false);
                throw new IOException("The other device no longer offers these files (its clipboard changed).");
        }
    }

    public void OnBundleMessage(ReadOnlyMemory<byte> message)
    {
        if (BundleCodec.TryReadId(message.Span, out var id) && _bundles.TryGetValue(id, out var reader))
        {
            reader.OnMessage(message.ToArray());
        }
    }

    internal void Forget(uint id) => _bundles.TryRemove(id, out _);

    /// <summary>Sends a small message on the File channel and does not mind if the connection is gone.</summary>
    internal void SendQuietly(byte[] message)
    {
        if (_closed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _session.SendAsync(ChannelId.File, message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        });
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
        foreach (var reader in _bundles.Values)
        {
            reader.Fail(new IOException($"The connection to {_session.PeerName} closed."));
        }

        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                waiter.TrySetException(new IOException($"The connection to {_session.PeerName} closed."));
            }
        }
    }
}
