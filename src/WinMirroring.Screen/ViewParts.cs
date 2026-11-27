using System.Drawing;

namespace WinMirroring.Screen;

/// <summary>
/// Which part of a picture to ask a source for when only part of it is in view — the arithmetic of zooming in,
/// apart from any window, so it can be read and tested on its own. Everything is in shares of the whole picture:
/// 0 to 1 across and down.
/// </summary>
public static class ViewParts
{
    /// <summary>The whole picture.</summary>
    public static readonly RectangleF Whole = new(0, 0, 1, 1);

    /// <summary>
    /// How much more than what is in view is asked for, on each side, as a share of what is in view. Enough that
    /// a small move stays inside what has already arrived; no more, because every pixel of it is encoded, sent and
    /// decoded without being looked at.
    /// </summary>
    public const double Margin = 0.15;

    /// <summary>A part that would be this much of the picture anyway is not worth asking for: the whole is.</summary>
    public const double WholeAbove = 0.85;

    /// <summary>The part worth asking for with <paramref name="visible"/> in view: it and a margin, or the whole.</summary>
    public static RectangleF For(RectangleF visible)
    {
        var left = Math.Max(0, visible.Left - (visible.Width * Margin));
        var top = Math.Max(0, visible.Top - (visible.Height * Margin));
        var right = Math.Min(1, visible.Right + (visible.Width * Margin));
        var bottom = Math.Min(1, visible.Bottom + (visible.Height * Margin));
        var width = right - left;
        var height = bottom - top;
        return width <= 0 || height <= 0 || width * height > WholeAbove
            ? Whole
            : new RectangleF((float)left, (float)top, (float)width, (float)height);
    }

    /// <summary>
    /// Whether the part already asked for still does for what is in view: all of it inside, at the zoom it was
    /// asked at, and with some margin left on every side that has any picture beyond it. A new part costs a whole
    /// picture from the encoder, so it is not asked for on every small move — only once the view is about to run
    /// off what it has.
    /// </summary>
    public static bool StillServes(RectangleF asked, RectangleF visible, double askedZoom, double zoom)
    {
        const double Slack = 0.002;
        if (askedZoom <= 0
            || Math.Abs((zoom / askedZoom) - 1) > 0.02
            || visible.Left < asked.Left - Slack || visible.Top < asked.Top - Slack
            || visible.Right > asked.Right + Slack || visible.Bottom > asked.Bottom + Slack)
        {
            return false;
        }

        var spareX = visible.Width * Margin / 3;
        var spareY = visible.Height * Margin / 3;
        return (asked.Left <= Slack || visible.Left - asked.Left >= spareX)
            && (asked.Top <= Slack || visible.Top - asked.Top >= spareY)
            && (asked.Right >= 1 - Slack || asked.Right - visible.Right >= spareX)
            && (asked.Bottom >= 1 - Slack || asked.Bottom - visible.Bottom >= spareY);
    }

    /// <summary>A part of a part: <paramref name="part"/> of the picture that is itself <paramref name="of"/> the source's.</summary>
    public static RectangleF Within(RectangleF of, RectangleF part) => new(
        of.X + (part.X * of.Width),
        of.Y + (part.Y * of.Height),
        part.Width * of.Width,
        part.Height * of.Height);
}
