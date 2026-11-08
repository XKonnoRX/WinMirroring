using System;
using System.Windows;
using System.Windows.Threading;
using WinMirroring.Clipboard;

namespace WinMirroring.App.Views;

/// <summary>A small bottom-right indicator with a progress bar while files are received from another device.</summary>
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
    public void Update(FileTransferProgress p)
    {
        _hideTimer.Stop();
        WhatText.Text = p.What;

        if (p.Total > 0)
        {
            Bar.IsIndeterminate = false;
            var percent = (int)Math.Clamp(p.Done * 100.0 / p.Total, 0, 100);
            Bar.Value = percent;
            PercentText.Text = $"{FormatSize(p.Done)} из {FormatSize(p.Total)} · {percent}%";
        }
        else
        {
            Bar.IsIndeterminate = !p.Finished;
            PercentText.Text = string.Empty;
        }

        if (p.Finished)
        {
            TitleText.Text = p.Ok ? "Файлы получены" : "Передача не удалась";
            Bar.IsIndeterminate = false;
            Bar.Value = p.Ok ? 100 : Bar.Value;
            PercentText.Text = p.Ok ? "Готово" : "Ошибка";
            _hideTimer.Interval = TimeSpan.FromSeconds(p.Ok ? 2 : 5);
            _hideTimer.Start();
        }
        else
        {
            TitleText.Text = "Передача файлов";
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
