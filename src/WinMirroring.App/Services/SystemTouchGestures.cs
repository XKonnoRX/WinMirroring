using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinMirroring.App.Services;

/// <summary>
/// Whether Windows keeps the gestures of three and four fingers on this device's touch screen for itself.
///
/// Windows 11 has such gestures of its own — three fingers sideways go to the program used before, up shows all
/// windows, down the desktop — and while they are on it takes them before any program is given the touches. They
/// are on unless switched off ("Three- and four-finger touch gestures" under Bluetooth and devices, Touch), and
/// there is nothing a program can do about it for its own window: the switch is the person's. All this does is
/// know, so that the window can say why its swipe between monitors does nothing instead of leaving that a puzzle.
/// </summary>
internal static class SystemTouchGestures
{
    private const int SmDigitizer = 94;
    private const int SmMaximumTouches = 95;
    private const int IntegratedTouch = 0x01;
    private const int ExternalTouch = 0x02;
    private const int Ready = 0x80;

    /// <summary>
    /// True where there is a touch screen to put a finger on, built in or plugged in, and it is working. What is
    /// only for fingers — the grips at the edges of a view — is shown where this is so.
    /// </summary>
    public static bool HasTouchScreen
    {
        get
        {
            var digitizer = GetSystemMetrics(SmDigitizer);
            return (digitizer & Ready) != 0 && (digitizer & (IntegratedTouch | ExternalTouch)) != 0
                && GetSystemMetrics(SmMaximumTouches) > 0;
        }
    }

    /// <summary>
    /// True where a swipe of three fingers will not reach this program: there is a touch screen that takes three
    /// fingers, the system is one that has these gestures (Windows 11), and they have not been switched off.
    /// </summary>
    public static bool TakeThreeFingers
    {
        get
        {
            try
            {
                var digitizer = GetSystemMetrics(SmDigitizer);
                if ((digitizer & Ready) == 0 || (digitizer & (IntegratedTouch | ExternalTouch)) == 0
                    || GetSystemMetrics(SmMaximumTouches) < 3
                    || Environment.OSVersion.Version.Build < 22000)
                {
                    return false;
                }

                // Absent means on: the value appears only once the switch has been used.
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                return key?.GetValue("TouchGestureSetting") is not int setting || setting != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
