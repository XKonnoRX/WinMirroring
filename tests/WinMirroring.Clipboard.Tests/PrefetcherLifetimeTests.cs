using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Clipboard.Files;

namespace WinMirroring.Clipboard.Tests;

/// <summary>
/// How the bundle that runs ahead of Explorer ends when Explorer does not read it to the end. Explorer never says
/// that it has stopped, and a stream it lets go of is not closed — so here a stream that is simply not read any more
/// stands for a paste given up half way, and nothing is disposed unless the test is about disposing.
/// </summary>
public sealed class PrefetcherLifetimeTests
{
    private const int FileSize = 64 * 1024;
    private const int SmallBudget = 2 * FileSize;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NotInThisTest = TimeSpan.FromHours(1);

    [Fact]
    public async Task APasteGivenUpHalfWayEndsTheBundleAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles([.. Enumerable.Repeat(FileSize, 12)]);
        var source = new ScriptedSource(files);
        var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance)
        {
            Budget = SmallBudget,
            StalledAfter = TimeSpan.FromMilliseconds(500),
        };
        var finished = Finishes(prefetcher);

        // Explorer copies the first file and is then told to stop.
        Assert.Equal(files[0], await ReadAsync(prefetcher.Open(0), ct));

        Assert.False(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));
        var bundle = Assert.Single(source.Bundles);
        await WaitUntilAsync(() => bundle.Disposed, ct, "the bundle is given back to the device that sends it");
        Assert.True(bundle.Read < 12 * (FileSize / ScriptedSource.RecordSize + 1), "the whole set was received with nobody reading it");

        // Pasted again later: served from a new bundle, all of it, and that one ends well.
        for (var index = 0; index < files.Length; index++)
        {
            Assert.Equal(files[index], await ReadAsync(prefetcher.Open(index), ct));
        }

        Assert.True(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));
        Assert.Equal(2, source.Bundles.Count);
    }

    [Fact]
    public async Task AnOfferNoLongerOnTheClipboardIsGivenUpSooner()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles([.. Enumerable.Repeat(FileSize, 12)]);
        var source = new ScriptedSource(files);
        var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance)
        {
            Budget = SmallBudget,
            StalledAfter = NotInThisTest,
            StalledAfterRetiring = TimeSpan.FromMilliseconds(300),
        };
        var finished = Finishes(prefetcher);

        Assert.Equal(files[0], await ReadAsync(prefetcher.Open(0), ct));

        // While the files are on the clipboard a reader may still come: the bundle waits.
        await Task.Delay(700, ct);
        Assert.False(finished.TryRead(out _));

        // Something else was copied. No new paste can come now, and the one there was has stopped reading.
        prefetcher.Retire();
        Assert.False(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));

        // A paste that began before that and is only slow goes on all the same, and still by bundle.
        Assert.Equal(files[1], await ReadAsync(prefetcher.Open(1), ct));
        Assert.Equal(2, source.Bundles.Count);
    }

    [Fact]
    public async Task AClosedConnectionEndsABundleThatWasWaitingForAReader()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles([.. Enumerable.Repeat(FileSize, 12)]);
        var source = new ScriptedSource(files);
        var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance)
        {
            Budget = SmallBudget,
            StalledAfter = NotInThisTest,
        };
        var finished = Finishes(prefetcher);

        Assert.Equal(files[0], await ReadAsync(prefetcher.Open(0), ct));
        await Task.Delay(300, ct); // full to its budget by now, and not reading from the device any more
        Assert.False(finished.TryRead(out _));

        source.Close();

        Assert.False(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));
        await WaitUntilAsync(() => source.Bundles[0].Disposed, ct, "the bundle is let go");
    }

    [Fact]
    public async Task DisposingEndsTheBundleAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles([.. Enumerable.Repeat(FileSize, 12)]);
        var source = new ScriptedSource(files);
        var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance)
        {
            Budget = SmallBudget,
            StalledAfter = NotInThisTest,
        };
        var finished = Finishes(prefetcher);

        Assert.Equal(files[0], await ReadAsync(prefetcher.Open(0), ct));
        await Task.Delay(300, ct);

        prefetcher.Dispose();

        Assert.False(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));
        await WaitUntilAsync(() => source.Bundles[0].Disposed, ct, "the bundle is let go");
        Assert.Null(prefetcher.Open(1));
    }

    [Fact]
    public async Task AStreamLeftUnreadDoesNotKeepTheNextOneWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles(8 * FileSize, FileSize, FileSize);
        var source = new ScriptedSource(files);
        var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance)
        {
            Budget = SmallBudget,
            StalledAfter = NotInThisTest,
        };

        // Explorer opens the large file, reads its beginning and lets the stream go (it is never closed). The bundle
        // fills up with the rest of that file, which nobody takes.
        var left = prefetcher.Open(0)!;
        var beginning = new byte[1000];
        await Task.Run(() => left.ReadExactly(beginning), ct);
        await Task.Delay(300, ct);

        // The next file has to arrive all the same, and at once — not when a timeout runs out.
        Assert.Equal(files[1], await ReadAsync(prefetcher.Open(1), ct).WaitAsync(Soon, ct));

        // And if the first stream is read after all, it carries on from where it was.
        var rest = await ReadAsync(left, ct).WaitAsync(Soon, ct);
        byte[] whole = [.. beginning, .. rest];
        Assert.Equal(files[0], whole);
    }

    [Fact]
    public async Task AFileWhoseBeginningWasLetGoIsNotServedFromItsMiddle()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles(16 * ScriptedSource.RecordSize, FileSize);

        // The device stops in the middle of the first file, so the bundle is certain to be there when it is asked for again.
        var source = new ScriptedSource(files) { HoldBeforeRecord = 4 };
        using var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance);

        var first = prefetcher.Open(0)!;
        var beginning = new byte[ScriptedSource.RecordSize];
        await Task.Run(() => first.ReadExactly(beginning), ct);
        first.Seek(100_000, SeekOrigin.Begin); // reads on its own from here, and its buffer is let go

        // Asked for again: what the bundle brings from now on is the file without its beginning.
        Assert.Null(prefetcher.Open(0));

        source.Bundles[0].Release();
        var tail = new byte[files[0].Length - 100_000];
        await Task.Run(() => first.ReadExactly(tail), ct);
        Assert.Equal(files[0].AsSpan(100_000).ToArray(), tail);
    }

    [Fact]
    public async Task MoreOfAFileThanWasAnnouncedStopsTheBundleAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = ScriptedSource.RandomFiles(3 * ScriptedSource.RecordSize, FileSize);

        // Three records are the whole first file; the device goes on sending it and never says it has ended.
        var source = new ScriptedSource(files)
        {
            Script = _ => ScriptedSource.DataOf(0, files[0]).Concat(Enumerable.Repeat(ScriptedSource.Data(0, ScriptedSource.RecordSize), 1000)),
        };
        using var prefetcher = new OfferPrefetcher(ScriptedSource.ManifestOf(files), source, NullLogger.Instance);
        var finished = Finishes(prefetcher);

        var stream = prefetcher.Open(0);

        Assert.False(await finished.ReadAsync(ct).AsTask().WaitAsync(Soon, ct));
        Assert.Equal(4, source.Bundles[0].Read); // stopped at the first record too many
        await WaitUntilAsync(() => source.Bundles[0].Disposed, ct, "the bundle is let go");

        // Explorer still gets the file, the old way.
        Assert.Equal(files[0], await ReadAsync(stream, ct).WaitAsync(Soon, ct));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>Every end of a transfer the prefetcher announces, in order.</summary>
    private static ChannelReader<bool> Finishes(OfferPrefetcher prefetcher)
    {
        var finished = Channel.CreateUnbounded<bool>();
        prefetcher.Finished += ok => finished.Writer.TryWrite(ok);
        return finished.Reader;
    }

    /// <summary>Reads a stream to its end the way Explorer does: on a thread of its own, blocking.</summary>
    private static Task<byte[]> ReadAsync(Stream? stream, CancellationToken ct)
    {
        Assert.NotNull(stream);
        return Task.Run(() =>
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }, ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"not reached in time: {what}");
            await Task.Delay(50, ct);
        }
    }
}
