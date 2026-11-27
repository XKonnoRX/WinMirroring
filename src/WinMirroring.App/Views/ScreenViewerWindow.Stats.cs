using System.Globalization;
using System.Windows;
using WinMirroring.Core.Messages;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// How the stream is doing, where the person watching it can see: a line over the picture, and a way to say "it
/// hitched just now".
///
/// The numbers that explain a stream were all being measured already, and written to the two devices' logs every
/// ten seconds — which is where they were read from, afterwards, by whoever was asked why it had felt slow. The
/// line puts the same numbers on the screen while it is happening. The mark is for the moments nobody can find
/// afterwards: it writes a line into both logs at once, each device saying what it was measuring right then.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>What this end had counted at the last tick, so the line can show what changed in a second.</summary>
    private ScreenViewer.ViewCounts? _countsBefore;

    /// <summary>Once a second: shows, hides or refreshes the line of numbers.</summary>
    private void ShowStats(int framesThisSecond)
    {
        if (_node?.Settings.Screen.ShowStats != true || !HasPicture)
        {
            if (StatsBorder.Visibility != Visibility.Collapsed)
            {
                StatsBorder.Visibility = Visibility.Collapsed;
            }

            _countsBefore = null;
            return;
        }

        var counts = _viewer.Counts;
        StatsOverlayText.Text = StatsLine(framesThisSecond, _viewer.Stats, _countsBefore, counts);
        _countsBefore = counts;
        StatsBorder.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The line itself. Frames are the ones this window actually drew in the last second; the delay, the encoder's
    /// time, the path and the bitrate are the source's; losses are this end's, in the last second.
    /// </summary>
    internal static string StatsLine(int framesThisSecond, ScreenStatsMessage? source, ScreenViewer.ViewCounts? before, ScreenViewer.ViewCounts now)
    {
        var parts = new List<string> { $"{framesThisSecond} к/с" };
        if (source is not null)
        {
            parts.Add($"отклик {source.AckMs} мс (здесь {source.ViewerMs}, кодер {source.EncoderMs})");
            parts.Add(source.Udp ? "UDP" : "TCP");
            parts.Add($"{(source.Kbps / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))} Мбит/с");
            if (source.SharePercent < 99)
            {
                parts.Add($"битрейт снижен до {source.SharePercent}%");
            }
        }

        if (before is not null)
        {
            var lost = now.GivenUp - before.GivenUp;
            var asked = now.AskedAgain - before.AskedAgain;
            var busy = now.NotShown - before.NotShown;
            if (lost > 0 || asked > 0)
            {
                parts.Add($"потеряно {lost}, досылок {asked}");
            }

            if (busy > 0)
            {
                parts.Add($"окно не успело {busy}");
            }
        }

        if (now.OnCard == 0 && now.Decoded > 0)
        {
            parts.Add("через память");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The person watching says it hitched just now (from the menu): both devices write it down.</summary>
    public void MarkHitch()
    {
        _viewer.MarkHitch($"zoom {_zoom:0.##}, {(_askedPart == WholePicture ? "whole picture" : "a part")}, {_framesSinceTick} frames drawn since the last second");
        ShowNotice("Отмечено в журналах обоих устройств");
    }
}
