using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// H.264 decoder built on the synchronous Microsoft H.264 decoder MFT (always present on Windows, DXVA-assisted
/// internally). It takes one Annex-B access unit at a time and converts the decoded NV12 picture to BGRA in the
/// caller's image buffer. The decoder renegotiates its output type on the stream's first real resolution report.
/// </summary>
internal sealed class H264VideoDecoder : IDisposable
{
    private readonly ILogger _logger;
    private readonly IMFTransform _transform;
    private byte[] _nv12 = [];
    private int _outputSize;
    private int _width;
    private int _height;
    private bool _disposed;

    private H264VideoDecoder(IMFTransform transform, int width, int height, ILogger logger)
    {
        _transform = transform;
        _width = width;
        _height = height;
        _logger = logger;
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Creates a decoder sized for the first frame, or null when none can be configured.</summary>
    public static H264VideoDecoder? TryCreate(int width, int height, ILogger logger)
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

        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoInterop.H264 };
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
                    var decoder = Configure(transform, width, height, logger);
                    if (decoder is not null)
                    {
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

        return null;
    }

    private static H264VideoDecoder? Configure(IMFTransform transform, int width, int height, ILogger logger)
    {
        transform.Attributes.Set(VideoInterop.LowLatency, true);

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoInterop.H264);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, VideoInterop.Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, VideoInterop.InterlaceProgressive);
            transform.SetInputType(0, inputType, 0);
        }

        var decoder = new H264VideoDecoder(transform, width, height, logger);
        decoder.SelectNv12Output();
        decoder.RefreshOutputSize();
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return decoder;
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
                    _transform.SetOutputType(0, candidate, 0);
                    if (candidate.GetUInt32(MediaTypeAttributeKeys.FrameSize, out var packed).Success && packed != 0)
                    {
                        _width = (int)(packed >> 32);
                        _height = (int)(packed & 0xFFFFFFFF);
                    }

                    return;
                }
            }
        }
    }

    private void RefreshOutputSize()
    {
        var info = _transform.GetOutputStreamInfo(0);
        _outputSize = Math.Max(info.Size, _width * _height * 2);
    }

    /// <summary>
    /// Decodes one access unit. When a picture is produced, <paramref name="bgra"/> is (re)sized and filled, and
    /// the method returns true with the picture's size in <paramref name="width"/>/<paramref name="height"/>.
    /// </summary>
    public bool DecodeInto(byte[] accessUnit, ref byte[] bgra, out int width, out int height)
    {
        width = _width;
        height = _height;
        if (_disposed || accessUnit.Length == 0)
        {
            return false;
        }

        FeedInput(accessUnit);

        var produced = false;
        while (DrainOne(ref bgra))
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

    private bool DrainOne(ref byte[] bgra)
    {
        var allocated = MediaFactory.MFCreateSample();
        allocated.AddBuffer(MediaFactory.MFCreateMemoryBuffer(_outputSize));
        var buffer = new OutputDataBuffer { StreamID = 0, Sample = allocated };

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
                return false;
            }

            if (result.Failure || buffer.Sample is not { } sample)
            {
                return false;
            }

            ReadFrame(sample, ref bgra);
            return true;
        }
        finally
        {
            buffer.Sample?.Dispose();
            buffer.Events?.Dispose();
        }
    }

    private void ReadFrame(IMFSample sample, ref byte[] bgra)
    {
        var nv12Size = Nv12Converter.Nv12Size(_width, _height);
        if (_nv12.Length < nv12Size)
        {
            _nv12 = new byte[nv12Size];
        }

        using var buffer = sample.GetBufferByIndex(0);
        using var buffer2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2d is not null)
        {
            var length = Math.Min(_nv12.Length, buffer2d.ContiguousLength);
            buffer2d.ContiguousCopyTo(_nv12, length);
        }
        else
        {
            buffer.Lock(out var pointer, out _, out var length);
            Marshal.Copy(pointer, _nv12, 0, Math.Min(_nv12.Length, length));
            buffer.Unlock();
        }

        var pixels = _width * _height * 4;
        if (bgra.Length != pixels)
        {
            bgra = new byte[pixels];
        }

        Nv12Converter.Nv12ToBgra(_nv12, _width, _height, bgra);
    }

    private static IMFSample BuildSample(byte[] accessUnit)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(accessUnit, 0, pointer, accessUnit.Length);
        buffer.Unlock();
        buffer.CurrentLength = accessUnit.Length;

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

        _transform.Dispose();
    }
}
