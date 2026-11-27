using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core.Media;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// A viewer that is missing pieces of a picture asks for them, and the source sends them again; and what the
/// viewer says back — that, and its acknowledgements — travels by datagram like the pictures do.
///
/// Both used to go differently. A picture that lost two pieces was given up whole and a complete new one asked
/// for; and acknowledgements went on the connection only, where on a lossy link each waits behind whichever
/// segment was dropped, so a source that waits on them stood still for pictures that had already arrived.
/// </summary>
public class ResendTests
{
    private const int Width = 1280;
    private const int Height = 720;

    /// <summary>
    /// A source's link and a viewer's reassembly with a wire between them that a test can lose things on. Time is
    /// the test's own, and nothing moves on it unless the test says so.
    /// </summary>
    private sealed class Wire : IDisposable
    {
        private readonly MediaCipher _viewerOpens;
        private readonly MediaCipher _viewerSeals;
        private readonly byte[] _plain = new byte[VideoDatagram.SafeDatagramBytes];
        private ulong _viewerSequence;

        public Wire(bool sourceAnswers = true)
        {
            Link = VideoLink.ForTest(NullLogger.Instance, () => Clock, datagram => Sent.Add(datagram));
            var key = Convert.FromBase64String(Link.Key);
            _viewerOpens = new MediaCipher(key);
            _viewerSeals = new MediaCipher(key);
            Reassembly = new VideoReassembly(
                (frameNo, _, _, _, picture, _) => Delivered.Add((frameNo, picture.ToArray())),
                GivenUp.Add,
                () => Clock,
                (frameNo, pieces) =>
                {
                    Requests.Add((frameNo, pieces.ToArray()));
                    if (sourceAnswers)
                    {
                        var payload = new byte[ViewerReplies.MaxPayload];
                        Assert.True(Link.Receive(Seal(ViewerReplies.ResendKind, payload.AsSpan(0, ViewerReplies.WriteResend(payload, frameNo, pieces))), []));
                    }
                });
            Link.ProbeIfDue();
            Assert.True(Link.Confirm());
            Sent.Clear();
        }

        public VideoLink Link { get; }

        public VideoReassembly Reassembly { get; }

        public long Clock { get; set; } = 10_000;

        /// <summary>Datagrams the source has put on the wire and the viewer has not been given yet.</summary>
        public List<byte[]> Sent { get; } = [];

        public List<(uint FrameNo, byte[] Picture)> Delivered { get; } = [];

        public List<uint> GivenUp { get; } = [];

        public List<(uint FrameNo, int[] Pieces)> Requests { get; } = [];

        public byte[] Seal(byte kind, ReadOnlySpan<byte> payload, ulong? sequence = null)
        {
            var packet = new byte[payload.Length + MediaCipher.Overhead];
            _viewerSeals.Seal(kind, 1, sequence ?? (ViewerReplies.ViewerSequenceBase + _viewerSequence++), payload, packet);
            return packet;
        }

        /// <summary>
        /// Gives the viewer everything on the wire except what <paramref name="lose"/> picks (by its place among
        /// the datagrams waiting), and anything sent in answer along the way, until the wire is empty.
        /// </summary>
        public void Carry(Func<int, bool>? lose = null)
        {
            var index = 0;
            while (Sent.Count > 0)
            {
                var datagram = Sent[0];
                Sent.RemoveAt(0);
                if (lose?.Invoke(index++) == true)
                {
                    continue;
                }

                Assert.True(_viewerOpens.TryOpen(datagram, _plain, out var length));
                Assert.True(VideoDatagram.TryReadHeader(_plain.AsSpan(0, length), out var header));
                Reassembly.Add(header, _plain.AsSpan(VideoDatagram.HeaderSize, length - VideoDatagram.HeaderSize));
                lose = index > 0 && Sent.Count == 0 ? null : lose;
            }
        }

        public void Dispose()
        {
            Link.Dispose();
            _viewerOpens.Dispose();
            _viewerSeals.Dispose();
        }
    }

    private static byte[] Picture(int pieces, int seed)
    {
        var picture = new byte[(VideoDatagram.PayloadSize * pieces) - 17];
        new Random(seed).NextBytes(picture);
        return picture;
    }

    [Fact]
    public void TwoPiecesLostFromOnePictureAreAskedForAndThePictureArrivesWhole()
    {
        using var wire = new Wire();
        var picture = Picture(pieces: 10, seed: 1);
        Assert.True(wire.Link.TrySend(1, Width, Height, false, picture));

        // Pieces 3 and 6: one more than the parity can put back. What is sent again is not lost a second time.
        var first = true;
        wire.Carry(i =>
        {
            var lost = first && i is 3 or 6;
            first &= i < 10;
            return lost;
        });

        Assert.Equal([(1u, new[] { 3, 6 })], wire.Requests.Select(r => (r.FrameNo, r.Pieces)).ToList(), RequestComparer.Instance);
        Assert.Empty(wire.GivenUp);
        Assert.Equal(picture, Assert.Single(wire.Delivered).Picture);
        Assert.Equal(2, wire.Link.Resent);
    }

    [Fact]
    public void APictureThatNeverArrivedAtAllIsAskedForWholeAndStillComesFirst()
    {
        using var wire = new Wire();
        var lost = Picture(pieces: 3, seed: 2);
        var next = Picture(pieces: 2, seed: 3);

        // One that arrives, so the viewer knows where the stream is: a picture missing before the very first it
        // ever sees is not something it can know about.
        Assert.True(wire.Link.TrySend(1, Width, Height, true, Picture(pieces: 2, seed: 9)));
        wire.Carry();

        Assert.True(wire.Link.TrySend(2, Width, Height, false, lost));
        wire.Sent.Clear(); // every datagram of it gone

        Assert.True(wire.Link.TrySend(3, Width, Height, false, next));
        wire.Carry();

        Assert.Equal(2u, wire.Requests[0].FrameNo);
        Assert.Empty(wire.Requests[0].Pieces);
        Assert.Empty(wire.GivenUp);
        Assert.Equal([1u, 2u, 3u], wire.Delivered.Select(d => d.FrameNo));
        Assert.Equal(lost, wire.Delivered[1].Picture);
        Assert.Equal(next, wire.Delivered[2].Picture);
    }

    [Fact]
    public void ALastPictureLeftInPiecesIsAskedAboutOnceItHasGoneQuiet()
    {
        using var wire = new Wire();
        var picture = Picture(pieces: 6, seed: 4);
        Assert.True(wire.Link.TrySend(1, Width, Height, false, picture));

        // Two pieces and the parity lost: the datagram that would have said "that was all of it" never comes,
        // and no later picture comes either.
        wire.Carry(i => i is 1 or 4 or 6);
        Assert.Empty(wire.Requests);

        wire.Reassembly.Tick();
        Assert.Empty(wire.Requests); // still arriving, as far as anyone can tell

        wire.Clock += (long)VideoReassembly.QuietBeforeAsking.TotalMilliseconds;
        wire.Reassembly.Tick();
        wire.Carry();

        Assert.Equal(new[] { 1, 4 }, Assert.Single(wire.Requests).Pieces);
        Assert.Equal(picture, Assert.Single(wire.Delivered).Picture);
        Assert.Empty(wire.GivenUp);
    }

    [Fact]
    public void ARequestThatBringsNothingIsRepeatedOnceAndThenThePictureIsGivenUp()
    {
        using var wire = new Wire(sourceAnswers: false);
        Assert.True(wire.Link.TrySend(1, Width, Height, false, Picture(pieces: 5, seed: 5)));
        wire.Carry(i => i is 0 or 2);
        Assert.Single(wire.Requests); // asked as its last datagram arrived

        Assert.True(wire.Link.TrySend(2, Width, Height, false, Picture(pieces: 1, seed: 6)));
        wire.Carry();
        Assert.Single(wire.Requests); // too soon to ask again
        Assert.Empty(wire.GivenUp);
        Assert.Empty(wire.Delivered); // the later picture waits its turn

        wire.Clock += (long)VideoReassembly.AskTwiceAfter.TotalMilliseconds;
        wire.Reassembly.Tick();
        Assert.Equal(2, wire.Requests.Count);

        wire.Clock += (long)VideoReassembly.RepairWait.TotalMilliseconds;
        wire.Reassembly.Tick();
        Assert.Equal(2, wire.Requests.Count); // and no more than twice
        Assert.Equal([1u], wire.GivenUp);
        Assert.Equal([2u], wire.Delivered.Select(d => d.FrameNo));
    }

    [Fact]
    public void WaitingForPiecesDoesNotHoldMoreThanAFewPicturesBack()
    {
        using var wire = new Wire(sourceAnswers: false);
        Assert.True(wire.Link.TrySend(1, Width, Height, false, Picture(pieces: 4, seed: 7)));
        wire.Carry(i => i is 0 or 1);

        for (uint later = 2; later <= 1 + VideoReassembly.LeadWhenAsking; later++)
        {
            Assert.Empty(wire.GivenUp);
            Assert.True(wire.Link.TrySend(later, Width, Height, false, Picture(pieces: 1, seed: (int)later)));
            wire.Carry();
        }

        Assert.Equal([1u], wire.GivenUp);
        Assert.Equal((int)VideoReassembly.LeadWhenAsking, wire.Delivered.Count);
    }

    [Fact]
    public void ThePictureIsNotSentAgainForEver()
    {
        using var wire = new Wire();
        Assert.True(wire.Link.TrySend(1, Width, Height, false, Picture(pieces: 3, seed: 8)));
        wire.Sent.Clear();

        for (var i = 0; i < VideoLink.MostResendsOfOne; i++)
        {
            Assert.True(wire.Link.Resend(1, []) > 0);
        }

        Assert.Equal(0, wire.Link.Resend(1, []));
        Assert.Equal(0, wire.Link.Resend(99, [])); // nor a picture it never sent
    }

    [Fact]
    public void AcknowledgementsArriveByDatagramAndEachCarriesTheFewBeforeIt()
    {
        using var wire = new Wire();
        var payload = new byte[ViewerReplies.MaxPayload];
        ViewerReplies.Ack[] acks = [new(7, 3, 1), new(6, 2, 0), new(5, 40, 2)];
        var length = ViewerReplies.WriteAcks(payload, acks);

        var received = new List<ViewerReplies.Ack>();
        Assert.True(wire.Link.Receive(wire.Seal(ViewerReplies.AckKind, payload.AsSpan(0, length)), received));

        Assert.Equal(acks, received);
    }

    [Fact]
    public void OnlyWhatTheViewerSealedIsListenedTo()
    {
        using var wire = new Wire();
        var payload = new byte[ViewerReplies.MaxPayload];
        var length = ViewerReplies.WriteAcks(payload, [new ViewerReplies.Ack(7, 3, 1)]);
        var acks = new List<ViewerReplies.Ack>();

        // Sealed with the right key but under a number from the source's own range: its own datagram come back.
        Assert.False(wire.Link.Receive(wire.Seal(ViewerReplies.AckKind, payload.AsSpan(0, length), sequence: 12), acks));

        // Tampered with on the way.
        var packet = wire.Seal(ViewerReplies.AckKind, payload.AsSpan(0, length));
        packet[^1] ^= 1;
        Assert.False(wire.Link.Receive(packet, acks));

        // Another key altogether.
        using var stranger = new MediaCipher(MediaCipher.GenerateKey());
        var forged = new byte[length + MediaCipher.Overhead];
        stranger.Seal(ViewerReplies.AckKind, 1, ViewerReplies.ViewerSequenceBase, payload.AsSpan(0, length), forged);
        Assert.False(wire.Link.Receive(forged, acks));

        // A picture is not a reply.
        Assert.False(wire.Link.Receive(wire.Seal(VideoLink.FrameKind, payload.AsSpan(0, length)), acks));
        Assert.Empty(acks);
    }

    [Fact]
    public void ARequestNamesItsPiecesAndOneForTooManyAsksForAll()
    {
        var payload = new byte[ViewerReplies.MaxPayload];
        var pieces = new List<int>();

        var length = ViewerReplies.WriteResend(payload, 41, [0, 5, 4095]);
        Assert.True(ViewerReplies.TryReadResend(payload.AsSpan(0, length), out var frameNo, pieces));
        Assert.Equal(41u, frameNo);
        Assert.Equal([0, 5, 4095], pieces);

        pieces.Clear();
        length = ViewerReplies.WriteResend(payload, 42, Enumerable.Range(0, ViewerReplies.MostPiecesAsked + 1).ToList());
        Assert.True(ViewerReplies.TryReadResend(payload.AsSpan(0, length), out frameNo, pieces));
        Assert.Equal(42u, frameNo);
        Assert.Empty(pieces);

        Assert.False(ViewerReplies.TryReadResend(payload.AsSpan(0, 5), out _, pieces));
        Assert.False(ViewerReplies.TryReadAcks(payload.AsSpan(0, 3), []));
    }

    /// <summary>
    /// A window that is answered by frame number forgets a frame the moment it is answered, so the same answer
    /// arriving a second time — once by datagram, once on the connection — is one answer.
    /// </summary>
    [Fact]
    public async Task TheSameAcknowledgementArrivingBothWaysIsCountedOnce()
    {
        using var window = new SendWindow(3, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(40));
        var ct = TestContext.Current.CancellationToken;
        await window.TakeAsync(1, ct);
        await window.TakeAsync(2, ct);

        window.OnAck(1, 2, 0);
        window.OnAck(1, 2, 0);

        Assert.Equal(1, window.InFlight);
        Assert.Equal(1, window.Reading.Acks);
        Assert.Equal(2u, window.OldestFrame);
    }

    private sealed class RequestComparer : IEqualityComparer<(uint FrameNo, int[] Pieces)>
    {
        public static readonly RequestComparer Instance = new();

        public bool Equals((uint FrameNo, int[] Pieces) x, (uint FrameNo, int[] Pieces) y) =>
            x.FrameNo == y.FrameNo && x.Pieces.SequenceEqual(y.Pieces);

        public int GetHashCode((uint FrameNo, int[] Pieces) value) => (int)value.FrameNo;
    }
}
