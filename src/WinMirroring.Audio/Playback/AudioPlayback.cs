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
        lock (_gate)
        {
            if (_inputs.Remove(input))
            {
                _mixer.RemoveMixerInput(input);
            }

            if (_inputs.Count == 0)
            {
                toStop = _player;
                _player = null;
            }
        }

        if (toStop is not null)
        {
            StopPlayer(toStop);
            _logger.LogInformation("Sound output closed");
        }
    }

    private void StopPlayer(WasapiPlayer player)
    {
        try
        {
            player.Stop();
            player.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Closing the sound output failed");
        }
    }

    public ValueTask DisposeAsync()
    {
        WasapiPlayer? player;
        lock (_gate)
        {
            player = _player;
            _player = null;
            _inputs.Clear();
            _mixer.RemoveAllMixerInputs();
        }

        if (player is not null)
        {
            StopPlayer(player);
        }

        return ValueTask.CompletedTask;
    }
}
