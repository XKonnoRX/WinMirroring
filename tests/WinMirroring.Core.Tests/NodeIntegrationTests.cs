using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Tests;

/// <summary>Two real nodes on loopback: discovery by probe, PIN pairing, pinned reconnect.</summary>
public sealed class NodeIntegrationTests : IAsyncLifetime
{
    private readonly Dictionary<MirroringNode, FixedAnswerPairingUi> _ui = [];

    private readonly List<MirroringNode> _nodes = [];
    private readonly List<string> _directories = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            var thumbprint = node.Settings.CertificateThumbprint;
            await node.DisposeAsync();
            DeviceIdentityStore.RemoveFromStore(thumbprint);
        }

        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task TwoNodesDiscoverPairAndReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("TestA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("TestB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();

        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        var paired = await a.PairAsync(b.Identity.Id, ct);
        Assert.True(paired, "pairing failed");

        // Nothing typed anywhere; both screens showed the same confirmation code.
        Assert.NotNull(_ui[a].ShownCode);
        Assert.Equal(_ui[a].ShownCode, _ui[b].ShownCode);

        await WaitUntilAsync(() => IsReady(a, b), TimeSpan.FromSeconds(10), "A has no ready session to B", ct);
        await WaitUntilAsync(() => IsReady(b, a), TimeSpan.FromSeconds(10), "B has no ready session to A", ct);

        var pairedRecordOnA = Assert.Single(a.Settings.PairedPeers);
        Assert.Equal(b.Identity.Id, pairedRecordOnA.Id);
        Assert.Equal(b.Identity.Fingerprint, pairedRecordOnA.Fingerprint);
        var pairedRecordOnB = Assert.Single(b.Settings.PairedPeers);
        Assert.Equal(a.Identity.Fingerprint, pairedRecordOnB.Fingerprint);

        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var live));
        var rtt = await live.PingAsync(TimeSpan.FromSeconds(2), ct);
        Assert.NotNull(rtt);
        Assert.InRange(rtt.Value.TotalMilliseconds, 0, 1000);
        Assert.True(Environment.TickCount64 - live.LastReceivedTicks < 2000);

        // Both sides are paired now, so a dropped link must come back on its own through discovery.
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var first));
        await first.CloseAsync("test");

        await WaitUntilAsync(
            () => a.Sessions.TryGetSession(b.Identity.Id, out var again) && again.State == SessionState.Ready && !ReferenceEquals(again, first),
            TimeSpan.FromSeconds(30), "A did not reconnect to B", ct);
        await WaitUntilAsync(() => IsReady(b, a), TimeSpan.FromSeconds(10), "B did not get the new session", ct);
    }

    [Fact]
    public async Task RejectedPairingIsNotStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("RejectA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("RejectB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"], accept: false);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        var paired = await a.PairAsync(b.Identity.Id, ct);

        Assert.False(paired);
        Assert.Empty(a.Settings.PairedPeers);
        Assert.Empty(b.Settings.PairedPeers);
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var session));
        Assert.Equal(SessionState.Unpaired, session.State);
    }

    /// <summary>
    /// A peer starts talking the moment a session is ready, and the features attach their handlers from that same
    /// event — so its opening statements could be read with nobody listening yet and were logged as unhandled and
    /// dropped. That is how a device that had never saved a device grid could fail to learn one at all. A handler
    /// that subscribes now also gets what it missed.
    /// </summary>
    [Fact]
    public async Task AHandlerThatSubscribesLateStillGetsWhatThePeerSaidFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("HeldA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("HeldB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct), "A could not pair with B");
        await WaitUntilAsync(() => IsReady(a, b), TimeSpan.FromSeconds(10), "A has no ready session to B", ct);

        // Each device tells the other who it is paired with as soon as the session is ready. Subscribing now, long
        // after that message has been read, must still produce it.
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var session));
        await WaitUntilAsync(
            () => Replayed(session).OfType<PeerVouchMessage>().Any(),
            TimeSpan.FromSeconds(15), "a late handler was told nothing about what B said first", ct);
    }

    /// <summary>
    /// Forgetting a device used to be one-sided. It went out of the list here and went on living in the list over
    /// there — offered in menus, and introduced to other devices as something that one knows — while the device
    /// that had been forgotten had no way to find out, and none to be told if it was switched off at the time.
    ///
    /// Nothing is said for it even now: the handshake already asks both sides whether they have the other paired,
    /// and a session that is merely Unpaired with a device this one does have paired can only mean the answer
    /// over there was no. So the news arrives by the forgotten device simply connecting again.
    /// </summary>
    [Fact]
    public async Task ForgettingADeviceReachesTheDeviceThatWasForgotten()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ForgetA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("ForgetB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct), "A could not pair with B");
        await WaitUntilAsync(() => IsReady(b, a), TimeSpan.FromSeconds(10), "B has no ready session to A", ct);

        var toldOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Sessions.UnpairedByPeer += peer => toldOnB.TrySetResult(peer);

        await a.Sessions.UnpairAsync(b.Identity.Id);
        Assert.Empty(a.Settings.PairedPeers);

        // B still has A, and still tries to reach it, which is how it finds out.
        await WaitUntilAsync(
            () => b.Settings.PairedPeers.Count == 0,
            TimeSpan.FromSeconds(45), "B never learned that A had forgotten it", ct);

        Assert.Equal("ForgetA", await toldOnB.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.True(b.Peers.TryGet(a.Identity.Id, out var remembered));
        Assert.False(remembered.IsPaired);
    }

    /// <summary>
    /// And a pairing that never happened is not a pairing that was broken. Both sides are unpaired while one is
    /// asking the other, and when the answer is no they stay that way — which must not look to either of them
    /// like the other has just forgotten it, or the first attempt at pairing would tear up a list for nothing.
    /// </summary>
    [Fact]
    public async Task APairingThatWasRefusedIsNotTakenForOneThatWasBroken()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("RefusedA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("RefusedB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"], accept: false);

        var told = new List<string>();
        a.Sessions.UnpairedByPeer += peer => { lock (told) { told.Add(peer); } };
        b.Sessions.UnpairedByPeer += peer => { lock (told) { told.Add(peer); } };

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        Assert.False(await a.PairAsync(b.Identity.Id, ct));
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var session));
        Assert.Equal(SessionState.Unpaired, session.State);

        lock (told)
        {
            Assert.Empty(told);
        }
    }

    /// <summary>What a handler subscribing right now is handed about the session so far.</summary>
    private static List<ControlMessage> Replayed(PeerSession session)
    {
        var held = new List<ControlMessage>();
        void Collect(PeerSession _, ControlMessage message) => held.Add(message);
        session.ControlMessageReceived += Collect;
        session.ControlMessageReceived -= Collect;
        return held;
    }

    /// <summary>
    /// The app disposes its services in order and the service container disposes them again afterwards; a second
    /// dispose that throws used to abort the exit before the app shut down, leaving it running with no windows.
    /// </summary>
    /// <summary>
    /// A rename is kept and introduces the device. Both halves, because either one without the other looks
    /// exactly like a rename that did not work: the name used to be saved only by the button that applies network
    /// settings, in another section, so a name typed and left alone came back as it had been — and a name saved
    /// but not put on the identity would still introduce the device by the old one in every handshake and every
    /// announcement.
    /// </summary>
    [Fact]
    public void ARenameIsKeptAndIsWhatTheDeviceIntroducesItselfBy()
    {
        var node = CreateNode("BeforeTheRename", FreeUdpPort(), FreeTcpPort(), ["127.0.0.1:1"]);

        var taken = node.Rename("  После переименования  ");

        Assert.Equal("После переименования", taken);
        Assert.Equal("После переименования", node.Identity.Name);
        Assert.Equal("После переименования", new Settings.SettingsStore(node.SettingsStore.DataDirectory).Load().DeviceName);

        // And an empty name is this machine's own, rather than a device with no name at all.
        Assert.Equal(Environment.MachineName, node.Rename("   "));
        Assert.Equal(Environment.MachineName, new Settings.SettingsStore(node.SettingsStore.DataDirectory).Load().DeviceName);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        var node = CreateNode("Twice", FreeUdpPort(), FreeTcpPort(), ["127.0.0.1:1"]);
        await node.StartAsync();

        await node.DisposeAsync();
        await node.DisposeAsync();
    }

    /// <summary>
    /// Three devices in a chain: A and C each know only B. Pairing is confirmed twice, A with B and C with B, and
    /// after that A and C recognise each other from one press — no third confirmation, and no code on anyone's
    /// screen — and reach each other directly, with B out of the way.
    /// </summary>
    [Fact]
    public async Task ADeviceTrustedByBothEndsIntroducesThemToEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var discoveryC = FreeUdpPort();

        // A and C are told about B and nothing else: anything they learn of each other comes through B.
        var a = CreateNode("ChainA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("ChainB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}", $"127.0.0.1:{discoveryC}"]);
        var c = CreateNode("ChainC", discoveryC, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);

        await a.StartAsync();
        await b.StartAsync();
        await c.StartAsync();

        await WaitUntilAsync(() => a.Peers.TryGet(b.Identity.Id, out var p) && p.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct), "A could not pair with B");

        await WaitUntilAsync(() => c.Peers.TryGet(b.Identity.Id, out var p) && p.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "C never discovered B", ct);
        Assert.True(await c.PairAsync(b.Identity.Id, ct), "C could not pair with B");

        await WaitUntilAsync(() => IsReady(a, b) && IsReady(b, a) && IsReady(c, b) && IsReady(b, c),
            TimeSpan.FromSeconds(20), "the chain never came up", ct);

        // B tells each end who else it is paired with.
        await WaitUntilAsync(() => a.Introductions.TryGet(c.Identity.Id, out _),
            TimeSpan.FromSeconds(20), "A was never told about C", ct);

        var known = Assert.Single(a.Introductions.Snapshot());
        Assert.Equal(
            (c.Identity.Id, c.Identity.Fingerprint, b.Settings.DeviceName),
            (known.Id, known.Fingerprint, known.ViaName));
        Assert.DoesNotContain(a.Settings.PairedPeers, p => p.Id == c.Identity.Id);

        // One press on A, and nothing is confirmed anywhere.
        Assert.True(await a.Introductions.TrustAsync(c.Identity.Id), "trusting the introduction failed");
        Assert.Empty(a.Introductions.Snapshot());
        Assert.Equal(c.Identity.Fingerprint, a.Settings.PairedPeers.Single(p => p.Id == c.Identity.Id).Fingerprint);

        // That press travels back through B, so C trusts A without being asked either.
        await WaitUntilAsync(() => c.Settings.PairedPeers.Any(p => p.Id == a.Identity.Id),
            TimeSpan.FromSeconds(20), "C was never told that A had accepted", ct);
        Assert.Equal(a.Identity.Fingerprint, c.Settings.PairedPeers.Single(p => p.Id == a.Identity.Id).Fingerprint);

        // And now they talk to each other, not through B.
        await WaitUntilAsync(() => IsReady(a, c) && IsReady(c, a),
            TimeSpan.FromSeconds(40), "A and C never connected to each other", ct);
    }

    /// <summary>
    /// The rule that keeps a chain from being a way in: the device in the middle only ever carries a device's own
    /// record, proved on that very connection, and only to a device it is itself paired with.
    /// </summary>
    [Fact]
    public void ADeviceMayOnlyEverPassOnItsOwnRecord()
    {
        var mine = new VouchedPeer("sender", "Sender", "AA:BB", 1, 2);
        var someoneElse = new VouchedPeer("stranger", "Stranger", "CC:DD", 1, 2);

        Assert.True(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", true, true));

        // Passing on a record that is not the sender's own: this is the one that must never go through.
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", someoneElse), "sender", "AA:BB", true, true));

        // Its own id with someone else's fingerprint is the same attempt wearing a hat.
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine with { Fingerprint = "CC:DD" }), "sender", "AA:BB", true, true));

        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", senderIsPaired: false, targetIsPaired: true));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", senderIsPaired: true, targetIsPaired: false));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("", mine), "sender", "AA:BB", true, true));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("sender", mine), "sender", "AA:BB", true, true));
    }

    /// <summary>
    /// Trusting an introduced device is done on one side first: the record is written here, and the far end only
    /// hears of it when the introduction has travelled back through the device in the middle — if that device has
    /// a session with it at all. A direct connection made in between finds this side saying "paired" and the far
    /// side saying "not", which is exactly what a device that has forgotten this one looks like, and was taken for
    /// it: the record just written was deleted and the person was told the device had unpaired. With nothing to
    /// carry the introduction back, that happened every time.
    ///
    /// Two nodes are enough to hold it. What the device in the middle does is hand each end the other's record,
    /// and that is done here by hand, with the pause between the two that the fault lived in.
    /// </summary>
    [Fact]
    public async Task ADeviceTrustedOnAnothersWordHasNotForgottenUsJustBecauseItHasNotHeardYet()
    {
        var ct = TestContext.Current.CancellationToken;

        // Neither is told about the other: the address A finds C at comes with the introduction.
        var one = CreateNode("WordA", FreeUdpPort(), FreeTcpPort(), []);
        var other = CreateNode("WordC", FreeUdpPort(), FreeTcpPort(), []);

        // The device with the lower id is the one that connects at once (the other waits fifteen seconds first),
        // so it plays the one that trusts: the order of things is the same either way, only slower.
        var (a, c) = string.CompareOrdinal(one.Identity.Id, other.Identity.Id) < 0 ? (one, other) : (other, one);

        var told = new ConcurrentQueue<string>();
        a.Sessions.UnpairedByPeer += told.Enqueue;
        c.Sessions.UnpairedByPeer += told.Enqueue;

        await a.StartAsync();
        await c.StartAsync();

        // The press on A, as PeerIntroductions.TrustAsync makes it. The introduction back is still on its way.
        Assert.True(a.Sessions.Trust(
            c.Identity.Id, c.Settings.DeviceName, c.Identity.Fingerprint, c.Settings.TransportPort, c.Settings.DiscoveryPort, ["127.0.0.1"], awaitingTheirs: true));

        // A finds C at that address and connects. C has never heard of A, so the session is not a paired one.
        await WaitUntilAsync(
            () => a.Sessions.TryGetSession(c.Identity.Id, out var early) && early.State == SessionState.Unpaired
                && c.Sessions.TryGetSession(a.Identity.Id, out var seen) && seen.State == SessionState.Unpaired,
            TimeSpan.FromSeconds(40), "A never reached C directly", ct);
        await Task.Delay(500, ct);

        // And that is all it is. C has not forgotten A; it has not been told yet.
        var waiting = Assert.Single(a.Settings.PairedPeers);
        Assert.Equal((c.Identity.Id, true), (waiting.Id, waiting.AwaitingTheirTrust));
        Assert.Empty(told);
        Assert.True(a.Peers.TryGet(c.Identity.Id, out var listed) && listed.IsPaired);

        // Now the introduction arrives at C, as the relay delivers it there.
        Assert.True(c.Sessions.Trust(
            a.Identity.Id, a.Settings.DeviceName, a.Identity.Fingerprint, a.Settings.TransportPort, a.Settings.DiscoveryPort, ["127.0.0.1"]));

        await WaitUntilAsync(() => IsReady(a, c) && IsReady(c, a),
            TimeSpan.FromSeconds(40), "A and C never came up paired", ct);

        // C has said yes now, so A is no longer waiting for it to.
        await WaitUntilAsync(() => a.Settings.PairedPeers.FirstOrDefault() is { AwaitingTheirTrust: false },
            TimeSpan.FromSeconds(5), "A went on waiting for a yes it had already been given", ct);
        Assert.Empty(told);

        // From here on it is a pairing like any other, and one that C ends does reach A.
        await c.Sessions.UnpairAsync(a.Identity.Id);
        await WaitUntilAsync(() => a.Settings.PairedPeers.Count == 0,
            TimeSpan.FromSeconds(45), "A never learned that C had forgotten it", ct);
        Assert.Equal(c.Settings.DeviceName, Assert.Single(told));
    }

    /// <summary>
    /// The rule that test rests on, as a table. A pairing is given up only when this device really did say "paired"
    /// in the handshake and the other one is known to have had it paired before; every other way of ending up in
    /// an unpaired session with a device this one has paired is something else.
    /// </summary>
    [Fact]
    public void ANoInTheHandshakeOnlyMeansForgottenWhenTheyOnceKnewUs()
    {
        // Strangers: nothing is paired here, so there is nothing to give up, whatever else is true.
        Assert.Equal(PeerSessionManager.UnpairedMeans.Nothing, PeerSessionManager.WhatUnpairedMeans(false, false, false));
        Assert.Equal(PeerSessionManager.UnpairedMeans.Nothing, PeerSessionManager.WhatUnpairedMeans(false, true, false));

        // The record was written while the connection was being made, after this device had already answered "no"
        // itself — an introduction arriving in the middle of a handshake. The session says nothing about them.
        Assert.Equal(PeerSessionManager.UnpairedMeans.AskAgain, PeerSessionManager.WhatUnpairedMeans(true, false, false));
        Assert.Equal(PeerSessionManager.UnpairedMeans.AskAgain, PeerSessionManager.WhatUnpairedMeans(true, false, true));

        // Trusted on a third device's word, and they have not said yes once yet.
        Assert.Equal(PeerSessionManager.UnpairedMeans.NotTheirsYet, PeerSessionManager.WhatUnpairedMeans(true, true, true));

        // The one case that is what it looks like.
        Assert.Equal(PeerSessionManager.UnpairedMeans.Forgotten, PeerSessionManager.WhatUnpairedMeans(true, true, false));
    }

    /// <summary>
    /// A device nobody is connected to is on the network for as long as it says so. One that stops — switched off,
    /// gone elsewhere — used to stay "on the network" in the list until the program was restarted.
    /// </summary>
    [Fact]
    public async Task ADeviceThatStopsAnnouncingItselfIsNoLongerShownAsOnTheNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("QuietA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("QuietB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.State == PeerState.Discovered,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        // For as long as B goes on answering, it stays that way: longer than the silence that would count.
        await Task.Delay(ProtocolConstants.PeerExpiry + TimeSpan.FromSeconds(3), ct);
        Assert.True(a.Peers.TryGet(b.Identity.Id, out var still));
        Assert.Equal(PeerState.Discovered, still.State);

        await b.StopAsync();

        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.State == PeerState.Offline,
            TimeSpan.FromSeconds(30), "B went quiet and A went on listing it as on the network", ct);

        // Still listed, and still where it was last seen: only no longer said to be there now.
        Assert.True(a.Peers.TryGet(b.Identity.Id, out var gone));
        Assert.Equal("QuietB", gone.Name);
        Assert.NotNull(gone.TransportEndPoint);

        // And it is back as soon as it is heard again.
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.State == PeerState.Discovered,
            TimeSpan.FromSeconds(15), "B came back and A did not notice", ct);
    }

    /// <summary>
    /// A message that goes out in parts is only measured by the device receiving it, as the parts add up; and what
    /// that device does with one that is too long is stop reading, which ends the session for everything on it.
    /// The sender knows the length before it writes a byte, so it is the sender that refuses — and the session is
    /// still there afterwards, for the message that does fit.
    /// </summary>
    [Fact]
    public async Task AMessageTooLongForTheOtherSideIsRefusedBeforeItIsSentAndTheSessionLives()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("LongA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("LongB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct), "A could not pair with B");
        await WaitUntilAsync(() => IsReady(a, b) && IsReady(b, a), TimeSpan.FromSeconds(10), "the session never came up", ct);

        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var sending));
        Assert.True(b.Sessions.TryGetSession(a.Identity.Id, out var receiving));
        var arrived = new ConcurrentQueue<int>();
        receiving.RegisterHandler(ChannelId.File, (_, payload, _) =>
        {
            arrived.Enqueue(payload.Length);
            return ValueTask.CompletedTask;
        });

        var refused = await Assert.ThrowsAsync<MessageTooLargeException>(
            async () => await sending.SendAsync(ChannelId.File, new byte[ProtocolConstants.MaxFrameSize + 1], ct));
        Assert.Equal((ChannelId.File, ProtocolConstants.MaxFrameSize + 1), (refused.Channel, refused.Length));

        // The largest message there is still goes, and arrives whole: the two ends agree on where the line is.
        await sending.SendAsync(ChannelId.File, new byte[ProtocolConstants.MaxFrameSize], ct);
        await WaitUntilAsync(() => !arrived.IsEmpty, TimeSpan.FromSeconds(30), "the message that fits never arrived", ct);
        Assert.Equal(ProtocolConstants.MaxFrameSize, Assert.Single(arrived));

        // Nothing of the refused one was written, so the same session carried the other, and is still up.
        Assert.Equal(SessionState.Ready, sending.State);
        Assert.Equal(SessionState.Ready, receiving.State);
    }

    /// <summary>
    /// A settings file that cannot be read means a new device: a new id, a new certificate, every pairing gone on
    /// both sides. That used to happen without a word. The node now says it, as an error, with where the old file
    /// was put — and the old file is still there afterwards, whole, although the node has saved its new settings.
    /// </summary>
    [Fact]
    public void ANodeWhoseSettingsCannotBeReadSaysSoAndKeepsTheFile()
    {
        const string cutShort = "{ \"deviceId\": \"0123456789abcdef\", \"pairedPeers\": [ { \"id\": \"fedcba\", \"finger";
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), cutShort);

        var log = new KeptLog();
        var node = CreateNode("Unreadable", FreeUdpPort(), FreeTcpPort(), [], directory: directory, log: log);

        // A new device, as it has to be with nothing to go on.
        Assert.NotEqual("0123456789abcdef", node.Identity.Id);
        Assert.Empty(node.Settings.PairedPeers);

        var kept = node.SettingsStore.Unreadable?.KeptAs;
        Assert.NotNull(kept);
        Assert.Equal(cutShort, File.ReadAllText(kept));
        Assert.Equal(node.Identity.Id, new Settings.SettingsStore(directory).Load().DeviceId);

        var said = Assert.Single(log.Lines, line => line.Level == LogLevel.Error);
        Assert.Contains(kept, said.Text, StringComparison.Ordinal);
        Assert.Contains("could not be read", said.Text, StringComparison.Ordinal);
    }

    private static bool IsReady(MirroringNode from, MirroringNode to) =>
        from.Sessions.TryGetSession(to.Identity.Id, out var session) && session.State == SessionState.Ready;

    /// <summary>
    /// A machine that moves to another network while the app is running. This is not a rare case — a laptop does
    /// it every time it leaves Wi-Fi for a cable, or a hotspot for the house — and until this it was noticed only
    /// if something told the node about it. What told it was Windows's address-changed event and a beacon failing
    /// to send, and each of those asked for a look three seconds later, pushing the previous request's deadline
    /// back. A beacon fails every two seconds for as long as the address is wrong, so the look never happened:
    /// one laptop spent five hours announcing itself on a network it had left, ignoring every beacon from the
    /// machine next to it as "outside selected subnets", and the two could not find each other at all.
    ///
    /// The network a node is on cannot be changed from a test, so what is changed here is the answer the node
    /// gets when it asks. What is being held is that it asks at all, by itself, and acts on the answer.
    /// </summary>
    [Fact]
    public async Task ANodeNoticesByItselfThatTheMachineMovedToAnotherNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode("Moved", FreeUdpPort(), FreeTcpPort(), []);
        await node.StartAsync();
        Assert.NotEmpty(node.ListeningEndPoints);
        Assert.Equal(0, node.NetworkRestarts);

        // The machine is now somewhere else: nothing it was bound to exists any more.
        node.ResolveNetwork = () => NetworkSelection.Resolve(["an-interface-that-is-not-here"], includeLoopback: false);

        await WaitUntilAsync(
            () => node.NetworkRestarts >= 1,
            TimeSpan.FromSeconds(20),
            "the node never noticed that its addresses had changed",
            ct);

        // And back again, without anyone telling it either way.
        var loopback = NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback);
        node.ResolveNetwork = () => NetworkSelection.Resolve([loopback.Id], includeLoopback: true);

        await WaitUntilAsync(
            () => node.NetworkRestarts >= 2 && node.ListeningEndPoints.Count > 0,
            TimeSpan.FromSeconds(20),
            "the node never came back onto the network it returned to",
            ct);
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "WinMirroring.Tests", Guid.NewGuid().ToString("N"));

    private MirroringNode CreateNode(
        string name,
        int discoveryPort,
        int transportPort,
        string[] manualAddresses,
        bool accept = true,
        string? directory = null,
        ILoggerFactory? log = null)
    {
        directory ??= NewDirectory();
        _directories.Add(directory);
        var ui = new FixedAnswerPairingUi(accept);

        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = transportPort,
                ManualAddresses = manualAddresses,
            },
            ui,
            log ?? NullLoggerFactory.Instance);
        _ui[node] = ui;

        var loopback = NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback);
        node.Settings.SelectedInterfaceIds.Add(loopback.Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(100, ct);
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>Plays the user on the device being asked; records the code each side was shown.</summary>
    private sealed class FixedAnswerPairingUi(bool accept) : IPairingUserInterface
    {
        public string? ShownCode { get; private set; }

        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
        {
            ShownCode = prompt.ConfirmationCode;
            return Task.FromResult(accept);
        }

        public void PairingStarted(PairingPrompt prompt) => ShownCode = prompt.ConfirmationCode;
    }

    /// <summary>Keeps what a node writes to its log, for the things a node must not do without saying so.</summary>
    private sealed class KeptLog : ILoggerFactory
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Writer(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Writer(KeptLog log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                log.Lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
