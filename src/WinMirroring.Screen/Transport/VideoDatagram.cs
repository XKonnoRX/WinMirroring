using System.Buffers.Binary;

namespace WinMirroring.Screen.Transport;

/// <summary>
/// How one encoded picture is cut into UDP datagrams, and how a single lost one is got back.
///
/// Why pictures go by UDP at all: on one connection a lost segment stops everything behind it until it has been
/// asked for again and sent again, which costs a whole round trip — and what is behind it is the next picture,
/// which by then is the one that should be on the screen. A picture is only worth anything while it is current,
/// so a late one is worth less than a fresh one, and that is the trade a reliable connection cannot make.
///
/// Each picture also travels with one parity datagram, the exclusive-or of all its others. Any single datagram
/// lost out of a picture is then rebuilt from the rest without asking for anything — which is the common case, and
/// the one worth spending seven per cent of the bandwidth on. Two lost out of the same picture are not, and that
/// picture is given up on (see <see cref="VideoReassembly"/>).
/// </summary>
internal static class VideoDatagram
{
    /// <summary>frameNo(4) total(4) width(2) height(2) count(2) index(2) flags(1) reserved(1).</summary>
    public const int HeaderSize = 18;

    /// <summary>A datagram kept below this stays in one IP packet on any ordinary link, tunnels included.</summary>
    public const int SafeDatagramBytes = 1200;

    /// <summary>What one datagram carries of a picture, once the sealing and this header are paid for.</summary>
    public const int PayloadSize = SafeDatagramBytes - Core.Media.MediaCipher.Overhead - HeaderSize;

    /// <summary>
    /// Pieces each parity datagram covers. One parity for a whole picture is right for an ordinary one — about
    /// twenty-three datagrams at 1080p — but a whole picture is measured at seven times that, and one parity over a
    /// hundred and fifty-eight datagrams repairs the first loss and nothing after it. At thirty-two the ordinary
    /// picture still carries exactly one, so it costs no more than it did, and the big one carries five.
    /// </summary>
    public const int ParityBlock = 32;

    /// <summary>
    /// Indices from here down mark parity datagrams rather than pieces of the picture: <see cref="ParityIndex"/>
    /// less the number of the block it covers. Counting down from the top is what keeps them out of the way of the
    /// pieces' own indices — and what makes a peer that does not understand them refuse them outright rather than
    /// mistake one for a piece, since an index that high is past the end of any picture.
    /// </summary>
    public const ushort ParityIndex = 0xFFFE;

    public static int Blocks(int fragments) => Math.Max(1, (fragments + ParityBlock - 1) / ParityBlock);

    /// <summary>
    /// Pictures are cut into at most this many pieces — four megabytes' worth, which is <see cref="MaxPictureBytes"/>
    /// and far below where the parity indices begin counting down from.
    /// </summary>
    public const int MaxFragments = 4096;

    /// <summary>
    /// The largest picture a datagram may claim to be part of. A whole 1080p key frame is a couple of hundred
    /// kilobytes, so four megabytes is room to spare — and it is a limit because the number is what the receiving
    /// side allocates, and the only thing that stops one datagram asking for a great deal of memory.
    /// </summary>
    public const int MaxPictureBytes = 4 << 20;

    public readonly record struct Header(uint FrameNo, int Total, int Width, int Height, int Count, int Index, bool Keyframe, byte Tag = 0)
    {
        public bool IsParity => Index <= ParityIndex && Index > ParityIndex - Blocks(Count);

        /// <summary>Which run of pieces this parity covers; meaningless for a piece.</summary>
        public int Block => IsParity ? ParityIndex - Index : Index / ParityBlock;

        /// <summary>The first piece of this datagram's block, and how many pieces are in it.</summary>
        public int BlockStart => Block * ParityBlock;

        public int BlockLength => Math.Min(ParityBlock, Count - BlockStart);

        /// <summary>Where this piece belongs in the picture, and how much of it there is.</summary>
        public int Offset => Index * PayloadSize;

        public int Length => IsParity ? PayloadSize : Math.Min(PayloadSize, Total - Offset);
    }

    public static int Fragments(int total) => Math.Max(1, (total + PayloadSize - 1) / PayloadSize);

    /// <summary>
    /// How many datagrams one picture becomes: its pieces, and a parity behind each block of them. A picture of a
    /// single piece gets no parity at all, since losing that piece loses the picture whatever travels with it.
    /// </summary>
    public static int Pieces(int total)
    {
        var fragments = Fragments(total);
        return fragments > 1 ? fragments + Blocks(fragments) : 1;
    }

    /// <summary>
    /// Writes piece <paramref name="piece"/> of a picture — header and payload — into <paramref name="plaintext"/>
    /// and returns its length. The pieces come first and the parities behind them, one per block.
    /// </summary>
    public static int Write(Span<byte> plaintext, uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> picture, int piece, byte tag = 0)
    {
        var count = Fragments(picture.Length);
        var parity = piece >= count;
        var block = parity ? piece - count : 0;
        var index = parity ? ParityIndex - block : piece;
        WriteHeader(plaintext, new Header(frameNo, picture.Length, width, height, count, index, keyframe, tag));

        if (parity)
        {
            var start = block * ParityBlock * PayloadSize;
            var length = Math.Min(ParityBlock * PayloadSize, picture.Length - start);
            Parity(picture.Slice(start, length), plaintext[HeaderSize..]);
            return HeaderSize + PayloadSize;
        }

        var offset = piece * PayloadSize;
        var pieceLength = Math.Min(PayloadSize, picture.Length - offset);
        picture.Slice(offset, pieceLength).CopyTo(plaintext[HeaderSize..]);
        return HeaderSize + pieceLength;
    }

    public static void WriteHeader(Span<byte> plaintext, Header header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(plaintext, header.FrameNo);
        BinaryPrimitives.WriteUInt32LittleEndian(plaintext[4..], (uint)header.Total);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext[8..], (ushort)header.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext[10..], (ushort)header.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext[12..], (ushort)header.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext[14..], (ushort)header.Index);
        plaintext[16] = (byte)(header.Keyframe ? 1 : 0);
        plaintext[17] = header.Tag; // which part of the screen the picture shows; it was a spare byte before
    }

    /// <summary>Reads a datagram's header, or false when it does not describe a piece of a picture at all.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> plaintext, out Header header)
    {
        header = default;
        if (plaintext.Length < HeaderSize)
        {
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(plaintext[4..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(plaintext[12..]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(plaintext[14..]);
        if (total == 0 || total > MaxPictureBytes || count == 0 || count > MaxFragments)
        {
            return false;
        }

        // Either a piece inside the picture, or a parity for one of its blocks. Anything else — a piece past the
        // end, a parity for a block the picture does not have — is refused rather than acted on.
        var parity = index <= ParityIndex && index > ParityIndex - Blocks(count);
        if (!parity && index >= count)
        {
            return false;
        }

        // The picture's own length has to match the number of pieces it was cut into, or a forged header could
        // have a piece written outside the picture it claims to belong to.
        if (Fragments((int)total) != count)
        {
            return false;
        }

        header = new Header(
            BinaryPrimitives.ReadUInt32LittleEndian(plaintext),
            (int)total,
            BinaryPrimitives.ReadUInt16LittleEndian(plaintext[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(plaintext[10..]),
            count,
            index,
            (plaintext[16] & 1) != 0,
            plaintext[17]);

        return plaintext.Length - HeaderSize == header.Length;
    }

    /// <summary>
    /// The parity for one picture: every piece of it exclusive-or'ed together, each padded out to a full piece so
    /// that the short last one lines up with the rest. One missing piece is this exclusive-or'ed with all the
    /// pieces that did arrive.
    /// </summary>
    public static void Parity(ReadOnlySpan<byte> picture, Span<byte> parity)
    {
        parity[..PayloadSize].Clear();
        for (var offset = 0; offset < picture.Length; offset += PayloadSize)
        {
            var piece = picture[offset..Math.Min(offset + PayloadSize, picture.Length)];
            for (var i = 0; i < piece.Length; i++)
            {
                parity[i] ^= piece[i];
            }
        }
    }
}
