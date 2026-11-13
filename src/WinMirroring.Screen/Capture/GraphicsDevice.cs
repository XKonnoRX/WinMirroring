using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen.Capture;

/// <summary>
/// One Direct3D device for the whole process: screen capture, the colour conversion and the hardware encoder all
/// work on the same frames, and a texture can only travel between them without a copy when it belongs to the same
/// device. Several threads use it (capture arrives on its own), so it is created multithread-protected.
/// </summary>
internal sealed class GraphicsDevice
{
    private static readonly Lazy<GraphicsDevice?> Instance = new(TryCreate, LazyThreadSafetyMode.ExecutionAndPublication);

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrt)
    {
        Device = device;
        Context = context;
        WinRt = winrt;
    }

    /// <summary>Null when Direct3D is unavailable; everything then falls back to the paths that use the processor.</summary>
    public static GraphicsDevice? Shared => Instance.Value;

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>The same device as WinRT sees it, for Windows.Graphics.Capture.</summary>
    public IDirect3DDevice WinRt { get; }

    /// <summary>Held around every use of <see cref="Context"/>: a device context serves one thread at a time.</summary>
    public object ContextGate { get; } = new();

    private static GraphicsDevice? TryCreate()
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device device,
                out ID3D11DeviceContext context);
            if (result.Failure)
            {
                return null;
            }

            // Capture, conversion and the encoder all reach for this device from their own threads.
            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                multithread?.SetMultithreadProtected(true);
            }

            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            var winrt = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return new GraphicsDevice(device, context, winrt);
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
