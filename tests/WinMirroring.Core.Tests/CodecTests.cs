using WinMirroring.Core.Discovery;
using WinMirroring.Core.Messages;
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

        var first = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var end = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);

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
            await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
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
        new AudioStopMessage(7),
    };

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
}
