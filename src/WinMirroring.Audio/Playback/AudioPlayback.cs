using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WinMirroring.Audio.Playback;

/// <summary>
/// Mixes every incoming stream into one shared-mode WASAPI output: on the default device, or on the one a picker
/// names (a microphone from another device goes into a virtual cable, see <see cref="MicrophoneOutput"/>).
/// </summary>
internal sealed class AudioPlayback : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly MixingSampleProvider _mixer = new(AudioFormat.Wave) { ReadFully = true };
    private readonly HashSet<ISampleProvider> _inputs = [];
    private readonly Func<MMDeviceEnumerator, MMDevice?>? _pick;
    private readonly string _what;
    private WasapiPlayer? _player;
    private MMDevice? _device;
    private string? _deviceName;
    private string? _deviceId;

    /// <param name="pick">Chooses the output, or says there is none (null); without it the default output is used.</param>
    /// <param name="what">What this output is, for the log.</param>
    public AudioPlayback(ILogger logger, Func<MMDeviceEnumerator, MMDevice?>? pick = null, string what = "Sound output")
    {
        _logger = logger;
        _pick = pick;
        _what = what;
    }

    /// <summary>The output that is open now, by its name and by its id; null while none is.</summary>
    public string? DeviceName
    {
        get
        {
            lock (_gate)
            {
                return _deviceName;
            }
        }
    }

    public string? DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _deviceId;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _inputs.Count > 0;
            }
        }
    }

    public void Add(ISampleProvider input)
    {
        lock (_gate)
        {
            if (_inputs.Add(input))
            {
                _mixer.AddMixerInput(input);
            }

            if (_player is not null)
            {
                return;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = _pick is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : _pick(enumerator);
                if (device is null)
                {
                    _logger.LogWarning("{What}: there is no device to open", _what);
                    return;
                }

                OpenLocked(device);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "{What}: cannot be opened", _what);
            }
        }
    }

    /// <summary>
    /// Opens the output on <paramref name="device"/>, which is kept beside the player and released with it: it
    /// holds COM references to the endpoint, and opening and closing the output many times would otherwise leave
    /// a pile of them.
    /// </summary>
    private void OpenLocked(MMDevice device)
    {
        try
        {
            var player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(20)
                .Build();
            player.Init(new SampleToWaveProvider(_mixer));
            player.Play();
            _player = player;
            _device = device;
            _deviceName = device.FriendlyName;
            _deviceId = device.ID;
            _logger.LogInformation("{What} opened on {Device} ({Latency} ms)", _what, _deviceName, player.LatencyMilliseconds);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The choice of output may have changed: if what the picker names now is not what is open, what is playing
    /// moves there. Nothing happens while nothing plays, or for an output that has no picker.
    /// </summary>
    public void Reopen()
    {
        lock (_gate)
        {
            if (_pick is null || _inputs.Count == 0)
            {
                return;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var next = _pick(enumerator);
                if (next is not null && next.ID == _deviceId)
                {
                    next.Dispose();
                    return;
                }

                if (_player is { } old)
                {
                    StopPlayer(old, _device);
                    _player = null;
                    _device = null;
                    _deviceName = null;
                    _deviceId = null;
                }

                if (next is not null)
                {
                    OpenLocked(next);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "{What}: cannot be moved to another device", _what);
            }
        }
    }

    public void Remove(ISampleProvider input)
    {
        WasapiPlayer? toStop = null;
        MMDevice? device = null;
        lock (_gate)
        {
            if (_inputs.Remove(input))
            {
                _mixer.RemoveMixerInput(input);
            }

            if (_inputs.Count == 0)
            {
                toStop = _player;
                device = _device;
                _player = null;
                _device = null;
                _deviceName = null;
                _deviceId = null;
            }
        }

        if (toStop is not null)
        {
            StopPlayer(toStop, device);
            _logger.LogInformation("{What} closed", _what);
        }
    }

    private void StopPlayer(WasapiPlayer player, MMDevice? device)
    {
        try
        {
            player.Stop();
            player.Dispose();
            device?.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Closing the sound output failed");
        }
    }

    public ValueTask DisposeAsync()
    {
        WasapiPlayer? player;
        MMDevice? device;
        lock (_gate)
        {
            player = _player;
            device = _device;
            _player = null;
            _device = null;
            _deviceName = null;
            _deviceId = null;
            _inputs.Clear();
            _mixer.RemoveAllMixerInputs();
        }

        if (player is not null)
        {
            StopPlayer(player, device);
        }

        return ValueTask.CompletedTask;
    }
}
