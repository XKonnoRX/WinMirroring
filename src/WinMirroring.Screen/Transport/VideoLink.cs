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
/// </summary>
internal sealed class VideoLink : IDisposable
{
    /// <summary>Media datagram kinds for the screen; sound is 1 (see AudioFormat.MediaKind).</summary>
    public const byte FrameKind = 2;
    public const byte ProbeKind = 3;

    /// <summary>How often a probe goes out while the viewer has not yet said the path works...</summary>
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(120);

    /// <summary>...and how long that is worth trying before the path is written off for this stream.</summary>
    private static readonly TimeSpan ProbeFor = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pictures sent by UDP with nothing acknowledged for this long means the path has stopped working — a network
    /// that changed under the stream, a tunnel that came up. The stream goes back to the connection.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeGivingUp = TimeSpan.FromSeconds(1);

    private readonly MirroringNode _node;
    private readonly PeerSession _session;
    private readonly uint _streamId;
    private readonly ILogger _logger;
    private readonly MediaCipher _cipher;
    private readonly byte[] _plain = new byte[VideoDatagram.HeaderSize + VideoDatagram.PayloadSize];
    private readonly byte[] _datagram = new byte[VideoDatagram.SafeDatagramBytes];
    private readonly long _startedTicks = Environment.TickCount64;

    private ulong _sequence;
    private long _probedTicks;
    private long _sentTicks;
    private long _datagrams;
    private bool _disposed;

    // Written by the stream's send loop, read by it; the two below are written from wherever a message arrives.
    private volatile bool _writtenOff;
    private volatile bool _confirmed;
    private long _ackedTicks;

    private VideoLink(MirroringNode node, PeerSession session, uint streamId, byte[] key, ILogger logger)
    {
        _node = node;
        _session = session;
        _streamId = streamId;
        _logger = logger;
        Key = Convert.ToBase64String(key);
        _cipher = new MediaCipher(key);
    }

    /// <summary>The key the viewer needs to open these datagrams, handed to it inside the session's TLS.</summary>
    public string Key { get; }

    /// <summary>True once the viewer has confirmed the path and the stream's pictures are going out on it.</summary>
    public bool Carries => _confirmed && !_writtenOff;

    public long Datagrams => Interlocked.Read(ref _datagrams);

    /// <summary>Null when this node has no media socket at all; the stream then stays on the connection.</summary>
    public static VideoLink? TryCreate(MirroringNode node, PeerSession session, uint streamId, ILogger logger)
    {
        if (node.MediaPort == 0)
        {
            return null;
        }

        var link = new VideoLink(node, session, streamId, MediaCipher.GenerateKey(), logger);
        node.PrioritizeMedia(session.PeerTransportEndPoint);
        return link;
    }

    /// <summary>The viewer says it is receiving the probes: from here on the pictures go by UDP.</summary>
    public void Confirm()
    {
        if (_confirmed || _writtenOff)
        {
            return;
        }

        Volatile.Write(ref _ackedTicks, Environment.TickCount64);
        _confirmed = true;
        _logger.LogInformation(
            "Pictures for stream {Stream} now go to {Peer} by UDP; a lost one no longer holds back the next",
            _streamId,
            _session.PeerName);
    }

    /// <summary>A frame came back, which is also the evidence that this path is still working.</summary>
    public void Acknowledged() => Volatile.Write(ref _ackedTicks, Environment.TickCount64);

    /// <summary>
    /// Sends a probe if one is due. Probes carry nothing: they exist so the viewer can say, on the connection, that
    /// datagrams from here reach it. Giving up on them is how a stream on a network that blocks UDP stays whole.
    /// </summary>
    public void ProbeIfDue()
    {
        var now = Environment.TickCount64;
        if (_confirmed || _writtenOff || now - _probedTicks < ProbeEvery.TotalMilliseconds)
        {
            return;
        }

        if (now - _startedTicks > ProbeFor.TotalMilliseconds)
        {
            _writtenOff = true;
            _logger.LogInformation(
                "{Peer} did not answer the UDP probes for stream {Stream}; its pictures stay on the connection",
                _session.PeerName,
                _streamId);
            return;
        }

        _probedTicks = now;
        Send(ProbeKind, []);
    }

    /// <summary>
    /// Cuts one encoded picture into datagrams and sends them, with a parity datagram behind them so that any one
    /// of them can be lost without the picture being. False means the path has stopped working and the caller must
    /// send this picture on the connection instead.
    /// </summary>
    public bool TrySend(uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> picture)
    {
        if (!Carries || picture.Length == 0 || picture.Length > VideoDatagram.MaxPictureBytes)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (_sentTicks != 0 && now - Volatile.Read(ref _ackedTicks) > SilenceBeforeGivingUp.TotalMilliseconds)
        {
            _writtenOff = true;
            _logger.LogWarning(
                "Nothing has come back from {Peer} for a second of UDP pictures (stream {Stream}); they go back on the connection",
                _session.PeerName,
                _streamId);
            return false;
        }

        if (VideoDatagram.Fragments(picture.Length) > VideoDatagram.MaxFragments)
        {
            return false;
        }

        var pieces = VideoDatagram.Pieces(picture.Length);
        for (var piece = 0; piece < pieces; piece++)
        {
            var length = VideoDatagram.Write(_plain, frameNo, width, height, keyframe, picture, piece);
            Send(FrameKind, _plain.AsSpan(0, length));
        }

        _sentTicks = now;
        return true;
    }

    private void Send(byte kind, ReadOnlySpan<byte> plaintext)
    {
        if (_disposed)
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
        _node.ReleaseMediaPriority(_session.PeerTransportEndPoint);
        _cipher.Dispose();
    }
}
