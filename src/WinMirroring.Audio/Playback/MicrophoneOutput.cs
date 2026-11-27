using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WinMirroring.Audio.Playback;

/// <summary>One of this device's sound outputs, as it is offered for choosing.</summary>
/// <param name="Distorts">
/// It is a virtual cable whose two ends are set to different formats and which does not convert between them, so
/// that what is played into it comes out as something else (see <see cref="MicrophoneOutput"/>).
/// </param>
public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault, bool Distorts = false);

/// <summary>
/// Where another device's microphone is played on this one, so that programs here can use it as a microphone.
///
/// Windows has no way for a program to be a microphone: that takes a driver. What it does have, wherever such a
/// driver is installed, is a "virtual cable" — a pair of devices where whatever is played into the one comes out of
/// the other as a recording device. So the other device's microphone is played into the playing end, and the
/// program that should hear it is pointed at the recording end ("CABLE Output" for VB-CABLE, "Microphone (Steam
/// Streaming Microphone)" for the one Steam installs).
///
/// Left to itself this picks such a cable by its name and never anything else: playing somebody's microphone out of
/// the speakers because no cable was found would be a surprise of the worst kind. An output that is not a cable is
/// used only where the person chose it by hand.
///
/// Not every cable is a good one. VB-CABLE brings what is played into it to whatever its other end is asked for.
/// The one Steam installs hands the bytes across as they are, and its two ends come set differently — 48 kHz
/// stereo to play into, 44.1 kHz mono to record from — so a 440 Hz tone played into it was recorded at 202 Hz, in
/// pieces (measured on the machine this was written on; feeding it alone in the recording end's own format did not
/// mend it either). Such a cable is told by comparing how its two ends are set, and is not picked on its own while
/// they differ; setting both alike in Windows' sound settings makes it an ordinary cable.
/// </summary>
public static class MicrophoneOutput
{
    /// <summary>
    /// Names by which the playing end of a known virtual cable is recognised, the preferred first. VB-CABLE before
    /// the rest: it is the one made for exactly this. Steam's is there on any machine that has streamed a game.
    /// </summary>
    private static readonly string[] Cables =
    [
        "CABLE Input",
        "VB-Audio",
        "VoiceMeeter",
        "Steam Streaming Microphone",
        "Virtual Audio Cable",
    ];

    /// <summary>The active outputs of this device. Empty when the sound system cannot be asked.</summary>
    public static IReadOnlyList<AudioOutputDevice> List()
    {
        var devices = new List<AudioOutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var standard = DefaultId(enumerator);
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName, device.ID == standard, EndsDiffer(enumerator, device)));
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        return devices;
    }

    /// <summary>
    /// The output to play a microphone into: the one named in <paramref name="configured"/> (by its id, or else by
    /// its name) when it is there, otherwise the first known virtual cable that is not the standard output and does
    /// not distort, and null when there is none — never the speakers on its own.
    /// </summary>
    public static AudioOutputDevice? Pick(IReadOnlyList<AudioOutputDevice> devices, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = devices.FirstOrDefault(d => string.Equals(d.Id, configured, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => string.Equals(d.Name, configured.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen is not null)
            {
                return chosen;
            }
        }

        foreach (var cable in Cables)
        {
            var found = devices.FirstOrDefault(d => !d.IsDefault && !d.Distorts && d.Name.Contains(cable, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The same choice, as a device to open. The caller releases it.</summary>
    internal static MMDevice? Open(MMDeviceEnumerator enumerator, string? configured)
    {
        var picked = Pick(List(), configured);
        if (picked is null)
        {
            return null;
        }

        try
        {
            return enumerator.GetDevice(picked.Id);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The cable Steam installs for the microphone of whoever plays remotely, by the name both its ends carry.</summary>
    private const string SteamCable = "Steam Streaming Microphone";

    /// <summary>
    /// Whether this output is the playing end of a cable that does not convert and whose recording end is set to
    /// another rate or number of channels. Only asked of the one cable known to be like that.
    /// </summary>
    private static bool EndsDiffer(MMDeviceEnumerator enumerator, MMDevice playingEnd)
    {
        try
        {
            if (!playingEnd.FriendlyName.Contains(SteamCable, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            MMDevice? recordingEnd = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (recordingEnd is null && device.FriendlyName.Contains(SteamCable, StringComparison.OrdinalIgnoreCase))
                {
                    recordingEnd = device;
                }
                else
                {
                    device.Dispose();
                }
            }

            if (recordingEnd is null)
            {
                return false;
            }

            using (recordingEnd)
            using (var recording = recordingEnd.CreateAudioClient())
            using (var playing = playingEnd.CreateAudioClient())
            {
                return recording.MixFormat.SampleRate != playing.MixFormat.SampleRate || recording.MixFormat.Channels != playing.MixFormat.Channels;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
