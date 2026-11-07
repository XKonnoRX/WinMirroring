using System.Buffers.Binary;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Injection;

/// <summary>
/// App side of the elevated helper: encodes each injection call and writes it to the pipe for the helper to replay.
/// Writes are serialized and never throw back into the injection threads; the first failure marks the sink
/// disconnected (so <see cref="InjectionRouter"/> falls back to local injection) and raises <see cref="Disconnected"/>.
/// </summary>
public sealed class RemoteInjectionSink : IInjectionSink, IDisposable
{
    private readonly PipeStream _pipe;
    private readonly ILogger _logger;
    private readonly object _writeGate = new();
    private volatile bool _connected = true;

    public RemoteInjectionSink(PipeStream pipe, ILogger logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>Raised once, when a write first fails (the helper went away).</summary>
    public event Action? Disconnected;

    public bool IsConnected => _connected && _pipe.IsConnected;

    public void MoveAbsolute(int x, int y) => Write(InjectionCodec.MoveAbsolute(x, y));

    public void MoveRelative(int dx, int dy) => Write(InjectionCodec.MoveRelative(dx, dy));

    public void Button(MouseButton button, bool down) => Write(InjectionCodec.Button(button, down));

    public void Wheel(int delta, bool horizontal) => Write(InjectionCodec.Wheel(delta, horizontal));

    public void Key(ushort virtualKey, ushort scanCode, bool up, bool extended) => Write(InjectionCodec.Key(virtualKey, scanCode, up, extended));

    public void Unicode(char ch, bool up) => Write(InjectionCodec.Unicode(ch, up));

    public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts) => Write(InjectionCodec.TouchFrame(contacts));

    /// <summary>Asks the helper to exit (best effort).</summary>
    public void SendBye() => Write(InjectionCodec.Bye());

    private void Write(byte[] message)
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            lock (_writeGate)
            {
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
                _pipe.Write(header);
                _pipe.Write(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (_connected)
            {
                _connected = false;
                _logger.LogInformation("Elevated input helper disconnected: {Message}", ex.Message);
                Disconnected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _connected = false;
        _pipe.Dispose();
    }
}
