using WinMirroring.Clipboard.Files;
using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Tests;

/// <summary>
/// A device whose files are held in memory. It answers chunk requests truthfully and sends bundles the way a test
/// tells it to: by default every file whole and in order, as a real device does; with <see cref="Script"/>, whatever
/// records the test wants a misbehaving device to send.
/// </summary>
internal sealed class ScriptedSource(byte[][] files) : IRemoteFileSource
{
    public const int RecordSize = 16 * 1024;

    private readonly CancellationTokenSource _closing = new();
    private readonly List<ScriptedBundle> _bundles = [];

    /// <summary>The records of a bundle asked for from a given file on, instead of the truthful ones.</summary>
    public Func<int, IEnumerable<BundleRecord>>? Script { get; init; }

    /// <summary>The bundle stops before its record of this number (counted from zero) until <see cref="ScriptedBundle.Release"/>.</summary>
    public int HoldBeforeRecord { get; init; } = -1;

    public CancellationToken Closed => _closing.Token;

    public IReadOnlyList<ScriptedBundle> Bundles
    {
        get
        {
            lock (_bundles)
            {
                return [.. _bundles];
            }
        }
    }

    public static FileOfferManifest ManifestOf(byte[][] files) =>
        new(Random.Shared.NextInt64(1, long.MaxValue), "pc", [.. files.Select((f, i) => new FileOfferEntry($"file{i}.bin", f.Length, false, DateTime.UtcNow.Ticks))]);

    public static byte[][] RandomFiles(params int[] sizes) => [.. sizes.Select(size =>
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    })];

    /// <summary>The connection to the device is gone.</summary>
    public void Close() => _closing.Cancel();

    public async Task<FileChunk> ReadAsync(long offerId, int index, long offset, int length, CancellationToken ct)
    {
        await Task.Yield();
        var data = files[index];
        var start = (int)Math.Min(offset, data.Length);
        return new FileChunk(FileChunkStatus.Ok, data.AsSpan(start, Math.Min(length, data.Length - start)).ToArray());
    }

    public Task<IBundleReader?> OpenBundleAsync(long offerId, int first, int end, CancellationToken ct)
    {
        var bundle = new ScriptedBundle(Script?.Invoke(first) ?? Whole(first, end), HoldBeforeRecord);
        lock (_bundles)
        {
            _bundles.Add(bundle);
        }

        return Task.FromResult<IBundleReader?>(bundle);
    }

    /// <summary>A record of data that is not part of any file: what a device sends when it sends too much.</summary>
    public static BundleRecord Data(int index, int length) => new(BundleRecordKind.Data, index, new byte[length]);

    public static BundleRecord End(int index) => new(BundleRecordKind.End, index, default);

    /// <summary>The contents of one file as the records a device sends them in, without the record that ends it.</summary>
    public static IEnumerable<BundleRecord> DataOf(int index, byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += RecordSize)
        {
            yield return new BundleRecord(BundleRecordKind.Data, index, content.AsMemory(offset, Math.Min(RecordSize, content.Length - offset)));
        }
    }

    private IEnumerable<BundleRecord> Whole(int first, int end)
    {
        for (var index = first; index < end; index++)
        {
            foreach (var record in DataOf(index, files[index]))
            {
                yield return record;
            }

            yield return End(index);
        }
    }
}

/// <summary>One bundle of a <see cref="ScriptedSource"/>; it remembers how far it was read and whether it was let go.</summary>
internal sealed class ScriptedBundle(IEnumerable<BundleRecord> records, int holdBeforeRecord) : IBundleReader
{
    private readonly IEnumerator<BundleRecord> _records = records.GetEnumerator();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _read;
    private volatile bool _disposed;

    /// <summary>How many records the receiver has taken.</summary>
    public int Read => Volatile.Read(ref _read);

    public bool Disposed => _disposed;

    public long WireBytes => 0;

    public long RawBytes => 0;

    public void Release() => _released.TrySetResult();

    public async ValueTask<BundleRecord?> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Read == holdBeforeRecord)
        {
            await _released.Task.WaitAsync(ct);
        }

        if (!_records.MoveNext())
        {
            return null;
        }

        Interlocked.Increment(ref _read);
        return _records.Current;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
