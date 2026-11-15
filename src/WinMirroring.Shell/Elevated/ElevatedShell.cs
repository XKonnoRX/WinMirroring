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
/// An administrator console on this device, as the app sees it: the helper runs the shell, this carries the words.
///
/// It looks exactly like a console the app started itself (<see cref="IHostedShell"/>) because inside the helper it
/// is one — the same <see cref="RemoteShell"/>, started elevated there. Nothing about feeding a shell is written
/// twice; what is new here is only the pipe in between and the prompt in front of it.
///
/// Why a helper at all: a program at normal integrity can ask Windows to start an elevated one, but it cannot have
/// its input and output, and a console with no input and output is nothing. So the helper is started with the
/// prompt, it starts the shell with its pipes in hand, and it passes what the shell says back down this channel.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedShell : IHostedShell
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private int _disposed;

    private ElevatedShell(NamedPipeServerStream pipe, ILogger logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    public event Action<ShellStreamKind, string>? Output;

    public event Action<int, string>? Done;

    public event Action? Ended;

    public Task Ready => _ready.Task;

    public string Directory { get; private set; } = string.Empty;

    /// <summary>
    /// Starts one, prompt and all: null when the helper is missing from this build, when whoever is at the device
    /// said no to the prompt, or when the program that connected was not the helper this app shipped with.
    /// </summary>
    public static async Task<ElevatedShell?> TryStartAsync(ILogger logger, CancellationToken ct)
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

            var shell = new ElevatedShell(pipe, logger);
            _ = Task.Run(() => shell.ReadLoopAsync(reader), CancellationToken.None);
            started = true;
            return shell;
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

    public bool Run(string command)
    {
        if (_disposed != 0 || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        _ = SendAsync(ShellCodec.EncodeCommand(0, command));
        return true;
    }

    public void Cancel() => _ = SendAsync([ShellCodec.Encode(ShellMessageType.Cancel, 0)]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Told rather than cut off, so the helper closes the shell down and goes away by itself; the pipe closing
        // says the same thing to a helper that is not listening any more.
        try
        {
            FrameCodec.WriteAsync(_pipe, ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Close, 0), CancellationToken.None)
                .AsTask()
                .Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
        }

        _pipe.Dispose();
        _writing.Dispose();
    }

    private async Task SendAsync(IReadOnlyList<byte[]> messages)
    {
        if (_disposed != 0)
        {
            return;
        }

        // One at a time and in order: a long command is several messages and the last of them is the one that
        // runs it.
        await _writing.WaitAsync().ConfigureAwait(false);
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
            _logger.LogDebug("An administrator console lost its helper: {Message}", ex.Message);
            Finish();
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
                    || !ShellCodec.TryReadHeader(frame.Payload, out var type, out _))
                {
                    break;
                }

                switch (type)
                {
                    case ShellMessageType.Opened when ShellCodec.TryDecodeText(frame.Payload, out var directory):
                        Directory = directory;
                        _ready.TrySetResult();
                        break;

                    case ShellMessageType.Output when ShellCodec.TryDecodeOutput(frame.Payload, out var stream, out var text):
                        Output?.Invoke(stream == ShellStream.Error ? ShellStreamKind.Error : ShellStreamKind.Output, text);
                        break;

                    case ShellMessageType.Done when ShellCodec.TryDecodeDone(frame.Payload, out var code, out var directory):
                        Directory = directory;
                        Done?.Invoke(code, directory);
                        break;

                    case ShellMessageType.Close:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            _logger.LogDebug("An administrator console's channel ended: {Message}", ex.Message);
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        _ready.TrySetResult();
        Ended?.Invoke();
    }
}
