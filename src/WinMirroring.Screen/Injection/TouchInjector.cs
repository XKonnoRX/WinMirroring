using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Input.Injection;

namespace WinMirroring.Screen.Injection;

/// <summary>
/// Real touch contacts on this device, so swipes scroll, pinches zoom and press-and-hold acts as it does on a touch
/// screen, instead of everything becoming mouse clicks. Works without touch hardware. Every injection carries all
/// active contacts, and a keepalive re-sends them while fingers rest. The injection itself goes through an
/// <see cref="IInjectionSink"/>, so it reaches administrator windows when the elevated helper is on.
/// </summary>
internal sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;

    /// <summary>How often resting fingers are re-sent, so the system does not decide they have gone.</summary>
    private const int KeepAliveMs = 50;
    private const uint PointerFlagInRange = 0x00000002;
    private const uint PointerFlagInContact = 0x00000004;
    private const uint PointerFlagDown = 0x00010000;
    private const uint PointerFlagUpdate = 0x00020000;
    private const uint PointerFlagUp = 0x00040000;
    private const uint PointerFlagCanceled = 0x00008000;

    private readonly IInjectionSink _sink;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Point> _contacts = [];
    private readonly Timer _keepAlive;
    private long _lastInjectTicks;
    private bool _ticking;

    public TouchInjector(ILogger logger, IInjectionSink? sink = null)
    {
        _ = logger;
        _sink = sink ?? InjectionRouter.Instance;

        // Stopped until a finger arrives; see KeepAliveWhileFingersRest.
        _keepAlive = new Timer(_ => KeepAlive(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public enum Action : byte
    {
        Down = 1,
        Move = 2,
        Up = 3,
        Cancel = 4,
    }

    public void Apply(Action action, uint id, Point point)
    {
        id %= MaxContacts;
        lock (_gate)
        {
            switch (action)
            {
                case Action.Down:
                    _contacts[id] = point;
                    Inject(id, PointerFlagDown | PointerFlagInRange | PointerFlagInContact);
                    break;

                case Action.Move when _contacts.ContainsKey(id):
                    _contacts[id] = point;
                    Inject(id, PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact);
                    break;

                case Action.Up when _contacts.ContainsKey(id):
                    _contacts[id] = point;
                    Inject(id, PointerFlagUp);
                    _contacts.Remove(id);
                    break;

                case Action.Cancel when _contacts.ContainsKey(id):
                    Inject(id, PointerFlagUp | PointerFlagCanceled);
                    _contacts.Remove(id);
                    break;
            }

            KeepAliveWhileFingersRest();
        }
    }

    /// <summary>Whether the keepalive is ticking, which it should be exactly while something is touched.</summary>
    internal bool KeepingAlive
    {
        get
        {
            lock (_gate)
            {
                return _ticking;
            }
        }
    }

    /// <summary>Lifts every finger (stream stopped, connection lost).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var id in _contacts.Keys.ToList())
            {
                Inject(id, PointerFlagUp | PointerFlagCanceled);
                _contacts.Remove(id);
            }

            KeepAliveWhileFingersRest();
        }
    }

    /// <summary>
    /// The keepalive runs while something is being touched and not otherwise. It used to tick twenty times a
    /// second from the moment a stream was opened, finding nothing to do every time: a finger resting is rare and
    /// a stream with nobody touching it is the usual state. Called with the lock held.
    /// </summary>
    private void KeepAliveWhileFingersRest()
    {
        var wanted = _contacts.Count > 0;
        if (wanted == _ticking)
        {
            return;
        }

        _ticking = wanted;
        var every = wanted ? KeepAliveMs : Timeout.Infinite;
        _keepAlive.Change(every, every);
    }

    private void KeepAlive()
    {
        lock (_gate)
        {
            if (_contacts.Count == 0 || Environment.TickCount64 - _lastInjectTicks < KeepAliveMs)
            {
                return;
            }

            Inject(null, 0);
        }
    }

    /// <summary>Injects all contacts; <paramref name="changed"/> gets <paramref name="flags"/>, the others an update.</summary>
    private void Inject(uint? changed, uint flags)
    {
        if (_contacts.Count == 0)
        {
            return;
        }

        var frame = new List<TouchContactInfo>(_contacts.Count);
        foreach (var (id, point) in _contacts)
        {
            var contactFlags = id == changed ? flags : PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact;
            frame.Add(new TouchContactInfo(id, point.X, point.Y, contactFlags));
        }

        _sink.TouchFrame(frame);
        _lastInjectTicks = Environment.TickCount64;
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }
}
