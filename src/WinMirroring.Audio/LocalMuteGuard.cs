using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using WinMirroring.Core.Settings;

namespace WinMirroring.Audio;

/// <summary>
/// Silences this device's speakers while its sound plays on another device, then puts them back exactly as they
/// were. It mutes <b>every</b> active render endpoint, not just the default one: the sound a listener hears may be
/// coming out of headphones or an HDMI/monitor output that is not the default, and muting only the default would
/// leave it audible here (the bug behind "sound on both devices"). Each endpoint's previous mute state is
/// remembered and restored, so an output the user had muted stays muted. A persisted flag lets a crash be repaired
/// at the next start by unmuting everything.
/// </summary>
internal sealed class LocalMuteGuard
{
    private readonly AudioSettings _settings;
    private readonly Action _save;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _previous = new(StringComparer.Ordinal);

    public LocalMuteGuard(AudioSettings settings, Action save, ILogger logger)
    {
        _settings = settings;
        _save = save;
        _logger = logger;
    }

    /// <summary>True while this app holds the speakers muted.</summary>
    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _previous.Count > 0;
            }
        }
    }

    public void Mute()
    {
        lock (_gate)
        {
            if (_previous.Count > 0)
            {
                return; // already muted by us
            }

            var muted = new List<string>();
            ForEachRenderEndpoint((device, volume) =>
            {
                _previous[device.ID] = volume.Mute;
                if (!volume.Mute)
                {
                    volume.Mute = true;
                    muted.Add(device.FriendlyName);
                }
            });

            if (_previous.Count == 0)
            {
                _logger.LogWarning("Found no audio output to mute while sending sound to another device");
                return;
            }

            _settings.MutedByApp = true;
            _save();
            _logger.LogInformation(
                "Muted {Muted} of {Total} output(s) while sound plays elsewhere: {Names}",
                muted.Count,
                _previous.Count,
                muted.Count > 0 ? string.Join(", ", muted) : "(all were already muted)");
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_previous.Count == 0 && !_settings.MutedByApp)
            {
                return;
            }

            // After a crash the remembered states are gone; the flag then means "unmute everything" (best effort).
            var known = _previous;
            ForEachRenderEndpoint((device, volume) =>
            {
                var target = known.TryGetValue(device.ID, out var prior) && prior;
                if (volume.Mute != target)
                {
                    volume.Mute = target;
                }
            });

            _previous.Clear();
            _settings.MutedByApp = false;
            _save();
            _logger.LogInformation("Local speakers restored");
        }
    }

    private void ForEachRenderEndpoint(Action<MMDevice, AudioEndpointVolume> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        action(device, device.AudioEndpointVolume);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        _logger.LogDebug(ex, "Could not change mute on {Device}", device.FriendlyName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Enumerating audio outputs failed");
        }
    }
}
