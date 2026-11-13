using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WinMirroring.Audio.Playback;

/// <summary>Mixes every incoming stream into one shared-mode WASAPI output on the default device.</summary>
internal sealed class AudioPlayback : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly MixingSampleProvider _mixer = new(AudioFormat.Wave) { ReadFully = true };
    private readonly HashSet<ISampleProvider> _inputs = [];
    private WasapiPlayer? _player;
    private MMDevice? _device;

    public AudioPlayback(ILogger logger)
    {
        _logger = logger;
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

                // The device is kept beside the player and released with it: it holds COM references to the
                // endpoint, and opening and closing the output many times would otherwise leave a pile of them.
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
                _logger.LogInformation("Sound output opened on {Device} ({Latency} ms)", device.FriendlyName, player.LatencyMilliseconds);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Cannot open the sound output");
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
            }
        }

        if (toStop is not null)
        {
            StopPlayer(toStop, device);
            _logger.LogInformation("Sound output closed");
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
