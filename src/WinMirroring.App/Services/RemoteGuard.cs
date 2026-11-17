using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WinMirroring.Input;
using WinMirroring.Screen;

namespace WinMirroring.App.Services;

/// <summary>
/// A second chance for a press that would cut this device off from whoever is driving it from another one.
///
/// Turning off control or viewing, restarting the network, anything that brings up a Windows administrator prompt
/// (which appears on the secure desktop, where input from another device does not reach): pressed by accident
/// through a screen view or shared input, each of these meant walking over to this machine to undo it. So when the
/// press very likely came from elsewhere — a viewing device pressed something here in the last few seconds, or
/// another device is driving this one's mouse right now — such an action asks first. The question appears where the
/// pointer is, so on the monitor being watched; "Отмена" is the default, "Да" only answers after a moment, so the
/// second half of a double tap cannot press it, and nobody answering means no.
///
/// Someone sitting at this device is not asked: the question is about not being able to reach it, which is not
/// their problem.
/// </summary>
public sealed class RemoteGuard
{
    /// <summary>How recent a press by a viewing device has to be for what happens now to count as done from there.</summary>
    private const long RecentMs = 4000;

    private static readonly TimeSpan ArmAfter = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private readonly InputSharingService _input;
    private readonly ILogger<RemoteGuard> _logger;

    public RemoteGuard(InputSharingService input, ILogger<RemoteGuard> logger)
    {
        _input = input;
        _logger = logger;
    }

    /// <summary>What is being done here right now is most likely being done from another device.</summary>
    public bool IsRemote => RemoteInputTracker.ActiveWithin(RecentMs) || _input.IsBeingControlled;

    /// <summary>
    /// True to go ahead: always when the press is local, otherwise only when it is confirmed here.
    /// </summary>
    /// <param name="what">The action, as a question: "Запретить управление этим устройством?".</param>
    /// <param name="why">What it would do to the connection, so the answer is an informed one.</param>
    /// <param name="yes">The confirming button: "Да, запретить".</param>
    public Task<bool> ConfirmAsync(string what, string why, string yes)
    {
        if (!IsRemote)
        {
            return Task.FromResult(true);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(false);
        }

        return dispatcher.CheckAccess()
            ? Task.FromResult(Ask(what, why, yes))
            : dispatcher.InvokeAsync(() => Ask(what, why, yes)).Task;
    }

    private bool Ask(string what, string why, string yes)
    {
        _logger.LogInformation("Asking before a remote press goes through: {What}", what);
        var window = new Window
        {
            Title = "WinMirroring — подтверждение",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
        };

        var no = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            IsDefault = true,
            MinWidth = 160,
            MinHeight = 48,
            Margin = new Thickness(0, 0, 10, 0),
            FontSize = 16,
            Padding = new Thickness(18, 8, 18, 8),
        };
        var go = new Button
        {
            Content = yes,
            MinWidth = 160,
            MinHeight = 48,
            FontSize = 16,
            Padding = new Thickness(18, 8, 18, 8),
            IsEnabled = false,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        };

        var answer = false;
        no.Click += (_, _) => window.Close();
        go.Click += (_, _) =>
        {
            answer = true;
            window.Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(no);
        buttons.Children.Add(go);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 560 };
        body.Children.Add(new TextBlock
        {
            Text = what,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = why,
            FontSize = 14.5,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Нажатие пришло с другого устройства, поэтому WinMirroring переспрашивает. Без ответа через 30 секунд будет «Отмена».",
            FontSize = 12.5,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(buttons);
        window.Content = body;

        // "Да" answers only after a moment: the second tap of a double tap lands here otherwise.
        var arm = new DispatcherTimer { Interval = ArmAfter };
        arm.Tick += (_, _) =>
        {
            arm.Stop();
            go.IsEnabled = true;
        };

        var giveUp = new DispatcherTimer { Interval = GiveUpAfter };
        giveUp.Tick += (_, _) =>
        {
            giveUp.Stop();
            window.Close();
        };

        window.Loaded += (_, _) =>
        {
            PlaceAtPointer(window);
            arm.Start();
            giveUp.Start();
            no.Focus();
        };
        window.Closed += (_, _) =>
        {
            arm.Stop();
            giveUp.Stop();
        };

        window.ShowDialog();
        _logger.LogInformation("{What}: {Answer}", what, answer ? "confirmed" : "cancelled");
        return answer;
    }

    /// <summary>Centres the question on the monitor the pointer is on: the one being looked at from over there.</summary>
    private static void PlaceAtPointer(Window window)
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var source = PresentationSource.FromVisual(window);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var work = info.rcWork;
        var topLeft = toDip.Transform(new Point(work.Left, work.Top));
        var bottomRight = toDip.Transform(new Point(work.Right, work.Bottom));
        window.Left = topLeft.X + ((bottomRight.X - topLeft.X - window.ActualWidth) / 2);
        window.Top = topLeft.Y + ((bottomRight.Y - topLeft.Y - window.ActualHeight) / 2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
}
