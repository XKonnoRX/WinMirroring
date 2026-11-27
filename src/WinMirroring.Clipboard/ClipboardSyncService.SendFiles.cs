using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Files;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard;

/// <summary>
/// Sending files to one chosen device, apart from the clipboard: the sender offers them (nothing travels yet), the
/// receiving side decides whether to take them, pulls them into a folder of its own and answers what it did. The
/// same chunked File channel as the clipboard carries the contents, so this adds no new transport.
/// </summary>
public sealed partial class ClipboardSyncService
{
    /// <summary>A whole transfer may take a while (a build is tens of megabytes), but it must not wait forever.</summary>
    private static readonly TimeSpan SendFilesTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The connection went before the answer came. Said on whichever side notices.</summary>
    private const string ConnectionLost = "связь с устройством прервалась";

    private readonly ConcurrentDictionary<uint, (PeerSession Session, TaskCompletionSource<SendFilesResult> Waiter)> _sendFilesWaiters = new();
    private uint _sendFilesNextId;

    /// <summary>
    /// Another device offers files to this one. The handler decides (asking the user for anything it would open or
    /// install) and then calls <see cref="AcceptFilesAsync"/> or <see cref="DeclineFiles"/>; until then nothing is
    /// transferred. Raised on a worker thread.
    /// </summary>
    public event Action<PeerSession, SendFilesRequest>? FilesOffered;

    /// <summary>
    /// Offers <paramref name="paths"/> to <paramref name="peerId"/> and waits for its answer (which comes only after
    /// it has taken them, so this covers the whole transfer). The offer is kept readable until the answer arrives.
    /// </summary>
    public async Task<SendFilesResult> SendFilesAsync(string peerId, IReadOnlyList<string> paths, SendFilesPurpose purpose, string? note = null, CancellationToken ct = default)
    {
        if (_fetchers.Keys.FirstOrDefault(s => s.PeerId == peerId && s.State == SessionState.Ready) is not { } session)
        {
            return new SendFilesResult(0, false, "устройство не на связи");
        }

        var manifest = _offers.Create(paths, _node.Settings.DeviceName, pinned: true);
        if (manifest is null)
        {
            return new SendFilesResult(0, false, "нечего отправлять");
        }

        var requestId = Interlocked.Increment(ref _sendFilesNextId);
        var waiter = new TaskCompletionSource<SendFilesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendFilesWaiters[requestId] = (session, waiter);
        try
        {
            var request = new SendFilesRequest(requestId, purpose, note, manifest);
            await session.SendAsync(ChannelId.Clipboard, ClipboardCodec.EncodeSendFiles(request), ct).ConfigureAwait(false);
            _logger.LogInformation("Offered {What} to {Peer} ({Purpose})", DescribeFiles(manifest), session.PeerName, purpose);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(SendFilesTimeout);
            await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled()).ConfigureAwait(false);
            var result = await waiter.Task.ConfigureAwait(false);
            _logger.LogInformation("{Peer} on {What}: {Result}", session.PeerName, DescribeFiles(manifest), result.Ok ? "taken" : result.Error);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new SendFilesResult(requestId, false, ct.IsCancellationRequested ? "отменено" : "устройство не ответило");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return new SendFilesResult(requestId, false, ex.Message);
        }
        finally
        {
            _sendFilesWaiters.TryRemove(requestId, out _);
            _offers.Release(manifest.OfferId);
        }
    }

    /// <summary>
    /// Takes the offered files into <paramref name="targetDirectory"/> (progress goes to
    /// <see cref="TransferProgress"/>) and tells the sender how it went. Returns the top-level paths, or null on
    /// failure.
    /// </summary>
    public async Task<IReadOnlyList<string>?> AcceptFilesAsync(PeerSession session, SendFilesRequest request, string targetDirectory, CancellationToken ct = default)
    {
        var manifest = request.Manifest;
        if (!_fetchers.TryGetValue(session, out var source))
        {
            // The connection went while this side was making up its mind. The answer will hardly get through
            // either (the sender notices the loss by itself), but it is not for this side to decide that.
            _logger.LogInformation("Files from {Peer} not taken: the connection is gone", session.PeerName);
            await AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, ConnectionLost)).ConfigureAwait(false);
            return null;
        }

        var what = string.Empty;
        IReadOnlyList<string> paths;
        try
        {
            what = DescribeFiles(manifest);
            Report($"Приём с {session.PeerName}: {what}…");
            ShowTransfer(new FileTransferProgress(manifest.OfferId, what, 0, manifest.TotalSize, false, false));
            paths = await OfferDownloader.DownloadAsync(
                manifest,
                source,
                (done, total) => ShowTransfer(new FileTransferProgress(manifest.OfferId, what, done, total, false, false)),
                ct,
                targetDirectory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Whatever it was, the sender is told now. It waits for this answer for as long as a whole transfer may
            // take, so a failure that was not one of the expected kinds — a target folder that cannot be made, an
            // entry with a date no file can have — left it waiting a quarter of an hour for files that were never
            // going to arrive.
            ShowTransfer(new FileTransferProgress(manifest.OfferId, what, 0, manifest.TotalSize, true, false));
            if (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Taking {What} from {Peer} failed: {Message}", what, session.PeerName, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Taking {What} from {Peer} failed", what, session.PeerName);
            }

            await AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, ex.Message)).ConfigureAwait(false);
            return null;
        }

        // The indicator (which cannot fail) and then the answer, before anything else is said on this side: the
        // files are here, and nothing that goes wrong while telling the user so changes that.
        ShowTransfer(new FileTransferProgress(manifest.OfferId, what, manifest.TotalSize, manifest.TotalSize, true, true));
        await AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, true, null)).ConfigureAwait(false);
        _logger.LogInformation("Took {What} from {Peer} into {Directory}", what, session.PeerName, targetDirectory);
        Report($"Получено с {session.PeerName}: {what}");
        return paths;
    }

    /// <summary>Progress of files being taken, for whoever shows it. An indicator must never break the transfer it shows.</summary>
    private void ShowTransfer(FileTransferProgress progress)
    {
        try
        {
            TransferProgress?.Invoke(progress);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A handler of the transfer progress failed");
        }
    }

    /// <summary>
    /// A connection closed: an answer that was awaited over it cannot come any more (the other side answers on the
    /// connection the offer came by), so whoever offered files over it is told now rather than at the timeout.
    /// </summary>
    private void OnSendFilesSessionClosed(PeerSession session)
    {
        foreach (var (requestId, waiting) in _sendFilesWaiters)
        {
            if (ReferenceEquals(waiting.Session, session))
            {
                waiting.Waiter.TrySetResult(new SendFilesResult(requestId, false, ConnectionLost));
            }
        }
    }

    /// <summary>Does not take the offered files and says why (shown on the sending device).</summary>
    public void DeclineFiles(PeerSession session, SendFilesRequest request, string reason)
    {
        _logger.LogInformation("Declined {What} from {Peer}: {Reason}", DescribeFiles(request.Manifest), session.PeerName, reason);
        _ = AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, reason));
    }

    private async Task AnswerSendFilesAsync(PeerSession session, SendFilesResult result)
    {
        try
        {
            await session.SendAsync(ChannelId.Clipboard, ClipboardCodec.EncodeSendFilesResult(result), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Answer to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    private void OnSendFilesFrame(PeerSession session, ReadOnlySpan<byte> payload)
    {
        if (payload[0] == ClipboardCodec.SendFilesResultMessage)
        {
            if (ClipboardCodec.TryDecodeSendFilesResult(payload) is { } result
                && _sendFilesWaiters.TryGetValue(result.RequestId, out var waiting))
            {
                waiting.Waiter.TrySetResult(result);
            }

            return;
        }

        if (ClipboardCodec.TryDecodeSendFiles(payload) is not { } request)
        {
            return;
        }

        var handler = FilesOffered;
        if (handler is null)
        {
            // Nothing here can act on them (an older or headless build): say so instead of leaving the sender waiting.
            _ = AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, "это устройство не принимает файлы напрямую"));
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                handler(session, request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handling files offered by {Peer} failed", session.PeerName);
                _ = AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, ex.Message));
            }
        });
    }
}
