using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Timing;

/// <summary>
/// Sub-millisecond sleeps without raising the global timer resolution: a high-resolution waitable timer
/// (Windows 10 1803+). Falls back to timeBeginPeriod(1) + Thread.Sleep on older systems.
/// Use from a single thread.
/// </summary>
internal sealed class PrecisionSleeper : IDisposable
{
    private readonly bool _raisedTimerResolution;
    private nint _timer;

    public PrecisionSleeper()
    {
        _timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_timer == 0)
        {
            timeBeginPeriod(1);
            _raisedTimerResolution = true;
        }
    }

    public void Sleep(long microseconds)
    {
        if (microseconds <= 0)
        {
            return;
        }

        if (_timer != 0)
        {
            var due = -microseconds * 10; // relative, in 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
            {
                WaitForSingleObject(_timer, INFINITE);
                return;
            }
        }

        Thread.Sleep((int)Math.Max(1, microseconds / 1000));
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }

        if (_raisedTimerResolution)
        {
            timeEndPeriod(1);
        }
    }
}
