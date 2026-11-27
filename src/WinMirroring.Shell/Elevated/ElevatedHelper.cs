using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Local;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Elevated;

/// <summary>
/// Where administrator consoles come from, as the service asking for them sees it. One of these exists per run of
/// the app, and every administrator console the app opens comes out of it.
///
/// It is an interface because a test cannot answer a UAC prompt and must still be able to hold the two things that
/// matter about this: that the question is asked once, and that one of these serves every console afterwards.
/// </summary>
internal interface IElevatedHost : IDisposable
{
    /// <summary>False once it is gone. The next console then needs a new host, which means asking again.</summary>
    bool IsAlive { get; }

    /// <summary>One console on it, or null when it is already gone.</summary>
    IHostedShell? Open();
}

/// <summary>
/// The elevated helper, as the app sees it: a program running with administrator rights that starts shells when
/// asked and carries their words back down one pipe.
///
/// The helper is started once per run of the app and kept, and that is the whole reason this type exists rather
/// than one-console-one-helper. The prompt Windows shows is a question about a program — this program, started by
/// this app, now — and the answer stays true for as long as that program runs. Starting a fresh helper per console
/// asked the same question again for the same answer, and the question is not free: it is shown on the device being
/// managed, which is usually not the device of the person managing it. A tablet on a shelf that asks twice per
/// script is a tablet somebody has to go and pick up.
///
/// What it costs to keep is stated plainly in the dialog the person agrees to: until the app is restarted, a device
/// they have allowed administrator commands can open an administrator console here without Windows asking again.
/// That is the agreement, and it is narrower than it sounds — the setting is off until turned on, the device is
/// confirmed by name, and a restart of the app ends all of it. What is still refused is the other way of avoiding
/// the prompt: a scheduled task that starts an elevated shell on demand, which would hand administrator rights to
/// anything at all running as this user, for ever, with nobody asked. A cold app still costs one prompt.
///
/// Many consoles down one pipe, each with its own id, each its own PowerShell inside the helper: two devices
/// managing this one at the same time are two consoles and must not find themselves in each other's. Id 0 is not a
/// console but the helper itself — a <see cref="ShellMessageType.Close"/> addressed to it means shut down.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedHelper : IElevatedHost
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<uint, ElevatedConsole> _consoles = new();
    private uint _lastId;
    private int _gone;

    private ElevatedHelper(NamedPipeServerStream pipe, ILogger logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    public bool IsAlive => Volatile.Read(ref _gone) == 0;

    /// <summary>
    /// Starts one, prompt and all: null when the helper is missing from this build, when whoever is at the device
    /// said no to the prompt, or when the program that connected was not the helper this app shipped with.
    /// </summary>
    public static async Task<ElevatedHelper?> TryStartAsync(ILogger logger, CancellationToken ct)
    {
        if (!File.Exists(ElevatedShellPipe.HelperPath))
        {
            logger.LogWarning("There is no elevated helper in this build, so there can be no administrator console");
            return null;
        }

        var name = ElevatedShellPipe.NewName();
        var token = ElevatedShellPipe.NewToken();
        var pipe = ElevatedShellPipe.Listen(name);
        var started = false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ElevatedShellPipe.HelperPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--shell-pipe {name} --shell-token {Convert.ToHexString(token)}",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return null;
            }

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waiting.CancelAfter(ElevatedShellPipe.HelperWait);
            await pipe.WaitForConnectionAsync(waiting.Token).ConfigureAwait(false);

            // The prompt said yes to a program; this says the program that turned up is that program.
            if (!PipePeer.TryGetClientProcessId(pipe, out var pid) || !PipePeer.Is(pid, ElevatedShellPipe.HelperPath))
            {
                logger.LogWarning("Something other than the elevated helper connected to an administrator console's channel");
                return null;
            }

            await FrameCodec.WriteAsync(pipe, ChannelId.Shell, token, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);

            var reader = new FrameCodec.Reader();
            if (await reader.ReadAsync(pipe, ct).ConfigureAwait(false) is not { } answer
                || !answer.Payload.AsSpan().SequenceEqual(token))
            {
                logger.LogWarning("The elevated helper did not answer with the token it was given");
                return null;
            }

            var helper = new ElevatedHelper(pipe, logger);
            _ = Task.Run(() => helper.ReadLoopAsync(reader), CancellationToken.None);
            started = true;
            logger.LogInformation("The elevated helper is running; administrator consoles will not prompt again until this app restarts");
            return helper;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Nobody let the elevated helper start, so no administrator console was opened");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A declined prompt is a Win32Exception with ERROR_CANCELLED, and reads the same as any other refusal:
            // there is no administrator console, and the far side is told so in words.
            logger.LogInformation("An administrator console was not started: {Message}", ex.Message);
            return null;
        }
        finally
        {
            if (!started)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public IHostedShell? Open()
    {
        if (!IsAlive)
        {
            return null;
        }

        // From one upwards, because zero is the helper itself.
        var id = Interlocked.Increment(ref _lastId);
        var console = new ElevatedConsole(this, id);
        _consoles[id] = console;
        _ = SendAsync([ShellCodec.Encode(ShellMessageType.OpenElevated, id)]);
        return console;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _gone, 1) != 0)
        {
            return;
        }

        // Told rather than cut off, so the helper closes its shells down and goes away by itself; the pipe closing
        // says the same thing to a helper that is not listening any more.
        try
        {
            FrameCodec.WriteAsync(_pipe, ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Close, ElevatedShellPipe.HelperItself), CancellationToken.None)
                .AsTask()
                .Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
        }

        _pipe.Dispose();
        _writing.Dispose();
        EndEveryConsole();
    }

    /// <summary>One console is finished with: the helper closes that shell and keeps the rest.</summary>
    internal void Close(uint id)
    {
        if (_consoles.TryRemove(id, out _) && IsAlive)
        {
            _ = SendAsync([ShellCodec.Encode(ShellMessageType.Close, id)]);
        }
    }

    internal async Task SendAsync(IReadOnlyList<byte[]> messages)
    {
        if (!IsAlive)
        {
            return;
        }

        // One at a time and in order: a long command is several messages and the last of them is the one that
        // runs it.
        try
        {
            await _writing.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            foreach (var message in messages)
            {
                await FrameCodec.WriteAsync(_pipe, ChannelId.Shell, message, CancellationToken.None).ConfigureAwait(false);
            }

            await _pipe.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SemaphoreFullException)
        {
            _logger.LogDebug("The elevated helper is no longer listening: {Message}", ex.Message);
            Lost();
        }
        finally
        {
            try
            {
                _writing.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task ReadLoopAsync(FrameCodec.Reader reader)
    {
        try
        {
            while (true)
            {
                if (await reader.ReadAsync(_pipe, CancellationToken.None).ConfigureAwait(false) is not { } frame
                    || !ShellCodec.TryReadHeader(frame.Payload, out var type, out var id))
                {
                    break;
                }

                if (type == ShellMessageType.Close && id == ElevatedShellPipe.HelperItself)
                {
                    break;
                }

                if (!_consoles.TryGetValue(id, out var console))
                {
                    continue;
                }

                switch (type)
                {
                    case ShellMessageType.Opened when ShellCodec.TryDecodeText(frame.Payload, out var directory):
                        console.OnOpened(directory);
                        break;

                    case ShellMessageType.Output when ShellCodec.TryDecodeOutput(frame.Payload, out var stream, out var text, out var goesOn):
                        console.OnOutput(stream, text, goesOn);
                        break;

                    case ShellMessageType.Done when ShellCodec.TryDecodeDone(frame.Payload, out var code, out var directory):
                        console.OnDone(code, directory);
                        break;

                    case ShellMessageType.Close:
                        _consoles.TryRemove(id, out _);
                        console.Finish();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            _logger.LogDebug("The elevated helper's channel ended: {Message}", ex.Message);
        }
        finally
        {
            Lost();
        }
    }

    /// <summary>
    /// The helper has gone — it was closed, it fell over, or somebody ended it. Every console on it is over, and
    /// the next one asked for starts a new helper, which means one prompt again.
    /// </summary>
    private void Lost()
    {
        Volatile.Write(ref _gone, 1);
        EndEveryConsole();
    }

    private void EndEveryConsole()
    {
        foreach (var id in _consoles.Keys.ToList())
        {
            if (_consoles.TryRemove(id, out var console))
            {
                console.Finish();
            }
        }
    }

    /// <summary>
    /// One administrator console: the same thing to the service as a PowerShell it started itself, because inside
    /// the helper it is one. All this adds is an id on every message, so that several of them share one pipe.
    /// </summary>
    private sealed class ElevatedConsole : IHostedShell
    {
        private readonly ElevatedHelper _helper;
        private readonly uint _id;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly OutputAssembly _lines = new();
        private int _closing;
        private int _finished;

        public ElevatedConsole(ElevatedHelper helper, uint id)
        {
            _helper = helper;
            _id = id;
        }

        public event Action<ShellStreamKind, string>? Output;

        public event Action<int, string>? Done;

        public event Action? Ended;

        public Task Ready => _ready.Task;

        public string Directory { get; private set; } = string.Empty;

        public bool Run(string command)
        {
            if (_closing != 0 || _finished != 0 || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            _ = _helper.SendAsync(ShellCodec.EncodeCommand(_id, command));
            return true;
        }

        public void Cancel() => _ = _helper.SendAsync([ShellCodec.Encode(ShellMessageType.Cancel, _id)]);

        /// <summary>This console is finished with — not the helper, which the rest of this app's run still needs.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
            {
                return;
            }

            _helper.Close(_id);
            Finish();
        }

        internal void OnOpened(string directory)
        {
            Directory = directory;
            _ready.TrySetResult();
        }

        /// <summary>
        /// A line too long for one message comes down the pipe as several. It is put back together here, so that
        /// what this console raises is a line — as it is for a shell this app started itself.
        /// </summary>
        internal void OnOutput(ShellStream stream, string text, bool goesOn)
        {
            if (_lines.Add(stream, text, goesOn, out var line))
            {
                Output?.Invoke(stream == ShellStream.Error ? ShellStreamKind.Error : ShellStreamKind.Output, line);
            }
        }

        internal void OnDone(int code, string directory)
        {
            Directory = directory;
            Done?.Invoke(code, directory);
        }

        internal void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return;
            }

            _ready.TrySetResult();
            Ended?.Invoke();
        }
    }
}
