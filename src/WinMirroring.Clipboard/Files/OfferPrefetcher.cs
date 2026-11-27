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
///
/// Explorer never says that it has stopped: a paste cancelled half way just stops asking, and a stream it lets go
/// of is not closed here (a COM object is not told when its last reference goes). So the bundle running ahead ends
/// by itself — when its source closes, when nobody has read anything for <see cref="StalledAfter"/> (sooner once
/// the offer is <see cref="Retire">retired</see>), or when this is disposed — and says so through
/// <see cref="Finished"/>, because whatever shows the progress is waiting for exactly that.
/// </summary>
internal sealed class OfferPrefetcher : IDisposable
{
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

    /// <summary>When Explorer last asked for a file or took some of one (tick count): the last sign of a reader.</summary>
    private long _lastReader = Environment.TickCount64;

    /// <summary>Progress has been reported and its end has not: someone is showing an indicator and waits to hide it.</summary>
    private bool _reporting;
    private bool _retired;
    private bool _disposed;

    public OfferPrefetcher(FileOfferManifest manifest, IRemoteFileSource source, ILogger logger)
    {
        _manifest = manifest;
        _source = source;
        _logger = logger;
    }

    /// <summary>How much may be received and not yet taken by Explorer.</summary>
    internal long Budget { get; init; } = 96L * 1024 * 1024;

    /// <summary>
    /// How long the bundle waits, full to its budget, for a reader that takes nothing, before it gives the paste up
    /// as abandoned. Explorer asking whether to replace a file can keep a person thinking for longer than this, and
    /// that is fine: giving up costs such a paste only a new bundle from the file it goes on with.
    /// </summary>
    internal TimeSpan StalledAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The same once the offer is no longer on the clipboard: only a paste already under way can still be reading.</summary>
    internal TimeSpan StalledAfterRetiring { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bytes received of everything from where the transfer started, and that whole size; then whether it went well.</summary>
    public event Action<long, long>? Progress;

    /// <summary>
    /// The transfer that <see cref="Progress"/> told of is over. Raised for every way it can end — complete, broken
    /// off, abandoned by its reader, the source closed, this disposed — except when a new bundle takes over at once
    /// and the same progress simply goes on.
    /// </summary>
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
            _lastReader = Environment.TickCount64;
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

            if (running && index == pump!.Started)
            {
                // Part of it has come already and was not kept (its buffer was let go, or nobody was expected to
                // ask for it): a buffer made now would get the file from the middle on.
                return null;
            }

            if (!running || SizeBetween(pump!.Next, index) > RestartAfterSkipping)
            {
                pump?.Supersede();
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

    /// <summary>
    /// The offer is no longer what a paste would take (something else is on the clipboard now, or the drag that
    /// carried it is over). Whoever is already reading it may go on, and still gets a bundle; but nothing waits long
    /// for a reader any more, because no new one can come.
    /// </summary>
    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            Monitor.PulseAll(_gate);
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

    /// <summary>Called under the lock. A reader that is still there carries on chunk by chunk from where it is.</summary>
    private void DropAll()
    {
        foreach (var index in _buffers.Keys.ToList())
        {
            Drop(index);
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

    /// <summary>
    /// Nothing can be pasted through this any more (the thread its data object lived on is gone). The bundle stops,
    /// what it brought is let go, and the indicator is told.
    /// </summary>
    public void Dispose()
    {
        Pump? pump;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pump = _pump;
            DropAll();
        }

        // Outside the lock: stopping runs the pump's own way out, which reports that the transfer is over.
        pump?.Stop();
    }

    // ---- the bundle running ahead -----------------------------------------------------------------------

    private sealed class Pump(OfferPrefetcher owner, int start)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly int _first = start;

        /// <summary>A newer pump took over: the transfer goes on, so this one's end is not the transfer's.</summary>
        private volatile bool _superseded;

        /// <summary>Gave up because nobody was reading.</summary>
        private bool _abandoned;

        /// <summary>The first file not yet received in full.</summary>
        public int Next { get; private set; } = start;

        /// <summary>The last file some of which has been handed on (kept or not), or -1 before the first.</summary>
        public int Started { get; private set; } = -1;

        public bool Done { get; private set; }

        public void Start() => _ = Task.Run(RunAsync);

        public void Stop() => _cts.Cancel();

        public void Supersede()
        {
            _superseded = true;
            _cts.Cancel();
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            var total = owner.SizeBetween(_first, owner._manifest.Entries.Count);
            long received = 0;
            var lastReport = 0L;
            var ok = false;

            // The file whose data is arriving, and how much of it has: more than its size is not accepted.
            var file = -1;
            var fileBytes = 0L;

            // A pump held back by its budget is not reading, so it would never notice that the connection has gone:
            // it is told.
            var closing = owner._source.Closed.Register(static state => ((CancellationTokenSource)state!).Cancel(), _cts);
            try
            {
                var bundle = await owner._source.OpenBundleAsync(owner._manifest.OfferId, _first, owner._manifest.Entries.Count, ct).ConfigureAwait(false);
                if (bundle is null)
                {
                    owner._logger.LogDebug("The source of {What} does not send bundles; Explorer reads it file by file", owner._manifest.SourceName);
                    return;
                }

                lock (owner._gate)
                {
                    owner._reporting = true;
                }

                owner.Progress?.Invoke(0, total);
                await using (bundle.ConfigureAwait(false))
                {
                    while (await bundle.ReadAsync(ct).ConfigureAwait(false) is { } record)
                    {
                        if (record.Index < 0 || record.Index >= owner._manifest.Entries.Count)
                        {
                            throw new IOException("The other device sent a file that is not in the offer.");
                        }

                        if (record.Index != file)
                        {
                            file = record.Index;
                            fileBytes = 0;
                        }

                        if (record.Kind == BundleRecordKind.Data)
                        {
                            // Explorer was told the size and reads no further; what came beyond it would only sit
                            // here, counted against the budget, with nobody ever taking it.
                            fileBytes += record.Data.Length;
                            if (fileBytes > owner._manifest.Entries[record.Index].Size)
                            {
                                throw new IOException("The other device sent more of a file than it announced.");
                            }

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
                // stopped on purpose: a newer pump took over, the source closed, or the offer is gone for good
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                owner._logger.LogInformation("Prefetching files from {Source} stopped: {Message}", owner._manifest.SourceName, ex.Message);
            }
            finally
            {
                closing.Dispose();
                bool tell;
                lock (owner._gate)
                {
                    Done = true;
                    if (_abandoned)
                    {
                        // Nobody is reading: nothing is kept for them. Should Explorer come back after all (a person
                        // took their time over a question), every stream goes on chunk by chunk from where it is,
                        // and the next file it opens starts a new bundle.
                        owner.DropAll();
                    }
                    else
                    {
                        // Whatever is still waiting for data from this pump will not get it: those readers go on
                        // chunk by chunk from where they are.
                        foreach (var buffer in owner._buffers.Values.Where(b => !b.Complete && b.Pump == this).ToList())
                        {
                            buffer.Interrupt();
                        }
                    }

                    // Told once per transfer, and whichever pump is the one to end it: a pump that took over from
                    // another and broke off before reporting anything of its own still ends what the first one began.
                    tell = !_superseded && owner._reporting;
                    if (tell)
                    {
                        owner._reporting = false;
                    }

                    Monitor.PulseAll(owner._gate);
                }

                if (tell)
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
                var heldSince = Environment.TickCount64;
                while (record.Kind == BundleRecordKind.Data && owner._buffered > 0 && owner._buffered + record.Data.Length > owner.Budget)
                {
                    ct.ThrowIfCancellationRequested();

                    // A reader with nothing to read while this holds back: then what fills the budget is not what
                    // anyone is reading — streams Explorer opened and let go of half way, which never get closed.
                    // Waiting for them would keep that reader (and the thread Explorer calls in on) waiting for
                    // good, so they are let go; if one of them is read after all, it carries on chunk by chunk.
                    if (owner._buffers.Values.Any(b => b.Starved))
                    {
                        foreach (var held in owner._buffers.Values.Where(b => b.Queued > 0).Select(b => b.Index).ToList())
                        {
                            owner.Drop(held);
                        }

                        break;
                    }

                    var allowed = owner._retired ? owner.StalledAfterRetiring : owner.StalledAfter;
                    if (Environment.TickCount64 - Math.Max(heldSince, owner._lastReader) >= allowed.TotalMilliseconds)
                    {
                        _abandoned = true;
                        throw new IOException("Nobody has read the files for a while: the paste was most likely given up half way.");
                    }

                    Monitor.Wait(owner._gate, 200);
                }

                ct.ThrowIfCancellationRequested();
                var startsFile = record.Index != Started;
                Started = record.Index;
                if (record.Kind is BundleRecordKind.End or BundleRecordKind.Error)
                {
                    Next = record.Index + 1;
                }

                if (!owner._buffers.ContainsKey(record.Index))
                {
                    // A file Explorer has gone past without opening: nobody will read it, so it is not kept. Nor is
                    // the rest of a file whose buffer was let go half way: a buffer made anew from here would hold
                    // the file without its beginning, and pass for the whole of it to whoever opened it next.
                    if (record.Index < owner._floor || !startsFile)
                    {
                        return;
                    }
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

        /// <summary>Its reader is inside a read, waiting (it may have been given something since and not have woken yet).</summary>
        public bool Awaited { get; set; }

        /// <summary>
        /// Its reader is waiting and there is still nothing for it. The queue is looked at as well as the flag: the
        /// pump can bring a great deal between waking a reader and that reader getting to run, and must not take a
        /// reader it has just served for one it is starving.
        /// </summary>
        public bool Starved => Awaited && _chunks.Count == 0 && !Complete && !Interrupted;

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
                bool dropped;
                lock (owner._gate)
                {
                    owner._lastReader = Environment.TickCount64;
                    if (buffer.Count == 0 && !buffer.Complete && !buffer.Interrupted && !buffer.Abandoned)
                    {
                        // Said out loud, so a pump holding back for other streams knows this one has run dry.
                        buffer.Awaited = true;
                        Monitor.PulseAll(owner._gate);
                        try
                        {
                            while (buffer.Count == 0 && !buffer.Complete && !buffer.Interrupted && !buffer.Abandoned)
                            {
                                Monitor.Wait(owner._gate, 500);
                            }
                        }
                        finally
                        {
                            buffer.Awaited = false;
                        }
                    }

                    if (buffer.Count > 0)
                    {
                        _current = buffer.Take();
                        _offset = 0;
                        owner._buffered -= _current.Length;
                        owner._lastReader = Environment.TickCount64;
                        Monitor.PulseAll(owner._gate);
                        continue;
                    }

                    dropped = buffer.Abandoned;
                }

                // A buffer that was let go has lost what it held, whatever else it says: that it was complete then
                // does not make the file short now.
                if (!dropped && buffer.Error is { } error)
                {
                    throw new IOException(error == FileChunkStatus.NotFound
                        ? "The other device no longer offers this file (its clipboard changed)."
                        : "The other device could not read this file.");
                }

                if (!dropped && buffer.Complete)
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
