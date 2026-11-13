using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.DragDrop;
using WinMirroring.Clipboard.Files;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard;

/// <summary>
/// Shared clipboard across all paired devices. A local copy is sent to every connected peer: small data inline,
/// large data as a reference the peers pull in bounded chunks (so mouse input on the same link stays responsive),
/// and files as an offer that becomes virtual files on the other side, transferred only when pasted.
/// </summary>
public sealed partial class ClipboardSyncService : IAsyncDisposable
{
    private const int InlineLimit = 256 * 1024;
    private const int MaxDirectPasteEntries = 5000;

    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly LocalOfferStore _offers = new();
    private readonly FileServer _server;
    private readonly ConcurrentDictionary<PeerSession, FileFetcher> _fetchers = new();
    private readonly Channel<ClipboardSnapshot> _outbox = Channel.CreateBounded<ClipboardSnapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    private ClipboardThread? _thread;
    private Task? _publisher;

    public ClipboardSyncService(MirroringNode node, ILogger<ClipboardSyncService> logger)
    {
        _node = node;
        _logger = logger;
        _server = new FileServer(_offers, logger);
    }

    /// <summary>Short Russian description of the last clipboard exchange, for the UI.</summary>
    public string LastActivity { get; private set; } = string.Empty;

    public event Action<string>? Activity;

    /// <summary>Progress of a received file offer being materialised locally (for a progress indicator).</summary>
    public event Action<FileTransferProgress>? TransferProgress;

    /// <summary>Another device asks this one to start a drag of its files under the cursor (we are controlled).</summary>
    public event Action<PeerSession, DragPayload>? DragStartReceived;

    /// <summary>A device this one controls says a drag is leaving it (we are the controller).</summary>
    public event Action<PeerSession, DragPayload>? DragOutReceived;

    /// <summary>Viewer drag and drop messages (type byte, message), raised inline on the receive loop.</summary>
    public event Action<PeerSession, byte, ViewerDragMessage>? ViewerDragReceived;

    public async Task SendViewerDragAsync(PeerSession session, byte type, ViewerDragMessage message)
    {
        try
        {
            await session.SendAsync(ChannelId.Clipboard, ClipboardCodec.EncodeViewerDrag(type, message), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Viewer drag message to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    /// <summary>OLE drag and drop on the clipboard thread; available after <see cref="Start"/>.</summary>
    public DragDropEngine? Drag { get; private set; }

    private long MaxInlineBytes => Math.Clamp(_node.Settings.Clipboard.MaxInlineMegabytes, 1, 1024) * 1024L * 1024;

    public void Start()
    {
        OfferDownloader.CleanUp();

        var thread = new ClipboardThread(_logger, () => MaxInlineBytes);
        thread.LocalChanged += snapshot => _outbox.Writer.TryWrite(snapshot);
        thread.Start();
        _thread = thread;

        // Drag and drop is optional: if its surface cannot be created, the clipboard itself keeps working.
        try
        {
            var drag = new DragDropEngine(thread, _logger);
            if (drag.InitializeAsync().Wait(TimeSpan.FromSeconds(5)))
            {
                Drag = drag;
            }
            else
            {
                _logger.LogWarning("Drag surface did not initialise in time; drag and drop is off");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drag surface failed; drag and drop is off");
        }

        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                OnSessionReady(session);
            }
        }

        _publisher = Task.Run(() => PublishLoopAsync(_cts.Token));
    }

    private void OnSessionReady(PeerSession session)
    {
        _fetchers[session] = new FileFetcher(session);
        session.RegisterHandler(ChannelId.Clipboard, OnClipboardFrameAsync);
        session.RegisterHandler(ChannelId.File, OnFileFrameAsync);
    }

    private void OnSessionClosed(PeerSession session)
    {
        if (_fetchers.TryRemove(session, out var fetcher))
        {
            fetcher.Close();
        }
    }

    // ---- outgoing ----------------------------------------------------------------------------------------

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var snapshot in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await PublishAsync(snapshot, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Publishing the clipboard failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishAsync(ClipboardSnapshot snapshot, CancellationToken ct)
    {
        var settings = _node.Settings.Clipboard;
        if (!settings.Enabled)
        {
            return;
        }

        var targets = _node.Sessions.Sessions.Where(s => s.State == SessionState.Ready).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        byte[] message;
        string what;
        if (snapshot.Files.Count > 0)
        {
            if (!settings.ShareFiles)
            {
                return;
            }

            var manifest = _offers.Create(snapshot.Files, _node.Settings.DeviceName);
            if (manifest is null)
            {
                return;
            }

            message = ClipboardCodec.EncodeOffer(manifest);
            what = DescribeFiles(manifest);
        }
        else
        {
            var encoded = ClipboardCodec.EncodeData(snapshot.Items);
            message = encoded.Length <= InlineLimit
                ? encoded
                : ClipboardCodec.EncodeDataRef(_offers.AddBlob(encoded), encoded.Length);
            what = DescribeItems(snapshot.Items);
        }

        if (message.Length > ProtocolConstants.MaxFrameSize)
        {
            Report($"Буфер обмена слишком большой для отправки ({what})");
            return;
        }

        var sent = 0;
        foreach (var session in targets)
        {
            try
            {
                await session.SendAsync(ChannelId.Clipboard, message, ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug("Clipboard not sent to {Peer}: {Message}", session.PeerName, ex.Message);
            }
        }

        _logger.LogInformation("Clipboard ({What}) sent to {Count} device(s)", what, sent);
        Report($"Отправлено: {what} → {sent} устр.");
    }

    /// <summary>Sends what is on the local clipboard right now to every connected device (manual resync).</summary>
    public async Task<bool> PublishNowAsync()
    {
        if (_thread is not { } thread)
        {
            return false;
        }

        var snapshot = await thread.ReadNowAsync().ConfigureAwait(false);
        if (snapshot is null || snapshot.IsEmpty)
        {
            return false;
        }

        return _outbox.Writer.TryWrite(snapshot);
    }

    // ---- drag and drop -----------------------------------------------------------------------------------

    /// <summary>Turns local paths (caught from a drag) into a payload this device serves.</summary>
    public DragPayload? CreateLocalDragPayload(IReadOnlyList<string> paths)
    {
        var manifest = _offers.Create(paths, _node.Settings.DeviceName);
        return manifest is null ? null : new DragPayload(_node.Identity.Id, manifest);
    }

    /// <summary>Reads files of a payload from the device that serves them, if we are connected to it.</summary>
    public IRemoteFileSource? SourceFor(string deviceId) =>
        _fetchers.FirstOrDefault(p => p.Key.PeerId == deviceId && p.Key.State == SessionState.Ready).Value;

    /// <summary>Small sets also get CF_HDROP, so programs that need real paths accept the drop.</summary>
    public bool AllowsDirectPaste(FileOfferManifest manifest)
    {
        var limit = Math.Max(0, _node.Settings.Clipboard.MaxDirectPasteMegabytes) * 1024L * 1024;
        return manifest.TotalSize <= limit && manifest.Entries.Count <= MaxDirectPasteEntries;
    }

    public async Task SendDragAsync(PeerSession session, DragPayload payload, bool start)
    {
        var message = ClipboardCodec.EncodeDrag(start ? ClipboardCodec.DragStartMessage : ClipboardCodec.DragOutMessage, payload);
        try
        {
            await session.SendAsync(ChannelId.Clipboard, message, _cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Drag of {Count} item(s) {Direction} {Peer}", payload.Manifest.TopLevel.Count(), start ? "started on" : "handed to", session.PeerName);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Drag message to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    // ---- incoming ----------------------------------------------------------------------------------------

    private ValueTask OnClipboardFrameAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        if (payload.Span[0] is ClipboardCodec.DragStartMessage or ClipboardCodec.DragOutMessage)
        {
            // Handled inline, not on the thread pool: the EdgeReached that follows a drag-out on the Input channel
            // must find the drag already registered.
            if (_node.Settings.Clipboard.DragDropEnabled && ClipboardCodec.TryDecodeDrag(payload.Span) is { } drag)
            {
                var start = payload.Span[0] == ClipboardCodec.DragStartMessage;
                _logger.LogInformation("Drag {Kind} from {Peer}: {Count} item(s) served by {Source}",
                    start ? "start" : "hand-off", session.PeerName, drag.Manifest.TopLevel.Count(), drag.Manifest.SourceName);
                var handler = start ? DragStartReceived : DragOutReceived;
                if (handler is null)
                {
                    _logger.LogWarning("Drag message from {Peer} ignored: drag and drop is not running here", session.PeerName);
                }

                try
                {
                    handler?.Invoke(session, drag);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Drag message handler failed");
                }
            }

            return ValueTask.CompletedTask;
        }

        if (payload.Span[0] is ClipboardCodec.SendFilesMessage or ClipboardCodec.SendFilesResultMessage)
        {
            // Files sent to this device on purpose: not the clipboard, and not gated by the clipboard settings.
            OnSendFilesFrame(session, payload.Span);
            return ValueTask.CompletedTask;
        }

        if (payload.Span[0] is ClipboardCodec.ViewerDropMessage or ClipboardCodec.ViewerCatchMessage or ClipboardCodec.ViewerCaughtMessage)
        {
            if (_node.Settings.Clipboard.DragDropEnabled && ClipboardCodec.TryDecodeViewerDrag(payload.Span) is { } viewerDrag)
            {
                try
                {
                    ViewerDragReceived?.Invoke(session, payload.Span[0], viewerDrag);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Viewer drag handler failed");
                }
            }

            return ValueTask.CompletedTask;
        }

        if (!_node.Settings.Clipboard.Enabled)
        {
            return ValueTask.CompletedTask;
        }

        var message = payload.ToArray();
        _ = Task.Run(() => ReceiveAsync(session, message));
        return ValueTask.CompletedTask;
    }

    private async Task ReceiveAsync(PeerSession session, byte[] message)
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        try
        {
            switch (message[0])
            {
                case ClipboardCodec.DataMessage:
                    await ApplyDataAsync(thread, session, message).ConfigureAwait(false);
                    break;

                case ClipboardCodec.DataRefMessage when ClipboardCodec.TryDecodeDataRef(message, out var blobId, out var length):
                    if (length > MaxInlineBytes + 1024 || !_fetchers.TryGetValue(session, out var fetcher))
                    {
                        return;
                    }

                    using (var buffer = new MemoryStream((int)length))
                    {
                        await using var remote = new RemoteFileStream(fetcher, blobId, 0, length);
                        await remote.CopyToAsync(buffer, RemoteFileStream.ChunkSize, _cts.Token).ConfigureAwait(false);
                        await ApplyDataAsync(thread, session, buffer.ToArray()).ConfigureAwait(false);
                    }

                    break;

                case ClipboardCodec.OfferMessage when _node.Settings.Clipboard.ShareFiles:
                    var manifest = ClipboardCodec.TryDecodeOffer(message);
                    if (manifest is null || !_fetchers.TryGetValue(session, out var source))
                    {
                        return;
                    }

                    var dataObject = new VirtualFileDataObject(manifest, source, AllowsDirectPaste(manifest), session.PeerId, _logger);
                    dataObject.DownloadStarted += m =>
                    {
                        Report($"Вставка: загрузка {DescribeFiles(m)} с {m.SourceName}…");
                        TransferProgress?.Invoke(new FileTransferProgress(m.OfferId, DescribeFiles(m), 0, m.TotalSize, false, false));
                    };
                    dataObject.DownloadProgress += (m, done, total) =>
                        TransferProgress?.Invoke(new FileTransferProgress(m.OfferId, DescribeFiles(m), done, total, false, false));
                    dataObject.DownloadFinished += (m, ok) =>
                        TransferProgress?.Invoke(new FileTransferProgress(m.OfferId, DescribeFiles(m), ok ? m.TotalSize : 0, m.TotalSize, true, ok));
                    await thread.SetDataObjectAsync(dataObject).ConfigureAwait(false);
                    _logger.LogInformation("Files from {Peer} put on the clipboard: {What}", session.PeerName, DescribeFiles(manifest));
                    Report($"Получено с {session.PeerName}: {DescribeFiles(manifest)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard from {Peer} not applied", session.PeerName);
        }
    }

    private async Task ApplyDataAsync(ClipboardThread thread, PeerSession session, byte[] message)
    {
        var items = ClipboardCodec.TryDecodeData(message, MaxInlineBytes);
        if (items is not { Count: > 0 })
        {
            return;
        }

        await thread.WriteItemsAsync(items).ConfigureAwait(false);
        _logger.LogInformation("Clipboard from {Peer} applied: {What}", session.PeerName, DescribeItems(items));
        Report($"Получено с {session.PeerName}: {DescribeItems(items)}");
    }

    private ValueTask OnFileFrameAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        switch (payload.Span[0])
        {
            case FileCodec.RequestMessage:
                var request = payload.ToArray();
                _ = Task.Run(() => _server.HandleRequestAsync(session, request));
                break;

            case FileCodec.ResponseMessage when _fetchers.TryGetValue(session, out var fetcher):
                fetcher.OnResponse(payload.Span);
                break;
        }

        return ValueTask.CompletedTask;
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private void Report(string text)
    {
        LastActivity = $"{DateTime.Now:HH:mm} {text}";
        Activity?.Invoke(LastActivity);
    }

    private static string DescribeItems(IReadOnlyList<ClipboardItem> items)
    {
        var size = FormatSize(items.Sum(i => (long)i.Data.Length));
        if (items.Any(i => i.Kind is ClipboardItemKind.Png or ClipboardItemKind.Dib))
        {
            return $"изображение ({size})";
        }

        return items.Any(i => i.Kind == ClipboardItemKind.UnicodeText) ? $"текст ({size})" : $"данные ({size})";
    }

    private static string DescribeFiles(FileOfferManifest manifest)
    {
        var files = manifest.Entries.Count(e => !e.IsDirectory);
        var folders = manifest.TopLevel.Count(e => e.IsDirectory);
        var parts = new List<string>();
        if (files > 0)
        {
            parts.Add($"файлов: {files}");
        }

        if (folders > 0)
        {
            parts.Add($"папок: {folders}");
        }

        return $"{string.Join(", ", parts)} ({FormatSize(manifest.TotalSize)})";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:F0} КБ"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F1} МБ"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ"),
    };

    /// <summary>Safe to call more than once: the app disposes it itself, then its service container does again.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        _outbox.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_publisher is not null)
        {
            try
            {
                await _publisher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var fetcher in _fetchers.Values)
        {
            fetcher.Close();
        }

        if (Drag is { } drag)
        {
            drag.CancelActiveDrag();
            drag.ShutdownAsync().Wait(TimeSpan.FromSeconds(2));
        }

        _thread?.Dispose();
        _server.Dispose();
        _cts.Dispose();
    }
}
