using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Clipboard.Files;
using WinMirroring.Clipboard.Native;
using WinMirroring.Clipboard.Protocol;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.Tests;

public sealed class FilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinMirroring.ClipTests", Guid.NewGuid().ToString("N"));

    public FilesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void OfferExpandsFoldersParentFirstAndServesOnlyItsFiles()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Проект"));
        File.WriteAllText(Path.Combine(folder.FullName, "a.txt"), "aaa");
        Directory.CreateDirectory(Path.Combine(folder.FullName, "sub"));
        File.WriteAllText(Path.Combine(folder.FullName, "sub", "b.txt"), "bb");
        var single = Path.Combine(_root, "single.bin");
        File.WriteAllBytes(single, new byte[10]);

        var store = new LocalOfferStore();
        var manifest = store.Create([folder.FullName, single], "laptop");

        Assert.NotNull(manifest);
        var paths = manifest.Entries.Select(e => e.Path).ToList();
        Assert.Equal("Проект", paths[0]);
        Assert.True(paths.IndexOf("Проект\\sub") < paths.IndexOf("Проект\\sub\\b.txt"));
        Assert.Contains("single.bin", paths);
        Assert.Equal(15, manifest.TotalSize);
        Assert.All(manifest.Entries, e => Assert.True(ClipboardCodec.IsSafeEntry(e)));

        var fileIndex = paths.IndexOf("Проект\\a.txt");
        Assert.True(store.TryGetFile(manifest.OfferId, fileIndex, out var full));
        Assert.Equal(Path.Combine(folder.FullName, "a.txt"), full);
        Assert.False(store.TryGetFile(manifest.OfferId, 0, out _)); // a directory has no contents
        Assert.False(store.TryGetFile(manifest.OfferId + 1, fileIndex, out _));
    }

    [Fact]
    public void DuplicateTopLevelNamesGetSuffixes()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "x")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "y", "x")).FullName;

        var manifest = new LocalOfferStore().Create([first, second], "pc");

        Assert.Equal(["x", "x (2)"], manifest!.TopLevel.Select(e => e.Path));
    }

    [Fact]
    public void RemoteStreamReadsAndSeeksAcrossChunks()
    {
        var data = new byte[RemoteFileStream.ChunkSize * 5 + 1234];
        Random.Shared.NextBytes(data);
        var source = new MemorySource(data);

        using var stream = new RemoteFileStream(source, 1, 0, data.Length);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());

        stream.Position = 300_000;
        var buffer = new byte[100];
        Assert.Equal(100, stream.Read(buffer));
        Assert.Equal(data.AsSpan(300_000, 100).ToArray(), buffer);
        Assert.True(source.MaxConcurrent <= 3);
    }

    [Fact]
    public void RemoteStreamReportsAnExpiredOffer()
    {
        using var stream = new RemoteFileStream(new MemorySource([1, 2, 3]) { Status = FileChunkStatus.NotFound }, 1, 0, 3);

        Assert.Throws<IOException>(() => stream.ReadByte());
    }

    [Fact]
    public void DataObjectDescribesAndStreamsFiles()
    {
        var content = Encoding.UTF8.GetBytes("hello from the other device");
        var manifest = new FileOfferManifest(5, "pc",
        [
            new FileOfferEntry("dir", 0, true, DateTime.UtcNow.Ticks),
            new FileOfferEntry("dir\\note.txt", content.Length, false, DateTime.UtcNow.Ticks),
        ]);
        ComTypes.IDataObject dataObject = new VirtualFileDataObject(manifest, new MemorySource(content), offerDropFiles: false, NullLogger.Instance);

        var descriptorFormat = Format(ClipboardFormats.FileGroupDescriptorW, ComTypes.TYMED.TYMED_HGLOBAL);
        Assert.Equal(0, dataObject.QueryGetData(ref descriptorFormat));
        dataObject.GetData(ref descriptorFormat, out var descriptor);
        var bytes = ClipboardNative.ReadGlobal(descriptor.unionmember, 1 << 20)!;
        ClipboardNative.ReleaseStgMedium(ref descriptor);

        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(0x10, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4 + 36)));
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4 + 592 + 68)));
        Assert.Equal("dir\\note.txt", Encoding.Unicode.GetString(bytes, 4 + 592 + 72, 24));

        var contentsFormat = Format(ClipboardFormats.FileContents, ComTypes.TYMED.TYMED_ISTREAM);
        contentsFormat.lindex = 1;
        dataObject.GetData(ref contentsFormat, out var medium);
        var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        var read = new byte[64];
        var countPointer = Marshal.AllocHGlobal(4);
        try
        {
            stream.Read(read, read.Length, countPointer);
            Assert.Equal(content.Length, Marshal.ReadInt32(countPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(countPointer);
            ClipboardNative.ReleaseStgMedium(ref medium);
        }

        Assert.Equal(content, read.AsSpan(0, content.Length).ToArray());

        contentsFormat.lindex = 0;
        Assert.Throws<COMException>(() => dataObject.GetData(ref contentsFormat, out _));

        var drop = Format(ClipboardNative.CF_HDROP, ComTypes.TYMED.TYMED_HGLOBAL);
        Assert.NotEqual(0, dataObject.QueryGetData(ref drop));
    }

    [Fact]
    public void DropFilesRoundTrip()
    {
        string[] paths = [@"C:\Users\x\Документы\a.txt", @"D:\b"];
        var handle = ClipboardNative.AllocGlobal(VirtualFileDataObject.BuildDropFiles(paths));
        try
        {
            Assert.Equal(paths, ClipboardThread.ReadDropFiles(handle));
        }
        finally
        {
            ClipboardNative.GlobalFree(handle);
        }
    }

    [Fact]
    public async Task DownloaderRecreatesTheTree()
    {
        var content = new byte[300_000];
        Random.Shared.NextBytes(content);
        var manifest = new FileOfferManifest(Random.Shared.NextInt64(1, long.MaxValue), "pc",
        [
            new FileOfferEntry("pack", 0, true, DateTime.UtcNow.Ticks),
            new FileOfferEntry("pack\\data.bin", content.Length, false, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc).Ticks),
        ]);

        var top = await OfferDownloader.DownloadAsync(manifest, new MemorySource(content), TestContext.Current.CancellationToken);
        try
        {
            var folder = Assert.Single(top);
            var file = Path.Combine(folder, "data.bin");
            Assert.Equal(content, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal(2024, File.GetLastWriteTimeUtc(file).Year);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(top[0])!, recursive: true);
        }
    }

    private static ComTypes.FORMATETC Format(uint id, ComTypes.TYMED tymed) => new()
    {
        cfFormat = unchecked((short)id),
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = tymed,
    };

    /// <summary>Serves every (offer, index) from one byte array and records request concurrency.</summary>
    private sealed class MemorySource(byte[] data) : IRemoteFileSource
    {
        private int _inFlight;

        public FileChunkStatus Status { get; init; } = FileChunkStatus.Ok;

        public int MaxConcurrent { get; private set; }

        public async Task<FileChunk> ReadAsync(long offerId, int index, long offset, int length, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Yield();
            Interlocked.Decrement(ref _inFlight);
            if (Status != FileChunkStatus.Ok)
            {
                return new FileChunk(Status, []);
            }

            var start = (int)Math.Min(offset, data.Length);
            var count = Math.Min(length, data.Length - start);
            return new FileChunk(FileChunkStatus.Ok, data.AsSpan(start, count).ToArray());
        }
    }
}
