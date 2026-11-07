using Microsoft.Extensions.Logging;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
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
            case InputEventKind.Enter:
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

            case InputEventKind.EdgeReached:
                // We are the controller here: the controlled device says the cursor is leaving it.
                _commands.Writer.TryWrite(new EdgeReachedCommand(session.PeerId, evt.Edge, evt.Relative));
                break;

            case InputEventKind.Alive:
                // The session already recorded the arrival time; that is all a keepalive is for.
                break;
        }
    }

    private void TakeControl(PeerSession session, ControlledState state, InputEvent evt)
    {
        var entry = evt.AtCenter
            ? VirtualScreen.PrimaryCenter
            : EdgeMath.PointOn(VirtualScreen.Current, evt.Edge, evt.Relative, inset: 1);

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

    private void OnPlayerEdgeReached(object owner, Edge edge, float relative)
    {
        if (owner is PeerSession session)
        {
            // Called under the player lock; the send runs elsewhere.
            _ = Task.Run(() => SendEdgeReachedAsync(session, edge, relative));
        }
    }

    private async Task SendEdgeReachedAsync(PeerSession session, Edge edge, float relative)
    {
        try
        {
            await session.SendAsync(ChannelId.Input, InputCodec.Single(w => InputCodec.WriteEdgeReached(w, edge, relative)), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "EdgeReached not delivered to {Peer}", session.PeerName);
        }
    }
}
