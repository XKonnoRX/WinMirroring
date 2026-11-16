using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Makes Explorer's paste of remote files fast. Explorer pulls a pasted set of virtual files one at a time — open
/// the next file's stream, read it to the end, close it — and each stream on its own used to ask the other device
/// for its first chunk and wait, so a folder of thousands of small files spent nearly all its time waiting for
/// round trips. Here, the first file Explorer asks for starts a single bundle of that file and everything after it;
/// the bundle runs ahead of Explorer (as far as <see cref="Budget"/> allows), and each stream Explorer opens finds
/// its file arriving or already there.
///
/// Whatever does not fit that pattern — a file asked for twice, a seek, a file already passed, a bundle that broke
/// off — simply gets the old chunk-by-chunk stream, so nothing depends on Explorer behaving exactly this way.
/// </summary>
internal sealed class OfferPrefetcher : IDisposable
{
    /// <summary>How much may be received and not yet taken by Explorer.</summary>
    private const long Budget = 96L * 1024 * 1024;

    /// <summary>Skipping ahead further than this (Explorer passed files over) starts a new bundle rather than reading through.</summary>
    private const long RestartAfterSkipping = 32L * 1024 * 1024;

    private readonly FileOfferManifest _manifest;
    private readonly IRemoteFileSource _source;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, FileBuffer> _buffers = [];

    private Pump? _pump;
    private long _buffered;

    /// <summary>The furthest file Explorer has opened: a file before it that nobody holds will not be read.</summary>
    private int _floor = -1;
    private bool _disposed;

    public OfferPrefetcher(FileOfferManifest manifest, IRemoteFileSource source, ILogger logger)
    {
        _manifest = manifest;
        _source = source;
        _logger = logger;
    }

    /// <summary>Bytes received of everything from where the transfer started, and that whole size; then whether it went well.</summary>
    public event Action<long, long>? Progress;

    public event Action<bool>? Finished;

    /// <summary>A stream for file <paramref name="index"/>, or null when it is better read chunk by chunk.</summary>
    public Stream? Open(int index)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            // Explorer moved on: what it left behind before this file will not be asked for any more.
            _floor = Math.Max(_floor, index);
            foreach (var passed in _buffers.Keys.Where(k => k < index && !_buffers[k].Opened).ToList())
            {
                Drop(passed);
            }

            if (_buffers.TryGetValue(index, out var ready))
            {
                if (ready.Opened)
                {
                    return null; // asked for twice: the second one reads on its own
                }

                ready.Opened = true;
                return new PrefetchStream(this, ready);
            }

            var pump = _pump;
            var running = pump is { Done: false };
            if (running && index < pump!.Next)
            {
                return null; // already gone past it
            }

            if (!running || SizeBetween(pump!.Next, index) > RestartAfterSkipping)
            {
                pump?.Stop();
                foreach (var stale in _buffers.Keys.ToList())
                {
                    if (!_buffers[stale].Opened)
                    {
                        Drop(stale);
                    }
                }

                _pump = pump = new Pump(this, index);
                _floor = index;
                pump.Start();
            }

            var buffer = BufferOf(index);
            buffer.Pump = pump;
            buffer.Opened = true;
            return new PrefetchStream(this, buffer);
        }
    }

    private long SizeBetween(int from, int to)
    {
        long size = 0;
        for (var i = Math.Max(0, from); i < to && i < _manifest.Entries.Count; i++)
        {
            size += _manifest.Entries[i].Size;
        }

        return size;
    }

    /// <summary>Called under the lock.</summary>
    private FileBuffer BufferOf(int index)
    {
        if (!_buffers.TryGetValue(index, out var buffer))
        {
            buffer = new FileBuffer(index, _manifest.Entries[index].Size);
            _buffers[index] = buffer;
        }

        return buffer;
    }

    /// <summary>Called under the lock.</summary>
    private void Drop(int index)
    {
        if (_buffers.Remove(index, out var buffer))
        {
            _buffered -= buffer.Queued;
            buffer.Abandon();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>A reader took <paramref name="bytes"/>; the pump may go on.</summary>
    private void Taken(FileBuffer buffer, long bytes, bool complete)
    {
        lock (_gate)
        {
            _buffered -= bytes;
            if (complete && _buffers.TryGetValue(buffer.Index, out var same) && ReferenceEquals(same, buffer))
            {
                _buffers.Remove(buffer.Index);
            }

            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pump?.Stop();
            foreach (var index in _buffers.Keys.ToList())
            {
                Drop(index);
            }
        }
    }

    // ---- the bundle running ahead -----------------------------------------------------------------------

    private sealed class Pump(OfferPrefetcher owner, int start)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly int _first = start;

        /// <summary>The first file not yet received in full.</summary>
        public int Next { get; private set; } = start;

        public bool Done { get; private set; }

        public void Start() => _ = Task.Run(RunAsync);

        public void Stop() => _cts.Cancel();

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            var total = owner.SizeBetween(_first, owner._manifest.Entries.Count);
            long received = 0;
            var lastReport = 0L;
            var ok = false;
            var announced = false;
            try
            {
                var bundle = await owner._source.OpenBundleAsync(owner._manifest.OfferId, _first, owner._manifest.Entries.Count, ct).ConfigureAwait(false);
                if (bundle is null)
                {
                    owner._logger.LogDebug("The source of {What} does not send bundles; Explorer reads it file by file", owner._manifest.SourceName);
                    return;
                }

                announced = true;
                owner.Progress?.Invoke(0, total);
                await using (bundle.ConfigureAwait(false))
                {
                    while (await bundle.ReadAsync(ct).ConfigureAwait(false) is { } record)
                    {
                        if (record.Index < 0 || record.Index >= owner._manifest.Entries.Count)
                        {
                            throw new IOException("The other device sent a file that is not in the offer.");
                        }

                        if (record.Kind == BundleRecordKind.Data)
                        {
                            received += record.Data.Length;
                        }

                        Deliver(record, ct);
                        if (Environment.TickCount64 - lastReport >= 100)
                        {
                            lastReport = Environment.TickCount64;
                            owner.Progress?.Invoke(received, total);
                        }
                    }
                }

                ok = true;
                owner.Progress?.Invoke(total, total);
            }
            catch (OperationCanceledException)
            {
                // stopped on purpose: a newer pump took over, or the clipboard moved on
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                owner._logger.LogInformation("Prefetching files from {Source} stopped: {Message}", owner._manifest.SourceName, ex.Message);
            }
            finally
            {
                lock (owner._gate)
                {
                    Done = true;

                    // Whatever is still waiting for data from this pump will not get it: those readers go on chunk
                    // by chunk from where they are.
                    foreach (var buffer in owner._buffers.Values.Where(b => !b.Complete && b.Pump == this).ToList())
                    {
                        buffer.Interrupt();
                    }

                    Monitor.PulseAll(owner._gate);
                }

                if (announced && !ct.IsCancellationRequested)
                {
                    owner.Finished?.Invoke(ok);
                }
            }
        }

        private void Deliver(BundleRecord record, CancellationToken ct)
        {
            lock (owner._gate)
            {
                // Hold back while too much waits for Explorer; a reader taking data, or dropping a file, wakes this.
                while (record.Kind == BundleRecordKind.Data && owner._buffered > 0 && owner._buffered + record.Data.Length > Budget)
                {
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(owner._gate, 200);
                }

                ct.ThrowIfCancellationRequested();
                if (record.Kind is BundleRecordKind.End or BundleRecordKind.Error)
                {
                    Next = record.Index + 1;
                }

                // A file Explorer has gone past without opening: nobody will read it, so it is not kept.
                if (!owner._buffers.ContainsKey(record.Index) && record.Index < owner._floor)
                {
                    return;
                }

                var buffer = owner.BufferOf(record.Index);
                buffer.Pump ??= this;
                switch (record.Kind)
                {
                    case BundleRecordKind.Data:
                        buffer.Add(record.Data.ToArray());
                        owner._buffered += record.Data.Length;
                        break;

                    case BundleRecordKind.End:
                        buffer.End();
                        break;

                    case BundleRecordKind.Error:
                        buffer.Fail(record.Status);
                        break;
                }

                Monitor.PulseAll(owner._gate);
            }
        }
    }

    // ---- one file on its way --------------------------------------------------------------------------------

    private sealed class FileBuffer(int index, long size)
    {
        private readonly Queue<byte[]> _chunks = new();

        public int Index { get; } = index;

        public long Size { get; } = size;

        public Pump? Pump { get; set; }

        public bool Opened { get; set; }

        /// <summary>Received bytes not yet read.</summary>
        public long Queued { get; private set; }

        public bool Complete { get; private set; }

        public FileChunkStatus? Error { get; private set; }

        /// <summary>The pump stopped before this file was complete.</summary>
        public bool Interrupted { get; private set; }

        public bool Abandoned { get; private set; }

        public int Count => _chunks.Count;

        public void Add(byte[] chunk)
        {
            if (!Abandoned)
            {
                _chunks.Enqueue(chunk);
                Queued += chunk.Length;
            }
        }

        public byte[] Take()
        {
            var chunk = _chunks.Dequeue();
            Queued -= chunk.Length;
            return chunk;
        }

        public void End() => Complete = true;

        public void Fail(FileChunkStatus status)
        {
            Error = status;
            Complete = true;
        }

        public void Interrupt() => Interrupted = true;

        public void Abandon()
        {
            Abandoned = true;
            _chunks.Clear();
            Queued = 0;
        }
    }

    /// <summary>
    /// What Explorer reads: the file's chunks as they arrive. A seek elsewhere, or a pump that stopped halfway, turns
    /// it into the ordinary remote stream from the current position on.
    /// </summary>
    private sealed class PrefetchStream(OfferPrefetcher owner, FileBuffer buffer) : Stream
    {
        private byte[]? _current;
        private int _offset;
        private long _position;
        private RemoteFileStream? _fallback;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => buffer.Size;

        public override long Position
        {
            get => _fallback?.Position ?? _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] target, int offset, int count) => Read(target.AsSpan(offset, count));

        public override int Read(Span<byte> target)
        {
            if (_fallback is not null)
            {
                return _fallback.Read(target);
            }

            if (target.IsEmpty || _position >= buffer.Size)
            {
                return 0;
            }

            while (_current is null || _offset >= _current.Length)
            {
                _current = null;
                lock (owner._gate)
                {
                    while (buffer.Count == 0 && !buffer.Complete && !buffer.Interrupted && !buffer.Abandoned)
                    {
                        Monitor.Wait(owner._gate, 500);
                    }

                    if (buffer.Count > 0)
                    {
                        _current = buffer.Take();
                        _offset = 0;
                        owner._buffered -= _current.Length;
                        Monitor.PulseAll(owner._gate);
                        continue;
                    }
                }

                if (buffer.Error is { } error)
                {
                    throw new IOException(error == FileChunkStatus.NotFound
                        ? "The other device no longer offers this file (its clipboard changed)."
                        : "The other device could not read this file.");
                }

                if (buffer.Complete)
                {
                    if (_position < buffer.Size)
                    {
                        throw new IOException("The remote file is shorter than announced.");
                    }

                    return 0;
                }

                // Interrupted or dropped: carry on the old way from here.
                SwitchToFallback(_position);
                return _fallback!.Read(target);
            }

            var count = Math.Min(target.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(target);
            _offset += count;
            _position += count;
            if (_position >= buffer.Size)
            {
                owner.Taken(buffer, 0, complete: true);
            }

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => buffer.Size + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (_fallback is not null)
            {
                return _fallback.Seek(target, SeekOrigin.Begin);
            }

            if (target == _position)
            {
                return _position;
            }

            SwitchToFallback(target);
            return _fallback!.Position;
        }

        private void SwitchToFallback(long position)
        {
            lock (owner._gate)
            {
                if (owner._buffers.TryGetValue(buffer.Index, out var same) && ReferenceEquals(same, buffer))
                {
                    owner.Drop(buffer.Index);
                }
            }

            _fallback = new RemoteFileStream(owner._source, owner._manifest.OfferId, buffer.Index, buffer.Size);
            _fallback.Seek(position, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] target, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _fallback is null)
            {
                lock (owner._gate)
                {
                    if (owner._buffers.TryGetValue(buffer.Index, out var same) && ReferenceEquals(same, buffer))
                    {
                        owner.Drop(buffer.Index);
                    }
                }
            }

            _fallback?.Dispose();
            base.Dispose(disposing);
        }
    }
}
