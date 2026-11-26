using System.Net;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Media;
using WinMirroring.Core.Peers;

namespace WinMirroring.Screen.Transport;

/// <summary>
/// The UDP path for one stream's pictures, and the decision of whether to use it at all.
///
/// A picture is only worth anything while it is current, so a late one is worth less than a fresh one. On the
/// session's one reliable connection a lost segment holds back everything behind it until it has been asked for and
/// sent again — a whole round trip during which the next picture, the one that should be on the screen by then, is
/// waiting its turn behind a picture nobody will ever want. That is the freeze this path exists to remove.
///
/// It is never assumed to work. UDP is blocked on plenty of networks and dropped by plenty of tunnels, so pictures
/// go on the connection as before until the viewer has said, on that connection, that it is receiving the probes
/// sent on this one. If it never says so the stream simply stays where it was, and if it stops arriving later the
/// stream goes back there: a slower picture beats a missing one every time.
///
/// Going back is not for good, though. It used to be, and a path lost to one bad second — or, as it turned out, to
/// one quiet one — stayed lost for as long as the view was open. Now the path rests for a while and is then probed
/// again, a little less eagerly each time it lets the stream down.
/// </summary>
internal sealed class VideoLink : IDisposable
{
    /// <summary>Media datagram kinds for the screen; sound is 1 (see AudioFormat.MediaKind).</summary>
    public const byte FrameKind = 2;
    public const byte ProbeKind = 3;

    private enum Path
    {
        /// <summary>Probes are going out; pictures stay on the connection until one is answered.</summary>
        Probing,

        /// <summary>The viewer confirmed the path and the pictures are on it.</summary>
        Carrying,

        /// <summary>The path did not answer, or stopped answering; it is left alone until <see cref="_restUntil"/>.</summary>
        Resting,
    }

    /// <summary>How often a probe goes out while the viewer has not yet said the path works...</summary>
    internal static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// ...and how many of them are worth sending before the path is left to rest. A count rather than a stretch of
    /// time, because probes only go out alongside pictures: on a screen that has stopped changing two seconds can
    /// pass with one probe sent, and one probe is not an answer.
    /// </summary>
    internal const int ProbesBeforeResting = 16;

    /// <summary>
    /// Pictures sent by UDP with nothing acknowledged for this long means the path has stopped working — a network
    /// that changed under the stream, a tunnel that came up. The stream goes back to the connection.
    ///
    /// Measured from the first picture that has not been answered, and only once several have gone unanswered. It
    /// used to be measured from the last acknowledgement, whenever that was — so a screen that had simply stood
    /// still for a second was read as a path that had gone quiet, and the first picture after any pause sent the
    /// stream back to the connection for the rest of the view. Every view did that within a minute or two.
    /// </summary>
    internal static readonly TimeSpan SilenceBeforeGivingUp = TimeSpan.FromSeconds(1);

    /// <summary>One picture unanswered proves nothing: a single one lost at the end of a burst looks the same.</summary>
    internal const int UnansweredBeforeGivingUp = 3;

    /// <summary>How long the path is left alone the first time it lets the stream down, and the most it ever is.</summary>
    internal static readonly TimeSpan FirstRest = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LongestRest = TimeSpan.FromMinutes(2);

    /// <summary>A path that carried pictures for this long before failing is a good path that had a bad moment.</summary>
    internal static readonly TimeSpan ProvenAfter = TimeSpan.FromSeconds(60);

    private readonly MirroringNode? _node;
    private readonly PeerSession? _session;
    private readonly uint _streamId;
    private readonly ILogger _logger;
    private readonly MediaCipher _cipher;
    private readonly Func<long> _now;
    private readonly string _peer;
    private readonly byte[] _plain = new byte[VideoDatagram.HeaderSize + VideoDatagram.PayloadSize];
    private readonly byte[] _datagram = new byte[VideoDatagram.SafeDatagramBytes];
    private readonly object _gate = new();

    private ulong _sequence;
    private long _datagrams;
    private bool _disposed;

    // Everything below is read and written under _gate: the stream's send loop probes and sends, and the
    // acknowledgements and the viewer's confirmation arrive from wherever messages arrive.
    private Path _path = Path.Probing;
    private long _probedTicks = long.MinValue / 2;
    private int _probes;
    private long _restUntil;
    private long _restMs = (long)FirstRest.TotalMilliseconds;
    private long _confirmedTicks;
    private long _waitingSince;
    private int _unanswered;
    private bool _everRested;

    private VideoLink(MirroringNode? node, PeerSession? session, uint streamId, byte[] key, ILogger logger, Func<long>? now)
    {
        _node = node;
        _session = session;
        _streamId = streamId;
        _logger = logger;
        _now = now ?? (() => Environment.TickCount64);
        _peer = session?.PeerName ?? "the viewer";
        Key = Convert.ToBase64String(key);
        _cipher = new MediaCipher(key);
    }

    /// <summary>The key the viewer needs to open these datagrams, handed to it inside the session's TLS.</summary>
    public string Key { get; }

    /// <summary>True once the viewer has confirmed the path and the stream's pictures are going out on it.</summary>
    public bool Carries
    {
        get
        {
            lock (_gate)
            {
                return _path == Path.Carrying;
            }
        }
    }

    public long Datagrams => Interlocked.Read(ref _datagrams);

    /// <summary>Null when this node has no media socket at all; the stream then stays on the connection.</summary>
    public static VideoLink? TryCreate(MirroringNode node, PeerSession session, uint streamId, ILogger logger)
    {
        if (node.MediaPort == 0)
        {
            return null;
        }

        var link = new VideoLink(node, session, streamId, MediaCipher.GenerateKey(), logger, null);
        node.PrioritizeMedia(session.PeerTransportEndPoint);
        return link;
    }

    /// <summary>A link that sends nowhere and reads the given clock: the decisions, without a network to make them on.</summary>
    internal static VideoLink ForTest(ILogger logger, Func<long> now) =>
        new(null, null, 1, MediaCipher.GenerateKey(), logger, now);

    /// <summary>
    /// The viewer says it is receiving the probes: from here on the pictures go by UDP. True when that is news —
    /// the caller then makes the first picture on the path a whole one.
    /// </summary>
    public bool Confirm()
    {
        lock (_gate)
        {
            if (_path != Path.Probing)
            {
                return false; // an answer to probes this link has since stopped waiting for
            }

            _path = Path.Carrying;
            _confirmedTicks = _now();
            _waitingSince = 0;
            _unanswered = 0;
        }

        _logger.LogInformation(
            "Pictures for stream {Stream} now go to {Peer} by UDP; a lost one no longer holds back the next",
            _streamId,
            _peer);
        return true;
    }

    /// <summary>A frame came back, which is also the evidence that this path is still working.</summary>
    public void Acknowledged()
    {
        lock (_gate)
        {
            _waitingSince = 0;
            _unanswered = 0;
        }
    }

    /// <summary>
    /// Sends a probe if one is due. Probes carry nothing: they exist so the viewer can say, on the connection, that
    /// datagrams from here reach it. Giving up on them is how a stream on a network that blocks UDP stays whole.
    /// </summary>
    public void ProbeIfDue()
    {
        var now = _now();
        var gaveUp = false;
        var first = false;
        long rest = 0;
        lock (_gate)
        {
            if (_path == Path.Carrying)
            {
                return;
            }

            if (_path == Path.Resting)
            {
                if (now < _restUntil)
                {
                    return;
                }

                _path = Path.Probing;
                _probes = 0;
            }

            if (now - _probedTicks < ProbeEvery.TotalMilliseconds)
            {
                return;
            }

            if (_probes >= ProbesBeforeResting)
            {
                first = !_everRested;
                rest = RestLocked(now);
                gaveUp = true;
            }
            else
            {
                _probes++;
                _probedTicks = now;
            }
        }

        if (!gaveUp)
        {
            Send(ProbeKind, []);
            return;
        }

        // Said once at a level anyone reads. A network that blocks UDP will not answer the next round either, and
        // a line every couple of minutes for as long as a view is open says nothing the first one did not.
        _logger.Log(
            first ? LogLevel.Information : LogLevel.Debug,
            "{Peer} did not answer the UDP probes for stream {Stream}; its pictures stay on the connection and the path is tried again in {Seconds} s",
            _peer,
            _streamId,
            rest / 1000);
    }

    /// <summary>Leaves the path alone for a while, longer each time; returns for how long.</summary>
    private long RestLocked(long now)
    {
        var rest = _restMs;
        _path = Path.Resting;
        _restUntil = now + rest;
        _restMs = Math.Min(_restMs * 2, (long)LongestRest.TotalMilliseconds);
        _everRested = true;
        _waitingSince = 0;
        _unanswered = 0;
        return rest;
    }

    /// <summary>
    /// Cuts one encoded picture into datagrams and sends them, with a parity datagram behind them so that any one
    /// of them can be lost without the picture being. False means the path is not carrying — it never was, or has
    /// just stopped working — and the caller must send this picture on the connection instead.
    /// </summary>
    public bool TrySend(uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> picture)
    {
        if (picture.Length == 0
            || picture.Length > VideoDatagram.MaxPictureBytes
            || VideoDatagram.Fragments(picture.Length) > VideoDatagram.MaxFragments)
        {
            return false;
        }

        var now = _now();
        long rest = -1;
        lock (_gate)
        {
            if (_path != Path.Carrying)
            {
                return false;
            }

            if (_waitingSince != 0
                && _unanswered >= UnansweredBeforeGivingUp
                && now - _waitingSince > SilenceBeforeGivingUp.TotalMilliseconds)
            {
                if (now - _confirmedTicks > ProvenAfter.TotalMilliseconds)
                {
                    _restMs = (long)FirstRest.TotalMilliseconds;
                }

                rest = RestLocked(now);
            }
            else
            {
                if (_waitingSince == 0)
                {
                    _waitingSince = now;
                }

                _unanswered++;
            }
        }

        if (rest >= 0)
        {
            _logger.LogWarning(
                "Nothing has come back from {Peer} for a second of UDP pictures (stream {Stream}); they go back on the connection and the path is tried again in {Seconds} s",
                _peer,
                _streamId,
                rest / 1000);
            return false;
        }

        var pieces = VideoDatagram.Pieces(picture.Length);
        for (var piece = 0; piece < pieces; piece++)
        {
            var length = VideoDatagram.Write(_plain, frameNo, width, height, keyframe, picture, piece);
            Send(FrameKind, _plain.AsSpan(0, length));
        }

        return true;
    }

    private void Send(byte kind, ReadOnlySpan<byte> plaintext)
    {
        if (_disposed || _node is null || _session is null)
        {
            return;
        }

        var length = _cipher.Seal(kind, _streamId, _sequence++, plaintext, _datagram);
        if (_node.SendMedia(_session.PeerTransportEndPoint, _datagram.AsSpan(0, length)))
        {
            Interlocked.Increment(ref _datagrams);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_node is not null && _session is not null)
        {
            _node.ReleaseMediaPriority(_session.PeerTransportEndPoint);
        }

        _cipher.Dispose();
    }
}
