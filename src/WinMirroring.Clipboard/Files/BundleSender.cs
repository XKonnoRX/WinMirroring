using System.Buffers;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Sends a range of an offer's files as one bundle (see <see cref="BundleCodec"/>). Three stages run at once, so
/// that neither the disk, the processor nor the network waits for the others:
/// <list type="number">
/// <item>reading — small files several at a time (opening a file costs more than reading it, and the antivirus
/// looks at every one), large ones streamed in order;</item>
/// <item>packing and compressing — whole records into blocks, each block compressed on a core of its own, and sent
/// as it is unless compressing really made it smaller;</item>
/// <item>sending — in order, never more than the receiver's window ahead of what it has taken.</item>
/// </list>
/// </summary>
internal sealed class BundleSender
{
    /// <summary>A file at most this big is read whole, in parallel with others.</summary>
    private const int SmallFile = 1024 * 1024;

    /// <summary>How many small files may be read at once, and how much of them may wait in memory.</summary>
    private const int ParallelReads = 8;
    private const long ReadAheadBytes = 48L * 1024 * 1024;

    private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(10);

    /// <summary>Compressing must save at least this share for the compressed form to be sent.</summary>
    private const double WorthCompressing = 0.92;

    private static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz4", ".cab", ".msi", ".nupkg", ".jar", ".apk",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".jxl",
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".wma",
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".ts",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".pdf", ".epub",
        ".pack", ".woff", ".woff2", ".br",
    };

    private readonly PeerSession _session;
    private readonly uint _id;
    private readonly IReadOnlyList<FileOfferEntry> _entries;
    private readonly IReadOnlyList<string?> _paths;
    private readonly int _first;
    private readonly int _end;
    private readonly long _window;
    private readonly bool _compress;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _credit = new(0);

    private long _sent;
    private long _acknowledged;

    /// <summary>Blocks in a row that compressing did not shrink: after a few, it is only tried now and then.</summary>
    private int _incompressibleRun;

    public BundleSender(
        PeerSession session,
        uint id,
        IReadOnlyList<FileOfferEntry> entries,
        IReadOnlyList<string?> paths,
        int first,
        int end,
        int window,
        bool compress,
        ILogger logger)
    {
        _session = session;
        _id = id;
        _entries = entries;
        _paths = paths;
        _first = Math.Clamp(first, 0, entries.Count);
        _end = Math.Clamp(end, _first, entries.Count);
        _window = Math.Clamp(window, 2 * (BundleCodec.BlockSize + 64), 256L * 1024 * 1024);
        _compress = compress;
        _logger = logger;
    }

    public void OnAck(long consumed)
    {
        if (consumed > Volatile.Read(ref _acknowledged))
        {
            Volatile.Write(ref _acknowledged, consumed);
            _credit.Release();
        }
    }

    public void Cancel() => _cts.Cancel();

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var started = Environment.TickCount64;
        long raw = 0;
        var status = FileChunkStatus.Ok;
        var blocks = Channel.CreateBounded<Task<Encoded>>(new BoundedChannelOptions(Math.Max(4, Environment.ProcessorCount * 2))
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var files = Channel.CreateBounded<Pending>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
        using var readAhead = new SemaphoreSlim(ParallelReads);
        var inMemory = new MemoryBudget(ReadAheadBytes);

        try
        {
            await SendAsync(BundleCodec.EncodeStatus(BundleCodec.StartMessage, _id, FileChunkStatus.Ok), ct).ConfigureAwait(false);

            var reading = Task.Run(() => StartReadsAsync(files.Writer, readAhead, inMemory, ct), ct);
            var packing = Task.Run(() => PackAsync(files.Reader, blocks.Writer, inMemory, ct), ct);

            await foreach (var next in blocks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var block = await next.ConfigureAwait(false);
                raw += block.RawLength;
                while (_sent - Volatile.Read(ref _acknowledged) + block.Frame.Length > _window)
                {
                    // A receiver may stop taking for a long while without anything being wrong — Explorer asking
                    // whether to replace a file waits for a person — so only a really long silence ends the bundle.
                    if (!await _credit.WaitAsync(StalledAfter, ct).ConfigureAwait(false))
                    {
                        throw new TimeoutException("The receiver stopped taking the bundle.");
                    }
                }

                await SendAsync(block.Frame, ct).ConfigureAwait(false);
                _sent += block.Frame.Length;
            }

            await Task.WhenAll(reading, packing).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = FileChunkStatus.NotFound;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or TimeoutException or ChannelClosedException)
        {
            _logger.LogInformation("Bundle {Id} to {Peer} stopped: {Message}", _id, _session.PeerName, ex.Message);
            status = FileChunkStatus.IoError;
        }
        finally
        {
            _cts.Cancel();
        }

        if (status != FileChunkStatus.NotFound)
        {
            try
            {
                await _session.SendAsync(ChannelId.File, BundleCodec.EncodeStatus(BundleCodec.EndMessage, _id, status)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        var seconds = Math.Max(0.001, (Environment.TickCount64 - started) / 1000.0);
        _logger.LogInformation(
            "Bundle {Id} to {Peer}: files {First}..{End}, {Raw:N0} bytes as {Sent:N0} in {Seconds:F1} s ({Speed:F1} MB/s), {Status}",
            _id, _session.PeerName, _first, _end, raw, _sent, seconds, raw / seconds / 1_000_000, status);
    }

    private ValueTask SendAsync(byte[] message, CancellationToken ct) => _session.SendAsync(ChannelId.File, message, ct);

    // ---- stage 1: reading -------------------------------------------------------------------------------

    /// <summary>A file in the order it is sent: read already (small), or to be streamed (large), or a folder to skip.</summary>
    private sealed record Pending(int Index, string? Path, long Size, Task<byte[]?>? Whole, bool Incompressible);

    private async Task StartReadsAsync(ChannelWriter<Pending> files, SemaphoreSlim readAhead, MemoryBudget inMemory, CancellationToken ct)
    {
        try
        {
            for (var index = _first; index < _end; index++)
            {
                var entry = _entries[index];
                var path = index < _paths.Count ? _paths[index] : null;
                if (entry.IsDirectory || path is null)
                {
                    continue;
                }

                var incompressible = Compressed.Contains(Path.GetExtension(path));
                Task<byte[]?>? whole = null;
                if (entry.Size <= SmallFile)
                {
                    await readAhead.WaitAsync(ct).ConfigureAwait(false);
                    await inMemory.TakeAsync(entry.Size, ct).ConfigureAwait(false);
                    var p = path;
                    whole = Task.Run(() =>
                    {
                        try
                        {
                            return ReadWhole(p);
                        }
                        finally
                        {
                            readAhead.Release();
                        }
                    }, ct);
                }

                await files.WriteAsync(new Pending(index, path, entry.Size, whole, incompressible), ct).ConfigureAwait(false);
            }

            files.TryComplete();
        }
        catch (Exception ex)
        {
            files.TryComplete(ex);
        }
    }

    /// <summary>The whole file, or null when it cannot be read.</summary>
    private static byte[]? ReadWhole(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- stage 2: packing ------------------------------------------------------------------------------

    private async Task PackAsync(ChannelReader<Pending> files, ChannelWriter<Task<Encoded>> blocks, MemoryBudget inMemory, CancellationToken ct)
    {
        var block = new BlockBuilder(this, blocks);
        try
        {
            await foreach (var file in files.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (file.Whole is { } pending)
                {
                    var bytes = await pending.ConfigureAwait(false);
                    try
                    {
                        if (bytes is null)
                        {
                            await block.AddErrorAsync(file.Index, FileChunkStatus.IoError, ct).ConfigureAwait(false);
                            continue;
                        }

                        // Never more than was announced: Explorer has been told the size already.
                        var length = (int)Math.Min(bytes.Length, file.Size);
                        await block.AddDataAsync(file.Index, bytes.AsMemory(0, length), file.Incompressible, ct).ConfigureAwait(false);
                        await block.AddEndAsync(file.Index, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        inMemory.Give(file.Size);
                    }

                    continue;
                }

                await StreamLargeAsync(block, file, ct).ConfigureAwait(false);
            }

            await block.FlushAsync(ct).ConfigureAwait(false);
            blocks.TryComplete();
        }
        catch (Exception ex)
        {
            blocks.TryComplete(ex);
        }
    }

    private static async Task StreamLargeAsync(BlockBuilder block, Pending file, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(file.Path!, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await block.AddErrorAsync(file.Index, FileChunkStatus.IoError, ct).ConfigureAwait(false);
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BundleCodec.BlockSize);
            try
            {
                var left = file.Size;
                while (left > 0)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        await block.AddErrorAsync(file.Index, FileChunkStatus.IoError, ct).ConfigureAwait(false);
                        return;
                    }

                    if (read == 0)
                    {
                        break; // shorter than announced: the receiver notices at the end record
                    }

                    await block.AddDataAsync(file.Index, buffer.AsMemory(0, read), file.Incompressible, ct).ConfigureAwait(false);
                    left -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        await block.AddEndAsync(file.Index, ct).ConfigureAwait(false);
    }

    /// <summary>Gathers records into blocks of <see cref="BundleCodec.BlockSize"/> and hands each one off to be encoded.</summary>
    private sealed class BlockBuilder(BundleSender owner, ChannelWriter<Task<Encoded>> blocks)
    {
        private byte[] _buffer = new byte[BundleCodec.BlockSize];
        private int _length;
        private long _incompressible;

        public async ValueTask AddDataAsync(int index, ReadOnlyMemory<byte> data, bool incompressible, CancellationToken ct)
        {
            while (!data.IsEmpty)
            {
                if (_buffer.Length - _length < BundleCodec.RecordHeaderSize + 1)
                {
                    await FlushAsync(ct).ConfigureAwait(false);
                }

                var room = _buffer.Length - _length - BundleCodec.RecordHeaderSize;
                var part = data[..Math.Min(room, data.Length)];
                BundleCodec.WriteRecordHeader(_buffer.AsSpan(_length), BundleRecordKind.Data, index, part.Length);
                part.Span.CopyTo(_buffer.AsSpan(_length + BundleCodec.RecordHeaderSize));
                _length += BundleCodec.RecordHeaderSize + part.Length;
                if (incompressible)
                {
                    _incompressible += part.Length;
                }

                data = data[part.Length..];
            }
        }

        public ValueTask AddEndAsync(int index, CancellationToken ct) => AddEmptyAsync(BundleRecordKind.End, index, [], ct);

        public ValueTask AddErrorAsync(int index, FileChunkStatus status, CancellationToken ct) =>
            AddEmptyAsync(BundleRecordKind.Error, index, [(byte)status], ct);

        private async ValueTask AddEmptyAsync(BundleRecordKind kind, int index, byte[] copy, CancellationToken ct)
        {
            if (_buffer.Length - _length < BundleCodec.RecordHeaderSize + copy.Length)
            {
                await FlushAsync(ct).ConfigureAwait(false);
            }

            BundleCodec.WriteRecordHeader(_buffer.AsSpan(_length), kind, index, copy.Length);
            copy.CopyTo(_buffer.AsSpan(_length + BundleCodec.RecordHeaderSize));
            _length += BundleCodec.RecordHeaderSize + copy.Length;
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_length == 0)
            {
                return;
            }

            var raw = _buffer;
            var length = _length;
            var mostlyCompressed = _incompressible * 2 > length;
            _buffer = new byte[BundleCodec.BlockSize];
            _length = 0;
            _incompressible = 0;
            await blocks.WriteAsync(Task.Run(() => owner.Encode(raw, length, mostlyCompressed), ct), ct).ConfigureAwait(false);
        }
    }

    // ---- encoding ------------------------------------------------------------------------------------------

    private sealed record Encoded(byte[] Frame, int RawLength);

    private Encoded Encode(byte[] raw, int length, bool mostlyCompressed)
    {
        // Already compressed data is not tried at all; and after a run of blocks that would not shrink, only every
        // eighth is, so a folder of videos costs no processor time and a folder of sources still gets compressed.
        var run = Volatile.Read(ref _incompressibleRun);
        var attempt = _compress && !mostlyCompressed && (run < 4 || run % 8 == 0);
        if (attempt)
        {
            var target = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(length));
            try
            {
                if (BrotliEncoder.TryCompress(raw.AsSpan(0, length), target, out var written, quality: 1, window: 22)
                    && written < length * WorthCompressing)
                {
                    Volatile.Write(ref _incompressibleRun, 0);
                    return new Encoded(BundleCodec.EncodeBlock(_id, BundleCodecKind.Brotli, length, target.AsSpan(0, written)), length);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(target);
            }
        }

        if (_compress && !mostlyCompressed)
        {
            Interlocked.Increment(ref _incompressibleRun);
        }

        return new Encoded(BundleCodec.EncodeBlock(_id, BundleCodecKind.Raw, length, raw.AsSpan(0, length)), length);
    }

    /// <summary>Bytes of small files read ahead and not yet packed: reading waits while too many are in memory.</summary>
    private sealed class MemoryBudget(long limit)
    {
        private readonly object _gate = new();
        private long _used;
        private TaskCompletionSource _freed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task TakeAsync(long bytes, CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    // One file larger than the whole budget still goes, alone.
                    if (_used == 0 || _used + bytes <= limit)
                    {
                        _used += bytes;
                        return;
                    }

                    wait = _freed.Task;
                }

                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public void Give(long bytes)
        {
            TaskCompletionSource freed;
            lock (_gate)
            {
                _used -= bytes;
                freed = _freed;
                _freed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            freed.TrySetResult();
        }
    }
}
