using Vortice.Direct3D11;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// What every video processor in this program is told before it is used, on both sides of a stream: the sending
/// side's <see cref="GpuNv12Converter"/> and the viewer's <see cref="GpuFrameReader"/>. One place, because the two
/// have to agree — a picture that is improved on the way out and not on the way back is a picture that changes
/// depending on which machine is doing the work.
/// </summary>
internal static class VideoProcessorRules
{
    /// <summary>
    /// Tells the card to do what it was asked and nothing else.
    ///
    /// A video processor is the part of a driver built for playing films, and by default it is allowed to improve
    /// what goes through it: Windows documents automatic image processing as enabled unless an application turns it
    /// off, and what "improving" means is the driver's own business — contrast, saturation, sharpening, skin tones.
    /// For a film that is a feature somebody chose in a control panel. For a desktop being mirrored it is a picture
    /// that does not match the screen it came from, and nothing in this program would explain why.
    ///
    /// The named filters are turned off as well. They are off by default, so this changes nothing on a driver that
    /// follows the rules; it is here for the ones that arrive with their own idea of a good picture.
    /// </summary>
    public static void NothingOfItsOwn(ID3D11VideoContext videoContext, ID3D11VideoProcessor processor)
    {
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        foreach (var filter in new[]
        {
            VideoProcessorFilter.Brightness,
            VideoProcessorFilter.Contrast,
            VideoProcessorFilter.Hue,
            VideoProcessorFilter.Saturation,
            VideoProcessorFilter.NoiseReduction,
            VideoProcessorFilter.EdgeEnhancement,
        })
        {
            videoContext.VideoProcessorSetStreamFilter(processor, 0, filter, false, 0);
        }
    }
}
