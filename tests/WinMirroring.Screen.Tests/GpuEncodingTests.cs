using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The encoding path that never copies a frame through main memory: the graphics card crops the picture and turns
/// it into NV12, and the encoder reads that texture directly. These tests pin it to the path it replaces — the same
/// colours, the same crop, the same picture out the other end — because a wrong colour matrix or a misread plane
/// looks like a working stream until you look at it.
/// </summary>
public class GpuEncodingTests
{
    /// <summary>Wide flat bands of saturated colour: a wrong colour matrix shows up as a large, even error.</summary>
    private static byte[] ColourBands(int width, int height, int bandHeight)
    {
        var image = new byte[width * height * 4];
        ReadOnlySpan<(byte B, byte G, byte R)> colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220), (250, 250, 250), (10, 10, 10)];
        for (var y = 0; y < height; y++)
        {
            var colour = colours[(y / bandHeight) % colours.Length];
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[i] = colour.B;
                image[i + 1] = colour.G;
                image[i + 2] = colour.R;
                image[i + 3] = 255;
            }
        }

        return image;
    }

    private static double MeanColorError(byte[] a, byte[] b)
    {
        long error = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            error += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return error / (double)(a.Length / 4 * 3);
    }

    private static ID3D11Texture2D CreateBgra(GraphicsDevice graphics, byte[] bgra, int width, int height)
    {
        var texture = graphics.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
        graphics.Context.UpdateSubresource(bgra, texture, 0, (uint)(width * 4));
        return texture;
    }

    /// <summary>Brings an NV12 texture back into a tightly packed buffer, the way the processor path produces one.</summary>
    private static byte[] ReadNv12(GraphicsDevice graphics, ID3D11Texture2D texture, int width, int height)
    {
        var description = texture.Description;
        using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription(
            description.Format, description.Width, description.Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        graphics.Context.CopyResource(staging, texture);
        var map = graphics.Context.Map(staging, 0, MapMode.Read);
        try
        {
            var nv12 = new byte[Nv12Converter.Nv12Size(width, height)];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(map.DataPointer + (nint)((long)y * map.RowPitch), nv12, y * width, width);
            }

            // In a mapped NV12 resource the chroma rows follow the full luma plane at the same pitch.
            var chroma = map.DataPointer + (nint)((long)map.RowPitch * description.Height);
            for (var y = 0; y < height / 2; y++)
            {
                Marshal.Copy(chroma + (nint)((long)y * map.RowPitch), nv12, width * height + y * width, width);
            }

            return nv12;
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// The graphics card must produce the same NV12 the processor would — BT.601, studio swing — and must crop the
    /// region it was asked for. Were it to use BT.709, or full-range levels, every stream would shift colour the
    /// moment this path took over.
    /// </summary>
    [Fact]
    public void TheGraphicsCardConvertsLikeTheProcessorAndHonoursTheCrop()
    {
        Assert.SkipWhen(GraphicsDevice.Shared is null, "No Direct3D device on this machine.");
        using var converter = GpuNv12Converter.TryCreate(NullLogger.Instance);
        Assert.SkipWhen(converter is null, "This graphics card does not convert colours itself.");

        var graphics = GraphicsDevice.Shared!;
        const int frameWidth = 256;
        const int frameHeight = 256;
        const int band = 32;
        var crop = new Rectangle(64, 64, 128, 128);
        var source = ColourBands(frameWidth, frameHeight, band);

        using var texture = CreateBgra(graphics, source, frameWidth, frameHeight);
        var target = converter!.NextTarget(crop.Width, crop.Height);
        Assert.SkipWhen(target is null, "This graphics card holds no NV12 target.");
        Assert.True(converter.TryConvert(texture, crop, target!), "the conversion failed");

        var mine = ReadNv12(graphics, target!, crop.Width, crop.Height);
        var reference = new byte[Nv12Converter.Nv12Size(crop.Width, crop.Height)];
        Nv12Converter.BgraCropToNv12(source, frameWidth, crop, reference);

        // Compare the middle of each band, away from the rows where any chroma filtering differs.
        for (var y = 4; y < crop.Height; y += band)
        {
            for (var x = 8; x < crop.Width; x += 16)
            {
                var i = y * crop.Width + x;
                Assert.InRange(Math.Abs(mine[i] - reference[i]), 0, 4);
            }

            var chroma = crop.Width * crop.Height + y / 2 * crop.Width;
            for (var x = 8; x < crop.Width; x += 16)
            {
                var i = chroma + (x & ~1);
                Assert.InRange(Math.Abs(mine[i] - reference[i]), 0, 6);
                Assert.InRange(Math.Abs(mine[i + 1] - reference[i + 1]), 0, 6);
            }
        }

        // The crop really moved: the first luma row belongs to the band at y = 64, not the one at y = 0.
        var topOfFrame = new byte[Nv12Converter.Nv12Size(crop.Width, crop.Height)];
        Nv12Converter.BgraCropToNv12(source, frameWidth, new Rectangle(0, 0, crop.Width, crop.Height), topOfFrame);
        Assert.True(Math.Abs(mine[0] - topOfFrame[0]) > 10, "the whole frame was converted instead of the crop");
    }

    /// <summary>
    /// The whole path end to end: the frame is cropped and converted on the card, handed to the encoder as a
    /// texture, and comes back out of the decoder as the picture that went in.
    /// </summary>
    [Fact]
    public void AFrameEncodedStraightFromTheCardDecodesToTheSamePicture()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        Assert.SkipWhen(GraphicsDevice.Shared is null, "No Direct3D device on this machine.");
        using var converter = GpuNv12Converter.TryCreate(NullLogger.Instance);
        Assert.SkipWhen(converter is null, "This graphics card does not convert colours itself.");

        var graphics = GraphicsDevice.Shared!;
        const int width = 1280;
        const int height = 720;
        var crop = new Rectangle(0, 0, width, height);
        var source = ColourBands(width, height, 40);

        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 8000, NullLogger.Instance);
        Assert.NotNull(encoder);
        Assert.SkipUnless(encoder.UsesSharedDevice, "The encoder is not on the shared device, so the texture path does not apply.");
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance);
        Assert.NotNull(decoder);

        using var texture = CreateBgra(graphics, source, width, height);
        var output = Array.Empty<byte>();
        var produced = false;
        var decodedWidth = 0;
        var decodedHeight = 0;

        for (var frame = 0; frame < 60 && !produced; frame++)
        {
            var target = converter!.NextTarget(width, height);
            Assert.SkipWhen(target is null, "This graphics card holds no NV12 target.");
            Assert.True(converter.TryConvert(texture, crop, target!), "the conversion failed");

            foreach (var unit in encoder.EncodeTexture(target!, forceKeyframe: frame == 0))
            {
                if (decoder.DecodeInto(unit.Data, ref output, out decodedWidth, out decodedHeight))
                {
                    produced = true;
                }
            }

            Thread.Sleep(5);
        }

        Assert.True(produced, "no frame came back from the encoder fed with a texture");
        Assert.Equal((width, height), (decodedWidth, decodedHeight));
        Assert.InRange(MeanColorError(source, output), 0, 24);
    }
}
