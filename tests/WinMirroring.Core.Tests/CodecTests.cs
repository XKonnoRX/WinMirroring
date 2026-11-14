using WinMirroring.Core.Discovery;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Tests;

public class CodecTests
{
    [Fact]
    public void BeaconRoundTrips()
    {
        var beacon = new Beacon("abc", "Laptop", 47621, 47620, "AABB", IsProbe: true);

        var decoded = Beacon.TryDecode(Beacon.Encode(beacon));

        Assert.Equal(beacon, decoded);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(new byte[] { (byte)'W', (byte)'M', (byte)'I', (byte)'R', 99, (byte)'{', (byte)'}' })]
    [InlineData(new byte[] { (byte)'W', (byte)'M', (byte)'I', (byte)'R', 1, (byte)'{', (byte)'}' })]
    public void BeaconRejectsGarbage(byte[] datagram)
    {
        Assert.Null(Beacon.TryDecode(datagram));
    }

    [Fact]
    public async Task FrameRoundTripsAndEndsCleanly()
    {
        using var stream = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await FrameCodec.WriteAsync(stream, ChannelId.Input, payload, TestContext.Current.CancellationToken);
        await FrameCodec.WriteAsync(stream, ChannelId.Control, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        stream.Position = 0;

        var reader = new FrameCodec.Reader();
        var first = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);
        var end = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(ChannelId.Input, first.Value.Channel);
        Assert.Equal(payload, first.Value.Payload);
        Assert.NotNull(second);
        Assert.Equal(ChannelId.Control, second.Value.Channel);
        Assert.Empty(second.Value.Payload);
        Assert.Null(end);
    }

    [Fact]
    public async Task FrameRejectsOversizedLength()
    {
        using var stream = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF, 0, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FrameCodec.Reader().ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The point of writing a message in parts: another message may be written between two of them (that is what
    /// lets a key press overtake a key frame), and both must come out whole and unmixed.
    /// </summary>
    [Fact]
    public async Task MessagesInPartsSurviveBeingInterleaved()
    {
        using var stream = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;
        var big = new byte[100];
        var other = new byte[40];
        Random.Shared.NextBytes(big);
        Random.Shared.NextBytes(other);

        await FrameCodec.WritePartAsync(stream, ChannelId.Screen, 7, big.AsMemory(0, 60), last: false, ct);
        await FrameCodec.WritePartAsync(stream, ChannelId.Screen, 8, other, last: true, ct);
        await FrameCodec.WriteAsync(stream, ChannelId.Input, new byte[] { 42 }, ct);
        await FrameCodec.WritePartAsync(stream, ChannelId.Screen, 7, big.AsMemory(60), last: true, ct);
        stream.Position = 0;

        var reader = new FrameCodec.Reader();
        var read = new List<Frame>();
        while (await reader.ReadAsync(stream, ct) is { } frame)
        {
            read.Add(frame);
        }

        Assert.Equal(3, read.Count);
        Assert.Equal(other, read[0].Payload);
        Assert.Equal([42], read[1].Payload);
        Assert.Equal(ChannelId.Input, read[1].Channel);
        Assert.Equal(big, read[2].Payload);
        Assert.Equal(ChannelId.Screen, read[2].Channel);
    }

    public static TheoryData<ControlMessage> Messages => new()
    {
        new HelloMessage("id", "name", 1, "Y2VydA==", null, 47621, 47620),
        new HelloMessage("id", "name", 1, "Y2VydA==", "bm9uY2U=", 1, 2),
        new AuthMessage("c2ln", "FP", true),
        new AuthResultMessage(false, false, "bad-signature"),
        new PairRequestMessage(),
        new PairResultMessage(true, null),
        new PairResultMessage(false, "rejected"),
        new PairCancelMessage("timeout"),
        new PingMessage(123456789),
        new PongMessage(42),
        new ByeMessage(null),
        new AudioStartMessage(7, "a2V5", "opus", 48000, 2, 10),
        new AudioStartMessage(7, "a2V5", "opus", 48000, 2, 10, Automatic: true),
        new AudioStopMessage(7),
        new AudioRouteRequestMessage(true),
        new AudioRouteRequestMessage(false, Refuse: true),
        new ScreenRequestMessage(5, 1),
        new ScreenRequestMessage(5, 1, @"\\.\DISPLAY2"),
        new ScreenStreamMessage(5, "monitor", "Монитор 1", 0, 2, true, Codec: "h264", MonitorKey: @"\\.\DISPLAY1"),
        new ScreenStreamMessage(9, "window", "Окно", 0, 0, false, Minimized: true, ScalePercent: 125),
        new ScreenStopMessage(5, "closed"),
        new ScreenCodecFallbackMessage(5, "нет декодера"),
        new LogRequestMessage(3),
        new LogResponseMessage(3, "app.log", "text", null),
        new InputReleaseRequestMessage(),
        new InputBlockedMessage("Диспетчер задач"),
        new DisplayInviteMessage(),
        new DisplayExtendRequestMessage(1920, 1280, 150),
        new DisplayExtendResultMessage(false, "нет драйвера"),
        new KeyboardLayoutMessage(unchecked((int)0xF0020409)),
        new DeviceCapabilitiesMessage(true, IsReply: true),
        new DeviceInfoMessage("1.0.13"),
        new DeviceInfoMessage("1.0.13", IsReply: true),
        new DeviceCapabilitiesMessage(false, CanEncodeH264: true, CanDecodeH264: true),
    };

    [Fact]
    public void CapabilitiesCarryTheMonitorList()
    {
        var message = new DeviceCapabilitiesMessage(false, false,
        [
            new RemoteMonitor(0, "1920×1080", true),
            new RemoteMonitor(2, "2560×1440", false),
        ]);

        var decoded = Assert.IsType<DeviceCapabilitiesMessage>(ControlMessageCodec.TryDecode(ControlMessageCodec.Encode(message)));

        Assert.False(decoded.HasVirtualDisplay);
        Assert.False(decoded.IsReply);
        Assert.Equal(message.Monitors, decoded.Monitors);
    }

    [Fact]
    public void MonitorListsAndPerMonitorLayoutCellsRoundTrip()
    {
        var monitors = new InputMonitorsMessage([new LayoutMonitor(@"\\.\DISPLAY1", "Монитор 1 · 1920×1080 · основной", true)], IsReply: true);
        var decodedMonitors = Assert.IsType<InputMonitorsMessage>(ControlMessageCodec.TryDecode(ControlMessageCodec.Encode(monitors)));
        Assert.True(decodedMonitors.IsReply);
        Assert.Equal(monitors.Monitors, decodedMonitors.Monitors);

        var layout = new LayoutMessage(42, [new LayoutCell("laptop", 0, 0, @"\\.\DISPLAY2"), new LayoutCell("tablet", 1, 0)]);
        var decodedLayout = Assert.IsType<LayoutMessage>(ControlMessageCodec.TryDecode(ControlMessageCodec.Encode(layout)));
        Assert.Equal(layout.Cells, decodedLayout.Cells);
    }

    [Fact]
    public void ALayoutFromAnOlderDeviceHasWholeDeviceCells()
    {
        var json = """{"t":"layout","version":7,"cells":[{"deviceId":"pc","column":1,"row":2}]}"""u8.ToArray();

        var decoded = Assert.IsType<LayoutMessage>(ControlMessageCodec.TryDecode(json));

        Assert.Equal(new LayoutCell("pc", 1, 2), Assert.Single(decoded.Cells));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void ControlMessagesRoundTrip(ControlMessage message)
    {
        var decoded = ControlMessageCodec.TryDecode(ControlMessageCodec.Encode(message));

        Assert.Equal(message, decoded);
    }

    [Fact]
    public void ControlMessageCodecReturnsNullForUnknownOrBrokenInput()
    {
        Assert.Null(ControlMessageCodec.TryDecode("{\"t\":\"from-the-future\"}"u8));
        Assert.Null(ControlMessageCodec.TryDecode("not json"u8));
        Assert.Null(ControlMessageCodec.TryDecode("{}"u8));
    }

    [Fact]
    public void ScreenStreamMessageFromOlderPeerDefaultsToJpeg()
    {
        // A peer built before the H.264 path omits "codec"; it must read as the tile codec, not crash.
        var message = ControlMessageCodec.TryDecode(
            "{\"t\":\"screen-stream\",\"streamId\":5,\"kind\":\"monitor\",\"title\":\"Монитор 1\",\"monitorIndex\":0,\"monitorCount\":2,\"controlAllowed\":true}"u8);

        var stream = Assert.IsType<ScreenStreamMessage>(message);
        Assert.Equal("jpeg", stream.Codec);
        Assert.Equal(5u, stream.StreamId);
    }
}
