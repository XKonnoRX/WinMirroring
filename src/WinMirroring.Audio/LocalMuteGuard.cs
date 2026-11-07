using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using WinMirroring.Core.Settings;

namespace WinMirroring.Audio;

/// <summary>
/// Mutes the default speakers while this device's sound plays elsewhere, and only undoes its own mute: if the
/// speakers were already muted, they stay that way. The flag is persisted, so a crash is repaired at next start.
/// </summary>
internal sealed class LocalMuteGuard
{
    private readonly AudioSettings _settings;
    private readonly Action _save;
    private readonly ILogger _logger;

    public LocalMuteGuard(AudioSettings settings, Action save, ILogger logger)
    {
        _settings = settings;
        _save = save;
        _logger = logger;
    }

    public void Mute()
    {
        if (_settings.MutedByApp)
        {
            return;
        }

        Use(volume =>
        {
            if (volume.Mute)
            {
                return;
            }

            volume.Mute = true;
            _settings.MutedByApp = true;
            _save();
            _logger.LogInformation("Local speakers muted while sound plays on another device");
        });
    }

    public void Restore()
    {
        if (!_settings.MutedByApp)
        {
            return;
        }

        Use(volume =>
        {
            volume.Mute = false;
            _logger.LogInformation("Local speakers unmuted");
        });
        _settings.MutedByApp = false;
        _save();
    }

    private void Use(Action<AudioEndpointVolume> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            action(device.AudioEndpointVolume);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Changing the speaker mute state failed");
        }
    }
}
