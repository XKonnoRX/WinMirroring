using System.Reflection;
using NAudio.CoreAudioApi;

namespace WinMirroring.Audio.Tests;

/// <summary>
/// Touching an endpoint's volume registers a change callback with the Windows audio service. One that is never
/// unregistered keeps being called for every volume change on the machine, and a dead one makes the caller wait
/// for an RPC timeout, so the app must leave nothing behind.
/// </summary>
public class EndpointVolumeTests
{
    [Fact]
    public void DisposingTheDeviceUnregistersItsVolumeCallback()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        Assert.SkipWhen(devices.Count == 0, "No active audio output on this machine.");

        var device = devices[0];
        var volume = device.AudioEndpointVolume;
        Assert.NotNull(CallbackOf(volume));

        // This is what the mute guard relies on: the device goes away and the registration goes with it.
        device.Dispose();
        Assert.Null(CallbackOf(volume));

        // And disposing it again stays harmless.
        volume.Dispose();
    }

    /// <summary>
    /// Diagnostic, on request: how long this machine takes to answer about each of its outputs. If the tray's
    /// volume panel is ever slow again, this says whether an endpoint itself is the slow part.
    /// </summary>
    [Fact(Explicit = true)]
    public void HowLongEachOutputTakesToAnswer()
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        TestContext.Current.SendDiagnosticMessage($"{devices.Count} output(s) listed in {total.ElapsedMilliseconds} ms");

        foreach (var device in devices)
        {
            var step = System.Diagnostics.Stopwatch.StartNew();
            using (device)
            {
                var name = device.FriendlyName;
                var named = step.ElapsedMilliseconds;
                var volume = device.AudioEndpointVolume;
                var opened = step.ElapsedMilliseconds;
                var muted = volume.Mute;
                var read = step.ElapsedMilliseconds;
                var peak = device.AudioMeterInformation.MasterPeakValue;
                TestContext.Current.SendDiagnosticMessage(
                    $"{name}: name {named} ms, volume {opened - named} ms, read {read - opened} ms, " +
                    $"meter {step.ElapsedMilliseconds - read} ms (muted={muted}, peak={peak:F3})");
            }
        }

        TestContext.Current.SendDiagnosticMessage($"total {total.ElapsedMilliseconds} ms");
    }

    private static object? CallbackOf(AudioEndpointVolume volume) =>
        typeof(AudioEndpointVolume)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType.Name.Contains("Callback", StringComparison.OrdinalIgnoreCase))
            ?.GetValue(volume);
}
