using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Runs a separate, elevated helper that does nothing but inject input, and routes injection to it. This keeps the
/// main app at normal integrity (so file drag-and-drop and pasting files keep working) while input can still reach
/// administrator windows, which Windows blocks for a normal process. The helper is a same-user process launched with
/// a UAC prompt; the pipe between them is restricted to the current user by its ACL, the helper is launched from a
/// fixed path with a one-time token, and the app checks the connecting process is that helper. It remains an opt-in
/// that lets this app's input reach administrator windows, so it is off by default.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedInjector : IDisposable
{
    /// <summary>Long enough for the user to read a UAC prompt and decide.</summary>
    private static readonly TimeSpan PromptedConnectTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Through the task nobody is asked anything, so a wait this long means it is not coming.</summary>
    private static readonly TimeSpan QuietConnectTimeout = TimeSpan.FromSeconds(25);

    private readonly InjectionRouter _router;
    private readonly ILogger<ElevatedInjector> _logger;
    private readonly object _gate = new();

    private NamedPipeServerStream? _pipe;
    private RemoteInjectionSink? _sink;
    private Process? _helper;
    private string? _helperPath;

    public ElevatedInjector(ILogger<ElevatedInjector> logger)
        : this(InjectionRouter.Instance, logger)
    {
    }

    public ElevatedInjector(InjectionRouter router, ILogger<ElevatedInjector> logger)
    {
        _router = router;
        _logger = logger;
    }

    /// <summary>Raised (active, detail) whenever the helper connects, fails or goes away.</summary>
    public event Action<bool, string>? StateChanged;

    public bool IsActive => _router.UsingRemote;

    /// <summary>The helper exe shipped next to the app; null when it is missing from this build.</summary>
    public static string? HelperPath => File.Exists(InjectionPipe.HelperPath) ? InjectionPipe.HelperPath : null;

    /// <summary>
    /// Whether the helper can be started without a prompt, because a scheduled task for this build's helper is
    /// registered. Registering it is the one-off that replaces the prompt at every start.
    /// </summary>
    public static bool StartsWithoutPrompting =>
        HelperPath is { } path && ElevatedHelperTask.IsRegisteredFor(path);

    /// <summary>
    /// Sets up the scheduled task, asking for administrator once. From then on turning control of administrator
    /// windows on — at every launch of the app, say — puts nothing on the screen.
    /// </summary>
    public bool AllowStartingWithoutPrompting(out string error)
    {
        if (HelperPath is not { } path)
        {
            error = "вспомогательная программа WinMirroring.InputHost.exe не найдена рядом с приложением";
            return false;
        }

        if (!ElevatedHelperTask.Register(path, out error))
        {
            _logger.LogInformation("The scheduled task for the input helper was not registered: {Error}", error);
            return false;
        }

        _logger.LogInformation("The input helper can now start without a prompt");
        return true;
    }

    /// <summary>Removes the task, so starting the helper asks again. Asks for administrator once.</summary>
    public bool RequirePromptAgain(out string error)
    {
        var removed = ElevatedHelperTask.Unregister(out error);
        if (removed)
        {
            _logger.LogInformation("The input helper will ask for administrator again");
        }

        return removed;
    }

    /// <summary>
    /// Starts the helper (UAC prompt) and routes injection to it. Returns false when the helper is missing, the
    /// prompt was declined, or the handshake failed; the app keeps working with local injection in every case.
    /// </summary>
    public async Task<bool> EnableAsync(CancellationToken ct = default)
    {
        if (IsActive)
        {
            return true;
        }

        var helperPath = HelperPath;
        if (helperPath is null)
        {
            Report(false, "вспомогательная программа WinMirroring.InputHost.exe не найдена рядом с приложением");
            return false;
        }

        Cleanup();

        // The channel has the same name every time, because the helper may be started by a scheduled task and a
        // task carries no arguments to tell it a new one. What binds the connection is not the name but the checks
        // at both ends: this app refuses any client that is not the helper beside it, and the helper refuses any
        // server that is not this app. The pipe's own permissions keep it to this user to begin with.
        var pipeName = InjectionPipe.NameForCurrentUser();
        var token = RandomNumberGeneratorToken();
        NamedPipeServerStream pipe;
        try
        {
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the injection pipe");
            Report(false, $"не удалось создать канал: {ex.Message}");
            return false;
        }

        lock (_gate)
        {
            _pipe = pipe;
            _helperPath = helperPath;
        }

        // Through the task first: that is the whole point of registering it, that nothing appears on the screen.
        Process? helper = null;
        var quietly = false;
        if (ElevatedHelperTask.IsRegisteredFor(helperPath))
        {
            quietly = ElevatedHelperTask.TryStart(out var taskError);
            if (!quietly)
            {
                _logger.LogWarning("The scheduled task did not start the input helper ({Error}); asking for administrator instead", taskError);
            }
        }

        if (!quietly)
        {
            try
            {
                helper = StartHelper(helperPath, pipeName, token);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = the user declined the UAC prompt.
                _logger.LogInformation("Elevated helper not started: {Message}", ex.Message);
                Cleanup();
                Report(false, ex.NativeErrorCode == 1223 ? "запуск от имени администратора отклонён" : ex.Message);
                return false;
            }
        }

        lock (_gate)
        {
            _helper = helper;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(quietly ? QuietConnectTimeout : PromptedConnectTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            VerifyClient(pipe, helperPath);
            await InjectionCodec.WriteMessageAsync(pipe, InjectionCodec.Handshake(token), ct).ConfigureAwait(false);

            var ack = await InjectionCodec.ReadMessageAsync(pipe, timeout.Token).ConfigureAwait(false);
            var echoed = ack is null ? null : InjectionCodec.ReadHandshake(ack);
            if (echoed is null || !echoed.AsSpan().SequenceEqual(token))
            {
                throw new InvalidOperationException("the helper did not confirm the handshake");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elevated helper handshake failed");
            Cleanup();
            Report(false, "вспомогательная программа не подтвердила соединение");
            return false;
        }

        var sink = new RemoteInjectionSink(pipe, _logger);
        sink.Disconnected += OnSinkDisconnected;
        if (helper is not null)
        {
            helper.EnableRaisingEvents = true;
            helper.Exited += OnHelperExited;
        }

        lock (_gate)
        {
            _sink = sink;
        }

        _router.SetRemote(sink, prefer: true);

        // Started through the task there is no process of ours to watch; the channel going quiet says the same
        // thing and is what the helper's own exit shows up as anyway.
        _logger.LogInformation("Elevated input helper connected{Quietly}", quietly ? " without a prompt" : string.Empty);
        Report(true, quietly
            ? "ввод идёт через помощника с правами администратора, запрос больше не появляется"
            : "ввод идёт через помощника с правами администратора");
        return true;
    }

    /// <summary>Stops routing to the helper and asks it to exit.</summary>
    public void Disable()
    {
        var wasActive = IsActive;
        Cleanup();
        if (wasActive)
        {
            Report(false, "ввод снова идёт напрямую");
        }
    }

    private void OnSinkDisconnected() => Dispatcher(() =>
    {
        if (_router.UsingRemote || _sink is not null)
        {
            Cleanup();
            Report(false, "соединение с помощником потеряно");
        }
    });

    private void OnHelperExited(object? sender, EventArgs e) => OnSinkDisconnected();

    private static void Dispatcher(Action action) => Task.Run(action);

    private void Cleanup()
    {
        RemoteInjectionSink? sink;
        NamedPipeServerStream? pipe;
        Process? helper;
        lock (_gate)
        {
            sink = _sink;
            pipe = _pipe;
            helper = _helper;
            _sink = null;
            _pipe = null;
            _helper = null;
        }

        _router.SetRemote(null, prefer: false);

        if (sink is not null)
        {
            sink.Disconnected -= OnSinkDisconnected;
            try
            {
                sink.SendBye();
            }
            catch
            {
                // best effort
            }

            sink.Dispose();
        }

        pipe?.Dispose();

        if (helper is not null)
        {
            helper.Exited -= OnHelperExited;
            try
            {
                if (!helper.WaitForExit(1500))
                {
                    helper.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            helper.Dispose();
        }
    }

    private void Report(bool active, string detail)
    {
        try
        {
            StateChanged?.Invoke(active, detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ElevatedInjector state handler failed");
        }
    }

    private static Process StartHelper(string helperPath, string pipeName, byte[] token) =>
        Process.Start(new ProcessStartInfo(helperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--inject-pipe {pipeName} --inject-token {Convert.ToHexString(token)}",
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("the helper process did not start");

    /// <summary>
    /// Only the helper beside this app may be on the other end. This is strict: the channel's name is one that
    /// another process of this user could work out, so a client we cannot identify is a client we refuse. What
    /// gets routed here is every keystroke and click, so being unable to tell who is listening is reason enough.
    /// </summary>
    private static void VerifyClient(NamedPipeServerStream pipe, string helperPath)
    {
        if (!InjectionPipe.TryGetClientProcessId(pipe, out var pid) || !InjectionPipe.IsPeer(pid, helperPath))
        {
            throw new InvalidOperationException("an unexpected process connected to the injection pipe");
        }
    }

    private static byte[] RandomNumberGeneratorToken()
    {
        var token = new byte[InjectionCodec.TokenLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(token);
        return token;
    }

    public void Dispose() => Cleanup();
}
