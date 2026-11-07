using System.Drawing;

namespace WinMirroring.Input.Layout;

public enum Edge : byte
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

/// <summary>Geometry shared by both sides of a crossing: where on an edge the cursor is, and where it re-appears.</summary>
public static class EdgeMath
{
    public static Edge Opposite(Edge edge) => edge switch
    {
        Edge.Left => Edge.Right,
        Edge.Right => Edge.Left,
        Edge.Top => Edge.Bottom,
        Edge.Bottom => Edge.Top,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>Position along <paramref name="edge"/> as 0..1 (top-to-bottom for vertical edges, left-to-right for horizontal ones).</summary>
    public static float RelativeAlong(Rectangle screen, Edge edge, Point point)
    {
        float value = edge is Edge.Left or Edge.Right
            ? (point.Y - screen.Top) / (float)Math.Max(1, screen.Height - 1)
            : (point.X - screen.Left) / (float)Math.Max(1, screen.Width - 1);
        return Math.Clamp(value, 0f, 1f);
    }

    /// <summary>The point <paramref name="inset"/> pixels inside <paramref name="edge"/> at relative position <paramref name="relative"/>.</summary>
    public static Point PointOn(Rectangle screen, Edge edge, float relative, int inset)
    {
        relative = Math.Clamp(relative, 0f, 1f);
        var along = edge is Edge.Left or Edge.Right
            ? screen.Top + (int)MathF.Round(relative * Math.Max(0, screen.Height - 1))
            : screen.Left + (int)MathF.Round(relative * Math.Max(0, screen.Width - 1));

        return edge switch
        {
            Edge.Left => new Point(screen.Left + inset, along),
            Edge.Right => new Point(screen.Right - 1 - inset, along),
            Edge.Top => new Point(along, screen.Top + inset),
            Edge.Bottom => new Point(along, screen.Bottom - 1 - inset),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }
}
