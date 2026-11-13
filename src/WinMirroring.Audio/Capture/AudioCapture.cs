using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace WinMirroring.Audio.Capture;

internal delegate void AudioFrameHandler(ReadOnlySpan<float> interleavedStereo);

/// <summary>
/// Captures what this device plays and slices it into 10 ms 48 kHz stereo frames.
/// Preferred: process loopback of every process except this one, so sound received from another device and
/// played here is never sent back (no echo loop). Fallback for older Windows 10: endpoint loopback in the mix
/// format, converted and resampled here; it does include our own playback (<see cref="IncludesOwnPlayback"/>).
/// Endpoint mute does not affect either kind of capture (verified on Windows 11).
/// </summary>
internal sealed class AudioCapture : IAsyncDisposable
{
    private readonly WasapiRecorder _recorder;
    private readonly ILogger _logger;
    private readonly WaveFormat _sourceFormat;
    private readonly WdlResampler? _resampler;
    private readonly float[] _frame = new float[AudioFormat.FrameValues];
    private float[] _converted = new float[4096];
    private float[] _resampled = new float[4096];
    private int _frameFill;

    /// <summary>Set only for the endpoint-loopback fallback, which captures from a device of its own.</summary>
    private MMDevice? _device;

    private AudioCapture(WasapiRecorder recorder, bool processLoopback, ILogger logger)
    {
        _recorder = recorder;
        _logger = logger;
        IncludesOwnPlayback = !processLoopback;
        _sourceFormat = recorder.WaveFormat.AsStandardWaveFormat();
        if (_sourceFormat.SampleRate != AudioFormat.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_sourceFormat.SampleRate, AudioFormat.SampleRate);
        }

        _recorder.DataAvailable += OnData;
    }

    public event AudioFrameHandler? FrameReady;

    /// <summary>True for the endpoint-loopback fallback, which also hears what this app plays.</summary>
    public bool IncludesOwnPlayback { get; }

    public string Description => $"{(IncludesOwnPlayback ? "endpoint" : "process")} loopback, {_sourceFormat}";

    public static async Task<AudioCapture> CreateAsync(ILogger logger)
    {
        try
        {
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithFormat(AudioFormat.Wave)
                .BuildAsync()
                .ConfigureAwait(false);
            return new AudioCapture(recorder, processLoopback: true, logger);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            logger.LogInformation("Process loopback unavailable ({Message}); using endpoint loopback", ex.Message);
        }

        using var enumerator = new MMDeviceEnumerator();

        // The device is kept beside the recorder and released with it: it holds COM references to the endpoint.
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var fallback = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().Build();
        return new AudioCapture(fallback, processLoopback: false, logger) { _device = device };
    }

    public void Start()
    {
        _recorder.StartRecording();
        _logger.LogInformation("Audio capture started: {Description}", Description);
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var frames = buffer.Length / Math.Max(1, _sourceFormat.BlockAlign);
        if (frames == 0)
        {
            return;
        }

        EnsureCapacity(ref _converted, frames * 2);
        var stereo = _converted.AsSpan(0, frames * 2);
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            stereo.Clear();
        }
        else if (!ToStereoFloat(buffer, frames, stereo))
        {
            return;
        }

        if (_resampler is null)
        {
            Push(stereo);
            return;
        }

        var wanted = _resampler.ResamplePrepare(frames, 2, out var input);
        stereo[..(Math.Min(wanted, frames) * 2)].CopyTo(input);
        var maxOut = (int)((long)frames * AudioFormat.SampleRate / _sourceFormat.SampleRate) + 16;
        EnsureCapacity(ref _resampled, maxOut * 2);
        var produced = _resampler.ResampleOut(_resampled, Math.Min(wanted, frames), maxOut, 2);
        Push(_resampled.AsSpan(0, produced * 2));
    }

    private bool ToStereoFloat(ReadOnlySpan<byte> buffer, int frames, Span<float> stereo)
    {
        var channels = _sourceFormat.Channels;
        if (_sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _sourceFormat.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var i = 0; i < frames; i++)
            {
                var left = samples[i * channels];
                stereo[i * 2] = left;
                stereo[i * 2 + 1] = channels > 1 ? samples[i * channels + 1] : left;
            }

            return true;
        }

        if (_sourceFormat.Encoding == WaveFormatEncoding.Pcm && _sourceFormat.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            for (var i = 0; i < frames; i++)
            {
                var left = samples[i * channels] / 32768f;
                stereo[i * 2] = left;
                stereo[i * 2 + 1] = channels > 1 ? samples[i * channels + 1] / 32768f : left;
            }

            return true;
        }

        return false;
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _frame.Length - _frameFill);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == _frame.Length)
            {
                _frameFill = 0;
                try
                {
                    FrameReady?.Invoke(_frame);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audio frame handler failed");
                }
            }
        }
    }

    private static void EnsureCapacity(ref float[] array, int length)
    {
        if (array.Length < length)
        {
            array = new float[Math.Max(length, array.Length * 2)];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        await _recorder.DisposeAsync().ConfigureAwait(false);
        _device?.Dispose();
        _device = null;
    }
}
