using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinMirroring.Screen.Capture;
using Size = System.Drawing.Size;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Turns a decoded NV12 picture that is still on the graphics card into the BGRA the window paints, on the card.
/// The mirror of <see cref="GpuNv12Converter"/>, for the viewer's side of the same stream.
///
/// What this replaces is the reason a tablet showed twenty-one frames a second out of thirty: the picture was
/// decoded into main memory, walked pixel by pixel by the processor to turn NV12 into BGRA, and only then handed to
/// the window — about twenty-two megabytes of memory traffic for every 1080p frame, and it measured at forty-two
/// milliseconds a frame, which is ninety-two per cent of the round trip the source was waiting on. Here the card
/// converts and scales nothing it does not have to, and only the finished picture crosses into memory, once.
/// </summary>
internal sealed class GpuFrameReader : IDisposable
{
    /// <summary>
    /// How to tell the card what the decoded picture is and what to make of it. Drivers read these settings loosely
    /// and disagree about what the names mean — "full" on one card is studio swing on another — so the only safe
    /// answer is the one measured on this machine, which is what <see cref="Probe"/> does. What it is measured
    /// against is <see cref="Nv12Converter"/>: the processor's conversion is the convention both ends of a stream
    /// have to agree on, or a picture would change its brightness the moment either side changed path.
    /// </summary>
    private enum Reading
    {
        /// <summary>Named colour spaces, saying the picture's levels run the whole way (ID3D11VideoContext1).</summary>
        NamedFull,

        /// <summary>The same, saying its levels are the narrower broadcast ones.</summary>
        NamedStudio,

        /// <summary>The older pair of descriptions, with the narrower levels.</summary>
        LegacyStudio,

        /// <summary>...and with the full ones.</summary>
        LegacyFull,
    }

    private static readonly object ProbeGate = new();
    private static Reading? _reading;
    private static bool _probed;

    private readonly GraphicsDevice _graphics;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly Reading _mode;
    private readonly ILogger _logger;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _target;
    private ID3D11Texture2D? _staging;
    private Size _inputSize;
    private Size _outputSize;
    private bool _disposed;

    private GpuFrameReader(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, Reading mode, ILogger logger)
    {
        _graphics = graphics;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _mode = mode;
        _logger = logger;
    }

    /// <summary>The device the decoder must hand its pictures to for this reader to be able to read them.</summary>
    public ID3D11Device Device => _graphics.Device;

    /// <summary>
    /// Null when this machine's card cannot convert, or does not produce the colours the sending side meant — the
    /// caller then decodes into memory and converts with the processor, exactly as before. The colour check is the
    /// same measurement the sending side makes, because the two have to agree on one convention or a stream would
    /// change its brightness the moment this path took over.
    /// </summary>
    public static GpuFrameReader? TryCreate(ILogger logger)
    {
        // Its own device, not the one capture and the encoder work on: a context serves one thread at a time, and
        // on a machine that is sending one screen while watching another the two would wait on each other.
        if (GraphicsDevice.CreateOwn() is not { } graphics)
        {
            return null;
        }

        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        try
        {
            videoDevice = graphics.Device.QueryInterface<ID3D11VideoDevice>();
            videoContext = graphics.Context.QueryInterface<ID3D11VideoContext>();
            if (Resolve(graphics, videoDevice, videoContext, logger) is { } mode)
            {
                return new GpuFrameReader(graphics, videoDevice, videoContext, mode, logger);
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidCastException)
        {
            logger.LogInformation("The graphics card cannot turn decoded pictures into pixels ({Message}); the processor will do it", ex.Message);
        }

        videoDevice?.Dispose();
        videoContext?.Dispose();
        graphics.DisposeIfOwned();
        return null;
    }

    /// <summary>
    /// Converts the <paramref name="width"/>×<paramref name="height"/> top-left part of a decoded picture into
    /// <paramref name="bgra"/>. The decoder's texture is the coded size — a multiple of sixteen, so 1088 rows for a
    /// 1080-line screen — and one slice of a pool it hands out in turn, which is what <paramref name="arraySlice"/>
    /// picks. False means the card would not do it and the caller must fall back.
    /// </summary>
    public bool TryRead(ID3D11Texture2D picture, int arraySlice, int width, int height, byte[] bgra)
    {
        if (_disposed || width < 1 || height < 1 || bgra.Length < width * height * 4)
        {
            return false;
        }

        var description = picture.Description;
        var inputSize = new Size((int)description.Width, (int)description.Height);
        var outputSize = new Size(width, height);

        lock (_graphics.ContextGate)
        {
            try
            {
                Prepare(inputSize, outputSize);
                if (!Blit(picture, arraySlice, outputSize))
                {
                    return false;
                }

                Read(outputSize, bgra);
                return true;
            }
            catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                _logger.LogWarning("The graphics card could not turn a decoded picture into pixels ({Message}); the processor takes over", ex.Message);
                return false;
            }
        }
    }

    /// <summary>The processor and both textures are built for one pair of sizes; a new picture size rebuilds them.</summary>
    private void Prepare(Size inputSize, Size outputSize)
    {
        if (_processor is not null && _inputSize == inputSize && _outputSize == outputSize)
        {
            return;
        }

        Release();
        (_enumerator, _processor) = CreateProcessor(_videoDevice, inputSize, outputSize);
        Apply(_videoContext, _processor, _mode);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        _target = _graphics.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)outputSize.Width, (uint)outputSize.Height, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
        _staging = _graphics.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)outputSize.Width, (uint)outputSize.Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        _inputSize = inputSize;
        _outputSize = outputSize;
    }

    private bool Blit(ID3D11Texture2D picture, int arraySlice, Size outputSize)
    {
        // Taken from the picture, and never more of it than there is: for a frame or two after a stream changes
        // size the decoder is still handing out textures of the old one, and asking for rows a texture does not
        // have would fail the whole conversion over a picture that is merely in transit. The card scales those
        // few frames by the little the two sizes differ, which is not something anyone sees.
        var source = new Size(Math.Min(outputSize.Width, _inputSize.Width), Math.Min(outputSize.Height, _inputSize.Height));
        using var input = _videoDevice.CreateVideoProcessorInputView(
            picture,
            _enumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)Math.Max(arraySlice, 0) },
            });
        using var output = _videoDevice.CreateVideoProcessorOutputView(
            _target!,
            _enumerator!,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        // Only the picture, not the rows the coded size rounds it up to.
        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, source.Width, source.Height));
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RawRect(0, 0, outputSize.Width, outputSize.Height));
        return _videoContext.VideoProcessorBlt(_processor!, output, 0, [new VideoProcessorStream { Enable = true, InputSurface = input }]).Success;
    }

    /// <summary>The one crossing into memory: the finished picture, row by row where the card's rows are padded.</summary>
    private void Read(Size size, byte[] bgra)
    {
        _graphics.Context.CopyResource(_staging!, _target!);
        var map = _graphics.Context.Map(_staging!, 0, MapMode.Read);
        try
        {
            var stride = size.Width * 4;
            if (map.RowPitch == (uint)stride)
            {
                Marshal.Copy(map.DataPointer, bgra, 0, stride * size.Height);
                return;
            }

            for (var y = 0; y < size.Height; y++)
            {
                Marshal.Copy(map.DataPointer + (nint)((long)y * map.RowPitch), bgra, y * stride, stride);
            }
        }
        finally
        {
            _graphics.Context.Unmap(_staging!, 0);
        }
    }

    private static (ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor) CreateProcessor(ID3D11VideoDevice videoDevice, Size inputSize, Size outputSize)
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inputSize.Width,
            InputHeight = (uint)inputSize.Height,
            InputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outputSize.Width,
            OutputHeight = (uint)outputSize.Height,
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };

        var enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
        return (enumerator, videoDevice.CreateVideoProcessor(enumerator, 0));
    }

    /// <summary>Tells the card what the picture it is given is, and that it must come out as ordinary full-range RGB.</summary>
    private static void Apply(ID3D11VideoContext videoContext, ID3D11VideoProcessor processor, Reading mode)
    {
        if (mode is Reading.NamedFull or Reading.NamedStudio)
        {
            using var named = videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>()
                ?? throw new InvalidOperationException("ID3D11VideoContext1 disappeared after it was measured.");
            named.VideoProcessorSetStreamColorSpace1(
                processor,
                0,
                mode == Reading.NamedFull ? ColorSpaceType.YcbcrFullG22LeftP601 : ColorSpaceType.YcbcrStudioG22LeftP601);
            named.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
            return;
        }

        // YCbCr_Matrix 0 is BT.601. Nominal_Range 1 is the card's name for the narrower broadcast levels and 2 for
        // the full ones; RGB_Range 0 is full-range RGB, which is what a window paints.
        var levels = mode == Reading.LegacyStudio ? 1u : 2u;
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 0, Nominal_Range = levels });
        videoContext.VideoProcessorSetOutputColorSpace(processor, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 0, Nominal_Range = levels });
    }

    private static Reading? Resolve(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ILogger logger)
    {
        lock (ProbeGate)
        {
            if (_probed)
            {
                return _reading;
            }

            _probed = true;
            _reading = Probe(graphics, videoDevice, videoContext, logger);
            return _reading;
        }
    }

    /// <summary>
    /// Converts one known picture every way the card offers and compares each with what the processor makes of the
    /// same picture. Whichever matches is used from then on; if none does, the whole path is refused and the
    /// processor keeps converting — a slower picture is better than a discoloured one.
    /// </summary>
    private static Reading? Probe(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ILogger logger)
    {
        const int size = 128;
        const int band = 16;

        var nv12 = TestPicture(size, band);
        var expected = new byte[size * size * 4];
        Nv12Converter.Nv12ToBgra(nv12, size, size, expected);

        ID3D11Texture2D? source = null;
        ID3D11Texture2D? staging = null;
        try
        {
            lock (graphics.ContextGate)
            {
                source = CreateSource(graphics, videoDevice, size, logger);
                if (source is null)
                {
                    logger.LogInformation("The graphics card will not hold a decoded picture the way it would have to be read; the processor keeps converting");
                    return null;
                }

                staging = graphics.Device.CreateTexture2D(new Texture2DDescription(
                    Format.NV12, (uint)size, (uint)size, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
                UploadNv12(graphics, staging, source, nv12, size);

                var hasNamed = videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>() is { } named && Discard(named);
                foreach (var mode in new[] { Reading.NamedStudio, Reading.NamedFull, Reading.LegacyStudio, Reading.LegacyFull })
                {
                    if (mode is Reading.NamedFull or Reading.NamedStudio && !hasNamed)
                    {
                        continue;
                    }

                    if (Produces(graphics, videoDevice, videoContext, mode, source, expected, size, band, out var worst))
                    {
                        logger.LogInformation("The graphics card turns decoded pictures into the colours the sending side meant ({Mode})", mode);
                        return mode;
                    }

                    logger.LogDebug("The graphics card's {Mode} reading of a decoded picture is off by {Worst}", mode, worst);
                }
            }

            logger.LogInformation("The graphics card does not produce the colours the sending side meant; the processor keeps converting");
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            logger.LogInformation("The graphics card could not be checked for colour conversion ({Message}); the processor keeps converting", ex.Message);
        }
        finally
        {
            staging?.Dispose();
            source?.Dispose();
        }

        return null;
    }

    /// <summary>
    /// A texture to stand in for the decoder's own, which is the thing the measurement has to be made on. A card
    /// will only let a picture be read as video when it was made for that, and which way of asking for one it
    /// accepts differs between drivers — the decoder's own textures are made for its decoder, which is not a thing
    /// that can be asked for here — so each way is tried until one both holds a picture and can be read as video.
    /// </summary>
    private static ID3D11Texture2D? CreateSource(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, int size, ILogger logger)
    {
        foreach (var bind in new[] { BindFlags.Decoder, BindFlags.ShaderResource, BindFlags.ShaderResource | BindFlags.RenderTarget, BindFlags.RenderTarget })
        {
            ID3D11Texture2D? texture = null;
            try
            {
                texture = graphics.Device.CreateTexture2D(new Texture2DDescription(
                    Format.NV12, (uint)size, (uint)size, 1, 1,
                    bind, ResourceUsage.Default, CpuAccessFlags.None));

                // Made, but is it readable as video? That is a separate answer, and the one that matters.
                using var enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputWidth = (uint)size,
                    InputHeight = (uint)size,
                    InputFrameRate = new Rational(60, 1),
                    OutputWidth = (uint)size,
                    OutputHeight = (uint)size,
                    OutputFrameRate = new Rational(60, 1),
                    Usage = VideoUsage.PlaybackNormal,
                });
                using var view = videoDevice.CreateVideoProcessorInputView(
                    texture,
                    enumerator,
                    new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D });

                logger.LogDebug("Colour check: a decoded picture is held as {Bind}", bind);
                return texture;
            }
            catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
            {
                logger.LogDebug("Colour check: {Bind} will not do ({Message})", bind, ex.Message);
                texture?.Dispose();
            }
        }

        return null;
    }

    private static bool Discard(ID3D11VideoContext1 named)
    {
        named.Dispose();
        return true;
    }

    private static bool Produces(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, Reading mode, ID3D11Texture2D source, byte[] expected, int size, int band, out int worst)
    {
        worst = int.MaxValue;
        var (enumerator, processor) = CreateProcessor(videoDevice, new Size(size, size), new Size(size, size));
        ID3D11Texture2D? target = null;
        ID3D11Texture2D? staging = null;
        try
        {
            Apply(videoContext, processor, mode);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            target = graphics.Device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)size, (uint)size, 1, 1,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
            staging = graphics.Device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)size, (uint)size, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

            using (var input = videoDevice.CreateVideoProcessorInputView(
                source,
                enumerator,
                new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D }))
            {
                using var output = videoDevice.CreateVideoProcessorOutputView(
                    target,
                    enumerator,
                    new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
                videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, size, size));
                videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, size, size));
                if (!videoContext.VideoProcessorBlt(processor, output, 0, [new VideoProcessorStream { Enable = true, InputSurface = input }]).Success)
                {
                    return false;
                }
            }

            var produced = new byte[size * size * 4];
            graphics.Context.CopyResource(staging, target);
            var map = graphics.Context.Map(staging, 0, MapMode.Read);
            try
            {
                for (var y = 0; y < size; y++)
                {
                    Marshal.Copy(map.DataPointer + (nint)((long)y * map.RowPitch), produced, y * size * 4, size * 4);
                }
            }
            finally
            {
                graphics.Context.Unmap(staging, 0);
            }

            // Only the middle row of each band: at a band's edge any chroma filtering of the card's own shows up,
            // and that difference is not the one being looked for.
            worst = 0;
            for (var y = band / 2; y < size; y += band)
            {
                for (var x = 8; x < size; x += 16)
                {
                    var i = ((y * size) + x) * 4;
                    worst = Math.Max(worst, Math.Abs(produced[i] - expected[i]));
                    worst = Math.Max(worst, Math.Abs(produced[i + 1] - expected[i + 1]));
                    worst = Math.Max(worst, Math.Abs(produced[i + 2] - expected[i + 2]));
                }
            }

            // Rounding differs by a unit or two; wrong levels or a wrong matrix are off by tens.
            return worst <= 6;
        }
        finally
        {
            staging?.Dispose();
            target?.Dispose();
            processor.Dispose();
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Bands of saturated colour, white, grey and near-black, as the sending side would have encoded them: wrong
    /// levels show up at the light and dark ends, a wrong matrix in the colours.
    /// </summary>
    private static byte[] TestPicture(int size, int band)
    {
        ReadOnlySpan<(byte B, byte G, byte R)> colours =
            [(0, 0, 0), (128, 128, 128), (250, 250, 250), (40, 200, 40), (40, 40, 230), (230, 60, 40), (30, 210, 220), (10, 10, 10)];
        var frame = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var colour = colours[(y / band) % colours.Length];
            for (var x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 4;
                frame[i] = colour.B;
                frame[i + 1] = colour.G;
                frame[i + 2] = colour.R;
                frame[i + 3] = 255;
            }
        }

        var nv12 = new byte[Nv12Converter.Nv12Size(size, size)];
        Nv12Converter.BgraCropToNv12(frame, size, new System.Drawing.Rectangle(0, 0, size, size), nv12);
        return nv12;
    }

    /// <summary>
    /// Puts an NV12 picture on the card: written through a staging texture a row at a time, because the card pads
    /// its rows, and the chroma plane follows the whole luma plane at that same padded pitch.
    /// </summary>
    private static void UploadNv12(GraphicsDevice graphics, ID3D11Texture2D staging, ID3D11Texture2D target, byte[] nv12, int size)
    {
        var map = graphics.Context.Map(staging, 0, MapMode.Write);
        try
        {
            for (var y = 0; y < size; y++)
            {
                Marshal.Copy(nv12, y * size, map.DataPointer + (nint)((long)y * map.RowPitch), size);
            }

            var chroma = map.DataPointer + (nint)((long)map.RowPitch * size);
            for (var y = 0; y < size / 2; y++)
            {
                Marshal.Copy(nv12, (size * size) + (y * size), chroma + (nint)((long)y * map.RowPitch), size);
            }
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }

        graphics.Context.CopyResource(target, staging);
    }

    private void Release()
    {
        _staging?.Dispose();
        _target?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _staging = null;
        _target = null;
        _processor = null;
        _enumerator = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_graphics.ContextGate)
        {
            Release();
        }

        _videoContext.Dispose();
        _videoDevice.Dispose();
        _graphics.DisposeIfOwned();
    }
}
