using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinMirroring.Audio.Capture;
using WinMirroring.Audio.Playback;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;

namespace WinMirroring.Audio.Tests;

/// <summary>One device's microphone as a microphone of another: which output it goes into, and the asking before it.</summary>
public sealed class MicrophoneTests : IAsyncLifetime
{
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

    private static readonly AudioOutputDevice Speakers = new("spk", "Динамики (USB Audio Device)", IsDefault: true);
    private static readonly AudioOutputDevice Monitor = new("hdmi", "Odyssey G61SH (NVIDIA High Definition Audio)", IsDefault: false);
    private static readonly AudioOutputDevice Steam = new("steam", "Динамики (Steam Streaming Microphone)", IsDefault: false);
    private static readonly AudioOutputDevice SteamSpeakers = new("steam-spk", "Динамики (Steam Streaming Speakers)", IsDefault: false);
    private static readonly AudioOutputDevice Cable = new("cable", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false);

    [Fact]
    public void ACableThatDistortsIsNotChosenOnItsOwn()
    {
        // Its two ends are set differently and it does not convert: what goes in is not what comes out.
        var poor = Steam with { Distorts = true };
        Assert.Null(MicrophoneOutput.Pick([Speakers, poor], string.Empty));
        Assert.Equal(Cable, MicrophoneOutput.Pick([poor, Cable], string.Empty));

        // Chosen by hand it is used all the same, and the settings page says what is wrong with it.
        Assert.Equal(poor, MicrophoneOutput.Pick([Speakers, poor], "steam"));
    }

    [Fact]
    public void LeftToItselfItGoesIntoAVirtualCableAndNeverIntoSpeakers()
    {
        // No cable: nowhere, rather than out loud.
        Assert.Null(MicrophoneOutput.Pick([Speakers, Monitor, SteamSpeakers], string.Empty));

        // The streaming speakers Steam installs are not its streaming microphone.
        Assert.Equal(Steam, MicrophoneOutput.Pick([Speakers, SteamSpeakers, Steam, Monitor], null));

        // The cable made for this comes before the one that happens to be there, whatever the order they are listed in.
        Assert.Equal(Cable, MicrophoneOutput.Pick([Steam, Speakers, Cable], string.Empty));
    }

    [Fact]
    public void ACableThatIsTheStandardOutputIsLeftAlone()
    {
        // Somebody who made the cable their standard output has this device's own sound going through it.
        var standardCable = Cable with { IsDefault = true };
        Assert.Null(MicrophoneOutput.Pick([standardCable, Monitor], string.Empty));
        Assert.Equal(Steam, MicrophoneOutput.Pick([standardCable, Steam], string.Empty));
    }

    [Fact]
    public void WhatWasChosenByHandIsUsedWhateverItIs()
    {
        // By id, by name, and even when it is the speakers: that is the person's to decide.
        Assert.Equal(Monitor, MicrophoneOutput.Pick([Speakers, Monitor, Cable], "hdmi"));
        Assert.Equal(Monitor, MicrophoneOutput.Pick([Speakers, Monitor, Cable], "odyssey g61sh (nvidia high definition audio)"));
        Assert.Equal(Speakers, MicrophoneOutput.Pick([Speakers, Cable], "spk"));

        // A choice that is no longer there (unplugged, uninstalled) falls back to looking for a cable.
        Assert.Equal(Cable, MicrophoneOutput.Pick([Speakers, Cable], "gone"));
        Assert.Null(MicrophoneOutput.Pick([Speakers], "gone"));
    }

    [Fact]
    public async Task ADeviceThatDoesNotTakeMicrophonesIsNotSentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await ConnectedPairAsync("MicNoA", "MicNoB", ct);
        b.Settings.Audio.AcceptMicrophone = false;

        await using var audioA = new AudioService(a, NullLogger<AudioService>.Instance);
        await using var audioB = new AudioService(b, NullLogger<AudioService>.Instance);
        var opened = 0;
        audioA.MicrophoneSourceForTest = _ =>
        {
            Interlocked.Increment(ref opened);
            return new ToneSource();
        };
        audioA.Start();
        audioB.Start();

        var result = await audioA.SendMicrophoneToAsync(b.Identity.Id);

        Assert.Equal(MicrophoneOutcome.Refused, result.Outcome);
        Assert.Equal(0, opened); // the microphone was not so much as opened
        Assert.Null(audioA.MicrophoneTargetId);
        Assert.False(audioB.IsMicrophoneFrom(a.Identity.Id));
    }

    [Fact]
    public async Task ADeviceThatIsNotConnectedIsSaidToBeSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, _) = await ConnectedPairAsync("MicGoneA", "MicGoneB", ct);
        await using var audioA = new AudioService(a, NullLogger<AudioService>.Instance);
        audioA.Start();

        var result = await audioA.SendMicrophoneToAsync("nobody");

        Assert.Equal(MicrophoneOutcome.Unreachable, result.Outcome);
    }

    /// <summary>
    /// The whole way, on a machine that has a virtual cable: a tone sent as one device's microphone must be what
    /// comes out of the other end of the cable. Needs the cable (VB-CABLE, or the streaming microphone Steam
    /// installs), so it runs only on request.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task AToneSentAsAMicrophoneComesOutOfTheVirtualCable()
    {
        var ct = TestContext.Current.CancellationToken;
        var outputs = MicrophoneOutput.List();

        // A cable that distorts is not picked on its own, but it will do here: see below how it is listened to.
        var cable = MicrophoneOutput.Pick(outputs, string.Empty) ?? outputs.FirstOrDefault(o => o.Distorts);
        Assert.SkipWhen(cable is null, "this machine has no virtual audio cable");
        using var recordingEnd = RecordingEndOf(cable!.Name);
        Assert.SkipWhen(recordingEnd is null, $"found no recording end for {cable.Name}");

        var log = Path.Combine(Path.GetTempPath(), "winmirroring-microphone.log");
        File.WriteAllText(log, string.Empty);
        WaveFormat playedAs;
        using (var enumerator = new MMDeviceEnumerator())
        using (var playingEnd = enumerator.GetDevice(cable.Id))
        {
            using var playingClient = playingEnd.CreateAudioClient();
            using var recordingClient = recordingEnd!.CreateAudioClient();
            playedAs = playingClient.MixFormat;
            File.AppendAllText(log, $"playing end {playingEnd.FriendlyName}: {playingClient.MixFormat}; recording end {recordingEnd.FriendlyName}: {recordingClient.MixFormat}; distorts: {cable.Distorts}{Environment.NewLine}");
        }

        var (a, b) = await ConnectedPairAsync("MicA", "MicB", ct);
        b.Settings.Audio.MicrophoneOutput = cable.Id;
        await using var audioA = new AudioService(a, new FileLogger<AudioService>(log));
        await using var audioB = new AudioService(b, new FileLogger<AudioService>(log));
        audioA.MicrophoneSourceForTest = _ => new ToneSource();
        audioA.Start();
        audioB.Start();

        // What comes out of the cable, as a program that records from it gets it.
        var heard = new List<float>();
        var builder = new WasapiRecorderBuilder().WithDevice(recordingEnd!);
        var recorder = builder.Build();
        var format = recorder.WaveFormat.AsStandardWaveFormat();
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                return;
            }

            lock (heard)
            {
                AppendFirstChannel(buffer, format, heard);
            }
        };
        recorder.StartRecording();
        try
        {
            var result = await audioA.SendMicrophoneToAsync(b.Identity.Id);
            Assert.Equal(MicrophoneOutcome.Started, result.Outcome);
            Assert.Equal(cable.Name, result.Device);
            Assert.True(audioA.IsMicrophoneSentTo(b.Identity.Id));
            await WaitUntilAsync(() => audioB.IsMicrophoneFrom(a.Identity.Id), ct);
            Assert.Equal(cable.Name, audioB.GetStatus().MicrophoneDevice);
            Assert.False(audioB.IsReceivingFrom(a.Identity.Id)); // it is not sound for the speakers

            await Task.Delay(700, ct); // while the playout buffer fills
            lock (heard)
            {
                heard.Clear();
            }

            await Task.Delay(2000, ct);
            float[] samples;
            lock (heard)
            {
                samples = heard.ToArray();
            }

            var (level, share) = ToneIn(samples, format.SampleRate, ToneSource.Hz);
            var pitch = PitchOf(samples, format.SampleRate);

            // A good cable gives back the tone. One that hands the bytes across unconverted gives back the very
            // numbers that were played into it, read at its recording end's rate and as that many channels: the
            // tone is all there, at a pitch that follows from how the two ends are set (440 Hz played as 48 kHz
            // stereo and read as 44.1 kHz mono is 202.1 Hz). Either way it is the tone that was sent, and nothing
            // else, that came through — which is what this program answers for.
            var expected = cable.Distorts
                ? ToneSource.Hz * (double)(format.SampleRate * format.Channels) / (playedAs.SampleRate * playedAs.Channels)
                : ToneSource.Hz;
            File.AppendAllText(log, $"heard {samples.Length} samples at {format.SampleRate} Hz x{format.Channels} from {recordingEnd!.FriendlyName}: level {level:F4}, pitch {pitch:F1} Hz (expected {expected:F1}), {share:P1} of it at {ToneSource.Hz} Hz{Environment.NewLine}");
            Assert.True(samples.Length > format.SampleRate, $"only {samples.Length} samples came out of the cable");
            Assert.InRange(level, 0.12, 0.25); // a sine of a quarter of full scale is 0.177
            Assert.InRange(pitch, expected * 0.98, expected * 1.02);
            if (!cable.Distorts)
            {
                Assert.True(share > 0.8, $"only {share:P0} of what came out of the cable is the tone that was sent");
            }

            await audioA.StopMicrophoneAsync();
            await WaitUntilAsync(() => !audioB.IsMicrophoneFrom(a.Identity.Id), ct);
            Assert.Null(audioB.GetStatus().MicrophoneDevice);
        }
        finally
        {
            recorder.StopRecording();
            await recorder.DisposeAsync();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>A steady tone in whole frames, twenty milliseconds apart, as a microphone would deliver sound.</summary>
    private sealed class ToneSource : IAudioSource
    {
        public const int Hz = 440;

        private readonly CancellationTokenSource _stop = new();
        private Thread? _thread;

        public event AudioFrameHandler? FrameReady;

        public bool IncludesOwnPlayback => false;

        public string Description => $"a {Hz} Hz tone";

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "test-tone" };
            _thread.Start();
        }

        private void Run()
        {
            var frame = new float[AudioFormat.FrameValues];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long sent = 0;
            long sample = 0;
            while (!_stop.IsCancellationRequested)
            {
                var due = clock.ElapsedMilliseconds / AudioFormat.FrameMs;
                while (sent < due)
                {
                    for (var i = 0; i < AudioFormat.FrameSamples; i++, sample++)
                    {
                        // In doubles: in single precision the phase loses its footing within a second.
                        var value = (float)(0.25 * Math.Sin(2 * Math.PI * Hz * (sample % AudioFormat.SampleRate) / AudioFormat.SampleRate));
                        frame[i * 2] = value;
                        frame[(i * 2) + 1] = value;
                    }

                    FrameReady?.Invoke(frame);
                    sent++;
                }

                Thread.Sleep(2);
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _thread?.Join(1000);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The recording device that is the other end of the cable whose playing end is called this.</summary>
    private static MMDevice? RecordingEndOf(string playingEnd)
    {
        var mark = playingEnd.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ? "CABLE Output"
            : playingEnd.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) ? "Steam Streaming Microphone"
            : null;
        if (mark is null)
        {
            return null;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? found = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (found is null && device.FriendlyName.Contains(mark, StringComparison.OrdinalIgnoreCase))
            {
                found = device;
            }
            else
            {
                device.Dispose();
            }
        }

        return found;
    }

    private static void AppendFirstChannel(ReadOnlySpan<byte> buffer, WaveFormat format, List<float> into)
    {
        var frames = buffer.Length / Math.Max(1, format.BlockAlign);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var i = 0; i < frames; i++)
            {
                into.Add(samples[i * format.Channels]);
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            for (var i = 0; i < frames; i++)
            {
                into.Add(samples[i * format.Channels] / 32768f);
            }
        }
    }

    /// <summary>How loud the samples are, and how much of that is one tone (a correlation against it, both phases).</summary>
    private static (double Level, double Share) ToneIn(float[] samples, int sampleRate, int hz)
    {
        if (samples.Length == 0)
        {
            return (0, 0);
        }

        double energy = 0, inPhase = 0, quadrature = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var angle = 2 * Math.PI * hz * i / sampleRate;
            energy += samples[i] * samples[i];
            inPhase += samples[i] * Math.Sin(angle);
            quadrature += samples[i] * Math.Cos(angle);
        }

        var tone = 2 * ((inPhase * inPhase) + (quadrature * quadrature)) / samples.Length;
        return (Math.Sqrt(energy / samples.Length), energy <= 0 ? 0 : Math.Min(1, tone / energy));
    }

    /// <summary>The pitch of a steady tone, from how often it crosses zero.</summary>
    private static double PitchOf(float[] samples, int sampleRate)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0) != (samples[i] < 0))
            {
                crossings++;
            }
        }

        return samples.Length == 0 ? 0 : crossings / 2.0 * sampleRate / samples.Length;
    }

    private async Task<(MirroringNode A, MirroringNode B)> ConnectedPairAsync(string nameA, string nameB, CancellationToken ct)
    {
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode(nameA, discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode(nameB, discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        await WaitUntilAsync(() => b.Sessions.TryGetSession(a.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        return (a, b);
    }

    private MirroringNode CreateNode(string name, int discoveryPort, string manualAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.AudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = FreeTcpPort(),
                ManualAddresses = [manualAddress],
            },
            new AcceptAll(),
            NullLoggerFactory.Instance);
        node.Settings.SelectedInterfaceIds.Add(NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback).Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "condition not reached in time");
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

    private sealed class AcceptAll : IPairingUserInterface
    {
        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>Writes every log line to a file, for a live test whose output is hard to see otherwise.</summary>
    private sealed class FileLogger<T>(string path) : ILogger<T>
    {
        private static readonly object Gate = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}{Environment.NewLine}");
            }
        }
    }
}
