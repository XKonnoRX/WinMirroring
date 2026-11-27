using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;
using WinMirroring.Screen;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.App.Views;

/// <summary>
/// Pictures drawn straight from the graphics card.
///
/// A picture used to be decoded on the card, read back into memory, copied into this window's bitmap and uploaded
/// to the card again to be drawn. Now, where the card is doing the decoding, the picture stays there
/// (<see cref="GpuSurface"/>) and this window is handed the surface it is in: WPF draws it from the card, through
/// <see cref="D3DImage"/>, which sits in the layout exactly where the bitmap did — so the zoom, the overlay, and
/// everything that measures positions on the picture go on working without knowing the difference.
///
/// None of this is relied on. The window asks for pictures this way only where it expects drawing them to work,
/// and the first time it does not — a card that will not open the surface, a driver that refuses — it says so in
/// the log, stops asking, and the very next picture arrives in memory as pictures always did.
/// </summary>
public partial class ScreenViewerWindow
{
    // One for the process: it holds a Direct3D 9 device, and every view draws through the same one. Made the first
    // time a view opens, on the thread all views draw on.
    private static Direct3D9Bridge? _bridge;
    private static bool _bridgeTried;

    /// <summary>The picture of the whole screen and of the part zoomed into, while they are drawn from the card.</summary>
    private D3DImage? _wholeSurface;
    private D3DImage? _partSurface;
    private (int Width, int Height) _wholeSurfaceSize;
    private bool _surfacesFailed;

    /// <summary>Whether there is a picture of the other device on the screen yet, whichever way it is drawn.</summary>
    private bool HasPicture => _bitmap is not null || _wholeSurface is not null;

    /// <summary>Says, before the first picture, whether this window can take pictures that stay on the card.</summary>
    private void InitializeSurfaces()
    {
        var allowed = _node?.Settings.Screen.DirectPresent != false;

        // WPF itself has to be drawing with the card for this to be a saving: where it draws in software, a
        // surface on the card is read back for every frame, which is the trip this exists to avoid, taken twice.
        var hardware = (RenderCapability.Tier >> 16) >= 2;
        var wanted = allowed && hardware && Bridge() is not null;
        _viewer.SurfacesWanted = wanted;
        if (!wanted)
        {
            Log.Information(
                "Pictures from {Peer} are drawn through memory: {Reason}",
                _viewer.PeerName,
                !allowed ? "drawing from the graphics card is switched off in the settings"
                : !hardware ? "this window is not drawn by the graphics card"
                : "no Direct3D 9 device here");
        }
    }

    private static Direct3D9Bridge? Bridge()
    {
        if (!_bridgeTried)
        {
            _bridgeTried = true;
            _bridge = Direct3D9Bridge.TryCreate();
        }

        return _bridge;
    }

    /// <summary>Points <paramref name="image"/> at <paramref name="surface"/> and has WPF draw it.</summary>
    private static void Draw(D3DImage image, GpuSurface surface)
    {
        var pointer = Bridge()!.Open(surface);
        image.Lock();
        try
        {
            // With the fallback on, a session that has lost its card for the moment — locked, or taken over by a
            // remote desktop — keeps drawing, slowly, instead of throwing here.
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, pointer, enableSoftwareFallback: true);
            image.AddDirtyRect(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        finally
        {
            image.Unlock();
        }
    }

    /// <summary>Draws a picture of the whole screen from the card. False: it could not, and the frame is dropped.</summary>
    private bool ShowWholeSurface(FrameUpdate update, GpuSurface surface)
    {
        if (surface.IsDisposed)
        {
            return false; // the view ended while this picture was on its way here; nothing has gone wrong
        }

        try
        {
            _wholeSurface ??= new D3DImage();
            Draw(_wholeSurface, surface);
        }
        catch (Exception ex)
        {
            _wholeSurface = null;
            GiveUpOnSurfaces(ex);
            return false;
        }

        var size = (update.Width, update.Height);
        if (_wholeSurfaceSize != size || !ReferenceEquals(ScreenImage.Source, _wholeSurface))
        {
            _wholeSurfaceSize = size;
            _bitmap = null;
            ScreenImage.Source = _wholeSurface;
            WaitingText.Visibility = Visibility.Collapsed;
            SizeToStream(update.Width, update.Height);
            ReportShownSize();
        }

        return true;
    }

    /// <summary>The same for the part zoomed into. False: it could not, and the frame is dropped.</summary>
    private bool ShowPartSurface(GpuSurface surface)
    {
        if (surface.IsDisposed)
        {
            return false;
        }

        try
        {
            _partSurface ??= new D3DImage();
            Draw(_partSurface, surface);
        }
        catch (Exception ex)
        {
            _partSurface = null;
            GiveUpOnSurfaces(ex);
            return false;
        }

        if (!ReferenceEquals(DetailImage.Source, _partSurface))
        {
            _detail = null;
            DetailImage.Source = _partSurface;
        }

        return true;
    }

    /// <summary>A picture has arrived in memory: whatever was being drawn from the card for it is let go.</summary>
    private void WholeArrivedInMemory()
    {
        if (_wholeSurface is not null)
        {
            _wholeSurface = null;
            _wholeSurfaceSize = default;
            _bitmap = null; // so the bitmap is made anew and put in the picture's place
        }
    }

    private void GiveUpOnSurfaces(Exception ex)
    {
        _viewer.SurfacesWanted = false;
        if (_surfacesFailed)
        {
            return;
        }

        _surfacesFailed = true;
        Log.Warning(ex, "Drawing pictures from {Peer} straight from the graphics card failed; they go through memory from here on", _viewer.PeerName);
    }
}
