using System;
using System.Windows;
using System.Windows.Threading;
using WinMirroring.Clipboard;

namespace WinMirroring.App.Views;

/// <summary>
/// A small bottom-right indicator with a progress bar for something that takes a while: files arriving from
/// another device, or an update being downloaded. It never takes the focus and is kept out of screen capture, so
/// a device watching this screen does not see it.
/// </summary>
public partial class FileTransferWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public FileTransferWindow()
    {
        InitializeComponent();
        Services.CaptureExclusion.Apply(this);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    /// <summary>Shows the window (first time), updates the bar, and auto-hides a short while after it finishes.</summary>
    public void Update(FileTransferProgress p) =>
        Report("Передача файлов", p.Ok ? "Файлы получены" : "Передача не удалась", p.What, p.Done, p.Total, p.Finished, p.Ok);

    /// <summary>
    /// The same indicator for any long job. <paramref name="total"/> zero runs the bar as a plain "working", for a
    /// job whose size is not known; <paramref name="finished"/> swaps the title and starts the auto-hide.
    /// </summary>
    public void Report(string title, string doneTitle, string what, long done, long total, bool finished, bool ok)
    {
        _hideTimer.Stop();
        WhatText.Text = what;

        if (total > 0)
        {
            Bar.IsIndeterminate = false;
            var percent = (int)Math.Clamp(done * 100.0 / total, 0, 100);
            Bar.Value = percent;
            PercentText.Text = $"{FormatSize(done)} из {FormatSize(total)} · {percent}%";
        }
        else
        {
            Bar.IsIndeterminate = !finished;
            PercentText.Text = done > 0 ? FormatSize(done) : string.Empty;
        }

        if (finished)
        {
            TitleText.Text = doneTitle;
            Bar.IsIndeterminate = false;
            Bar.Value = ok ? 100 : Bar.Value;
            PercentText.Text = ok ? "Готово" : "Ошибка";
            _hideTimer.Interval = TimeSpan.FromSeconds(ok ? 2 : 5);
            _hideTimer.Start();
        }
        else
        {
            TitleText.Text = title;
        }

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
