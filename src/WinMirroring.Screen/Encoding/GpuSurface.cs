using Vortice.Direct3D11;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// A finished picture that stays on the graphics card: a texture the decoder's card writes into and the window's
/// draws from, with nothing in between.
///
/// What it replaces is three trips across the same pixels. A decoded picture was turned into BGRA on the card,
/// read back into memory (which makes the card finish everything it has queued and wait), copied into the
/// window's bitmap, and uploaded to the card again to be drawn — eight megabytes each way for a 1080p frame, sixty
/// times a second, on the device that is usually a tablet. The texture is shared instead: the card that decoded it
/// and the one WPF draws with are the same card, and a shared handle is how one of its textures is named to both.
/// </summary>
public sealed class GpuSurface : IDisposable
{
    internal GpuSurface(ID3D11Texture2D texture, nint handle, int width, int height)
    {
        Texture = texture;
        Handle = handle;
        Width = width;
        Height = height;
    }

    internal ID3D11Texture2D Texture { get; }

    /// <summary>The name this texture goes by to another device on the same card (a legacy shared handle).</summary>
    public nint Handle { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// True once this end has let go of it. Whoever opened it on the other side still holds the picture — a shared
    /// texture lives until its last holder lets go — but nothing new will be written into it, and its handle may
    /// come round again as the name of a different texture, so it must not be looked up by handle after this.
    /// </summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Texture.Dispose();
    }
}
