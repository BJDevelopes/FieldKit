// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Fieldkit.Features.WindowTools;

/// <summary>Physical direction of a neighbouring display.</summary>
public enum DisplayDirection { Left, Right, Up, Down }

/// <summary>
/// Window placement maths (port of upstream WindowLayoutGeometry) in screen coordinates
/// with Y growing downwards. Every frame is the window's VISIBLE frame inside a monitor's
/// work area, in physical pixels.
/// </summary>
public static class LayoutGeometry
{
    /// <summary>Pixels a window may miss its target by and still count as there.</summary>
    public const double FrameTolerance = 8;

    public const double DefaultMarginPercent = 5;
    public const double MaxMarginPercent = 25;
    public static readonly int[] GapPresets = [0, 8, 16, 32, 64, 128];

    public static double SanitizedMargin(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0, MaxMarginPercent) : DefaultMarginPercent;

    /// <summary>
    /// What a key press really does: a second Top maximizes; with the size cycle on, a repeated
    /// Left/Right goes half → two thirds → one third.
    /// </summary>
    public static LayoutAction EffectiveAction(LayoutAction action, LayoutAction? previous, bool sideRepeatCyclesThirds)
    {
        if (action == LayoutAction.TopHalf && previous == LayoutAction.TopHalf) return LayoutAction.Maximize;
        if (sideRepeatCyclesThirds && SideCycleAction(action, previous) is { } next) return next;
        return action;
    }

    public static LayoutAction? SideCycleAction(LayoutAction action, LayoutAction? previous)
    {
        LayoutAction[] cycle = action switch
        {
            LayoutAction.LeftHalf => [LayoutAction.LeftHalf, LayoutAction.LeftTwoThirds, LayoutAction.LeftThird],
            LayoutAction.RightHalf => [LayoutAction.RightHalf, LayoutAction.RightTwoThirds, LayoutAction.RightThird],
            _ => [],
        };
        if (cycle.Length == 0 || previous is null) return null;
        var index = Array.IndexOf(cycle, previous.Value);
        return index < 0 ? null : cycle[(index + 1) % cycle.Length];
    }

    /// <summary>
    /// A half asked for twice pushes the window through that edge onto the neighbouring display,
    /// landing against the opposite edge. With the size cycle on, Left/Right never cross.
    /// </summary>
    public static (LayoutAction Action, DisplayDirection Direction)? DisplayCrossing(
        LayoutAction action, LayoutAction? previous, bool sideRepeatCyclesThirds)
    {
        if (previous != action) return null;
        if (sideRepeatCyclesThirds && action is LayoutAction.LeftHalf or LayoutAction.RightHalf) return null;
        return action switch
        {
            LayoutAction.LeftHalf => (LayoutAction.RightHalf, DisplayDirection.Left),
            LayoutAction.RightHalf => (LayoutAction.LeftHalf, DisplayDirection.Right),
            LayoutAction.TopHalf => (LayoutAction.BottomHalf, DisplayDirection.Up),
            LayoutAction.BottomHalf => (LayoutAction.TopHalf, DisplayDirection.Down),
            _ => null,
        };
    }

    /// <summary>The target frame for an action inside a work area, with gaps applied.</summary>
    public static Rect RectFor(LayoutAction action, Rect current, Rect workArea,
        double windowGap = 0, double screenGap = 0, double marginPercent = DefaultMarginPercent)
    {
        var frame = action is LayoutAction.MarginMaximize or LayoutAction.Center or LayoutAction.Restore
            or LayoutAction.PreviousDisplay or LayoutAction.NextDisplay or LayoutAction.FullScreen
            or LayoutAction.Grow or LayoutAction.Shrink
            ? workArea
            : ScreenGapFrame(workArea, screenGap);
        var rect = Ungapped(action, current, frame, marginPercent);
        return WindowGapped(rect, action, frame, windowGap);
    }

    /// <summary>The work area pulled in by the screen gap, keeping at least 80 px per axis.</summary>
    public static Rect ScreenGapFrame(Rect workArea, double screenGap)
    {
        if (screenGap <= 0) return workArea;
        var dx = Math.Min(screenGap, Math.Max(0, (workArea.Width - 80) / 2));
        var dy = Math.Min(screenGap, Math.Max(0, (workArea.Height - 80) / 2));
        return new Rect(workArea.X + dx, workArea.Y + dy, workArea.Width - 2 * dx, workArea.Height - 2 * dy);
    }

    private static Rect WindowGapped(Rect rect, LayoutAction action, Rect frame, double windowGap)
    {
        if (windowGap <= 0) return rect;
        if (action is LayoutAction.Maximize or LayoutAction.MarginMaximize or LayoutAction.FullScreen
            or LayoutAction.Center or LayoutAction.Restore or LayoutAction.PreviousDisplay or LayoutAction.NextDisplay
            or LayoutAction.Grow or LayoutAction.Shrink)
            return rect;
        var half = windowGap / 2;
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
        if (l - frame.Left > 1) l += half;
        if (frame.Right - r > 1) r -= half;
        if (t - frame.Top > 1) t += half;
        if (frame.Bottom - b > 1) b -= half;
        return Edges(l, t, Math.Max(l + 1, r), Math.Max(t + 1, b));
    }

    private static Rect Ungapped(LayoutAction action, Rect current, Rect f, double marginPercent)
    {
        double L = f.Left, T = f.Top, R = f.Right, B = f.Bottom;
        double w = f.Width, h = f.Height;
        double midX = L + w / 2, midY = T + h / 2;
        double third = w / 3, thirdH = h / 3, quarter = w / 4, quarterH = h / 4;
        return action switch
        {
            LayoutAction.LeftHalf => Edges(L, T, midX, B),
            LayoutAction.RightHalf => Edges(midX, T, R, B),
            LayoutAction.TopHalf => Edges(L, T, R, midY),
            LayoutAction.BottomHalf => Edges(L, midY, R, B),
            LayoutAction.CenterHalf => Edges(midX - w / 4, T, midX + w / 4, B),
            LayoutAction.LeftThird => Edges(L, T, L + third, B),
            LayoutAction.CenterThird => Edges(L + third, T, L + 2 * third, B),
            LayoutAction.RightThird => Edges(R - third, T, R, B),
            LayoutAction.LeftTwoThirds => Edges(L, T, L + 2 * third, B),
            LayoutAction.RightTwoThirds => Edges(R - 2 * third, T, R, B),
            LayoutAction.CenterTwoThirds => Edges(midX - third, T, midX + third, B),
            LayoutAction.TopThird => Edges(L, T, R, T + thirdH),
            LayoutAction.MiddleThird => Edges(L, T + thirdH, R, T + 2 * thirdH),
            LayoutAction.BottomThird => Edges(L, B - thirdH, R, B),
            LayoutAction.TopTwoThirds => Edges(L, T, R, T + 2 * thirdH),
            LayoutAction.BottomTwoThirds => Edges(L, B - 2 * thirdH, R, B),
            LayoutAction.TopQuarter => Edges(L, T, R, T + quarterH),
            LayoutAction.UpperMiddleQuarter => Edges(L, T + quarterH, R, T + 2 * quarterH),
            LayoutAction.LowerMiddleQuarter => Edges(L, T + 2 * quarterH, R, T + 3 * quarterH),
            LayoutAction.BottomQuarter => Edges(L, B - quarterH, R, B),
            LayoutAction.LeftQuarter => Edges(L, T, L + quarter, B),
            LayoutAction.LeftMiddleQuarter => Edges(L + quarter, T, L + 2 * quarter, B),
            LayoutAction.RightMiddleQuarter => Edges(L + 2 * quarter, T, L + 3 * quarter, B),
            LayoutAction.RightQuarter => Edges(R - quarter, T, R, B),
            LayoutAction.TopLeftSixth => Edges(L, T, L + third, midY),
            LayoutAction.TopCenterSixth => Edges(L + third, T, L + 2 * third, midY),
            LayoutAction.TopRightSixth => Edges(R - third, T, R, midY),
            LayoutAction.BottomLeftSixth => Edges(L, midY, L + third, B),
            LayoutAction.BottomCenterSixth => Edges(L + third, midY, L + 2 * third, B),
            LayoutAction.BottomRightSixth => Edges(R - third, midY, R, B),
            LayoutAction.TopLeft => Edges(L, T, midX, midY),
            LayoutAction.TopRight => Edges(midX, T, R, midY),
            LayoutAction.BottomLeft => Edges(L, midY, midX, B),
            LayoutAction.BottomRight => Edges(midX, midY, R, B),
            LayoutAction.Maximize => Edges(L, T, R, B),
            LayoutAction.MarginMaximize => MarginRect(f, marginPercent),
            LayoutAction.Center => CenterRect(current, f),
            LayoutAction.Grow => StepRect(current, f, +1),
            LayoutAction.Shrink => StepRect(current, f, -1),
            _ => Edges(current.Left, current.Top, current.Right, current.Bottom),
        };
    }

    private static Rect MarginRect(Rect f, double marginPercent)
    {
        var fraction = SanitizedMargin(marginPercent) / 100;
        var dx = f.Width * fraction;
        var dy = f.Height * fraction;
        return Edges(f.Left + dx, f.Top + dy, f.Right - dx, f.Bottom - dy);
    }

    /// <summary>Fraction of the work area added to (or taken from) each side per grow/shrink step.</summary>
    public const double ResizeStep = 0.05;

    /// <summary>Smallest size a shrink step leaves (pixels per axis).</summary>
    public const double MinStepSize = 200;

    /// <summary>
    /// One grow (+1) or shrink (-1) step around the window's centre, kept inside the work area: a window
    /// that hits an edge keeps growing towards the free side instead of leaving the screen.
    /// </summary>
    private static Rect StepRect(Rect current, Rect f, int direction)
    {
        var dx = f.Width * ResizeStep * direction;
        var dy = f.Height * ResizeStep * direction;
        var w = Math.Clamp(current.Width + 2 * dx, Math.Min(MinStepSize, f.Width), f.Width);
        var h = Math.Clamp(current.Height + 2 * dy, Math.Min(MinStepSize, f.Height), f.Height);
        var cx = current.Left + current.Width / 2;
        var cy = current.Top + current.Height / 2;
        var x = Math.Clamp(cx - w / 2, f.Left, f.Right - w);
        var y = Math.Clamp(cy - h / 2, f.Top, f.Bottom - h);
        return Edges(x, y, x + w, y + h);
    }

    private static Rect CenterRect(Rect current, Rect f)
    {
        var w = Math.Min(current.Width, f.Width);
        var h = Math.Min(current.Height, f.Height);
        var x = f.Left + (f.Width - w) / 2;
        var y = f.Top + (f.Height - h) / 2;
        return Edges(x, y, x + w, y + h);
    }

    /// <summary>A rectangle from rounded edges, so neighbouring placements share an exact edge.</summary>
    public static Rect Edges(double left, double top, double right, double bottom)
    {
        var l = Math.Round(left);
        var t = Math.Round(top);
        var r = Math.Round(right);
        var b = Math.Round(bottom);
        return new Rect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    }

    /// <summary>
    /// The display in one physical direction: sideways only displays starting further along
    /// that axis, vertically only displays fully beyond the edge. Nearest wins, then the closest
    /// centre on the other axis.
    /// </summary>
    public static int? NeighbourIndex(int currentIndex, IReadOnlyList<Rect> frames, DisplayDirection direction)
    {
        if (currentIndex < 0 || currentIndex >= frames.Count) return null;
        var current = frames[currentIndex];
        int? best = null;
        for (var i = 0; i < frames.Count; i++)
        {
            if (i == currentIndex) continue;
            var f = frames[i];
            var qualifies = direction switch
            {
                DisplayDirection.Left => f.Left < current.Left,
                DisplayDirection.Right => f.Left > current.Left,
                DisplayDirection.Up => f.Bottom <= current.Top,
                _ => f.Top >= current.Bottom,
            };
            if (!qualifies) continue;
            if (best is null || Closer(i, best.Value)) best = i;
        }
        return best;

        bool Closer(int a, int b)
        {
            var fa = frames[a];
            var fb = frames[b];
            double pa, pb, ca, cb;
            switch (direction)
            {
                case DisplayDirection.Left:
                case DisplayDirection.Right:
                    pa = fa.Left; pb = fb.Left;
                    ca = Math.Abs(Mid(fa).Y - Mid(current).Y); cb = Math.Abs(Mid(fb).Y - Mid(current).Y);
                    break;
                case DisplayDirection.Up:
                    // The facing (bottom) edge of a display above.
                    pa = fa.Bottom; pb = fb.Bottom;
                    ca = Math.Abs(Mid(fa).X - Mid(current).X); cb = Math.Abs(Mid(fb).X - Mid(current).X);
                    break;
                default:
                    pa = fa.Top; pb = fb.Top;
                    ca = Math.Abs(Mid(fa).X - Mid(current).X); cb = Math.Abs(Mid(fb).X - Mid(current).X);
                    break;
            }
            if (pa != pb)
            {
                return direction switch
                {
                    DisplayDirection.Right or DisplayDirection.Down => pa < pb,
                    _ => pa > pb,
                };
            }
            if (ca != cb) return ca < cb;
            return a < b;
        }
    }

    /// <summary>Next/previous display in left-to-right, then top-to-bottom order, wrapping.</summary>
    public static int? AdjacentDisplayIndex(int currentIndex, IReadOnlyList<Rect> frames, bool forward)
    {
        if (frames.Count < 2 || currentIndex < 0 || currentIndex >= frames.Count) return null;
        var ordered = Enumerable.Range(0, frames.Count)
            .OrderBy(i => frames[i].Left).ThenBy(i => frames[i].Top)
            .ThenBy(i => frames[i].Width).ThenBy(i => frames[i].Height).ThenBy(i => i)
            .ToList();
        var position = ordered.IndexOf(currentIndex);
        var destination = (position + (forward ? 1 : ordered.Count - 1)) % ordered.Count;
        return ordered[destination];
    }

    /// <summary>
    /// Where a window lands on another display: same size (only shrunk to fit), same insets from
    /// the left/top edge unless it was flush with the right/bottom. A window filling the source
    /// fills the destination.
    /// </summary>
    public static Rect RectForDisplay(Rect current, Rect source, Rect destination)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
            return Edges(current.Left, current.Top, current.Right, current.Bottom);
        if (current.Width >= source.Width - FrameTolerance && current.Height >= source.Height - FrameTolerance)
            return Edges(destination.Left, destination.Top, destination.Right, destination.Bottom);

        var width = Math.Min(destination.Width, Math.Max(1, current.Width));
        var height = Math.Min(destination.Height, Math.Max(1, current.Height));
        var x = UnscaledOrigin(source.Left, source.Right, current.Left, current.Right, destination.Left, destination.Right, width);
        var y = UnscaledOrigin(source.Top, source.Bottom, current.Top, current.Bottom, destination.Top, destination.Bottom, height);
        x = Math.Clamp(x, destination.Left, destination.Right - width);
        y = Math.Clamp(y, destination.Top, destination.Bottom - height);
        return Edges(x, y, x + width, y + height);
    }

    private static double UnscaledOrigin(double sourceMin, double sourceMax, double currentMin, double currentMax,
        double destinationMin, double destinationMax, double size)
    {
        var minInset = currentMin - sourceMin;
        var maxInset = sourceMax - currentMax;
        var slack = (sourceMax - sourceMin) - (currentMax - currentMin);
        if (slack <= FrameTolerance) return destinationMin + minInset;
        if (maxInset <= FrameTolerance && maxInset < minInset) return destinationMax - maxInset - size;
        return destinationMin + minInset;
    }

    /// <summary>Index of the frame that contains most of <paramref name="window"/> (or is nearest).</summary>
    public static int DisplayIndexFor(Rect window, IReadOnlyList<Rect> frames)
    {
        var best = -1;
        var bestArea = -1.0;
        for (var i = 0; i < frames.Count; i++)
        {
            var overlap = Rect.Intersect(window, frames[i]);
            var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }
        if (bestArea > 0 || frames.Count == 0) return best;
        var centre = Mid(window);
        return Enumerable.Range(0, frames.Count).MinBy(i => DistanceSquared(centre, frames[i]));
    }

    /// <summary>The frame that holds a point, or the nearest one.</summary>
    public static int DisplayIndexAt(Point point, IReadOnlyList<Rect> frames)
    {
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Contains(point)) return i;
        return frames.Count == 0 ? -1 : Enumerable.Range(0, frames.Count).MinBy(i => DistanceSquared(point, frames[i]));
    }

    internal static Point Mid(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    internal static double DistanceSquared(Point p, Rect r)
    {
        var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return dx * dx + dy * dy;
    }

    /// <summary>Whether two frames are within <paramref name="tolerance"/> on every edge.</summary>
    public static bool IsClose(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;
}

/// <summary>Recent frames per window (bounded) for Restore, kept for this app session only.</summary>
public sealed class LayoutHistory
{
    public const int PerWindowLimit = 20;
    private readonly Dictionary<IntPtr, List<Rect>> _frames = [];

    public void Record(IntPtr window, Rect frame)
    {
        if (!_frames.TryGetValue(window, out var list)) _frames[window] = list = [];
        list.Add(frame);
        if (list.Count > PerWindowLimit) list.RemoveRange(0, list.Count - PerWindowLimit);
    }

    /// <summary>The most recent recorded frame that differs from <paramref name="current"/>, removing what it skips.</summary>
    public Rect? PopPrevious(IntPtr window, Rect current)
    {
        if (!_frames.TryGetValue(window, out var list)) return null;
        Rect? result = null;
        while (list.Count > 0)
        {
            var previous = list[^1];
            list.RemoveAt(list.Count - 1);
            if (!LayoutGeometry.IsClose(previous, current, 1))
            {
                result = previous;
                break;
            }
        }
        if (list.Count == 0) _frames.Remove(window);
        return result;
    }

    public void RemoveStale(Func<IntPtr, bool> isAlive)
    {
        foreach (var key in _frames.Keys.Where(k => !isAlive(k)).ToList()) _frames.Remove(key);
    }

    public int Count(IntPtr window) => _frames.TryGetValue(window, out var list) ? list.Count : 0;
}
