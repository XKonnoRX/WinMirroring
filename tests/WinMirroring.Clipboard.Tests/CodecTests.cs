using System.Text;
using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Tests;

public class CodecTests
{
    [Fact]
    public void SmallDataRoundTripsUncompressed()
    {
        var items = new List<ClipboardItem>
        {
            new(ClipboardItemKind.UnicodeText, Encoding.Unicode.GetBytes("привет\0")),
            new(ClipboardItemKind.Html, Encoding.UTF8.GetBytes("<b>x</b>")),
        };

        var message = ClipboardCodec.EncodeData(items);
        var decoded = ClipboardCodec.TryDecodeData(message, 1024 * 1024);

        Assert.Equal(0, message[1]);
        Assert.NotNull(decoded);
        Assert.Equal(items.Select(i => i.Kind), decoded.Select(i => i.Kind));
        Assert.Equal(items[0].Data, decoded[0].Data);
        Assert.Equal(items[1].Data, decoded[1].Data);
    }

    [Fact]
    public void LargeDataIsCompressedAndRespectsTheLimit()
    {
        var image = new byte[2 * 1024 * 1024]; // a flat bitmap compresses very well
        var message = ClipboardCodec.EncodeData([new ClipboardItem(ClipboardItemKind.Dib, image)]);

        Assert.Equal(1, message[1]);
        Assert.True(message.Length < image.Length / 10);
        Assert.Equal(image, ClipboardCodec.TryDecodeData(message, 4 * 1024 * 1024)![0].Data);
        Assert.Null(ClipboardCodec.TryDecodeData(message, 1024 * 1024));
    }

    [Fact]
    public void TruncatedDataIsRejected()
    {
        var message = ClipboardCodec.EncodeData([new ClipboardItem(ClipboardItemKind.UnicodeText, new byte[100])]);

        Assert.Null(ClipboardCodec.TryDecodeData(message.AsSpan(0, message.Length - 10), 1024));
        Assert.Null(ClipboardCodec.TryDecodeData([], 1024));
    }

    [Fact]
    public void OfferAndReferenceRoundTrip()
    {
        var manifest = new FileOfferManifest(42, "PC",
        [
            new FileOfferEntry("Фото", 0, true, 1),
            new FileOfferEntry("Фото\\a.jpg", 1234, false, 2),
        ]);

        var decoded = ClipboardCodec.TryDecodeOffer(ClipboardCodec.EncodeOffer(manifest));

        Assert.NotNull(decoded);
        Assert.Equal(42, decoded.OfferId);
        Assert.Equal(1234, decoded.TotalSize);
        Assert.Single(decoded.TopLevel);

        Assert.True(ClipboardCodec.TryDecodeDataRef(ClipboardCodec.EncodeDataRef(7, 99), out var id, out var length));
        Assert.Equal((7L, 99L), (id, length));
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("\\abs")]
    [InlineData("a/b")]
    [InlineData("a\\\\b")]
    [InlineData("dir\\..\\x")]
    [InlineData("name.")]
    [InlineData("")]
    public void UnsafeOfferEntriesAreRejected(string path)
    {
        var manifest = new FileOfferManifest(1, "x", [new FileOfferEntry(path, 1, false, 0)]);

        Assert.Null(ClipboardCodec.TryDecodeOffer(ClipboardCodec.EncodeOffer(manifest)));
    }

    [Fact]
    public void FileMessagesRoundTrip()
    {
        Assert.True(FileCodec.TryDecodeRequest(FileCodec.EncodeRequest(5, 77, 3, 1L << 33, 4096), out var id, out var offer, out var index, out var offset, out var length));
        Assert.Equal((5u, 77L, 3, 1L << 33, 4096), (id, offer, index, offset, length));

        var response = FileCodec.EncodeResponse(9, FileChunkStatus.Ok, [1, 2, 3]);
        Assert.True(FileCodec.TryDecodeResponse(response, out var responseId, out var status));
        Assert.Equal((9u, FileChunkStatus.Ok), (responseId, status));
        Assert.Equal(new byte[] { 1, 2, 3 }, response[FileCodec.ResponseHeaderSize..]);
    }
}
