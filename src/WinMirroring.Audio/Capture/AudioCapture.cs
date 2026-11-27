using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace WinMirroring.Audio.Capture;

internal delegate void AudioFrameHandler(ReadOnlySpan<float> interleavedStereo);

/// <summary>Where a stream's sound comes from, in whole frames of the one format (see <see cref="AudioFormat"/>).</summary>
internal interface IAudioSource : IAsyncDisposable
{
    event AudioFrameHandler? FrameReady;

    /// <summary>True when it also hears what this app plays, so that sending must pause while it does.</summary>
    bool IncludesOwnPlayback { get; }

    string Description { get; }

    void Start();
}

/// <summary>
/// Captures what this device plays and slices it into 10 ms 48 kHz stereo frames.
/// Preferred: process loopback of every process except this one, so sound received from another device and
/// played here is never sent back (no echo loop). Fallback for older Windows 10: endpoint loopback in the mix
/// format, converted and resampled here; it does include our own playback (<see cref="IncludesOwnPlayback"/>).
/// Endpoint mute does not affect either kind of capture (verified on Windows 11).
/// </summary>
internal sealed class AudioCapture : IAudioSource
{
    private readonly WasapiRecorder _recorder;
    private readonly ILogger _logger;
    private readonly WaveFormat _sourceFormat;
    private readonly WdlResampler? _resampler;
    private readonly float[] _frame = new float[AudioFormat.FrameValues];
    private float[] _converted = new float[4096];
    private float[] _resampled = new float[4096];
    private int _frameFill;

    /// <summary>Set for a capture that is from a device of its own: the endpoint-loopback fallback, and a microphone.</summary>
    private MMDevice? _device;

    /// <summary>What it is, when it is not a loopback at all.</summary>
    private string? _label;

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

    public string Description => _label is null
        ? $"{(IncludesOwnPlayback ? "endpoint" : "process")} loopback, {_sourceFormat}"
        : $"{_label}, {_sourceFormat}";

    /// <summary>
    /// This device's microphone: the one Windows uses for calls, or else its ordinary one. With
    /// <paramref name="forCalls"/> it is opened the way a calling program opens it, which asks the system for what
    /// it does for calls — taking the echo of the speakers out, steadying the level — where the device has that;
    /// some will not open that way, and then it is opened plainly. Throws when there is no microphone.
    /// </summary>
    public static AudioCapture CreateForMicrophone(ILogger logger, bool forCalls)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        try
        {
            var builder = new WasapiRecorderBuilder().WithDevice(device);
            if (forCalls)
            {
                builder = builder.WithCommunicationsMode();
            }

            var recorder = builder.Build();
            return new AudioCapture(recorder, processLoopback: true, logger)
            {
                _device = device,
                _label = $"microphone {device.FriendlyName}{(forCalls ? ", as for a call" : string.Empty)}",
            };
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

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
