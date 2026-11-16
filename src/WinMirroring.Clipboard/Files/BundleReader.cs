using System.IO.Compression;
using System.Threading.Channels;
using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Files;

/// <summary>One record of a bundle; <see cref="Data"/> is only valid until the next record is read.</summary>
public readonly record struct BundleRecord(BundleRecordKind Kind, int Index, ReadOnlyMemory<byte> Data)
{
    public FileChunkStatus Status => Kind == BundleRecordKind.Error && !Data.IsEmpty ? (FileChunkStatus)Data.Span[0] : FileChunkStatus.IoError;
}

/// <summary>Files of an offer arriving in order, as a stream of records (see <see cref="BundleCodec"/>).</summary>
public interface IBundleReader : IAsyncDisposable
{
    /// <summary>The next record, or null once the bundle is complete. Throws <see cref="IOException"/> if it broke off.</summary>
    ValueTask<BundleRecord?> ReadAsync(CancellationToken ct);

    /// <summary>Bytes that came over the network so far, and what they were before compression.</summary>
    long WireBytes { get; }

    long RawBytes { get; }
}

/// <summary>
/// The receiving end of one bundle: blocks are queued as they arrive (the session's reader is never held up), decoded
/// as they are read, and the sender is told how far reading has got so it may send on. What is not read is not
/// acknowledged, so a slow reader slows the sender instead of filling memory here.
/// </summary>
internal sealed class BundleReader : IBundleReader
{
    private readonly FileFetcher _owner;
    private readonly uint _id;
    private readonly int _window;
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<FileChunkStatus> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] _raw = [];
    private int _rawLength;
    private int _position;
    private long _wire;
    private long _rawTotal;
    private long _acknowledged;
    private bool _ended;
    private int _disposed;

    public BundleReader(FileFetcher owner, uint id, int window)
    {
        _owner = owner;
        _id = id;
        _window = window;
    }

    public uint Id => _id;

    public long WireBytes => Volatile.Read(ref _wire);

    public long RawBytes => Volatile.Read(ref _rawTotal);

    /// <summary>A message for this bundle, as it came off the session (already copied).</summary>
    public void OnMessage(byte[] message)
    {
        if (message[0] == BundleCodec.StartMessage)
        {
            _start.TrySetResult(BundleCodec.ReadStatus(message));
            return;
        }

        // Blocks and the end go through one queue, so the end can never overtake the last block.
        _frames.Writer.TryWrite(message);
    }

    /// <summary>How the sender answered the request, or null when it did not answer in time (an older device).</summary>
    public async Task<FileChunkStatus?> WaitStartAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _start.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void Fail(Exception error)
    {
        _start.TrySetException(error);
        _frames.Writer.TryComplete(error);
    }

    public async ValueTask<BundleRecord?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_position < _rawLength)
            {
                if (!BundleCodec.TryReadRecordHeader(_raw.AsSpan(_position, _rawLength - _position), out var kind, out var index, out var length))
                {
                    throw new IOException("A damaged block arrived from the other device.");
                }

                var data = _raw.AsMemory(_position + BundleCodec.RecordHeaderSize, length);
                _position += BundleCodec.RecordHeaderSize + length;
                return new BundleRecord(kind, index, data);
            }

            if (_ended)
            {
                return null;
            }

            byte[] frame;
            try
            {
                frame = await _frames.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw ex.InnerException as IOException ?? new IOException("The connection closed during the transfer.", ex);
            }

            if (frame[0] == BundleCodec.EndMessage)
            {
                _ended = true;
                var status = BundleCodec.ReadStatus(frame);
                if (status != FileChunkStatus.Ok)
                {
                    throw new IOException(status == FileChunkStatus.NotFound
                        ? "The other device no longer offers these files."
                        : "The other device stopped sending the files.");
                }

                continue;
            }

            Decode(frame);
            Acknowledge(frame.Length);
        }
    }

    private void Decode(byte[] frame)
    {
        if (!BundleCodec.TryDecodeBlockHeader(frame, out var codec, out var rawLength))
        {
            throw new IOException("A damaged block arrived from the other device.");
        }

        if (_raw.Length < rawLength)
        {
            _raw = new byte[Math.Max(rawLength, BundleCodec.BlockSize + BundleCodec.RecordHeaderSize)];
        }

        var payload = frame.AsSpan(BundleCodec.BlockHeaderSize);
        if (codec == BundleCodecKind.Raw)
        {
            if (payload.Length != rawLength)
            {
                throw new IOException("A damaged block arrived from the other device.");
            }

            payload.CopyTo(_raw);
        }
        else if (!BrotliDecoder.TryDecompress(payload, _raw.AsSpan(0, rawLength), out var written) || written != rawLength)
        {
            throw new IOException("A compressed block from the other device could not be unpacked.");
        }

        _rawLength = rawLength;
        _position = 0;
        Interlocked.Add(ref _rawTotal, rawLength);
    }

    /// <summary>Lets the sender go on once a quarter of the window has been taken, not after every block.</summary>
    private void Acknowledge(int frameLength)
    {
        var wire = Interlocked.Add(ref _wire, frameLength);
        if (wire - _acknowledged >= _window / 4)
        {
            _acknowledged = wire;
            _owner.SendQuietly(BundleCodec.EncodeAck(_id, wire));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (!_ended)
            {
                _owner.SendQuietly(BundleCodec.EncodeCancel(_id));
            }

            _owner.Forget(_id);
            _frames.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}
