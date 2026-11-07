using System.Drawing;
using Vortice.Mathematics;
using Vortice.WIC;

namespace WinMirroring.Screen.Encoding;

/// <summary>A JPEG tile positioned inside the streamed image.</summary>
public sealed record EncodedTile(int X, int Y, int Width, int Height, byte[] Jpeg);

/// <summary>
/// Splits the (cropped) image into 128×128 tiles, compares each with what was sent last time and JPEG-encodes
/// only the changed ones, in parallel. A static desktop therefore costs nothing; typing or a moving cursor costs
/// a few tiles.
/// </summary>
internal sealed class TileEncoder : IDisposable
{
    public const int TileSize = 128;

    private readonly IWICImagingFactory _wic = new();
    private byte[] _previous = [];
    private int _width;
    private int _height;

    /// <summary>JPEG quality, 0.3–0.95.</summary>
    public float Quality { get; set; } = 0.7f;

    public int Width => _width;

    public int Height => _height;

    /// <param name="frame">Captured BGRA pixels, <paramref name="frameWidth"/> pixels per row.</param>
    /// <param name="crop">The part of the frame that is streamed; tiles are relative to it.</param>
    public List<EncodedTile> Encode(byte[] frame, int frameWidth, int frameHeight, Rectangle crop, bool forceAll)
    {
        crop = Rectangle.Intersect(crop, new Rectangle(0, 0, frameWidth, frameHeight));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            return [];
        }

        if (crop.Width != _width || crop.Height != _height)
        {
            _width = crop.Width;
            _height = crop.Height;
            _previous = new byte[_width * _height * 4];
            forceAll = true;
        }

        var changed = new List<Rectangle>();
        for (var ty = 0; ty < _height; ty += TileSize)
        {
            for (var tx = 0; tx < _width; tx += TileSize)
            {
                var tile = new Rectangle(tx, ty, Math.Min(TileSize, _width - tx), Math.Min(TileSize, _height - ty));
                if (forceAll || Differs(frame, frameWidth, crop, tile))
                {
                    changed.Add(tile);
                }
            }
        }

        if (changed.Count == 0)
        {
            return [];
        }

        var results = new EncodedTile[changed.Count];
        var quality = Math.Clamp(Quality, 0.3f, 0.95f);
        Parallel.For(0, changed.Count, () => new byte[TileSize * TileSize * 3], (i, _, bgr) =>
        {
            results[i] = EncodeTile(frame, frameWidth, crop, changed[i], bgr, quality);
            return bgr;
        }, _ => { });

        foreach (var tile in changed)
        {
            for (var y = 0; y < tile.Height; y++)
            {
                frame.AsSpan(SourceOffset(frameWidth, crop, tile.X, tile.Y + y), tile.Width * 4)
                    .CopyTo(_previous.AsSpan(((tile.Y + y) * _width + tile.X) * 4));
            }
        }

        return [.. results];
    }

    private bool Differs(byte[] frame, int frameWidth, Rectangle crop, Rectangle tile)
    {
        var rowBytes = tile.Width * 4;
        for (var y = 0; y < tile.Height; y++)
        {
            var current = frame.AsSpan(SourceOffset(frameWidth, crop, tile.X, tile.Y + y), rowBytes);
            var previous = _previous.AsSpan(((tile.Y + y) * _width + tile.X) * 4, rowBytes);
            if (!current.SequenceEqual(previous))
            {
                return true;
            }
        }

        return false;
    }

    private EncodedTile EncodeTile(byte[] frame, int frameWidth, Rectangle crop, Rectangle tile, byte[] bgr, float quality)
    {
        for (var y = 0; y < tile.Height; y++)
        {
            var source = frame.AsSpan(SourceOffset(frameWidth, crop, tile.X, tile.Y + y), tile.Width * 4);
            var target = bgr.AsSpan(y * tile.Width * 3, tile.Width * 3);
            for (var x = 0; x < tile.Width; x++)
            {
                target[x * 3] = source[x * 4];
                target[x * 3 + 1] = source[x * 4 + 1];
                target[x * 3 + 2] = source[x * 4 + 2];
            }
        }

        using var output = new MemoryStream(8 * 1024);
        using (var encoder = _wic.CreateEncoder(ContainerFormat.Jpeg, output))
        {
            using var encodedFrame = encoder.CreateNewFrame(out var options);
            using (options)
            {
                options.Set("ImageQuality", quality);
                encodedFrame.Initialize(options);
            }

            encodedFrame.SetSize((uint)tile.Width, (uint)tile.Height);
            var format = PixelFormat.Format24bppBGR;
            encodedFrame.SetPixelFormat(ref format);
            encodedFrame.WritePixels((uint)tile.Height, (uint)(tile.Width * 3), new ReadOnlySpan<byte>(bgr, 0, tile.Width * tile.Height * 3));
            encodedFrame.Commit();
            encoder.Commit();
        }

        return new EncodedTile(tile.X, tile.Y, tile.Width, tile.Height, output.ToArray());
    }

    private static int SourceOffset(int frameWidth, Rectangle crop, int x, int y) => ((crop.Y + y) * frameWidth + crop.X + x) * 4;

    public void Dispose() => _wic.Dispose();
}

/// <summary>Decodes JPEG tiles straight into a BGRA image buffer (viewer side).</summary>
internal sealed class TileDecoder : IDisposable
{
    private readonly IWICImagingFactory _wic = new();

    public unsafe void DecodeInto(EncodedTile tile, byte[] image, int imageWidth, int imageHeight)
    {
        if (tile.X < 0 || tile.Y < 0 || tile.X + tile.Width > imageWidth || tile.Y + tile.Height > imageHeight)
        {
            throw new InvalidDataException("Tile outside the image.");
        }

        using var input = new MemoryStream(tile.Jpeg, writable: false);
        using var decoder = _wic.CreateDecoderFromStream(input, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        using var converter = _wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA);

        var size = converter.Size;
        var width = Math.Min(size.Width, tile.Width);
        var height = Math.Min(size.Height, tile.Height);
        var stride = (uint)(imageWidth * 4);
        var offset = ((long)tile.Y * imageWidth + tile.X) * 4;
        var available = (uint)(image.Length - offset);
        fixed (byte* pixels = image)
        {
            converter.CopyPixels(new RectI(0, 0, width, height), stride, available, (nint)(pixels + offset));
        }
    }

    public void Dispose() => _wic.Dispose();
}
