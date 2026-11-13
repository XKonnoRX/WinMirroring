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
            ForEachRenderEndpoint(
                (device, volume) =>
                {
                    _previous[device.ID] = volume.Mute;
                    if (!volume.Mute)
                    {
                        volume.Mute = true;
                        muted.Add(device.FriendlyName);
                    }
                },
                onlyAudible: true);

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

            // After a crash the remembered states are gone; the flag then means "unmute everything" (best effort),
            // and only then is every output looked at — otherwise just the ones this app muted.
            var known = _previous;
            var repairing = known.Count == 0;
            ForEachRenderEndpoint(
                (device, volume) =>
                {
                    if (!repairing && !known.ContainsKey(device.ID))
                    {
                        return;
                    }

                    var target = known.TryGetValue(device.ID, out var prior) && prior;
                    if (volume.Mute != target)
                    {
                        volume.Mute = target;
                    }
                },
                onlyAudible: false);

            _previous.Clear();
            _settings.MutedByApp = false;
            _save();
            _logger.LogInformation("Local speakers restored");
        }
    }

    /// <param name="onlyAudible">
    /// Touch just the outputs sound can actually be coming out of: the default one, the one Windows uses for calls,
    /// and any that is playing something right now. Muting every output, including an HDMI monitor that is asleep
    /// and virtual devices, means a system-wide change nobody asked for — and every such change is announced to
    /// every program that watches the volume, the tray's own panel among them.
    /// </param>
    private void ForEachRenderEndpoint(Action<MMDevice, AudioEndpointVolume> action, bool onlyAudible)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var touched = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var wanted = onlyAudible ? Audible(enumerator) : null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        if (wanted is not null && !wanted.Contains(device.ID))
                        {
                            continue;
                        }

                        touched++;
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

        // Windows can take its time over an endpoint; if it ever does, the log says so.
        if (started.ElapsedMilliseconds > 400)
        {
            _logger.LogInformation("Working with {Count} audio output(s) took {Ms} ms", touched, started.ElapsedMilliseconds);
        }
    }

    /// <summary>The outputs worth muting: the defaults, plus whatever is making a sound at this moment.</summary>
    private HashSet<string> Audible(MMDeviceEnumerator enumerator)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in (ReadOnlySpan<Role>)[Role.Multimedia, Role.Console, Role.Communications])
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                wanted.Add(device.ID);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // No default for this role: nothing to add.
            }
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    if (device.AudioMeterInformation.MasterPeakValue > 0)
                    {
                        wanted.Add(device.ID);
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }
            }
        }

        return wanted;
    }
}
