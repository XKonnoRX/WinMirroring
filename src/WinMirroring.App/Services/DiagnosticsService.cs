using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;

namespace WinMirroring.App.Services;

/// <summary>
/// Fetches the application log of another paired device over the session, so problems that happen on a device
/// you are not sitting at (a tablet) can be read from this one. Only paired sessions are answered.
/// </summary>
public sealed class DiagnosticsService
{
    private const int MaxLogBytes = 2 * 1024 * 1024;

    private readonly MirroringNode _node;
    private readonly ILogger<DiagnosticsService> _logger;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<LogResponseMessage>> _pending = new();
    private int _nextId;

    public DiagnosticsService(MirroringNode node, ILogger<DiagnosticsService> logger)
    {
        _node = node;
        _logger = logger;
    }

    public string LogDirectory => Path.Combine(_node.SettingsStore.DataDirectory, "logs");

    public string RemoteLogDirectory => Path.Combine(_node.SettingsStore.DataDirectory, "remote-logs");

    public void Start()
    {
        _node.Sessions.SessionReady += session => session.ControlMessageReceived += OnControlMessage;
        foreach (var session in _node.Sessions.Sessions.Where(s => s.State == SessionState.Ready))
        {
            session.ControlMessageReceived += OnControlMessage;
        }
    }

    /// <summary>Asks <paramref name="peerId"/> for its log and saves it here; returns the saved file path.</summary>
    public async Task<string> FetchLogAsync(string peerId, CancellationToken ct = default)
    {
        if (!_node.Sessions.TryGetSession(peerId, out var session) || session.State != SessionState.Ready)
        {
            throw new InvalidOperationException("Устройство не подключено.");
        }

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var waiter = new TaskCompletionSource<LogResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await session.SendControlAsync(new LogRequestMessage(id), ct).ConfigureAwait(false);
            var response = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                throw new IOException(error);
            }

            Directory.CreateDirectory(RemoteLogDirectory);
            var safeName = string.Concat(session.PeerName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(RemoteLogDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(path, response.Text, Encoding.UTF8, ct).ConfigureAwait(false);
            _logger.LogInformation("Saved the log of {Peer} to {Path}", session.PeerName, path);
            return path;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        switch (message)
        {
            case LogRequestMessage request when session.State == SessionState.Ready:
                _ = Task.Run(() => AnswerAsync(session, request));
                break;

            case LogResponseMessage response when _pending.TryGetValue(response.RequestId, out var waiter):
                waiter.TrySetResult(response);
                break;
        }
    }

    private async Task AnswerAsync(PeerSession session, LogRequestMessage request)
    {
        LogResponseMessage response;
        try
        {
            var latest = new DirectoryInfo(LogDirectory).EnumerateFiles("app-*.log").MaxBy(f => f.LastWriteTimeUtc)
                ?? throw new FileNotFoundException("Журнал не найден.");

            // The logger keeps the file open for writing; share it back.
            await using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxLogBytes)
            {
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            response = new LogResponseMessage(request.RequestId, latest.Name, await reader.ReadToEndAsync().ConfigureAwait(false), null);
            _logger.LogInformation("Sent this log to {Peer}", session.PeerName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            response = new LogResponseMessage(request.RequestId, string.Empty, string.Empty, ex.Message);
        }

        try
        {
            await session.SendControlAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Log not sent to {Peer}: {Message}", session.PeerName, ex.Message);
        }
    }
}
