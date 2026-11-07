using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen.Capture;

/// <summary>
/// Windows.Graphics.Capture of one monitor or one window (a window is captured even while covered by others).
/// Each frame is copied to a staging texture and read back into a managed BGRA buffer; consumers pull the latest
/// frame when they are ready, so slow consumers simply skip frames.
/// </summary>
internal sealed class WgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly SemaphoreSlim _frameSignal = new(0, 1);

    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private byte[] _latest = [];
    private int _width;
    private int _height;
    private long _version;
    private bool _disposed;

    private WgcCapture(GraphicsCaptureItem item, ILogger logger)
    {
        _logger = logger;
        _item = item;

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        try
        {
            _session.IsBorderRequired = false;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.LogDebug("Capture border stays on: {Message}", ex.Message);
        }

        _session.IsCursorCaptureEnabled = true;
        _item.Closed += (_, _) => Closed?.Invoke();
    }

    /// <summary>The captured window was closed.</summary>
    public event Action? Closed;

    public string Title => _item.DisplayName;

    public static WgcCapture ForMonitor(nint monitor, ILogger logger) =>
        new(CreateItem(interop =>
        {
            var iid = GraphicsCaptureItemIid;
            return interop.CreateForMonitor(monitor, ref iid);
        }), logger);

    public static WgcCapture ForWindow(nint window, ILogger logger) =>
        new(CreateItem(interop =>
        {
            var iid = GraphicsCaptureItemIid;
            return interop.CreateForWindow(window, ref iid);
        }), logger);

    public void Start() => _session.StartCapture();

    /// <summary>Waits until a frame newer than <paramref name="seenVersion"/> exists (or the timeout).</summary>
    public async Task<bool> WaitForFrameAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        if (Interlocked.Read(ref _version) > seenVersion)
        {
            return true;
        }

        try
        {
            await _frameSignal.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return Interlocked.Read(ref _version) > seenVersion;
    }

    /// <summary>Size of the last captured frame (0×0 before the first one).</summary>
    public System.Drawing.Size FrameSize
    {
        get
        {
            lock (_gate)
            {
                return new System.Drawing.Size(_width, _height);
            }
        }
    }

    private void SignalFrame()
    {
        if (_frameSignal.CurrentCount == 0)
        {
            try
            {
                _frameSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Copies the latest frame into <paramref name="buffer"/> (resized when needed).</summary>
    public bool TryCopyLatest(ref byte[] buffer, out int width, out int height, out long version)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            version = _version;
            if (version == 0)
            {
                return false;
            }

            var size = width * height * 4;
            if (buffer.Length != size)
            {
                buffer = new byte[size];
            }

            _latest.AsSpan(0, size).CopyTo(buffer);
            return true;
        }
    }

    private static GraphicsCaptureItem CreateItem(Func<IGraphicsCaptureItemInterop, nint> create)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        var pointer = create(interop);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var contentSize = frame.ContentSize;
                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // The window was resized: rebuild the pool; the next frame has the new size.
                    _poolSize = contentSize;
                    _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                    return;
                }

                ReadBack(frame);
            }

            SignalFrame();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or SharpGen.Runtime.SharpGenException)
        {
            _logger.LogDebug(ex, "Capture frame dropped");
        }
    }

    private unsafe void ReadBack(Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<Native.ScreenNative.IDirect3DDxgiInterfaceAccess>();
        var textureIid = typeof(ID3D11Texture2D).GUID;
        using var texture = new ID3D11Texture2D(access.GetInterface(ref textureIid));
        var description = texture.Description;
        var width = (int)Math.Min(description.Width, (uint)_poolSize.Width);
        var height = (int)Math.Min(description.Height, (uint)_poolSize.Height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_staging is null || _staging.Description.Width != description.Width || _staging.Description.Height != description.Height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription(
                description.Format, description.Width, description.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        }

        _context.CopyResource(_staging, texture);
        var map = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            var size = width * height * 4;
            if (_latest.Length != size)
            {
                _latest = new byte[size];
            }

            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)map.DataPointer + (long)y * map.RowPitch, rowBytes).CopyTo(_latest.AsSpan(y * rowBytes, rowBytes));
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        _width = width;
        _height = height;
        _version++;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _staging?.Dispose();
        _context.Dispose();
        _device.Dispose();
        SignalFrame();
    }
}
