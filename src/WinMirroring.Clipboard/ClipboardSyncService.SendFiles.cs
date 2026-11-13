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

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<SendFilesResult>> _sendFilesWaiters = new();
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
        _sendFilesWaiters[requestId] = waiter;
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
        if (!_fetchers.TryGetValue(session, out var source))
        {
            return null;
        }

        var manifest = request.Manifest;
        var what = DescribeFiles(manifest);
        Report($"Приём с {session.PeerName}: {what}…");
        TransferProgress?.Invoke(new FileTransferProgress(manifest.OfferId, what, 0, manifest.TotalSize, false, false));
        try
        {
            var paths = await OfferDownloader.DownloadAsync(
                manifest,
                source,
                (done, total) => TransferProgress?.Invoke(new FileTransferProgress(manifest.OfferId, what, done, total, false, false)),
                ct,
                targetDirectory).ConfigureAwait(false);

            TransferProgress?.Invoke(new FileTransferProgress(manifest.OfferId, what, manifest.TotalSize, manifest.TotalSize, true, true));
            Report($"Получено с {session.PeerName}: {what}");
            _logger.LogInformation("Took {What} from {Peer} into {Directory}", what, session.PeerName, targetDirectory);
            await AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, true, null)).ConfigureAwait(false);
            return paths;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            TransferProgress?.Invoke(new FileTransferProgress(manifest.OfferId, what, 0, manifest.TotalSize, true, false));
            _logger.LogWarning("Taking {What} from {Peer} failed: {Message}", what, session.PeerName, ex.Message);
            await AnswerSendFilesAsync(session, new SendFilesResult(request.RequestId, false, ex.Message)).ConfigureAwait(false);
            return null;
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
                && _sendFilesWaiters.TryGetValue(result.RequestId, out var waiter))
            {
                waiter.TrySetResult(result);
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
