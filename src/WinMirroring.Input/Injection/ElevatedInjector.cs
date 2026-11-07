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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);

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
    public static string? HelperPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "WinMirroring.InputHost.exe");
            return File.Exists(path) ? path : null;
        }
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

        // A random, single-instance pipe name created before the helper launches: no other process can pre-create
        // it or guess it, and the default pipe security is limited to this user. The handshake token and the
        // client-image check below bind the connection to exactly the helper we started.
        var pipeName = $"WinMirroring.Inject.{Guid.NewGuid():N}";
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

        Process helper;
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

        lock (_gate)
        {
            _helper = helper;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
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
        helper.EnableRaisingEvents = true;
        helper.Exited += OnHelperExited;

        lock (_gate)
        {
            _sink = sink;
        }

        _router.SetRemote(sink, prefer: true);
        _logger.LogInformation("Elevated input helper connected (pid {Pid})", helper.Id);
        Report(true, "ввод идёт через помощника с правами администратора");
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

    private void VerifyClient(NamedPipeServerStream pipe, string helperPath)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
        {
            _logger.LogDebug("Could not read the injection client's process id; trusting the pipe ACL");
            return;
        }

        var image = ProcessImagePath(pid);
        if (image is null)
        {
            _logger.LogDebug("Could not read the injection client's image path (pid {Pid})", pid);
            return;
        }

        if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"an unexpected process connected to the injection pipe: {image}");
        }
    }

    private static byte[] RandomNumberGeneratorToken()
    {
        var token = new byte[InjectionCodec.TokenLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(token);
        return token;
    }

    private static string? ProcessImagePath(uint pid)
    {
        const uint queryLimited = 0x1000;
        var process = OpenProcess(queryLimited, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var capacity = 1024;
            var buffer = new char[capacity];
            return QueryFullProcessImageNameW(process, 0, buffer, ref capacity) ? new string(buffer, 0, capacity) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public void Dispose() => Cleanup();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
