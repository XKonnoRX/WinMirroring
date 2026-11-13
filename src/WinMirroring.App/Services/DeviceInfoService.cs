using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;

namespace WinMirroring.App.Services;

/// <summary>
/// Which version of the app each connected device runs. Exchanged when a session becomes ready (request and reply,
/// so a message that arrives before the subscription is not lost), and used to show the versions and to offer an
/// older device the build from a newer one.
/// </summary>
public sealed class DeviceInfoService
{
    private readonly MirroringNode _node;
    private readonly ILogger<DeviceInfoService> _logger;
    private readonly ConcurrentDictionary<string, Version> _versions = new(StringComparer.Ordinal);

    public DeviceInfoService(MirroringNode node, ILogger<DeviceInfoService> logger)
    {
        _node = node;
        _logger = logger;
    }

    /// <summary>A device's version became known, or it went away.</summary>
    public event Action? Changed;

    public static Version LocalVersion => GithubUpdater.CurrentVersion;

    /// <summary>The version <paramref name="peerId"/> runs, or null while it is unknown (an older build never says).</summary>
    public Version? PeerVersion(string peerId) => _versions.TryGetValue(peerId, out var version) ? version : null;

    /// <summary>This device's build can bring that device up to date.</summary>
    public bool CanUpdate(string peerId) => PeerVersion(peerId) is { } version && version < LocalVersion;

    public void Start()
    {
        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        foreach (var session in _node.Sessions.Sessions.Where(s => s.State == SessionState.Ready))
        {
            OnSessionReady(session);
        }
    }

    private void OnSessionReady(PeerSession session)
    {
        session.ControlMessageReceived += OnControlMessage;
        _ = SendAsync(session, isReply: false);
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;
        if (_versions.TryRemove(session.PeerId, out _))
        {
            Changed?.Invoke();
        }
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        if (message is not DeviceInfoMessage info)
        {
            return;
        }

        if (Version.TryParse(info.AppVersion, out var version))
        {
            _versions[session.PeerId] = version;
            _logger.LogInformation("{Peer} runs WinMirroring {Version}", session.PeerName, version);
            Changed?.Invoke();
        }

        if (!info.IsReply)
        {
            _ = SendAsync(session, isReply: true);
        }
    }

    private async Task SendAsync(PeerSession session, bool isReply)
    {
        try
        {
            await session.SendControlAsync(new DeviceInfoMessage(LocalVersion.ToString(3), isReply)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Version not sent to {Peer}: {Message}", session.PeerName, ex.Message);
        }
    }
}
