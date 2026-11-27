using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// H.264 decoder built on the synchronous Microsoft H.264 decoder MFT (always present on Windows). It takes one
/// Annex-B access unit at a time and leaves a BGRA picture in the caller's image buffer. The decoder renegotiates
/// its output type on the stream's first real resolution report.
///
/// Where the machine has a graphics card that will take it, the card is handed the whole job: it is given a Direct3D
/// device manager, which is what makes the decoder use the card's own video decoder rather than the processor, and
/// the picture it produces stays on the card until <see cref="GpuFrameReader"/> has turned it into pixels there.
/// Without that the processor decodes the stream and walks every picture to convert it — which measured at
/// forty-two milliseconds a frame on a tablet watching a 1080p screen, and was ninety-two per cent of the round
/// trip the source was waiting on. That path is still here, because a card that cannot do it must not mean no
/// picture at all.
/// </summary>
internal sealed class H264VideoDecoder : IDisposable
{
    private readonly ILogger _logger;
    private readonly IMFTransform _transform;
    private readonly MfOutputSample _output = new();
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly GpuFrameReader? _gpu;
    private byte[] _nv12 = [];
    private int _outputSize;

    /// <summary>
    /// On the card the decoder hands out pictures from a pool of its own and will not write into a buffer of ours,
    /// so which of the two provides the sample is its answer rather than a choice. Read again after every change of
    /// output type, since that is what changes it.
    /// </summary>
    private bool _providesSamples;
    private int _width;
    private int _height;
    private bool _disposed;

    private H264VideoDecoder(IMFTransform transform, IMFDXGIDeviceManager? manager, GpuFrameReader? gpu, int width, int height, ILogger logger)
    {
        _transform = transform;
        _manager = manager;
        _gpu = gpu;
        _width = width;
        _height = height;
        _logger = logger;
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>True while the card is doing the decoding and the colour conversion.</summary>
    public bool UsesGraphicsCard => _gpu is not null;

    /// <summary>
    /// The card took the stream and then would not finish a picture, several times over. The viewer rebuilds the
    /// decoder without it rather than showing nothing: a stream that runs on the processor is slower, not broken.
    /// It takes more than one failure because a single picture can be refused while a stream is changing size,
    /// and giving up the card over one frame in transit would cost the rest of the view for nothing.
    /// </summary>
    public bool GraphicsCardFailed => _gpuFailures >= GiveUpOnCardAfter;

    private const int GiveUpOnCardAfter = 4;
    private int _gpuFailures;

    /// <summary>
    /// Creates a decoder sized for the first frame, or null when none can be configured.
    /// <paramref name="useGraphicsCard"/> false keeps the whole thing in memory, which is what a retry after
    /// <see cref="GraphicsCardFailed"/> asks for.
    /// </summary>
    /// <summary>The format this decoder reads (see <see cref="VideoFormat"/>).</summary>
    public VideoFormat Format { get; private set; }

    public static H264VideoDecoder? TryCreate(int width, int height, ILogger logger, bool useGraphicsCard = true, VideoFormat format = VideoFormat.H264)
    {
        if (width < 16 || height < 16)
        {
            return null;
        }

        try
        {
            VideoInterop.EnsureStarted();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Media Foundation did not start: {Message}", ex.Message);
            return null;
        }

        var gpu = useGraphicsCard ? GpuFrameReader.TryCreate(logger) : null;
        var manager = gpu is null ? null : TryCreateManager(gpu, logger);
        if (manager is null)
        {
            gpu?.Dispose();
            gpu = null;
        }

        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoInterop.SubtypeOf(format) };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoInterop.Nv12 };
        IMFActivateCollection? activates = null;
        try
        {
            activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder,
                (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
                input,
                output);

            foreach (var activate in activates)
            {
                IMFTransform? transform = null;
                try
                {
                    transform = activate.ActivateObject<IMFTransform>();
                    var decoder = Configure(transform, manager, gpu, width, height, logger, format);
                    if (decoder is not null)
                    {
                        logger.LogInformation(
                            decoder.UsesGraphicsCard
                                ? "The graphics card decodes this stream and turns it into pixels; pictures are not copied through memory"
                                : "The processor decodes this stream and converts every picture");
                        transform = null;
                        return decoder;
                    }
                }
                catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException or ArgumentException)
                {
                    logger.LogDebug("H.264 decoder candidate rejected: {Message}", ex.Message);
                }
                finally
                {
                    transform?.Dispose();
                    activate.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException)
        {
            logger.LogDebug("Enumerating H.264 decoders failed: {Message}", ex.Message);
        }
        finally
        {
            activates?.Dispose();
        }

        manager?.Dispose();
        gpu?.Dispose();
        return null;
    }

    /// <summary>
    /// The device manager that makes the decoder work on the card — the same device the reader converts on, since a
    /// texture only travels between the two without a copy when it belongs to one device.
    /// </summary>
    private static IMFDXGIDeviceManager? TryCreateManager(GpuFrameReader gpu, ILogger logger)
    {
        try
        {
            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(gpu.Device).CheckError();
            return manager;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            logger.LogDebug("No Direct3D device manager for the decoder ({Message}); it will decode in memory", ex.Message);
            return null;
        }
    }

    private static H264VideoDecoder? Configure(IMFTransform transform, IMFDXGIDeviceManager? manager, GpuFrameReader? gpu, int width, int height, ILogger logger, VideoFormat format)
    {
        transform.Attributes.Set(VideoInterop.LowLatency, true);

        // Before the media types, as a transform expects it, and only when the transform says it understands the
        // card at all: a decoder that is handed a device it cannot use may take it and then produce nothing.
        var onGpu = manager is not null && gpu is not null && IsCardAware(transform);
        if (onGpu)
        {
            try
            {
                transform.ProcessMessage(TMessageType.MessageSetD3DManager, new UIntPtr((ulong)manager!.NativePointer.ToInt64()));
            }
            catch (Exception ex) when (ex is SharpGenException or COMException)
            {
                logger.LogDebug("This H.264 decoder would not take the graphics card ({Message})", ex.Message);
                onGpu = false;
            }
        }

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoInterop.SubtypeOf(format));
            inputType.Set(MediaTypeAttributeKeys.FrameSize, VideoInterop.Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, VideoInterop.InterlaceProgressive);
            transform.SetInputType(0, inputType, 0);
        }

        var decoder = new H264VideoDecoder(transform, onGpu ? manager : null, onGpu ? gpu : null, width, height, logger) { Format = format };
        decoder.SelectNv12Output();
        decoder.RefreshOutputSize();
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return decoder;
    }

    /// <summary>Whether this transform says it can work on a Direct3D 11 device.</summary>
    private static bool IsCardAware(IMFTransform transform)
    {
        try
        {
            return transform.Attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var aware).Success && aware != 0;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            return false;
        }
    }


    /// <summary>Picks and sets the NV12 output type the decoder exposes once its input type is set.</summary>
    private void SelectNv12Output()
    {
        for (var i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == VideoInterop.MF_E_NO_MORE_TYPES)
            {
                throw new InvalidOperationException("The H.264 decoder does not offer NV12 output.");
            }

            using (candidate)
            {
                if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoInterop.Nv12)
                {
                    // Deliberately not taking the size from the type it offers: for H.264 that is the coded size,
                    // rounded up to whole macroblocks — 1088 rows for a 1080-line screen — and what is wanted here
                    // is the size of the picture itself. The source says that in every frame message, which is
                    // where it comes from (see Expect).
                    _transform.SetOutputType(0, candidate, 0);
                    return;
                }
            }
        }
    }

    private void RefreshOutputSize()
    {
        var info = _transform.GetOutputStreamInfo(0);
        _outputSize = Math.Max(info.Size, _width * _height * 2);
        _providesSamples = (info.Flags & VideoInterop.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
    }

    /// <summary>
    /// The size the pictures in this stream are from now on, as the source announces it with every frame. A
    /// stream changes size while it is running when the viewer's window does — the picture is encoded at the size
    /// it is shown at — and the decoder cannot be asked for that size itself: what it offers is the coded size,
    /// rounded up to whole macroblocks.
    /// </summary>
    public void Expect(int width, int height)
    {
        if (width < 16 || height < 16 || (width == _width && height == _height))
        {
            return;
        }

        _width = width;
        _height = height;
        RefreshOutputSize();
    }

    /// <summary>
    /// Decodes one access unit. When a picture is produced, <paramref name="bgra"/> is (re)sized and filled, and
    /// the method returns true with the picture's size in <paramref name="width"/>/<paramref name="height"/>.
    ///
    /// With <paramref name="read"/> false the picture is decoded and left where it is: <paramref name="bgra"/> is
    /// not touched. The decoder has to see every picture, because the next is read against it — but turning one
    /// into pixels and bringing it into memory is the larger half of the work, and it is wasted on a picture that
    /// a newer one, already waiting, is about to replace.
    /// </summary>
    /// <remarks>
    /// With <paramref name="onto"/> given and the card doing the work, the picture is left on the card in that
    /// surface instead of being brought into <paramref name="bgra"/> (which is then not touched), and
    /// <see cref="LastWasLeftOnCard"/> says so. When the processor is doing the decoding there is no card to leave
    /// it on, and it goes into <paramref name="bgra"/> as ever.
    /// </remarks>
    public bool DecodeInto(byte[] accessUnit, ref byte[] bgra, out int width, out int height, bool read = true, GpuSurface? onto = null)
    {
        _onto = onto;
        LastWasLeftOnCard = false;
        width = _width;
        height = _height;
        if (_disposed || accessUnit.Length == 0)
        {
            return false;
        }

        FeedInput(accessUnit);

        var produced = false;
        while (DrainOne(ref bgra, read))
        {
            produced = true;
            width = _width;
            height = _height;
        }

        return produced;
    }

    private void FeedInput(byte[] accessUnit)
    {
        var sample = BuildSample(accessUnit);
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == VideoInterop.MF_E_TRANSFORM_NEED_MORE_INPUT_OR_NOTACCEPTING)
        {
            // The decoder has output waiting; it will be drained by the caller. Drop this input to avoid a stall.
            _logger.LogTrace("H.264 decoder not accepting input");
        }
        finally
        {
            sample.Dispose();
        }
    }

    /// <summary>
    /// Takes one decoded picture if there is one. The first picture of a stream comes with a format change: the
    /// output type is set again and the picture taken right away, not with the next access unit (on a still screen
    /// there may be no next one, and the view would stay empty).
    /// </summary>
    private bool DrainOne(ref byte[] bgra, bool read)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // On the card the decoder provides the sample; in memory it writes into the one kept for its lifetime.
            // Which of the two it is has to be read once and held: a change of output type below changes the answer,
            // and giving back a sample of ours as though it were the decoder's destroys the one that is kept —
            // after which the next call reads freed memory.
            var provided = _providesSamples;
            var ours = provided ? null : _output.ForSize(_outputSize);
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = ours };

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            try
            {
                if (result.Code == VideoInterop.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return false;
                }

                if (result.Code == VideoInterop.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    SelectNv12Output();
                    RefreshOutputSize();
                    continue;
                }

                if (result.Failure)
                {
                    return false;
                }

                // In memory, read through our own handle on the sample and not the one that came back: that one is
                // a second wrapper around the same sample and disposing it would destroy a sample we keep. On the
                // card the sample belongs to the decoder and the wrapper that came back is the only handle on it.
                var sample = ours ?? buffer.Sample;
                return sample is not null && (!read || ReadFrame(sample, ref bgra));
            }
            finally
            {
                buffer.Events?.Dispose();
                if (provided)
                {
                    // Giving the decoder its picture back is what keeps its pool going round.
                    buffer.Sample?.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>Where the picture being decoded is to be left, for the length of one <see cref="DecodeInto"/>.</summary>
    private GpuSurface? _onto;

    /// <summary>The last picture <see cref="DecodeInto"/> produced is in the surface it was given, not in memory.</summary>
    public bool LastWasLeftOnCard { get; private set; }

    /// <summary>A surface for <see cref="DecodeInto"/> to leave pictures in, or null while the card is not decoding.</summary>
    public GpuSurface? CreateSurface(int width, int height) => _disposed ? null : _gpu?.CreateSurface(width, height);

    private bool ReadFrame(IMFSample sample, ref byte[] bgra)
    {
        if (_gpu is not null && _onto is { } surface)
        {
            return LastWasLeftOnCard = LeaveFrameOnGpu(sample, surface);
        }

        var pixels = _width * _height * 4;
        if (bgra.Length != pixels)
        {
            bgra = new byte[pixels];
        }

        return _gpu is not null ? ReadFrameOnGpu(sample, bgra) : ReadFrameInMemory(sample, bgra);
    }

    /// <summary>
    /// Takes the decoder's picture where it already is — a texture on the card, one slice of the pool it hands out
    /// in turn — and has the card turn it into the pixels the window paints.
    /// </summary>
    private bool ReadFrameOnGpu(IMFSample sample, byte[] bgra)
    {
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is null)
        {
            _gpuFailures = GiveUpOnCardAfter;
            _logger.LogWarning("The decoder produced a picture that is not on the graphics card after all");
            return false;
        }

        // GetResource hands back a reference of its own, which is given back when the wrapper is disposed.
        using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        if (_gpu!.TryRead(texture, (int)dxgi.SubresourceIndex, _width, _height, bgra))
        {
            _gpuFailures = 0;
            return true;
        }

        _gpuFailures++;
        return false;
    }

    private bool LeaveFrameOnGpu(IMFSample sample, GpuSurface surface)
    {
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is null)
        {
            _gpuFailures = GiveUpOnCardAfter;
            _logger.LogWarning("The decoder produced a picture that is not on the graphics card after all");
            return false;
        }

        using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        if (_gpu!.TryConvertOnto(texture, (int)dxgi.SubresourceIndex, surface))
        {
            _gpuFailures = 0;
            return true;
        }

        _gpuFailures++;
        return false;
    }

    private bool ReadFrameInMemory(IMFSample sample, byte[] bgra)
    {
        using var buffer = sample.GetBufferByIndex(0);
        using var buffer2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();

        // The whole frame is taken, not just the rows that are shown: its luma plane is as tall as the coded
        // picture (a multiple of 16), and the chroma plane begins only after that.
        int length;
        if (buffer2d is not null)
        {
            length = buffer2d.ContiguousLength;
            Grow(length);
            buffer2d.ContiguousCopyTo(_nv12, length);
        }
        else
        {
            buffer.Lock(out var pointer, out _, out length);
            Grow(length);
            Marshal.Copy(pointer, _nv12, 0, length);
            buffer.Unlock();
        }

        var rows = Nv12Converter.RowsIn(length, _width, _height);
        Nv12Converter.Nv12ToBgra(_nv12, _width, _height, bgra, _width * rows);
        return true;
    }

    private void Grow(int length)
    {
        if (_nv12.Length < length)
        {
            _nv12 = new byte[length];
        }
    }

    /// <summary>
    /// An access unit delimiter (NAL type 9): the decoder finds the end of a picture only where the next one
    /// starts, so without it every picture would come out one access unit late, and the last one of a still screen
    /// never.
    /// </summary>
    private IMFSample BuildSample(byte[] accessUnit)
    {
        var AccessUnitDelimiter = VideoInterop.DelimiterOf(Format);
        var length = accessUnit.Length + AccessUnitDelimiter.Length;
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(accessUnit, 0, pointer, accessUnit.Length);
        Marshal.Copy(AccessUnitDelimiter, 0, pointer + accessUnit.Length, AccessUnitDelimiter.Length);
        buffer.Unlock();
        buffer.CurrentLength = length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        return sample;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
        }

        _output.Dispose();
        _transform.Dispose();

        // After the transform: it holds the device manager while it is still streaming.
        _manager?.Dispose();
        _gpu?.Dispose();
    }
}
