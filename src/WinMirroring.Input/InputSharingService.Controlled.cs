using Microsoft.Extensions.Logging;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Injection;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;

namespace WinMirroring.Input;

/// <summary>Controlled side: replay of a remote controller's input, keepalives and stuck-key protection.</summary>
public sealed partial class InputSharingService
{
    private static readonly byte[] AliveFrame = [(byte)InputEventKind.Alive];

    /// <summary>Per controller; <see cref="Generation"/> retires keepalive loops of earlier Enter/Leave cycles.</summary>
    private sealed class ControlledState
    {
        public volatile bool Active;
        public int Generation;
        public int AliveInFlight;
        public int HandOffPending;
    }

    private ValueTask OnInputFrameAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var state = _controllers.GetOrAdd(session.PeerId, static _ => new ControlledState());
        var data = payload.Span;
        while (InputCodec.TryRead(data, out var evt, out var consumed))
        {
            data = data[consumed..];
            Apply(session, state, evt);
        }

        return ValueTask.CompletedTask;
    }

    private void Apply(PeerSession session, ControlledState state, InputEvent evt)
    {
        switch (evt.Kind)
        {
            case InputEventKind.Enter or InputEventKind.EnterOnMonitor:
                TakeControl(session, state, evt);
                break;

            case InputEventKind.MouseMove when state.Active:
                _player.Motion(session, evt.Timestamp, evt.Dx, evt.Dy);
                break;

            case InputEventKind.MouseButton when state.Active:
                _player.Execute(session, () => _injector.Button(evt.Button, evt.Down));
                break;

            case InputEventKind.MouseWheel when state.Active:
                _player.Execute(session, () => _injector.Wheel(evt.WheelDelta, evt.Horizontal));
                break;

            case InputEventKind.Key when state.Active:
                _player.Execute(session, () => _injector.Key(evt.VirtualKey, evt.ScanCode, evt.Up, evt.Extended));
                break;

            case InputEventKind.Leave:
                ReleaseControl(session, state, "released by the controller");
                break;

            case InputEventKind.EdgeReached or InputEventKind.EdgeReachedOnMonitor:
                // We are the controller here: the controlled device says the cursor is leaving it.
                _commands.Writer.TryWrite(new EdgeReachedCommand(session.PeerId, evt.Edge, evt.Relative, evt.RelativeToScreen, evt.Monitor));
                break;

            case InputEventKind.Alive:
                // The session already recorded the arrival time; that is all a keepalive is for.
                break;
        }
    }

    private void TakeControl(PeerSession session, ControlledState state, InputEvent evt)
    {
        // Onto the named monitor, or along the whole desktop; never into a gap where no monitor is.
        var monitors = MonitorCatalog.Current;
        var area = monitors.Find(evt.Monitor)?.Bounds ?? VirtualScreen.Current;
        var entry = evt.AtCenter
            ? VirtualScreen.PrimaryCenter
            : monitors.Snap(EdgeMath.PointOn(area, evt.Edge, evt.Relative, inset: 1));

        state.Active = true;
        var generation = Interlocked.Increment(ref state.Generation);
        _player.Begin(session, entry);
        _ = KeepControlAliveAsync(session, state, generation);
        _logger.LogInformation("{Peer} took control, cursor at {Entry}", session.PeerName, evt.AtCenter ? "centre" : evt.Edge.ToString());
        ControlledByChanged?.Invoke(session.PeerId);
    }

    private void ReleaseControl(PeerSession session, ControlledState state, string reason)
    {
        if (!state.Active)
        {
            return;
        }

        state.Active = false;
        Interlocked.Increment(ref state.Generation);
        try
        {
            // Lets a drag started on the controller's behalf cancel itself before its button is released.
            ControlEnding?.Invoke(session.PeerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ControlEnding handler failed");
        }

        if (_player.End(session))
        {
            _injector.ReleaseAll();
        }

        _logger.LogInformation("{Peer} no longer in control: {Reason}", session.PeerName, reason);
        if (!_player.HasOwner)
        {
            ControlledByChanged?.Invoke(null);
        }
    }

    /// <summary>
    /// While a controller is in charge: tell it every <see cref="AliveInterval"/> that we are here (its capture lease),
    /// and if it goes silent, release everything it holds pressed instead of leaving keys stuck down.
    /// </summary>
    private async Task KeepControlAliveAsync(PeerSession session, ControlledState state, int generation)
    {
        try
        {
            while (state.Active && Volatile.Read(ref state.Generation) == generation)
            {
                if (Interlocked.Exchange(ref state.AliveInFlight, 1) == 0)
                {
                    _ = SendAliveAsync(session, state);
                }

                await Task.Delay(AliveInterval, _cts.Token).ConfigureAwait(false);
                if (!state.Active || Volatile.Read(ref state.Generation) != generation)
                {
                    return;
                }

                if (Environment.TickCount64 - session.LastReceivedTicks > ControlledSilenceMs)
                {
                    _logger.LogWarning("{Peer} went silent while in control; releasing its keys and buttons", session.PeerName);
                    ReleaseControl(session, state, "controller silent");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAliveAsync(PeerSession session, ControlledState state)
    {
        try
        {
            await session.SendAsync(ChannelId.Input, AliveFrame).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Keepalive to {Peer} failed: {Message}", session.PeerName, ex.Message);
        }
        finally
        {
            Volatile.Write(ref state.AliveInFlight, 0);
        }
    }

    private void OnInjectionBlocked(object owner, string window)
    {
        if (owner is PeerSession session)
        {
            _ = SendBlockedAsync(session, window);
        }
    }

    private async Task SendBlockedAsync(PeerSession session, string window)
    {
        try
        {
            await session.SendControlAsync(new Core.Messages.InputBlockedMessage(window), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Blocked-input notice to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    }

    private void OnPlayerEdgeReached(object owner, PlayerEdge reached)
    {
        if (owner is PeerSession session && _controllers.TryGetValue(session.PeerId, out var state))
        {
            // Called under the player lock; the hand-off runs elsewhere.
            _ = Task.Run(() => HandOffAsync(session, state, reached));
        }
    }

    private async Task HandOffAsync(PeerSession session, ControlledState state, PlayerEdge reached)
    {
        if (Interlocked.Exchange(ref state.HandOffPending, 1) != 0)
        {
            return; // the cursor keeps pushing while a hand-off is being prepared
        }

        try
        {
            if (BeforeEdgeHandOff is { } prepare && _injector.IsButtonDown(MouseButton.Left))
            {
                try
                {
                    await prepare(session, reached.At).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Preparing the hand-off to {Peer} failed", session.PeerName);
                }
            }

            await SendEdgeReachedAsync(session, reached).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref state.HandOffPending, 0);
        }
    }

    /// <summary>Which monitor the cursor leaves goes only to a controller that knows monitors; older ones get the plain event.</summary>
    private async Task SendEdgeReachedAsync(PeerSession session, PlayerEdge reached)
    {
        try
        {
            var frame = _peerMonitors.ContainsKey(session.PeerId) && reached.Monitor.Length > 0
                ? InputCodec.Single(w => InputCodec.WriteEdgeReachedOnMonitor(w, reached.Edge, reached.Relative, reached.RelativeToScreen, reached.Monitor))
                : InputCodec.Single(w => InputCodec.WriteEdgeReached(w, reached.Edge, reached.RelativeToScreen));
            await session.SendAsync(ChannelId.Input, frame, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "EdgeReached not delivered to {Peer}", session.PeerName);
        }
    }
}
