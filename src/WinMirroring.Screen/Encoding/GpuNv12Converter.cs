using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinMirroring.Screen.Capture;
using Size = System.Drawing.Size;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Turns a captured BGRA frame into the NV12 the encoder wants, on the graphics card, cropping on the way. The
/// processor used to do this for every frame — reading the whole picture back from the card, walking it pixel by
/// pixel and handing the result over as plain memory. Here the frame never leaves the card.
/// </summary>
internal sealed class GpuNv12Converter : IDisposable
{
    /// <summary>
    /// How many NV12 textures are used in turn. The encoder keeps reading a frame for a while after it is handed
    /// over, so writing the next one straight back into the same texture would tear the picture it is still working
    /// on. Going round a handful of them means a texture is only written again once the encoder is long done with it.
    /// </summary>
    private const int TargetCount = 4;

    private static readonly object ProbeGate = new();
    private static ColourMode? _colourMode;
    private static bool _probed;

    private readonly GraphicsDevice _graphics;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ColourMode _mode;
    private readonly ILogger _logger;
    private readonly ID3D11Texture2D?[] _targets = new ID3D11Texture2D?[TargetCount];

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private Size _inputSize;
    private Size _outputSize;
    private Size _targetSize;
    private int _targetIndex;
    private bool _disposed;

    private GpuNv12Converter(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ColourMode mode, ILogger logger)
    {
        _graphics = graphics;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _mode = mode;
        _logger = logger;
    }

    /// <summary>
    /// Which way of asking makes this driver produce the colours <see cref="Nv12Converter"/> produces. Drivers read
    /// these settings loosely and disagree about what the names mean, so the only safe answer is the one that was
    /// measured on this machine.
    /// </summary>
    private enum ColourMode
    {
        /// <summary>The named DXGI colour spaces, through ID3D11VideoContext1.</summary>
        Named,

        /// <summary>The older pair of descriptions on ID3D11VideoContext.</summary>
        Legacy,
    }

    /// <summary>
    /// Null when this machine's graphics card cannot do the conversion, or does not do it the way the receiving
    /// side expects; the caller then uses the processor, exactly as before.
    /// </summary>
    public static GpuNv12Converter? TryCreate(ILogger logger)
    {
        if (GraphicsDevice.Shared is not { } graphics)
        {
            return null;
        }

        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        try
        {
            videoDevice = graphics.Device.QueryInterface<ID3D11VideoDevice>();
            videoContext = graphics.Context.QueryInterface<ID3D11VideoContext>();
            if (ResolveColourMode(graphics, videoDevice, videoContext, logger) is { } mode)
            {
                return new GpuNv12Converter(graphics, videoDevice, videoContext, mode, logger);
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidCastException)
        {
            logger.LogInformation("The graphics card cannot convert colours itself ({Message}); the processor will do it", ex.Message);
        }

        videoDevice?.Dispose();
        videoContext?.Dispose();
        return null;
    }

    /// <summary>
    /// The next NV12 texture to convert into, which the encoder then reads straight from. Null when this size cannot
    /// be held on the card at all, and the caller falls back to the processor.
    /// </summary>
    public ID3D11Texture2D? NextTarget(int width, int height)
    {
        if (_disposed)
        {
            return null;
        }

        var size = new Size(width, height);
        if (_targetSize != size)
        {
            ReleaseTargets();
            _targetSize = size;
        }

        _targetIndex = (_targetIndex + 1) % TargetCount;
        if (_targets[_targetIndex] is { } ready)
        {
            return ready;
        }

        try
        {
            return _targets[_targetIndex] = CreateNv12Target(_graphics, width, height);
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            _logger.LogWarning("The graphics card would not hold a {Width}×{Height} frame ({Message}); the processor converts instead", width, height, ex.Message);
            return null;
        }
    }

    /// <summary>Copies <paramref name="crop"/> of the frame into <paramref name="target"/> as NV12.</summary>
    public bool TryConvert(ID3D11Texture2D source, Rectangle crop, ID3D11Texture2D target)
    {
        if (_disposed)
        {
            return false;
        }

        var description = target.Description;
        var outputSize = new Size((int)description.Width, (int)description.Height);
        var sourceDescription = source.Description;
        var inputSize = new Size((int)sourceDescription.Width, (int)sourceDescription.Height);

        lock (_graphics.ContextGate)
        {
            try
            {
                Prepare(inputSize, outputSize);
                return Blit(_videoDevice, _videoContext, _enumerator!, _processor!, source, crop, target);
            }
            catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                _logger.LogWarning("Converting a frame on the graphics card failed ({Message}); the processor takes over", ex.Message);
                return false;
            }
        }
    }

    /// <summary>The processor is built for one pair of sizes; a new crop or screen size rebuilds it.</summary>
    private void Prepare(Size inputSize, Size outputSize)
    {
        if (_processor is not null && _inputSize == inputSize && _outputSize == outputSize)
        {
            return;
        }

        _processor?.Dispose();
        _enumerator?.Dispose();
        _processor = null;
        _enumerator = null;

        (_enumerator, _processor) = CreateProcessor(_videoDevice, inputSize, outputSize);
        _inputSize = inputSize;
        _outputSize = outputSize;
        ApplyColourMode(_videoContext, _processor, _mode);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    private void ReleaseTargets()
    {
        for (var i = 0; i < _targets.Length; i++)
        {
            _targets[i]?.Dispose();
            _targets[i] = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseTargets();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
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

    private static ID3D11Texture2D CreateNv12Target(GraphicsDevice graphics, int width, int height) =>
        graphics.Device.CreateTexture2D(new Texture2DDescription
        {
            Format = Format.NV12,
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.RenderTarget,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    private static bool Blit(ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ID3D11VideoProcessorEnumerator enumerator, ID3D11VideoProcessor processor, ID3D11Texture2D source, Rectangle crop, ID3D11Texture2D target)
    {
        using var input = videoDevice.CreateVideoProcessorInputView(
            source,
            enumerator,
            new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        using var output = videoDevice.CreateVideoProcessorOutputView(
            target,
            enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(crop.Left, crop.Top, crop.Right, crop.Bottom));
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, crop.Width, crop.Height));
        var result = videoContext.VideoProcessorBlt(processor, output, 0, [new VideoProcessorStream { Enable = true, InputSurface = input }]);
        return result.Success;
    }

    /// <summary>
    /// Tells the card what it is given and what it must produce. The receiving side turns NV12 back into colours
    /// with the BT.601 studio-swing numbers in <see cref="Nv12Converter"/>, so anything else here would shift a
    /// stream's brightness and colour the moment this path took over from the processor.
    /// </summary>
    private static void ApplyColourMode(ID3D11VideoContext videoContext, ID3D11VideoProcessor processor, ColourMode mode)
    {
        if (mode == ColourMode.Named)
        {
            using var named = videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>()
                ?? throw new InvalidOperationException("ID3D11VideoContext1 disappeared after it was measured.");
            named.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            named.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.YcbcrFullG22LeftP601);
            return;
        }

        // RGB_Range 0 is full-range input, YCbCr_Matrix 0 is BT.601, Nominal_Range 2 is the card's name for the
        // levels that come out matching Nv12Converter. The names are not what they look like, which is exactly
        // why they are measured rather than trusted.
        var space = new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 0, Nominal_Range = 2 };
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, space);
        videoContext.VideoProcessorSetOutputColorSpace(processor, space);
    }

    private static ColourMode? ResolveColourMode(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ILogger logger)
    {
        lock (ProbeGate)
        {
            if (_probed)
            {
                return _colourMode;
            }

            _probed = true;
            _colourMode = Probe(graphics, videoDevice, videoContext, logger);
            return _colourMode;
        }
    }

    /// <summary>
    /// Converts one known picture on the card and compares it with what the processor makes of the same picture.
    /// Whichever way of asking matches is the one used from then on; if none does, the whole path is refused and
    /// streams keep being converted by the processor — a slower picture is better than a discoloured one.
    /// </summary>
    private static ColourMode? Probe(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ILogger logger)
    {
        const int size = 128;
        const int band = 16;
        var frame = TestFrame(size, band);
        var expected = new byte[Nv12Converter.Nv12Size(size, size)];
        Nv12Converter.BgraCropToNv12(frame, size, new Rectangle(0, 0, size, size), expected);

        ID3D11Texture2D? source = null;
        ID3D11Texture2D? target = null;
        try
        {
            lock (graphics.ContextGate)
            {
                source = graphics.Device.CreateTexture2D(new Texture2DDescription(
                    Format.B8G8R8A8_UNorm, size, size, 1, 1,
                    BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
                graphics.Context.UpdateSubresource(frame, source, 0, size * 4);
                target = CreateNv12Target(graphics, size, size);

                bool hasNamed;
                using (var probe = videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>())
                {
                    hasNamed = probe is not null;
                }

                foreach (var mode in new[] { ColourMode.Named, ColourMode.Legacy })
                {
                    if (mode == ColourMode.Named && !hasNamed)
                    {
                        continue;
                    }

                    if (Produces(graphics, videoDevice, videoContext, mode, source, target, expected, size, band, out var worst))
                    {
                        logger.LogInformation("The graphics card converts colours as the receiving side expects ({Mode}); frames will not be copied through memory", mode);
                        return mode;
                    }

                    logger.LogDebug("The graphics card's {Mode} colour handling is off by {Worst}", mode, worst);
                }
            }

            logger.LogInformation("The graphics card does not produce the colours the receiving side expects; the processor keeps converting");
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            logger.LogInformation("The graphics card could not be checked for colour conversion ({Message}); the processor keeps converting", ex.Message);
        }
        finally
        {
            source?.Dispose();
            target?.Dispose();
        }

        return null;
    }

    private static bool Produces(GraphicsDevice graphics, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ColourMode mode, ID3D11Texture2D source, ID3D11Texture2D target, byte[] expected, int size, int band, out int worst)
    {
        worst = int.MaxValue;
        var (enumerator, processor) = CreateProcessor(videoDevice, new Size(size, size), new Size(size, size));
        try
        {
            ApplyColourMode(videoContext, processor, mode);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            if (!Blit(videoDevice, videoContext, enumerator, processor, source, new Rectangle(0, 0, size, size), target))
            {
                return false;
            }

            var produced = ReadNv12(graphics, target, size);

            // Only the middle row of each band: at a band's edge any chroma filtering of the card's own shows up,
            // and that difference is not the one being looked for.
            worst = 0;
            for (var y = band / 2; y < size; y += band)
            {
                for (var x = 8; x < size; x += 16)
                {
                    var luma = y * size + x;
                    worst = Math.Max(worst, Math.Abs(produced[luma] - expected[luma]));
                    var chroma = size * size + y / 2 * size + (x & ~1);
                    worst = Math.Max(worst, Math.Abs(produced[chroma] - expected[chroma]));
                    worst = Math.Max(worst, Math.Abs(produced[chroma + 1] - expected[chroma + 1]));
                }
            }

            // Rounding differs by a unit or two; a wrong matrix or wrong levels are off by tens.
            return worst <= 6;
        }
        finally
        {
            processor.Dispose();
            enumerator.Dispose();
        }
    }

    /// <summary>Flat bands of saturated colour, black, white and grey: wrong levels or a wrong matrix stand out.</summary>
    private static byte[] TestFrame(int size, int band)
    {
        ReadOnlySpan<(byte B, byte G, byte R)> colours =
            [(0, 0, 0), (128, 128, 128), (250, 250, 250), (40, 200, 40), (40, 40, 230), (230, 60, 40), (30, 210, 220), (10, 10, 10)];
        var frame = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var colour = colours[(y / band) % colours.Length];
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                frame[i] = colour.B;
                frame[i + 1] = colour.G;
                frame[i + 2] = colour.R;
                frame[i + 3] = 255;
            }
        }

        return frame;
    }

    private static byte[] ReadNv12(GraphicsDevice graphics, ID3D11Texture2D texture, int size)
    {
        var description = texture.Description;
        using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(
            description.Format, description.Width, description.Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        graphics.Context.CopyResource(staging, texture);
        var map = graphics.Context.Map(staging, 0, MapMode.Read);
        try
        {
            var nv12 = new byte[Nv12Converter.Nv12Size(size, size)];
            for (var y = 0; y < size; y++)
            {
                Marshal.Copy(map.DataPointer + (nint)((long)y * map.RowPitch), nv12, y * size, size);
            }

            // The chroma rows follow the whole luma plane, at the same pitch.
            var chroma = map.DataPointer + (nint)((long)map.RowPitch * description.Height);
            for (var y = 0; y < size / 2; y++)
            {
                Marshal.Copy(chroma + (nint)((long)y * map.RowPitch), nv12, size * size + y * size, size);
            }

            return nv12;
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }
    }
}
