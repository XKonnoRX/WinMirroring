using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace WinMirroring.Screen.Encoding;

/// <summary>One encoded H.264 access unit and whether it begins with a key frame.</summary>
internal readonly record struct VideoAccessUnit(byte[] Data, bool Keyframe);

/// <summary>
/// Hardware H.264 encoder over an asynchronous Media Foundation transform. A D3D11 device manager is supplied to
/// the MFT (hardware encoders need one even for system-memory input), and the MFT is driven by its own event pump
/// thread (NV12 in, Annex-B access units out). <see cref="Encode"/> feeds one captured frame and returns whatever
/// access units are ready, so a one- or two-frame pipeline latency simply shows up as a later return. Everything
/// is best-effort: <see cref="TryCreate"/> returns null when no usable hardware encoder exists, and the caller
/// then stays on the JPEG tile path.
/// </summary>
internal sealed class H264VideoEncoder : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameDurationHns;
    private readonly byte[] _nv12;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly ID3D11Device? _device;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private ICodecAPI? _codecApi;
    private object? _codecApiRcw;

    private readonly ConcurrentQueue<IMFSample> _inputs = new();
    private readonly ConcurrentQueue<VideoAccessUnit> _outputs = new();
    private readonly Thread _pump;
    private volatile bool _running = true;
    private int _pendingNeedInput;
    private long _frameIndex;

    // Diagnostics for the unit test; not used in production paths.
    internal int DiagEvents;
    internal int DiagInputs;
    internal int DiagOutputs;
    internal int DiagNeedInput;
    internal string? DiagLastError;
    internal bool DiagHasDeviceManager => _deviceManager is not null;

    private H264VideoEncoder(IMFTransform transform, IMFMediaEventGenerator events, IMFDXGIDeviceManager? deviceManager, ID3D11Device? device, bool providesSamples, int outputSize, int width, int height, int fps, ILogger logger)
    {
        _transform = transform;
        _events = events;
        _deviceManager = deviceManager;
        _device = device;
        _providesSamples = providesSamples;
        _outputSize = outputSize;
        _width = width;
        _height = height;
        _frameDurationHns = Math.Max(1, 10_000_000L / Math.Clamp(fps, 1, 60));
        _nv12 = new byte[Nv12Converter.Nv12Size(width, height)];
        _logger = logger;
        _pump = new Thread(Pump) { IsBackground = true, Name = "h264-encoder" };
        _pump.Start();
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Creates a hardware H.264 encoder for the given size, or null when none can be configured.</summary>
    public static H264VideoEncoder? TryCreate(int width, int height, int fps, int bitrateKbps, ILogger logger)
    {
        width = Nv12Converter.Even(width);
        height = Nv12Converter.Even(height);
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

        ID3D11Device? device = null;
        IMFDXGIDeviceManager? manager = null;
        var moved = false;
        try
        {
            CreateDeviceManager(logger, out device, out manager);

            var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoInterop.Nv12 };
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoInterop.H264 };
            using var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
                input,
                output);

            foreach (var activate in activates)
            {
                IMFTransform? transform = null;
                try
                {
                    transform = activate.ActivateObject<IMFTransform>();
                    var encoder = Configure(transform, manager, device, width, height, fps, bitrateKbps, logger);
                    if (encoder is not null)
                    {
                        transform = null; // ownership moved into the encoder
                        moved = true;
                        return encoder;
                    }
                }
                catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException or ArgumentException)
                {
                    logger.LogDebug("H.264 encoder candidate rejected: {Message}", ex.Message);
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
            logger.LogDebug("Enumerating H.264 encoders failed: {Message}", ex.Message);
        }
        finally
        {
            if (!moved)
            {
                manager?.Dispose();
                device?.Dispose();
            }
        }

        return null;
    }

    private static void CreateDeviceManager(ILogger logger, out ID3D11Device? device, out IMFDXGIDeviceManager? manager)
    {
        device = null;
        manager = null;
        try
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out var created).CheckError();
            if (created is null)
            {
                return;
            }

            device = created;

            // Media Foundation drives the device from its own threads, so it must be multithread-protected.
            using (var multithread = created.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                multithread?.SetMultithreadProtected(true);
            }

            manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(created).CheckError();
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            logger.LogDebug("D3D11 device manager not created (encoder will try system memory): {Message}", ex.Message);
            manager?.Dispose();
            device?.Dispose();
            device = null;
            manager = null;
        }
    }

    private static H264VideoEncoder? Configure(IMFTransform transform, IMFDXGIDeviceManager? manager, ID3D11Device? device, int width, int height, int fps, int bitrateKbps, ILogger logger)
    {
        var attributes = transform.Attributes;
        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);
        attributes.Set(VideoInterop.LowLatency, true);

        if (manager is not null)
        {
            transform.ProcessMessage(TMessageType.MessageSetD3DManager, new UIntPtr((ulong)manager.NativePointer.ToInt64()));
        }

        using (var outputType = MediaFactory.MFCreateMediaType())
        {
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoInterop.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(bitrateKbps, 500, 100_000) * 1000u);
            outputType.Set(MediaTypeAttributeKeys.FrameSize, VideoInterop.Pack(width, height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, VideoInterop.Pack(fps, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, VideoInterop.Pack(1, 1));
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, VideoInterop.InterlaceProgressive);
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, VideoInterop.H264ProfileMain);
            transform.SetOutputType(0, outputType, 0);
        }

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoInterop.Nv12);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, VideoInterop.Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, VideoInterop.Pack(fps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, VideoInterop.Pack(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, VideoInterop.InterlaceProgressive);
            transform.SetInputType(0, inputType, 0);
        }

        var streamInfo = transform.GetOutputStreamInfo(0);
        var providesSamples = (streamInfo.Flags & (VideoInterop.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | VideoInterop.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        var outputSize = Math.Max(streamInfo.Size, width * height * 2);

        var events = transform.QueryInterface<IMFMediaEventGenerator>();
        var encoder = new H264VideoEncoder(transform, events, manager, device, providesSamples, outputSize, width, height, fps, logger);
        encoder.TryTuneCodec(fps, bitrateKbps);

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return encoder;
    }

    /// <summary>Best-effort low-latency, CBR, no-B-frame and GOP tuning via ICodecAPI; failures are ignored.</summary>
    private void TryTuneCodec(int fps, int bitrateKbps)
    {
        try
        {
            _codecApiRcw = Marshal.GetObjectForIUnknown(_transform.NativePointer);
            _codecApi = _codecApiRcw as ICodecAPI;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _codecApi = null;
        }

        if (_codecApi is null)
        {
            return;
        }

        Set(VideoInterop.LowLatency, CodecPropVariant.Bool(true));
        Set(VideoInterop.AVEncCommonRateControlMode, CodecPropVariant.UInt32(VideoInterop.RateControlCbr));
        Set(VideoInterop.AVEncCommonMeanBitRate, CodecPropVariant.UInt32((uint)Math.Clamp(bitrateKbps, 500, 100_000) * 1000u));
        Set(VideoInterop.AVEncMPVDefaultBPictureCount, CodecPropVariant.UInt32(0));
        Set(VideoInterop.AVEncMPVGOPSize, CodecPropVariant.UInt32((uint)Math.Clamp(fps * 2, 1, 600)));

        void Set(Guid api, CodecPropVariant value)
        {
            try
            {
                _codecApi!.SetValue(ref api, ref value);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _logger.LogTrace("Codec setting {Api} ignored: {Message}", api, ex.Message);
            }
        }
    }

    /// <summary>
    /// Feeds the <paramref name="crop"/> region of <paramref name="frameBgra"/> (crop size must equal the encoder
    /// size) and returns whatever access units are ready. May be empty while the encoder pipeline fills.
    /// </summary>
    public IReadOnlyList<VideoAccessUnit> Encode(byte[] frameBgra, int frameWidth, Rectangle crop, bool forceKeyframe)
    {
        if (crop.Width != _width || crop.Height != _height)
        {
            // Size changed under us: the caller is expected to recreate the encoder, so just drain what we have.
            return DrainOutputs();
        }

        if (forceKeyframe && _codecApi is not null)
        {
            var api = VideoInterop.AVEncVideoForceKeyFrame;
            var value = CodecPropVariant.UInt32(1);
            try
            {
                _codecApi.SetValue(ref api, ref value);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _logger.LogTrace("Force-keyframe ignored: {Message}", ex.Message);
            }
        }

        Nv12Converter.BgraCropToNv12(frameBgra, frameWidth, crop, _nv12);
        var sample = BuildInputSample();
        _inputs.Enqueue(sample);
        return DrainOutputs();
    }

    private IReadOnlyList<VideoAccessUnit> DrainOutputs()
    {
        List<VideoAccessUnit>? units = null;
        while (_outputs.TryDequeue(out var unit))
        {
            (units ??= []).Add(unit);
        }

        return (IReadOnlyList<VideoAccessUnit>?)units ?? [];
    }

    private IMFSample BuildInputSample()
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12.Length);
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(_nv12, 0, pointer, _nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = _nv12.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = _frameIndex * _frameDurationHns;
        sample.SampleDuration = _frameDurationHns;
        _frameIndex++;
        buffer.Dispose();
        return sample;
    }

    private void Pump()
    {
        while (_running)
        {
            while (Volatile.Read(ref _pendingNeedInput) > 0 && _inputs.TryDequeue(out var input))
            {
                try
                {
                    _transform.ProcessInput(0, input, 0);
                    DiagInputs++;
                }
                catch (Exception ex) when (ex is SharpGenException or COMException)
                {
                    DiagLastError = $"ProcessInput {ex.Message}";
                    _logger.LogDebug("H.264 encode input dropped: {Message}", ex.Message);
                }
                finally
                {
                    input.Dispose();
                }

                Interlocked.Decrement(ref _pendingNeedInput);
            }

            IMFMediaEvent? mediaEvent = null;
            try
            {
                mediaEvent = _events.GetEvent(VideoInterop.MF_EVENT_FLAG_NO_WAIT);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == VideoInterop.MF_E_NO_EVENTS)
            {
                // No event pending right now.
            }
            catch (SharpGenException ex)
            {
                DiagLastError = $"GetEvent 0x{(uint)ex.ResultCode.Code:X8}";
                Thread.Sleep(2);
                continue;
            }
            catch (Exception ex)
            {
                DiagLastError = $"GetEvent {ex.GetType().Name}";
                break; // the transform shut down
            }

            if (mediaEvent is null)
            {
                Thread.Sleep(1);
                continue;
            }

            DiagEvents++;
            try
            {
                switch (mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        Interlocked.Increment(ref _pendingNeedInput);
                        DiagNeedInput++;
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        DrainTransformOutput();
                        break;
                }
            }
            catch (Exception ex) when (ex is SharpGenException or COMException)
            {
                _logger.LogDebug("H.264 encode output dropped: {Message}", ex.Message);
            }
            finally
            {
                mediaEvent.Dispose();
            }
        }
    }

    private void DrainTransformOutput()
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        if (!_providesSamples)
        {
            var allocated = MediaFactory.MFCreateSample();
            allocated.AddBuffer(MediaFactory.MFCreateMemoryBuffer(_outputSize));
            buffer.Sample = allocated;
        }

        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
        try
        {
            if (result.Success && buffer.Sample is { } sample)
            {
                using var contiguous = sample.ConvertToContiguousBuffer();
                contiguous.Lock(out var pointer, out _, out var length);
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                contiguous.Unlock();
                if (bytes.Length > 0)
                {
                    _outputs.Enqueue(new VideoAccessUnit(bytes, VideoInterop.ContainsKeyframe(bytes)));
                    DiagOutputs++;
                }
            }
            else if (result.Failure)
            {
                DiagLastError = $"ProcessOutput 0x{(uint)result.Code:X8}";
            }
        }
        finally
        {
            buffer.Sample?.Dispose();
            buffer.Events?.Dispose();
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_pump.IsAlive && !_pump.Join(TimeSpan.FromSeconds(1)))
        {
            _logger.LogDebug("H.264 encoder pump did not stop in time");
        }

        while (_inputs.TryDequeue(out var sample))
        {
            sample.Dispose();
        }

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
        }

        if (_codecApiRcw is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_codecApiRcw);
            }
            catch (ArgumentException)
            {
            }

            _codecApiRcw = null;
            _codecApi = null;
        }

        _events.Dispose();
        _transform.Dispose();
        _deviceManager?.Dispose();
        _device?.Dispose();
    }
}
