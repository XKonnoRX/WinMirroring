using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Pictures cut into UDP datagrams and put back together. What this has to get right is what happens when one does
/// not arrive: a picture is only worth anything while it is current, so waiting for a missing piece is the worst of
/// the choices available — the picture after it is already here, and holding that back is the freeze that UDP was
/// meant to remove. So a piece lost on its own is rebuilt from the parity, and anything worse gives the picture up
/// at once and asks for a whole one.
/// </summary>
public class VideoDatagramTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void AHeaderRoundTrips()
    {
        var picture = Picture(5000);
        var plain = new byte[VideoDatagram.SafeDatagramBytes];

        var length = VideoDatagram.Write(plain, frameNo: 42, Width, Height, keyframe: true, picture, piece: 1);

        Assert.True(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out var header));
        Assert.Equal((42u, 5000, Width, Height, true), (header.FrameNo, header.Total, header.Width, header.Height, header.Keyframe));
        Assert.Equal(1, header.Index);
        Assert.False(header.IsParity);
        Assert.Equal(VideoDatagram.Fragments(5000), header.Count);
    }

    /// <summary>
    /// A header that does not agree with itself is refused rather than acted on: the length it claims is what the
    /// receiving side allocates and where it writes, so a datagram that lies about it must go no further.
    /// </summary>
    [Fact]
    public void AHeaderThatDoesNotAgreeWithItselfIsRefused()
    {
        var picture = Picture(5000);
        var plain = new byte[VideoDatagram.SafeDatagramBytes];
        var length = VideoDatagram.Write(plain, 1, Width, Height, false, picture, piece: 0);

        // One byte short of what the header says the piece holds.
        Assert.False(VideoDatagram.TryReadHeader(plain.AsSpan(0, length - 1), out _));

        // A total that does not match the number of pieces it was supposedly cut into.
        VideoDatagram.WriteHeader(plain, new VideoDatagram.Header(1, 5000, Width, Height, Count: 2, Index: 0, Keyframe: false));
        Assert.False(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out _));

        // And nothing at all.
        VideoDatagram.WriteHeader(plain, new VideoDatagram.Header(1, 0, Width, Height, 1, 0, false));
        Assert.False(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(VideoDatagram.PayloadSize)]
    [InlineData(VideoDatagram.PayloadSize + 1)]
    [InlineData(40_000)]
    public void APictureArrivesAsItWasSent(int size)
    {
        var sent = Picture(size);
        var link = new Loopback();

        link.SendPicture(7, sent);

        var whole = Assert.Single(link.Delivered);
        Assert.Equal(sent, whole.Picture);
        Assert.Equal(7u, whole.FrameNo);
        Assert.Equal(0, link.Reassembly.Lost);
    }

    /// <summary>
    /// The common case, and the one worth spending the parity on: one datagram out of a picture is lost and the
    /// picture arrives whole anyway, with nothing asked for and nothing given up.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void OneLostDatagramIsRebuiltFromTheParity(int drop)
    {
        var sent = Picture(20_000);
        var link = new Loopback();
        var pieces = VideoDatagram.Pieces(sent.Length);
        var lose = drop < 0 ? pieces - 2 : drop; // -1 means the last piece of the picture, which is a short one

        link.SendPicture(1, sent, lose);

        Assert.Equal(sent, Assert.Single(link.Delivered).Picture);
        Assert.Equal(1, link.Reassembly.Repaired);
        Assert.Equal(0, link.Reassembly.Lost);
    }

    /// <summary>Losing the parity itself costs nothing, because the picture was all there.</summary>
    [Fact]
    public void LosingTheParityCostsNothing()
    {
        var sent = Picture(20_000);
        var link = new Loopback();

        link.SendPicture(1, sent, VideoDatagram.Pieces(sent.Length) - 1);

        Assert.Equal(sent, link.Delivered[0].Picture);
        Assert.Equal(0, link.Reassembly.Repaired);
    }

    /// <summary>
    /// A whole picture is seven times the size of an ordinary one — a hundred and fifty-eight datagrams at 1080p,
    /// measured — and one parity across all of them would cover the first loss and nothing after it. So each block
    /// of thirty-two carries its own, and a picture that big survives a loss in every one of them.
    /// </summary>
    [Fact]
    public void ABigPictureSurvivesALossInEveryBlockOfIt()
    {
        var sent = Picture(158 * VideoDatagram.PayloadSize);
        var link = new Loopback();
        var blocks = VideoDatagram.Blocks(VideoDatagram.Fragments(sent.Length));
        Assert.True(blocks >= 5, $"a picture this size should be more than one block, and it is {blocks}");

        // One piece gone out of each block, which a single parity for the whole picture could never put back.
        var lose = Enumerable.Range(0, blocks).Select(b => (b * VideoDatagram.ParityBlock) + 1).ToArray();
        link.SendPicture(1, sent, lose);

        Assert.Equal(sent, Assert.Single(link.Delivered).Picture);
        Assert.Equal(blocks, link.Reassembly.Repaired);
        Assert.Equal(0, link.Reassembly.Lost);
    }

    /// <summary>...and two out of the same block is where that ends, because one parity cannot tell them apart.</summary>
    [Fact]
    public void TwoLostOutOfOneBlockIsBeyondTheParity()
    {
        var sent = Picture(158 * VideoDatagram.PayloadSize);
        var next = Picture(3000, seed: 7);
        var link = new Loopback();

        link.SendPicture(1, sent, 3, 7);
        link.SendPicture(2, next);
        link.SendPicture(3, next);

        Assert.Equal([1u], link.GivenUp);
        Assert.Equal(1, link.Reassembly.Lost);
    }

    /// <summary>
    /// Two holes in one picture cannot be told apart by a single parity, so that picture is given up on — and the
    /// point is what happens next: the one after it is handed over rather than held, and the viewer is told so it
    /// can both acknowledge the loss and ask for a whole picture. A moment is allowed first, because a piece that
    /// is merely out of order deserves one; a second newer picture does away with even that.
    /// </summary>
    [Fact]
    public void TwoLostDatagramsGiveThePictureUpWithoutHoldingTheNextOneForLong()
    {
        var first = Picture(20_000);
        var second = Picture(20_000, seed: 9);
        var third = Picture(3000, seed: 10);
        var link = new Loopback();

        link.SendPicture(1, first, 2, 5);
        link.SendPicture(2, second);

        // Still within the moment a late piece is allowed, so nothing has been given up on yet.
        Assert.Empty(link.Delivered);

        link.SendPicture(3, third);

        Assert.Equal(1, link.Reassembly.Lost);
        Assert.Equal([1u], link.GivenUp);
        Assert.Equal([2u, 3u], link.Delivered.Select(d => d.FrameNo));
        Assert.Equal(second, link.Delivered[0].Picture);
    }

    /// <summary>
    /// Datagrams do not arrive in the order they were sent, and a picture is read against the one before it, so
    /// they have to reach the decoder in order whatever order they turn up in.
    /// </summary>
    [Fact]
    public void PicturesAreHandedOverInOrderWhateverOrderTheyArriveIn()
    {
        var one = Picture(3000);
        var two = Picture(3000, seed: 2);
        var three = Picture(3000, seed: 3);
        var link = new Loopback();

        // The first picture establishes where the stream is; a view that opens mid-stream starts from whatever it
        // first sees, since there is nothing behind that it could ever be given.
        link.SendPicture(1, one);
        Assert.Equal([1u], link.Delivered.Select(d => d.FrameNo));

        // Now the fourth picture's datagrams arrive before the third's.
        var third = link.Cut(3, three);
        foreach (var datagram in third)
        {
            link.Receive(datagram);
        }

        Assert.Equal([1u], link.Delivered.Select(d => d.FrameNo)); // nothing can be shown before what precedes it

        foreach (var datagram in link.Cut(2, two))
        {
            link.Receive(datagram);
        }

        Assert.Equal([1u, 2u, 3u], link.Delivered.Select(d => d.FrameNo));
        Assert.Equal(two, link.Delivered[1].Picture);
        Assert.Equal(three, link.Delivered[2].Picture);
        Assert.Equal(0, link.Reassembly.Lost);
    }

    /// <summary>
    /// A loss on the last picture of a burst, after which the screen stops changing. Nothing newer is coming to
    /// make the loss obvious, and everything else here happens because a datagram arrived — so without a nudge the
    /// view would sit on the picture before the loss until the screen moved again, which can be a long time. The
    /// nudge is what the viewer's own clock is for.
    /// </summary>
    [Fact]
    public void APictureLostAsTheScreenStopsIsGivenUpOnByTheClock()
    {
        var lost = Picture(20_000);
        var last = Picture(3000, seed: 4);
        var link = new Loopback();

        link.SendPicture(1, lost, 1, 4);
        link.SendPicture(2, last);

        // The newer picture is here and whole, and nothing else is coming.
        Assert.Empty(link.Delivered);

        Thread.Sleep(20);
        link.Reassembly.Tick();

        Assert.Equal([1u], link.GivenUp);
        Assert.Equal([2u], link.Delivered.Select(d => d.FrameNo));
        Assert.Equal(last, link.Delivered[0].Picture);
    }

    private static byte[] Picture(int size, int seed = 1)
    {
        var picture = new byte[size];
        new Random(seed).NextBytes(picture);
        return picture;
    }

    /// <summary>Cuts pictures up and feeds them back in, losing whichever datagrams a test asks it to.</summary>
    private sealed class Loopback
    {
        public Loopback() => Reassembly = new VideoReassembly(
            (frameNo, width, height, keyframe, picture) => Delivered.Add(new Whole(frameNo, width, height, keyframe, picture.ToArray())),
            GivenUp.Add);

        public VideoReassembly Reassembly { get; }

        public List<Whole> Delivered { get; } = [];

        public List<uint> GivenUp { get; } = [];

        public readonly record struct Whole(uint FrameNo, int Width, int Height, bool Keyframe, byte[] Picture);

        public List<byte[]> Cut(uint frameNo, byte[] picture)
        {
            var datagrams = new List<byte[]>();
            var plain = new byte[VideoDatagram.SafeDatagramBytes];
            var pieces = VideoDatagram.Pieces(picture.Length);
            for (var piece = 0; piece < pieces; piece++)
            {
                var length = VideoDatagram.Write(plain, frameNo, Width, Height, keyframe: frameNo == 1, picture, piece);
                datagrams.Add(plain.AsSpan(0, length).ToArray());
            }

            return datagrams;
        }

        public void Receive(byte[] datagram)
        {
            Assert.True(VideoDatagram.TryReadHeader(datagram, out var header));
            Reassembly.Add(header, datagram.AsSpan(VideoDatagram.HeaderSize));
        }

        public void SendPicture(uint frameNo, byte[] picture, params int[] lose)
        {
            var datagrams = Cut(frameNo, picture);
            for (var i = 0; i < datagrams.Count; i++)
            {
                if (!lose.Contains(i))
                {
                    Receive(datagrams[i]);
                }
            }
        }
    }
}
