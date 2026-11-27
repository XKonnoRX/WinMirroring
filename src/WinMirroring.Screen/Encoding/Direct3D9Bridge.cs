using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D9;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Opens a <see cref="GpuSurface"/> as a Direct3D 9 surface, which is the only kind of surface WPF will draw
/// straight from the card (its <c>D3DImage</c>). Nothing is copied: the surface that comes back is the same memory
/// on the card as the texture the decoder wrote, under its other name.
///
/// One of these serves a whole process. It is used from the thread that draws (the window's), and from tests.
/// </summary>
public sealed class Direct3D9Bridge : IDisposable
{
    /// <summary>Surfaces kept open at once: two pictures' worth of three each, and a little room.</summary>
    private const int MostOpen = 8;

    private readonly IDirect3D9Ex _direct3D;
    private readonly IDirect3DDevice9Ex _device;
    private readonly List<(GpuSurface Shared, IDirect3DTexture9 Texture, IDirect3DSurface9 Surface)> _open = [];
    private bool _disposed;

    private Direct3D9Bridge(IDirect3D9Ex direct3D, IDirect3DDevice9Ex device)
    {
        _direct3D = direct3D;
        _device = device;
    }

    /// <summary>Null when this machine will not give out a Direct3D 9 device; pictures then go through memory as before.</summary>
    public static Direct3D9Bridge? TryCreate(ILogger? logger = null)
    {
        IDirect3D9Ex? direct3D = null;
        try
        {
            direct3D = D3D9.Direct3DCreate9Ex();
            var window = GetDesktopWindow();
            var parameters = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = window,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
                BackBufferFormat = Format.Unknown,
                PresentationInterval = PresentInterval.Immediate,
            };

            // Nothing is ever drawn with this device; it exists to hold surfaces. Multithreaded, because WPF's own
            // drawing thread reads the surfaces it is given.
            var device = direct3D.CreateDeviceEx(
                0,
                DeviceType.Hardware,
                window,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                parameters);
            return new Direct3D9Bridge(direct3D, device);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger?.LogInformation("Pictures cannot be drawn straight from the graphics card here ({Message}); they go through memory", ex.Message);
            direct3D?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Whether the device surfaces are opened on is still there. It can be lost under a program — the card reset,
    /// a driver replaced, some ways of sleeping — and what was opened on it is then not a picture any more: a new
    /// bridge has to be made, and nothing from this one handed on.
    /// </summary>
    public bool IsUsable
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                // Throws for a device that is lost, hung or removed. Occluded, or the display mode changed, are
                // not failures: both are a device that is there.
                _device.CheckDeviceState(GetDesktopWindow());
                return true;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The Direct3D 9 surface that is <paramref name="shared"/>, as the pointer <c>D3DImage.SetBackBuffer</c> takes.
    /// Opened once and kept while the picture is in use; throws when the card will not open it, and the caller
    /// then stops asking for pictures this way.
    /// </summary>
    public nint Open(GpuSurface shared)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shared);

        for (var i = 0; i < _open.Count; i++)
        {
            if (ReferenceEquals(_open[i].Shared, shared))
            {
                return _open[i].Surface.NativePointer;
            }
        }

        if (shared.IsDisposed)
        {
            throw new InvalidOperationException("The picture has already been given up by the decoder.");
        }

        // By the object, never by its handle: a handle is reused once its texture is gone, and a surface found
        // under a reused handle would be a picture that never changes again.
        var handle = shared.Handle;
        var texture = _device.CreateTexture((uint)shared.Width, (uint)shared.Height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref handle);
        var surface = texture.GetSurfaceLevel(0);
        _open.Add((shared, texture, surface));
        Trim();
        return surface.NativePointer;
    }

    /// <summary>
    /// Lets go of what is no longer wanted: first whatever the decoder has given up, then the longest unused.
    /// A surface WPF is still drawing from is held by WPF itself, so letting go of it here takes nothing away.
    /// </summary>
    private void Trim()
    {
        for (var i = _open.Count - 2; i >= 0 && _open.Count > 1; i--)
        {
            if (_open[i].Shared.IsDisposed)
            {
                Release(i);
            }
        }

        while (_open.Count > MostOpen)
        {
            Release(0);
        }
    }

    private void Release(int index)
    {
        _open[index].Surface.Dispose();
        _open[index].Texture.Dispose();
        _open.RemoveAt(index);
    }

    /// <summary>
    /// The pixels of <paramref name="shared"/> as this side sees them, top row first, four bytes each (blue, green,
    /// red, alpha). For checking that the two names really are one picture; nothing that draws uses it.
    /// </summary>
    public unsafe byte[] Read(GpuSurface shared)
    {
        var pointer = Open(shared);
        var surface = _open.First(o => o.Surface.NativePointer == pointer).Surface;
        using var copy = _device.CreateOffscreenPlainSurface((uint)shared.Width, (uint)shared.Height, Format.A8R8G8B8, Pool.SystemMemory);
        _device.GetRenderTargetData(surface, copy);
        var pixels = new byte[shared.Width * shared.Height * 4];
        var locked = copy.LockRect(LockFlags.ReadOnly);
        try
        {
            var stride = shared.Width * 4;
            for (var y = 0; y < shared.Height; y++)
            {
                Marshal.Copy(locked.DataPointer + (nint)((long)y * locked.Pitch), pixels, y * stride, stride);
            }
        }
        finally
        {
            copy.UnlockRect();
        }

        return pixels;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_open.Count > 0)
        {
            Release(_open.Count - 1);
        }

        _device.Dispose();
        _direct3D.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
