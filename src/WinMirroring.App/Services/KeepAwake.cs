using System.Runtime.InteropServices;
using Serilog;

namespace WinMirroring.App.Services;

/// <summary>
/// Keeps the display on (and the device from sleeping) while a screen is being watched: here, in a view of another
/// device, or there, when another device shows this device's screen. Windows only counts local input as activity, so
/// without this a tablet that just shows a stream dims and locks after its usual timeout.
/// </summary>
/// <remarks>The request belongs to the calling thread: call it on the UI thread only.</remarks>
internal static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static bool _on;

    public static void Set(bool on)
    {
        if (on == _on)
        {
            return;
        }

        var flags = on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous;
        if (SetThreadExecutionState(flags) == 0)
        {
            Log.Warning("Could not {Action} the display", on ? "keep on" : "release");
            return;
        }

        _on = on;
        Log.Information(on ? "Display kept on while a screen is watched" : "Display may turn off again");
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
