namespace WinMirroring.Screen.Transport;

/// <summary>
/// Puts pictures back together from the datagrams they arrived in, in the order they were sent, and decides when
/// one is not coming.
///
/// Two things it has to get right. A picture is read against the one before it, so they have to reach the decoder
/// in order even though datagrams do not arrive in order; and when a piece of one is missing for good, waiting for
/// it is the worst of the three choices available — the picture after it is already here, and holding that back is
/// a freeze, which is the thing UDP was meant to stop. So a piece lost on its own is rebuilt from the parity, and
/// anything worse gives the picture up at once: the viewer says so, which both frees the sender's window and asks
/// it for a whole picture, and what is on the screen in the meantime is a frame or two of mush rather than a stall.
/// </summary>
internal sealed class VideoReassembly
{
    /// <summary>A picture is given up on at once once this many later ones have begun to arrive.</summary>
    private const uint Lead = 2;

    /// <summary>
    /// How long a picture is waited for after a later one has turned up. Datagrams do arrive out of order, so a
    /// piece that is merely late deserves a moment — but only a moment, because what is being held meanwhile is a
    /// picture that is ready to show. Well past any reordering worth the name, and half a frame at sixty a second.
    /// </summary>
    private static readonly TimeSpan Reorder = TimeSpan.FromMilliseconds(8);

    /// <summary>A whole picture, in order. The span is only valid for the call.</summary>
    public delegate void PictureHandler(uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> picture);

    private readonly PictureHandler _deliver;
    private readonly Action<uint> _giveUp;
    private readonly Dictionary<uint, Picture> _pictures = [];

    private uint _next;
    private uint _newest;
    private bool _started;

    /// <summary>When a later picture first turned up while this one was still missing a piece, or 0.</summary>
    private long _overdueSince;
    private long _delivered;
    private long _repaired;
    private long _lost;

    /// <summary>
    /// <paramref name="deliver"/> takes each whole picture in order; <paramref name="giveUp"/> is told the number
    /// of each one that will never be whole, so the viewer can acknowledge it and ask for a fresh picture.
    /// </summary>
    public VideoReassembly(PictureHandler deliver, Action<uint> giveUp)
    {
        _deliver = deliver;
        _giveUp = giveUp;
    }

    /// <summary>Pictures handed over whole, of which this many had a lost piece rebuilt from the parity.</summary>
    public long Delivered => _delivered;

    public long Repaired => _repaired;

    /// <summary>Pictures given up on: more than one piece of them was lost.</summary>
    public long Lost => _lost;

    /// <summary>One datagram. Not thread-safe: it is called from the one loop that receives them.</summary>
    public void Add(in VideoDatagram.Header header, ReadOnlySpan<byte> payload)
    {
        if (!_started)
        {
            // Wherever the stream is now is where this starts: a view may open, or move onto UDP, at any point in
            // one. The first picture a source sends this way is a whole one, so there is nothing to catch up on.
            _started = true;
            _next = header.FrameNo;
        }

        if (header.FrameNo < _next)
        {
            return; // already delivered or given up on; a straggler changes nothing
        }

        if (header.FrameNo > _newest)
        {
            _newest = header.FrameNo;
        }

        if (_newest > _next && _overdueSince == 0)
        {
            _overdueSince = Environment.TickCount64;
        }

        var picture = Taken(header);
        if (header.IsParity)
        {
            (picture.Parity ??= new byte[VideoDatagram.PayloadSize]).AsSpan().Clear();
            payload.CopyTo(picture.Parity);
            picture.HasParity = true;
        }
        else if (!picture.Have[header.Index])
        {
            payload.CopyTo(picture.Data.AsSpan(header.Offset, payload.Length));
            picture.Have[header.Index] = true;
            picture.Received++;
        }

        Repair(picture);
        Flush();
    }

    /// <summary>
    /// The one piece missing out of a picture, rebuilt from the parity and the pieces that did arrive. Only ever
    /// one: the parity is a single exclusive-or, so two holes in the same picture cannot be told apart.
    /// </summary>
    private void Repair(Picture picture)
    {
        if (picture.Complete || !picture.HasParity || picture.Received != picture.Count - 1)
        {
            return;
        }

        var missing = Array.IndexOf(picture.Have, false);
        if (missing < 0)
        {
            return;
        }

        var rebuilt = picture.Parity!.AsSpan();
        for (var i = 0; i < picture.Count; i++)
        {
            if (i == missing)
            {
                continue;
            }

            var offset = i * VideoDatagram.PayloadSize;
            var piece = picture.Data.AsSpan(offset, Math.Min(VideoDatagram.PayloadSize, picture.Total - offset));
            for (var b = 0; b < piece.Length; b++)
            {
                rebuilt[b] ^= piece[b];
            }
        }

        var hole = missing * VideoDatagram.PayloadSize;
        var length = Math.Min(VideoDatagram.PayloadSize, picture.Total - hole);
        rebuilt[..length].CopyTo(picture.Data.AsSpan(hole, length));
        picture.Have[missing] = true;
        picture.Received++;
        _repaired++;
    }

    /// <summary>
    /// Hands over every picture that is whole and whose turn it is, giving up on one whose turn it is and which is
    /// not going to be whole. Called after every datagram, which is also what makes the giving up timely: a
    /// datagram for a later picture is the news that an earlier one is overdue.
    /// </summary>
    private void Flush()
    {
        while (true)
        {
            if (_pictures.TryGetValue(_next, out var picture) && picture.Complete)
            {
                _delivered++;
                _deliver(_next, picture.Width, picture.Height, picture.Keyframe, picture.Data.AsSpan(0, picture.Total));
                Advance();
                continue;
            }

            var overdue = _newest > _next
                && (_newest - _next >= Lead
                    || (_overdueSince != 0 && Environment.TickCount64 - _overdueSince >= Reorder.TotalMilliseconds));
            if (!overdue)
            {
                return;
            }

            _lost++;
            var number = _next;
            Advance();
            _giveUp(number);
        }
    }

    private void Advance()
    {
        _pictures.Remove(_next);
        _next++;

        // The wait for this one starts now, however long the one before it took.
        _overdueSince = _newest > _next ? Environment.TickCount64 : 0;
    }

    /// <summary>
    /// Moves things along without a datagram to move them. Everything else here happens because one arrived, which
    /// is enough while pictures keep coming — but a picture lost just as the screen stops changing would otherwise
    /// hold the one after it, the one that shows what the screen settled on, until the screen moved again.
    /// </summary>
    public void Tick() => Flush();

    /// <summary>
    /// The picture this datagram belongs to, made if it is the first piece of it to arrive. A datagram that
    /// disagrees with the others about the picture's size starts it again: that is what the first piece of a
    /// picture whose number has come round again looks like.
    /// </summary>
    private Picture Taken(in VideoDatagram.Header header)
    {
        if (_pictures.TryGetValue(header.FrameNo, out var picture)
            && picture.Total == header.Total
            && picture.Count == header.Count)
        {
            picture.Keyframe |= header.Keyframe;
            return picture;
        }

        picture = new Picture
        {
            Data = new byte[header.Total],
            Have = new bool[header.Count],
            Total = header.Total,
            Count = header.Count,
            Width = header.Width,
            Height = header.Height,
            Keyframe = header.Keyframe,
        };

        _pictures[header.FrameNo] = picture;
        return picture;
    }

    private sealed class Picture
    {
        public byte[] Data = [];
        public bool[] Have = [];
        public byte[]? Parity;
        public bool HasParity;
        public int Total;
        public int Count;
        public int Received;
        public int Width;
        public int Height;
        public bool Keyframe;

        public bool Complete => Received == Count;
    }
}
