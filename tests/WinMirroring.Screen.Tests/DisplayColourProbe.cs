using Vortice.DXGI;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// What colour space each monitor of the machine it is run on actually is. Explicit, because it is a measurement
/// rather than a thing that can pass or fail — and it is the first thing to run when a stream looks too bright or
/// too saturated on the far side while the same path looks right for something else.
///
/// Why it is worth having at all: everything this program does with a captured frame assumes the numbers in it are
/// plain sRGB. A monitor with HDR or Windows's automatic colour management switched on is composed in a wider space
/// than that, and the desktop captured from it carries wider numbers — which, shown as sRGB on the other device,
/// is exactly a picture that is too saturated and too bright. The virtual monitor a second-monitor session creates
/// is always plain sRGB, so that case looks right and the real monitors do not, and the difference is not in any
/// code of ours. This says which it is in one line per monitor instead of by argument.
/// </summary>
public class DisplayColourProbe
{
    /// <summary>Where the answer is left, since a diagnostic message is not always shown by the runner.</summary>
    public static string Report => Path.Combine(Path.GetTempPath(), "WinMirroring-monitors.txt");

    [Fact(Explicit = true)]
    public void WhatColourSpaceEachMonitorIsIn()
    {
        var lines = new List<string>();
        void Say(string line)
        {
            lines.Add(line);
            TestContext.Current.SendDiagnosticMessage(line);
        }

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var found = 0;
        for (var card = 0; factory.EnumAdapters1((uint)card, out var adapter).Success; card++)
        {
            using (adapter)
            {
                for (var screen = 0; adapter.EnumOutputs((uint)screen, out var output).Success; screen++)
                {
                    using (output)
                    {
                        found++;
                        using var advanced = output.QueryInterfaceOrNull<IDXGIOutput6>();
                        if (advanced is null)
                        {
                            var plain = output.Description;
                            Say($"{plain.DeviceName}: {plain.DesktopCoordinates} (too old a Windows to say its colour space)");
                            continue;
                        }

                        var description = advanced.Description1;
                        Say(
                            $"{description.DeviceName}: {description.DesktopCoordinates}, {description.ColorSpace}, {description.BitsPerColor} bits per colour, "
                            + $"luminance {description.MinLuminance}–{description.MaxLuminance} (full frame {description.MaxFullFrameLuminance}), "
                            + $"red {description.RedPrimary[0]:F3},{description.RedPrimary[1]:F3} green {description.GreenPrimary[0]:F3},{description.GreenPrimary[1]:F3} "
                            + $"blue {description.BluePrimary[0]:F3},{description.BluePrimary[1]:F3}");

                        // Said plainly rather than left to be read off the numbers: sRGB's own primaries are
                        // red 0.640,0.330 green 0.300,0.600 blue 0.150,0.060, and anything much wider than that
                        // is a monitor whose desktop does not mean what this program assumes it means.
                        var wide = description.RedPrimary[0] > 0.66f || description.GreenPrimary[1] > 0.63f || description.BluePrimary[0] < 0.14f;
                        var sdrSrgb = description.ColorSpace == ColorSpaceType.RgbFullG22NoneP709;
                        Say(
                            $"{description.DeviceName}: {(sdrSrgb ? "plain sRGB" : "NOT plain sRGB — advanced colour is on")}"
                            + $"{(wide ? ", and the panel is wider than sRGB" : string.Empty)}");
                    }
                }
            }
        }

        File.WriteAllLines(Report, lines);
        Assert.True(found > 0, "no monitors to look at");
    }
}
