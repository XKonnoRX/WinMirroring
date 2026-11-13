using System.Buffers;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Tests;

public class InputCodecTests
{
    [Fact]
    public void EveryEventKindRoundTripsInOneFrame()
    {
        var writer = new ArrayBufferWriter<byte>();
        InputCodec.WriteMouseMove(writer, 0xFFFF_FFF0, -12, 345);
        InputCodec.WriteMouseButton(writer, MouseButton.X2, down: true);
        InputCodec.WriteMouseWheel(writer, -120, horizontal: true);
        InputCodec.WriteKey(writer, 0x41, 0x1E, up: true, extended: true);
        InputCodec.WriteEnter(writer, Edge.Bottom, 0.25f);
        InputCodec.WriteEnterAtCenter(writer);
        InputCodec.WriteLeave(writer);
        InputCodec.WriteEdgeReached(writer, Edge.Right, 1f);
        InputCodec.WriteAlive(writer);

        var events = ReadAll(writer.WrittenSpan);

        Assert.Equal(9, events.Count);
        Assert.Equal((InputEventKind.MouseMove, 0xFFFF_FFF0u, -12, 345), (events[0].Kind, events[0].Timestamp, events[0].Dx, events[0].Dy));
        Assert.Equal((InputEventKind.MouseButton, MouseButton.X2, true), (events[1].Kind, events[1].Button, events[1].Down));
        Assert.Equal((InputEventKind.MouseWheel, -120, true), (events[2].Kind, events[2].WheelDelta, events[2].Horizontal));
        Assert.Equal((InputEventKind.Key, (ushort)0x41, (ushort)0x1E, true, true), (events[3].Kind, events[3].VirtualKey, events[3].ScanCode, events[3].Up, events[3].Extended));
        Assert.Equal((InputEventKind.Enter, Edge.Bottom, 0.25f, false), (events[4].Kind, events[4].Edge, events[4].Relative, events[4].AtCenter));
        Assert.Equal((InputEventKind.Enter, true), (events[5].Kind, events[5].AtCenter));
        Assert.Equal(InputEventKind.Leave, events[6].Kind);
        Assert.Equal((InputEventKind.EdgeReached, Edge.Right, 1f, false), (events[7].Kind, events[7].Edge, events[7].Relative, events[7].AtCenter));
        Assert.Equal(InputEventKind.Alive, events[8].Kind);
    }

    [Fact]
    public void MonitorEventsCarryTheMonitorAndBothPositions()
    {
        var writer = new ArrayBufferWriter<byte>();
        InputCodec.WriteEnterOnMonitor(writer, Edge.Left, 0.3f, @"\\.\DISPLAY2");
        InputCodec.WriteEdgeReachedOnMonitor(writer, Edge.Top, 0.75f, 0.4f, @"\\.\DISPLAY1");
        InputCodec.WriteEdgeReached(writer, Edge.Bottom, 0.6f);
        InputCodec.WriteAlive(writer);

        var events = ReadAll(writer.WrittenSpan);

        Assert.Equal(4, events.Count);
        Assert.Equal((InputEventKind.EnterOnMonitor, Edge.Left, 0.3f, @"\\.\DISPLAY2", false), (events[0].Kind, events[0].Edge, events[0].Relative, events[0].Monitor, events[0].AtCenter));
        Assert.Equal((InputEventKind.EdgeReachedOnMonitor, Edge.Top, 0.75f, 0.4f, @"\\.\DISPLAY1"), (events[1].Kind, events[1].Edge, events[1].Relative, events[1].RelativeToScreen, events[1].Monitor));

        // The plain event from an older device: no monitor, and its position is along the whole desktop.
        Assert.Equal((InputEventKind.EdgeReached, 0.6f, 0.6f, (string?)null), (events[2].Kind, events[2].Relative, events[2].RelativeToScreen, events[2].Monitor));
    }

    [Fact]
    public void ATruncatedMonitorEventIsNotRead()
    {
        var frame = InputCodec.Single(w => InputCodec.WriteEnterOnMonitor(w, Edge.Right, 0.5f, "MON"));

        Assert.False(InputCodec.TryRead(frame.AsSpan(0, frame.Length - 1), out _, out _));
        Assert.True(InputCodec.TryRead(frame, out var evt, out var consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal("MON", evt.Monitor);
    }

    [Fact]
    public void MouseMoveClampsToInt16()
    {
        var writer = new ArrayBufferWriter<byte>();
        InputCodec.WriteMouseMove(writer, 1, 100_000, -100_000);

        var events = ReadAll(writer.WrittenSpan);

        Assert.Equal(short.MaxValue, events[0].Dx);
        Assert.Equal(short.MinValue, events[0].Dy);
    }

    [Fact]
    public void TruncatedAndUnknownInputStopsTheReader()
    {
        Assert.False(InputCodec.TryRead([], out _, out _));
        Assert.False(InputCodec.TryRead([(byte)InputEventKind.MouseMove, 1, 2, 3, 4, 5], out _, out _));
        Assert.False(InputCodec.TryRead([0xEE, 1, 2, 3, 4, 5, 6], out _, out _));
    }

    [Fact]
    public void SingleProducesExactlyOneEvent()
    {
        var frame = InputCodec.Single(w => InputCodec.WriteMouseButton(w, MouseButton.Left, false));

        Assert.Equal(3, frame.Length);
        Assert.True(InputCodec.TryRead(frame, out var evt, out var consumed));
        Assert.Equal(3, consumed);
        Assert.Equal(MouseButton.Left, evt.Button);
        Assert.False(evt.Down);
    }

    private static List<InputEvent> ReadAll(ReadOnlySpan<byte> data)
    {
        var list = new List<InputEvent>();
        while (InputCodec.TryRead(data, out var evt, out var consumed))
        {
            list.Add(evt);
            data = data[consumed..];
        }

        Assert.True(data.IsEmpty, "trailing bytes left unread");
        return list;
    }
}
