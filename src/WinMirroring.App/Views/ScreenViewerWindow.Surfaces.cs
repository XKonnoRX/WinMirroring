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

    /// <summary>Pictures were drawn from the card somewhere in this run of the program (see the app's handling of a drawing thread that died).</summary>
    public static bool SurfacesWereUsed { get; private set; }

    /// <summary>Raised when the device the surfaces were opened on is being replaced: every window lets go of what it was given.</summary>
    private static event Action? SurfacesMustBeLetGo;

    /// <summary>Says, before the first picture, whether this window can take pictures that stay on the card.</summary>
    private void InitializeSurfaces()
    {
        var settings = _node?.Settings.Screen;
        var allowed = settings?.DirectPresent != false;
        var broke = settings is not null && settings.DirectPresentBrokeIn.Length > 0
            && settings.DirectPresentBrokeIn == Services.GithubUpdater.CurrentVersion.ToString();

        // WPF itself has to be drawing with the card for this to be a saving: where it draws in software, a
        // surface on the card is read back for every frame, which is the trip this exists to avoid, taken twice.
        var hardware = (RenderCapability.Tier >> 16) >= 2;
        var wanted = allowed && !broke && hardware && Bridge() is not null;
        _surfacesAllowed = wanted;
        _viewer.SurfacesWanted = wanted;
        if (!wanted)
        {
            Log.Information(
                "Pictures from {Peer} are drawn through memory: {Reason}",
                _viewer.PeerName,
                !allowed ? "drawing from the graphics card is switched off in the settings"
                : broke ? "drawing from the graphics card brought WPF's drawing down in this version, so it is left off until the next one"
                : !hardware ? "this window is not drawn by the graphics card"
                : "no Direct3D 9 device here");
            return;
        }

        SurfacesMustBeLetGo += DetachSurfaces;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _surfaceEventsHooked = true;
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

    /// <summary>
    /// The bridge, with a device that is still there. A Direct3D 9 device can be lost under a program — the card
    /// reset, a driver replaced, some ways of sleeping — and surfaces opened on a lost one are not pictures any
    /// more. Then every window lets go of what it holds from it, and a new device is made.
    /// </summary>
    private static Direct3D9Bridge LiveBridge()
    {
        var bridge = Bridge() ?? throw new InvalidOperationException("There is no Direct3D 9 device to draw pictures through.");
        if (bridge.IsUsable)
        {
            return bridge;
        }

        Log.Information("The Direct3D 9 device pictures are drawn through was lost; making a new one");
        SurfacesMustBeLetGo?.Invoke();
        bridge.Dispose();
        _bridge = Direct3D9Bridge.TryCreate();
        return _bridge ?? throw new InvalidOperationException("The Direct3D 9 device was lost and a new one could not be made.");
    }

    // ---- when WPF cannot draw from the card --------------------------------------------------------------
    //
    // WPF draws a D3DImage from the card only while it has the card's front buffer, and it loses that whenever the
    // desktop does: the screen locks, the display sleeps, the card is reset. There are two ways to live with it.
    // One hands the watching to the program ("software fallback": WPF keeps the surface it was given and reads it
    // however it can, and the program must notice by itself that its device has gone and take the surface back).
    // That is the way this used at first, without doing the noticing — and on the tablet, after it had slept with
    // a view open, WPF's drawing thread died over it: every window then failed with "insufficient memory", which
    // is how WPF reports that thread's death, on a device with half its memory free.
    //
    // This is the other way, the one WPF looks after itself: when it loses the front buffer it lets go of the
    // surface and says so; nothing is given to it until it has the front buffer back; and meanwhile pictures come
    // through memory, as they do wherever drawing from the card is not possible at all.

    /// <summary>This window may draw from the card at all (the settings, the card, the device).</summary>
    private bool _surfacesAllowed;

    /// <summary>Drawing from the card is set aside for now; pictures arrive in memory until it is tried again.</summary>
    private bool _surfacesPaused;
    private bool _surfaceEventsHooked;
    private System.Windows.Threading.DispatcherTimer? _surfaceRetry;

    /// <summary>How long until drawing from the card is tried again: longer each time it is still not possible.</summary>
    private TimeSpan _surfaceRetryAfter = FirstSurfaceRetry;

    private static readonly TimeSpan FirstSurfaceRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestSurfaceRetry = TimeSpan.FromSeconds(60);

    private D3DImage NewSurfaceImage()
    {
        var image = new D3DImage();
        image.IsFrontBufferAvailableChanged += OnFrontBufferChanged;
        return image;
    }

    private void OnFrontBufferChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            PauseSurfaces("WPF has lost the graphics card's front buffer (the screen locked, the display slept, or the card was reset)");
        }
        else if (_surfacesPaused)
        {
            RetrySurfaces();
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            // Before the device sleeps, nothing of the card's is left in WPF's hands: what the card is when it
            // wakes is not known, and a surface from before is the last thing its drawing thread should find.
            Dispatcher.BeginInvoke(() =>
            {
                DetachSurfaces();
                PauseSurfaces("the device is going to sleep");
            });
        }
    }

    /// <summary>Takes back from WPF the surfaces it was given. The pictures they were go blank until the next one arrives.</summary>
    private void DetachSurfaces()
    {
        foreach (var image in new[] { _wholeSurface, _partSurface })
        {
            if (image is null)
            {
                continue;
            }

            try
            {
                image.Lock();
                try
                {
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                }
                finally
                {
                    image.Unlock();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
        }
    }

    /// <summary>Pictures go through memory from here, until drawing from the card is tried again in a while.</summary>
    private void PauseSurfaces(string reason)
    {
        if (_surfacesFailed || !_surfacesAllowed)
        {
            return;
        }

        _viewer.SurfacesWanted = false;
        if (!_surfacesPaused)
        {
            _surfacesPaused = true;
            Log.Information("Pictures from {Peer} go through memory for now: {Reason}", _viewer.PeerName, reason);

            // What was on the screen has gone with the surface it was in; a screen on which nothing moves would
            // otherwise stay blank until something did.
            _viewer.AskForWholePicture();
        }

        _surfaceRetry ??= new System.Windows.Threading.DispatcherTimer(
            _surfaceRetryAfter, System.Windows.Threading.DispatcherPriority.Background, (_, _) => RetrySurfaces(), Dispatcher);
        _surfaceRetry.Stop();
        _surfaceRetry.Interval = _surfaceRetryAfter;
        _surfaceRetry.Start();
    }

    /// <summary>
    /// Tries drawing from the card again: the next picture is left on the card, and drawing it finds out. If WPF
    /// still cannot, it is set aside again, for longer.
    /// </summary>
    private void RetrySurfaces()
    {
        _surfaceRetry?.Stop();
        if (_surfacesFailed || !_surfacesAllowed || !_surfacesPaused)
        {
            return;
        }

        if ((RenderCapability.Tier >> 16) < 2)
        {
            _surfaceRetry?.Start(); // WPF is not drawing with the card at all just now (a remote session): later
            return;
        }

        _surfacesPaused = false;
        _surfaceRetryAfter = TimeSpan.FromSeconds(Math.Min(LongestSurfaceRetry.TotalSeconds, _surfaceRetryAfter.TotalSeconds * 2));
        _viewer.SurfacesWanted = true;
    }

    /// <summary>The window is closing: nothing of it is left listening or ticking.</summary>
    private void StopSurfaces()
    {
        _surfaceRetry?.Stop();
        if (_surfaceEventsHooked)
        {
            _surfaceEventsHooked = false;
            SurfacesMustBeLetGo -= DetachSurfaces;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    /// <summary>
    /// Points <paramref name="image"/> at <paramref name="surface"/> and has WPF draw it. False when WPF cannot
    /// draw from the card just now: the picture is dropped, and the ones after it come through memory.
    /// </summary>
    private bool Draw(D3DImage image, GpuSurface surface)
    {
        if (!image.IsFrontBufferAvailable)
        {
            PauseSurfaces("WPF does not have the graphics card's front buffer");
            return false;
        }

        var pointer = LiveBridge().Open(surface);
        image.Lock();
        try
        {
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, pointer);
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            {
                // It did not take the surface: the front buffer went between the question above and here.
                PauseSurfaces("WPF would not take a surface");
                return false;
            }

            image.AddDirtyRect(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        finally
        {
            image.Unlock();
        }

        SurfacesWereUsed = true;
        _surfaceRetryAfter = FirstSurfaceRetry;
        return true;
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
            _wholeSurface ??= NewSurfaceImage();
            if (!Draw(_wholeSurface, surface))
            {
                return false;
            }
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
            _partSurface ??= NewSurfaceImage();
            if (!Draw(_partSurface, surface))
            {
                return false;
            }
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
        _surfaceRetry?.Stop();
        _viewer.SurfacesWanted = false;
        if (_surfacesFailed)
        {
            return;
        }

        _surfacesFailed = true;
        Log.Warning(ex, "Drawing pictures from {Peer} straight from the graphics card failed; they go through memory from here on", _viewer.PeerName);
    }
}
