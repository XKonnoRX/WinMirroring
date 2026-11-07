using WinMirroring.Input.Injection;
using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Tests;

public class InjectionCodecTests
{
    /// <summary>Records every call so an encoded-then-applied message can be compared to the original.</summary>
    private sealed class RecordingSink : IInjectionSink
    {
        public List<string> Calls { get; } = [];

        public bool Connected { get; set; } = true;

        public bool IsConnected => Connected;

        public void MoveAbsolute(int x, int y) => Calls.Add($"abs {x} {y}");

        public void MoveRelative(int dx, int dy) => Calls.Add($"rel {dx} {dy}");

        public void Button(MouseButton button, bool down) => Calls.Add($"btn {button} {down}");

        public void Wheel(int delta, bool horizontal) => Calls.Add($"wheel {delta} {horizontal}");

        public void Key(ushort vk, ushort scan, bool up, bool extended) => Calls.Add($"key {vk} {scan} {up} {extended}");

        public void Unicode(char ch, bool up) => Calls.Add($"uni {(int)ch} {up}");

        public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts) =>
            Calls.Add("touch " + string.Join(";", contacts.Select(c => $"{c.Id},{c.X},{c.Y},{c.Flags}")));
    }

    [Fact]
    public void EveryInjectionCallSurvivesEncodeAndApply()
    {
        var sink = new RecordingSink();
        InjectionCodec.Apply(InjectionCodec.MoveAbsolute(-1920, 540), sink);
        InjectionCodec.Apply(InjectionCodec.MoveRelative(1, -1), sink);
        InjectionCodec.Apply(InjectionCodec.Button(MouseButton.X2, true), sink);
        InjectionCodec.Apply(InjectionCodec.Wheel(-120, horizontal: true), sink);
        InjectionCodec.Apply(InjectionCodec.Key(0x1B, 0x01, up: true, extended: false), sink);
        InjectionCodec.Apply(InjectionCodec.Unicode('я', up: false), sink);
        InjectionCodec.Apply(
            InjectionCodec.TouchFrame([new TouchContactInfo(0, 100, 200, 7), new TouchContactInfo(3, 400, 500, 0x40000)]),
            sink);

        Assert.Equal(
            [
                "abs -1920 540",
                "rel 1 -1",
                $"btn {MouseButton.X2} True",
                "wheel -120 True",
                "key 27 1 True False",
                $"uni {(int)'я'} False",
                "touch 0,100,200,7;3,400,500,262144",
            ],
            sink.Calls);
    }

    [Fact]
    public void HandshakeCarriesTheToken()
    {
        var token = Enumerable.Range(0, InjectionCodec.TokenLength).Select(i => (byte)i).ToArray();
        var decoded = InjectionCodec.ReadHandshake(InjectionCodec.Handshake(token));
        Assert.NotNull(decoded);
        Assert.Equal(token, decoded);
        Assert.Null(InjectionCodec.ReadHandshake(InjectionCodec.Bye()));
        Assert.Equal(InjectionCodec.Op.Bye, InjectionCodec.PeekOp(InjectionCodec.Bye()));
    }

    [Fact]
    public void HandshakeRejectsAWrongLengthToken()
    {
        Assert.Throws<ArgumentException>(() => InjectionCodec.Handshake(new byte[InjectionCodec.TokenLength - 1]));
    }

    [Fact]
    public async Task FramingRoundTripsSeveralMessagesInOrder()
    {
        using var stream = new MemoryStream();
        await InjectionCodec.WriteMessageAsync(stream, InjectionCodec.MoveAbsolute(10, 20), CancellationToken.None);
        await InjectionCodec.WriteMessageAsync(stream, InjectionCodec.Button(MouseButton.Left, true), CancellationToken.None);
        await InjectionCodec.WriteMessageAsync(stream, InjectionCodec.Bye(), CancellationToken.None);
        stream.Position = 0;

        var sink = new RecordingSink();
        var seen = new List<InjectionCodec.Op>();
        while (await InjectionCodec.ReadMessageAsync(stream, CancellationToken.None) is { } message)
        {
            var op = InjectionCodec.PeekOp(message);
            seen.Add(op);
            if (op != InjectionCodec.Op.Bye)
            {
                InjectionCodec.Apply(message, sink);
            }
        }

        Assert.Equal([InjectionCodec.Op.MoveAbsolute, InjectionCodec.Op.Button, InjectionCodec.Op.Bye], seen);
        Assert.Equal(["abs 10 20", $"btn {MouseButton.Left} True"], sink.Calls);
    }

    [Fact]
    public async Task ReadRejectsAnAbsurdLength()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]); // length ~2 GB
        await Assert.ThrowsAsync<InvalidDataException>(async () => await InjectionCodec.ReadMessageAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void RouterPrefersTheRemoteOnlyWhileItIsConnected()
    {
        var local = new RecordingSink();
        var remote = new RecordingSink();
        var router = new InjectionRouter(local);

        router.Button(MouseButton.Left, true);           // no remote yet -> local
        router.SetRemote(remote, prefer: true);
        router.Button(MouseButton.Left, false);          // remote connected -> remote
        remote.Connected = false;
        router.MoveAbsolute(5, 6);                        // remote dropped -> local again
        remote.Connected = true;
        router.SetRemote(remote, prefer: false);
        router.MoveAbsolute(7, 8);                        // remote present but not preferred -> local

        Assert.Equal([$"btn {MouseButton.Left} True", "abs 5 6", "abs 7 8"], local.Calls);
        Assert.Equal([$"btn {MouseButton.Left} False"], remote.Calls);
        Assert.False(router.UsingRemote);
    }

    [Fact]
    public void TrackingSinkReleasesWhatIsStillHeld()
    {
        var inner = new RecordingSink();
        var tracking = new TrackingInjectionSink(inner);

        tracking.Key(0x11, 0x1D, up: false, extended: false); // Ctrl down
        tracking.Button(MouseButton.Left, down: true);
        tracking.Key(0x43, 0x2E, up: false, extended: false); // C down
        tracking.Key(0x43, 0x2E, up: true, extended: false);  // C up again
        inner.Calls.Clear();

        tracking.ReleaseAll();

        Assert.Contains("key 17 29 True False", inner.Calls); // Ctrl released
        Assert.Contains($"btn {MouseButton.Left} False", inner.Calls);
        Assert.DoesNotContain(inner.Calls, c => c.StartsWith("key 67", StringComparison.Ordinal)); // C already up
    }
}
