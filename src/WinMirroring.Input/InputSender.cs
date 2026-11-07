using System.Buffers;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Protocol;

namespace WinMirroring.Input;

/// <summary>
/// Outbound Input channel for one capture. Producers (the hook thread, the worker) only append to an in-memory
/// buffer and never wait for the network; a background loop sends whatever accumulated as one frame, and sends
/// a keepalive when there was nothing to send for a while. A stalled link therefore can never block local input.
/// In-flight writes are never cancelled, because a half-written TLS record would corrupt the session.
/// </summary>
internal sealed class InputSender
{
    private const int MaxPendingBytes = 256 * 1024;
    private static readonly TimeSpan AliveInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ArrayBufferWriter<byte> _pending = new(4096);
    private readonly SemaphoreSlim _signal = new(0);
    private int _signaled;
    private bool _completing;
    private volatile bool _faulted;

    public InputSender(PeerSession session, ILogger logger)
    {
        Session = session;
        _logger = logger;
        _ = Task.Run(LoopAsync);
    }

    public PeerSession Session { get; }

    /// <summary>The link failed or the buffer overflowed; the capture must end.</summary>
    public bool IsFaulted => _faulted;

    public bool PostMove(uint timestamp, int dx, int dy)
    {
        lock (_gate)
        {
            if (!CanWrite())
            {
                return false;
            }

            InputCodec.WriteMouseMove(_pending, timestamp, dx, dy);
            return Commit();
        }
    }

    public bool PostButton(MouseButton button, bool down)
    {
        lock (_gate)
        {
            if (!CanWrite())
            {
                return false;
            }

            InputCodec.WriteMouseButton(_pending, button, down);
            return Commit();
        }
    }

    public bool PostWheel(int delta, bool horizontal)
    {
        lock (_gate)
        {
            if (!CanWrite())
            {
                return false;
            }

            InputCodec.WriteMouseWheel(_pending, delta, horizontal);
            return Commit();
        }
    }

    public bool PostKey(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        lock (_gate)
        {
            if (!CanWrite())
            {
                return false;
            }

            InputCodec.WriteKey(_pending, virtualKey, scanCode, up, extended);
            return Commit();
        }
    }

    public bool PostEnter(bool atCenter, Edge edge, float relative)
    {
        lock (_gate)
        {
            if (!CanWrite())
            {
                return false;
            }

            if (atCenter)
            {
                InputCodec.WriteEnterAtCenter(_pending);
            }
            else
            {
                InputCodec.WriteEnter(_pending, edge, relative);
            }

            return Commit();
        }
    }

    /// <summary>Queues Leave, flushes it and stops the loop. Returns immediately.</summary>
    public void CompleteWithLeave()
    {
        lock (_gate)
        {
            if (CanWrite())
            {
                InputCodec.WriteLeave(_pending);
            }

            _completing = true;
        }

        Signal();
    }

    /// <summary>Drops whatever is queued and stops after the write in flight (if any) finishes.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _pending.Clear();
            _completing = true;
            _faulted = true;
        }

        Signal();
    }

    private bool CanWrite() => !_completing && !_faulted;

    private bool Commit()
    {
        if (_pending.WrittenCount > MaxPendingBytes)
        {
            _faulted = true;
            _pending.Clear();
            Signal();
            return false;
        }

        Signal();
        return true;
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0)
        {
            _signal.Release();
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                var woke = await _signal.WaitAsync(AliveInterval).ConfigureAwait(false);
                Volatile.Write(ref _signaled, 0);

                byte[]? frame = null;
                bool completing;
                lock (_gate)
                {
                    completing = _completing;
                    if (!woke && !completing && !_faulted && _pending.WrittenCount == 0)
                    {
                        InputCodec.WriteAlive(_pending);
                    }

                    if (_pending.WrittenCount > 0)
                    {
                        frame = _pending.WrittenSpan.ToArray();
                        _pending.Clear();
                    }
                }

                if (frame is not null)
                {
                    await Session.SendAsync(ChannelId.Input, frame).ConfigureAwait(false);
                }

                if (completing)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _faulted = true;
            _logger.LogInformation("Input link to {Peer} failed: {Message}", Session.PeerName, ex.Message);
        }
    }
}
