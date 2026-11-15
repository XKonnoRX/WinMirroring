using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Power;

/// <summary>
/// Whether this device is spending less at the moment, and the few numbers that depend on it.
///
/// Asked from loops that run hundreds of times a second, so the answer is worked out at most every few seconds and
/// handed out from memory in between. Windows is asked only about the one thing it alone knows — whether there is
/// a cable in the socket; everything else is the user's setting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerMode
{
    /// <summary>How long an answer about the mains is kept before Windows is asked again.</summary>
    private static readonly long AskAgainAfterMs = 5_000;

    private readonly AppSettings _settings;
    private readonly Func<bool> _ask;
    private readonly long _keepAnswerFor;
    private long _askedAt = long.MinValue;
    private bool _onBattery;

    /// <summary>
    /// <paramref name="onBattery"/> replaces asking Windows, which is for tests — and an answer that costs
    /// nothing is not worth keeping, so a supplied one is read every time rather than remembered for seconds.
    /// </summary>
    public PowerMode(AppSettings settings, Func<bool>? onBattery = null)
    {
        _settings = settings;
        _ask = onBattery ?? AskWindows;
        _keepAnswerFor = onBattery is null ? AskAgainAfterMs : 0;
    }

    /// <summary>Whether the device is running off its battery; false when it has none or Windows will not say.</summary>
    public bool OnBattery
    {
        get
        {
            var now = Environment.TickCount64;
            if (_keepAnswerFor > 0 && now - Volatile.Read(ref _askedAt) < _keepAnswerFor)
            {
                return Volatile.Read(ref _onBattery);
            }

            var answer = _ask();
            Volatile.Write(ref _onBattery, answer);
            Volatile.Write(ref _askedAt, now);
            return answer;
        }
    }

    /// <summary>Whether to spend less right now.</summary>
    public bool Saving => _settings.Power.Saving switch
    {
        PowerSaving.Always => true,
        PowerSaving.OnBattery => OnBattery,
        _ => false,
    };

    /// <summary>Frames a second this device will send or ask for, given what it is set to and how it is powered.</summary>
    public int Fps(int configured) => Saving ? Math.Min(configured, Math.Max(1, _settings.Power.Fps)) : configured;

    /// <summary>
    /// One of a pair of intervals, the longer one while saving. Written this way so that every place that has a
    /// period to choose says both numbers in one line and neither is hidden somewhere else.
    /// </summary>
    public TimeSpan Every(TimeSpan usually, TimeSpan whileSaving) => Saving ? whileSaving : usually;

    private static bool AskWindows()
    {
        try
        {
            // 0 means no mains; 1 means mains; 255 means Windows does not know, and not knowing is not a reason
            // to start cutting things down.
            return GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
