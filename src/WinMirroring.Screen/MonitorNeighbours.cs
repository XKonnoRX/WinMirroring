using System.Drawing;

namespace WinMirroring.Screen;

/// <summary>
/// Which of a device's monitors is beside the one being looked at — for moving from one to the next with a swipe,
/// the way one would look from one to the next on the desk. By where they actually stand on that device's desktop,
/// not by their numbers: monitor 3 is as often to the left of monitor 1 as to its right.
/// </summary>
public static class MonitorNeighbours
{
    /// <summary>One monitor, by the name it is asked for by and where it stands.</summary>
    public readonly record struct Placed(string Key, Rectangle Bounds);

    /// <summary>
    /// The monitor next to <paramref name="current"/> on the side <paramref name="direction"/> points to (negative:
    /// left, positive: right), or null when there is none on that side — or when where they stand is not known.
    /// Of several on that side, the nearest; of several equally near, the one that shares most of its height.
    /// </summary>
    public static string? Beside(IReadOnlyList<Placed> monitors, string current, int direction)
    {
        if (direction == 0 || monitors.Count < 2)
        {
            return null;
        }

        var here = monitors.FirstOrDefault(m => string.Equals(m.Key, current, StringComparison.OrdinalIgnoreCase));
        if (here.Key is null || here.Bounds.Width <= 0 || here.Bounds.Height <= 0)
        {
            return null;
        }

        var centre = here.Bounds.Left + (here.Bounds.Width / 2.0);
        string? best = null;
        var bestGap = double.MaxValue;
        var bestOverlap = -1;
        foreach (var other in monitors)
        {
            if (other.Bounds.Width <= 0 || other.Bounds.Height <= 0 || string.Equals(other.Key, here.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var otherCentre = other.Bounds.Left + (other.Bounds.Width / 2.0);
            var gap = direction > 0 ? otherCentre - centre : centre - otherCentre;
            if (gap <= 0)
            {
                continue; // not on that side
            }

            var overlap = Math.Max(0, Math.Min(here.Bounds.Bottom, other.Bounds.Bottom) - Math.Max(here.Bounds.Top, other.Bounds.Top));
            if (gap < bestGap - 0.5 || (Math.Abs(gap - bestGap) <= 0.5 && overlap > bestOverlap))
            {
                best = other.Key;
                bestGap = gap;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a movement of fingers together is a swipe sideways: clearly more across than up or down. A
    /// movement up or down drifts sideways a little and must not be taken for one.
    /// </summary>
    public static bool IsSideways(double dx, double dy) => Math.Abs(dx) >= Math.Abs(dy) * 2 && Math.Abs(dx) > 0;

    /// <summary>
    /// How far a swipe has to travel to count, for a view this wide: far enough not to happen by accident, and no
    /// further than a thumb reaches on a small screen.
    /// </summary>
    public static double CommitsAfter(double viewWidth) => Math.Clamp(viewWidth * 0.12, 90, 260);

    /// <summary>What a finger that has taken a grip at the edge of the picture is doing with it.</summary>
    public enum GripDoes
    {
        /// <summary>Nothing yet: it has not moved far enough to say. Let go like this, it was a tap.</summary>
        Nothing,

        /// <summary>Pulling it inwards, and the monitor on that side with it.</summary>
        Pull,

        /// <summary>Moving it along the edge, out of the way of something.</summary>
        Move,
    }

    /// <summary>
    /// What a grip that has been moved by this much is being used for. Pulling is what a grip is for, so anything
    /// that is not clearly along the edge is a pull: a thumb pulling across drifts up or down as it goes, and
    /// must not find itself moving the grip instead.
    /// </summary>
    public static GripDoes WhatGripDoes(double dx, double dy, double decidesAfter)
    {
        if (Math.Sqrt((dx * dx) + (dy * dy)) <= decidesAfter)
        {
            return GripDoes.Nothing;
        }

        return Math.Abs(dy) >= Math.Abs(dx) * 2 ? GripDoes.Move : GripDoes.Pull;
    }

    /// <summary>
    /// How far the picture goes with a grip pulled by <paramref name="dx"/>: with it inwards, and not at all the
    /// other way — the left grip (<paramref name="side"/> negative) only to the right, the right one only to the left.
    /// </summary>
    public static double GripPull(int side, double dx) => side < 0 ? Math.Max(0, dx) : Math.Min(0, dx);
}
