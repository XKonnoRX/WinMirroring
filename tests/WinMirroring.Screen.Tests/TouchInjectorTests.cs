using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Input.Injection;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Injection;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Touch contacts injected on behalf of a device watching this screen. A frame carries every contact that is down,
/// and while fingers rest on the glass nothing new arrives — so a keepalive re-sends them, or Windows decides the
/// fingers have gone.
///
/// Which is worth exactly as much as it costs, and it used to cost all the time: the keepalive ticked twenty times
/// a second from the moment a stream opened until it closed, and a stream with nobody touching it is the usual
/// state of one. It now runs while something is touched and not otherwise.
/// </summary>
public class TouchInjectorTests
{
    [Fact]
    public void TheKeepaliveRunsOnlyWhileSomethingIsTouched()
    {
        using var touch = new TouchInjector(NullLogger.Instance, new NowhereSink());

        Assert.False(touch.KeepingAlive);

        touch.Apply(TouchInjector.Action.Down, 1, new Point(10, 10));
        Assert.True(touch.KeepingAlive);

        // A second finger and the first one lifting: still something down, so it keeps going.
        touch.Apply(TouchInjector.Action.Down, 2, new Point(20, 20));
        touch.Apply(TouchInjector.Action.Up, 1, new Point(10, 10));
        Assert.True(touch.KeepingAlive);

        touch.Apply(TouchInjector.Action.Up, 2, new Point(20, 20));
        Assert.False(touch.KeepingAlive);
    }

    /// <summary>Lifting everything at once — a stream closing, a connection lost — stops it as well.</summary>
    [Fact]
    public void ReleasingEverythingStopsTheKeepalive()
    {
        using var touch = new TouchInjector(NullLogger.Instance, new NowhereSink());
        touch.Apply(TouchInjector.Action.Down, 1, new Point(5, 5));
        Assert.True(touch.KeepingAlive);

        touch.ReleaseAll();

        Assert.False(touch.KeepingAlive);
    }

    /// <summary>Injection that reaches no operating system: these tests are about the clock, not the fingers.</summary>
    private sealed class NowhereSink : IInjectionSink
    {
        public bool IsConnected => true;

        public void MoveAbsolute(int x, int y)
        {
        }

        public void MoveRelative(int dx, int dy)
        {
        }

        public void Button(MouseButton button, bool down)
        {
        }

        public void Wheel(int delta, bool horizontal)
        {
        }

        public void Key(ushort virtualKey, ushort scanCode, bool up, bool extended)
        {
        }

        public void Unicode(char ch, bool up)
        {
        }

        public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts)
        {
        }
    }
}
