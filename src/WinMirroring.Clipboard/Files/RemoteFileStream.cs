using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Read-only, seekable view of a remote file. Keeps a few chunk requests in flight ahead of the reader, which
/// hides the round trip; a seek simply restarts the pipeline at the new position.
/// </summary>
internal sealed class RemoteFileStream : Stream
{
    internal const int ChunkSize = 128 * 1024;
    private const int Window = 3;

    private readonly IRemoteFileSource _source;
    private readonly long _offerId;
    private readonly int _index;
    private readonly long _length;
    private readonly Queue<(long Offset, int Length, Task<FileChunk> Task)> _inflight = new();

    private long _position;
    private long _nextRequestOffset;
    private byte[]? _current;
    private long _currentOffset;

    public RemoteFileStream(IRemoteFileSource source, long offerId, int index, long length)
    {
        _source = source;
        _offerId = offerId;
        _index = index;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (_current is null || _position < _currentOffset || _position >= _currentOffset + _current.Length)
        {
            LoadChunkAt(_position);
        }

        var start = (int)(_position - _currentOffset);
        var available = _current!.Length - start;
        var count = Math.Min(available, buffer.Length);
        _current.AsSpan(start, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    private void LoadChunkAt(long position)
    {
        if (_inflight.Count == 0 || _inflight.Peek().Offset != position)
        {
            // Not the next chunk in line: a seek or a short read. Start over from here.
            _inflight.Clear();
            _nextRequestOffset = position;
        }

        Fill();
        var (offset, requested, task) = _inflight.Dequeue();
        FileChunk chunk;
        try
        {
            chunk = task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException("Reading the remote file failed.", ex);
        }

        if (chunk.Status != FileChunkStatus.Ok)
        {
            throw new IOException(chunk.Status == FileChunkStatus.NotFound
                ? "The other device no longer offers this file (its clipboard changed)."
                : "The other device could not read this file.");
        }

        if (chunk.Data.Length == 0)
        {
            throw new IOException("The remote file is shorter than announced.");
        }

        _current = chunk.Data;
        _currentOffset = offset;
        if (chunk.Data.Length < requested)
        {
            // Short chunk before the announced end (the file changed meanwhile): the requests queued behind it
            // start at the wrong offset, so drop them and continue right after what actually arrived.
            _inflight.Clear();
            _nextRequestOffset = offset + chunk.Data.Length;
        }

        Fill();
    }

    private void Fill()
    {
        while (_inflight.Count < Window && _nextRequestOffset < _length)
        {
            var length = (int)Math.Min(ChunkSize, _length - _nextRequestOffset);
            _inflight.Enqueue((_nextRequestOffset, length, _source.ReadAsync(_offerId, _index, _nextRequestOffset, length, CancellationToken.None)));
            _nextRequestOffset += length;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        _position = Math.Clamp(target, 0, _length);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
