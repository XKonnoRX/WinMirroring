using System.Runtime.InteropServices;

namespace WinMirroring.Screen;

/// <summary>
/// Asks Windows for a one-millisecond timer while anything is being streamed, and gives it back afterwards.
///
/// By default a process's waits are rounded up to the system timer's tick, which is about fifteen and a half
/// milliseconds. That is half a frame at thirty a second, and both the places that matter here are shorter than
/// it: the pacing between frames asks to wait the remainder of a frame, often five or ten milliseconds, and the
/// encoder's event pump waits a millisecond between looks. Rounded up, a thirty-frame stream came out at twenty to
/// twenty-five, unevenly — which looks exactly like a stuttering picture — and every frame carried up to fifteen
/// extra milliseconds of delay through the encoder.
///
/// Since Windows 10 this affects only the process that asks, so it costs other applications nothing. It does keep
/// this one a little busier, which is why it is held only while a stream is actually running.
/// </summary>
internal static class MediaTimerResolution
{
    private const uint Milliseconds = 1;

    private static readonly object Gate = new();
    private static int _users;
    private static bool _granted;

    /// <summary>Takes the fine timer; dispose the result to give it back.</summary>
    public static IDisposable Hold()
    {
        lock (Gate)
        {
            if (_users++ == 0)
            {
                try
                {
                    _granted = timeBeginPeriod(Milliseconds) == 0;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _granted = false;
                }
            }
        }

        return new Holder();
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (--_users > 0 || !_granted)
            {
                return;
            }

            _granted = false;
            try
            {
                timeEndPeriod(Milliseconds);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint period);

    private sealed class Holder : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Release();
            }
        }
    }
}
