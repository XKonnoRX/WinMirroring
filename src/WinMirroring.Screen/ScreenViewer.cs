using System.Drawing;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen;

/// <summary>Changed part of the shared image after one frame message; the UI copies these rectangles.</summary>
public sealed record FrameUpdate(uint FrameNo, int Width, int Height, byte[] Image, IReadOnlyList<Rectangle> Dirty, bool Resized);

/// <summary>
/// Viewer side of one stream, independent of the UI. Decodes frames into a persistent BGRA image and hands each
/// update to the UI; only after the UI reports it presented the update is the frame acknowledged, so the source
/// never runs ahead of what is actually shown. Pointer, keyboard and crop requests go back in order.
/// </summary>
public sealed class ScreenViewer : IAsyncDisposable
{
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TileDecoder _decoder = new();
    private readonly CancellationTokenSource _cts = new();
    private Action<FrameUpdate>? _present;
    private TaskCompletionSource? _presented;
    private H264VideoDecoder? _videoDecoder;
    private bool _videoDecodeUnavailable;
    private byte[] _image = [];
    private int _width;
    private int _height;
    private int _closed;

    internal ScreenViewer(PeerSession session, ScreenStreamMessage info, ILogger logger)
    {
        Session = session;
        Info = info;
        _logger = logger;
        _ = Task.Run(DecodeLoopAsync);
        _ = Task.Run(SendLoopAsync);
    }

    public PeerSession Session { get; }

    public uint StreamId => Info.StreamId;

    public ScreenStreamMessage Info { get; private set; }

    public string PeerName => Session.PeerName;

    public event Action<ScreenViewer>? InfoChanged;

    /// <summary>The stream ended (closed here or by the source); the reason is for display.</summary>
    public event Action<ScreenViewer, string?>? Closed;

    /// <summary>Starts decoding; <paramref name="present"/> runs on a worker thread and must call <see cref="FramePresented"/>.</summary>
    public void Attach(Action<FrameUpdate> present)
    {
        _present = present;
        _attached.TrySetResult();
    }

    public void FramePresented() => _presented?.TrySetResult();

    public void SendPointer(PointerAction action, MouseButton button, float x, float y, int wheel = 0) =>
        _outbox.Writer.TryWrite(ScreenCodec.EncodePointer(StreamId, action, button, x, y, wheel));

    /// <summary>Touch contact: action 1 down, 2 move, 3 up, 4 cancel; <paramref name="contact"/> 0–9.</summary>
    public void SendTouch(byte action, byte contact, float x, float y) =>
        _outbox.Writer.TryWrite(ScreenCodec.EncodeTouch(StreamId, action, contact, x, y));

    public void SendKey(ushort virtualKey, ushort scanCode, bool up, bool extended) =>
        _outbox.Writer.TryWrite(ScreenCodec.EncodeKey(StreamId, virtualKey, scanCode, up, extended));

    /// <summary>Characters that came without a usable key (touch keyboard, IME, emoji).</summary>
    public void SendText(string text)
    {
        if (text.Length > 0)
        {
            _outbox.Writer.TryWrite(ScreenCodec.EncodeText(StreamId, text));
        }
    }

    /// <summary>Stream only this normalized part of the source image (the full image is 0,0,1,1).</summary>
    public void SetCrop(RectangleF crop) => _outbox.Writer.TryWrite(ScreenCodec.EncodeCrop(StreamId, ScreenCodec.Normalize(crop)));

    public void SwitchMonitor(int index, string? key = null) => _ = SendControlAsync(new ScreenRequestMessage(StreamId, index, key));

    /// <summary>Asks the source to act on its real window (restore it from minimised).</summary>
    public void SendWindowCommand(WindowCommand command) => _outbox.Writer.TryWrite(ScreenCodec.EncodeWindowCommand(StreamId, command));

    public void Close() => _ = CloseAsync(notifySource: true, reason: null);

    internal void OnInfo(ScreenStreamMessage info)
    {
        Info = info;
        InfoChanged?.Invoke(this);
    }

    internal void Enqueue(byte[] message) => _frames.Writer.TryWrite(message);

    internal Task CloseAsync(bool notifySource, string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _frames.Writer.TryComplete();
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        _presented?.TrySetResult();
        Closed?.Invoke(this, reason);
        return notifySource ? SendControlAsync(new ScreenStopMessage(StreamId, "closed by viewer")) : Task.CompletedTask;
    }

    private async Task DecodeLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await _attached.Task.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var message in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ScreenCodec.TryReadHeader(message, out var type, out _) && type == ScreenMessageType.VideoFrame)
                {
                    await DecodeVideoAsync(message, ct).ConfigureAwait(false);
                    continue;
                }

                await DecodeTilesAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await CloseAsync(notifySource: false, reason: "соединение потеряно").ConfigureAwait(false);
        }
    }

    private async Task DecodeTilesAsync(byte[] message, CancellationToken ct)
    {
        var frame = ScreenCodec.TryDecodeFrame(message);
        if (frame is null || frame.Width == 0 || frame.Height == 0)
        {
            return;
        }

        var resized = frame.Width != _width || frame.Height != _height;
        if (resized)
        {
            _width = frame.Width;
            _height = frame.Height;
            _image = new byte[_width * _height * 4];
        }

        Parallel.ForEach(frame.Tiles, tile =>
        {
            try
            {
                _decoder.DecodeInto(tile, _image, _width, _height);
            }
            catch (Exception ex) when (ex is InvalidDataException or SharpGen.Runtime.SharpGenException)
            {
                _logger.LogDebug("Bad tile at {X},{Y}: {Message}", tile.X, tile.Y, ex.Message);
            }
        });

        var dirty = frame.Tiles.Select(t => new Rectangle(t.X, t.Y, t.Width, t.Height)).ToList();
        await PresentAndAckAsync(frame.FrameNo, _width, _height, dirty, resized, ct).ConfigureAwait(false);
    }

    private async Task DecodeVideoAsync(byte[] message, CancellationToken ct)
    {
        var frame = ScreenCodec.TryDecodeVideoFrame(message);
        if (frame is null || frame.Width == 0 || frame.Height == 0)
        {
            return;
        }

        if (_videoDecoder is null && !_videoDecodeUnavailable)
        {
            _videoDecoder = H264VideoDecoder.TryCreate(frame.Width, frame.Height, _logger);
            if (_videoDecoder is null)
            {
                _videoDecodeUnavailable = true;
                _logger.LogWarning("No H.264 decoder available for the stream from {Peer}", PeerName);
                RequestTiles("на этом устройстве нет декодера H.264");
            }
        }

        var produced = false;
        var resized = false;
        if (_videoDecoder is not null)
        {
            try
            {
                produced = _videoDecoder.DecodeInto(frame.Data, ref _image, out var width, out var height);
                if (produced)
                {
                    resized = width != _width || height != _height;
                    _width = width;
                    _height = height;
                }
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _logger.LogDebug("H.264 decode failed: {Message}", ex.Message);
            }
        }

        if (!produced)
        {
            // Video keeps arriving but nothing comes out: ask for tiles rather than show an empty view.
            _videoSilentSince ??= Environment.TickCount64;
            if (Environment.TickCount64 - _videoSilentSince > VideoSilenceBeforeFallbackMs)
            {
                RequestTiles("видео H.264 не декодируется");
            }

            // Still acknowledge so the source's in-flight window does not stall while the decoder primes.
            await AckAsync(frame.FrameNo, ct).ConfigureAwait(false);
            return;
        }

        _videoSilentSince = null;
        var dirty = new List<Rectangle> { new(0, 0, _width, _height) };
        await PresentAndAckAsync(frame.FrameNo, _width, _height, dirty, resized, ct).ConfigureAwait(false);
    }

    private async Task PresentAndAckAsync(uint frameNo, int width, int height, IReadOnlyList<Rectangle> dirty, bool resized, CancellationToken ct)
    {
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _presented = presented;
        _present?.Invoke(new FrameUpdate(frameNo, width, height, _image, dirty, resized));
        try
        {
            await presented.Task.WaitAsync(PresentTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A hidden or stuck window must not stall the source forever.
        }

        await AckAsync(frameNo, ct).ConfigureAwait(false);
    }

    private const long VideoSilenceBeforeFallbackMs = 3000;

    /// <summary>When video frames started arriving without a picture coming out, or null.</summary>
    private long? _videoSilentSince;
    private int _tilesRequested;

    /// <summary>Asks the source, once, to send JPEG tiles instead of H.264.</summary>
    private void RequestTiles(string reason)
    {
        if (Interlocked.Exchange(ref _tilesRequested, 1) != 0)
        {
            return;
        }

        _logger.LogWarning("Asking {Peer} for JPEG tiles instead of H.264: {Reason}", PeerName, reason);
        _ = SendControlAsync(new ScreenCodecFallbackMessage(StreamId, reason));
    }

    private Task AckAsync(uint frameNo, CancellationToken ct) =>
        Session.SendAsync(ChannelId.Screen, ScreenCodec.EncodeAck(StreamId, frameNo), ct).AsTask();

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await Session.SendAsync(ChannelId.Screen, message, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private async Task SendControlAsync(ControlMessage message)
    {
        try
        {
            await Session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Screen control message not sent: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(notifySource: true, reason: null).ConfigureAwait(false);
        _decoder.Dispose();
        _videoDecoder?.Dispose();
    }
}
