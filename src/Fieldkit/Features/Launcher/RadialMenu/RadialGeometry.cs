// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>Shared wheel dimensions in DIPs (upstream <c>RadialMenuLayout</c>, points).</summary>
public static class RadialLayout
{
    /// <summary>The overlay is wider than the wheel so the soft shadow fades out instead of being clipped.</summary>
    public const double PanelSize = 400;
    public const double WheelDiameter = 300;
    public const double RingRadius = 112;
    public const double ChipSize = 52;
    public const double HubDiameter = 76;
    public const double DeadZoneRadius = 40;
    /// <summary>The pointer must travel this far before slices start highlighting.</summary>
    public const double MoveActivationDistance = 8;
    /// <summary>The whole opening sweep lands inside this, however many slices there are.</summary>
    public const double SweepDuration = 0.17;
}

/// <summary>Where a click on an open wheel lands.</summary>
public enum RadialPointerZone { Outside, Hub, Ring }

/// <summary>
/// Pure slice maths (upstream <c>RadialMenuGeometry</c>). Slice 0 sits at 12 o'clock and indices
/// grow clockwise; angles are radians measured clockwise from the top. <c>dyUp</c> grows toward
/// the top of the screen (screen y flipped).
/// </summary>
public static class RadialGeometry
{
    private const double TwoPi = 2 * Math.PI;

    /// <summary>Angle of (dx, dyUp) in [0, 2π), clockwise from 12 o'clock.</summary>
    public static double Angle(double dx, double dyUp)
    {
        var raw = Math.Atan2(dx, dyUp);
        return raw < 0 ? raw + TwoPi : raw;
    }

    /// <summary>The slice whose wedge contains <paramref name="angle"/> (each slice is centred on its index).</summary>
    public static int? IndexForAngle(double angle, int itemCount)
    {
        if (itemCount <= 0) return null;
        var step = TwoPi / itemCount;
        var shifted = (angle + step / 2) % TwoPi;
        if (shifted < 0) shifted += TwoPi;
        var index = (int)(shifted / step);
        return Math.Clamp(index, 0, itemCount - 1);
    }

    /// <summary>The slice under the pointer, null inside the dead zone around the hub.</summary>
    public static int? HighlightedIndex(double dx, double dyUp, double deadZoneRadius, int itemCount)
    {
        if (itemCount <= 0) return null;
        if (Math.Sqrt(dx * dx + dyUp * dyUp) < deadZoneRadius) return null;
        return IndexForAngle(Angle(dx, dyUp), itemCount);
    }

    /// <summary>Unit-circle position of a slice centre (dyUp toward the screen top).</summary>
    public static (double Dx, double DyUp) UnitPosition(int index, int itemCount)
    {
        if (itemCount <= 0) return (0, 1);
        var theta = TwoPi * index / itemCount;
        return (Math.Sin(theta), Math.Cos(theta));
    }

    /// <summary>A chip's centre in element coordinates (y down) around <paramref name="center"/>.</summary>
    public static Point ChipCenter(int index, int itemCount, double radius, Point center)
    {
        var (dx, dyUp) = UnitPosition(index, itemCount);
        return new Point(center.X + dx * radius, center.Y - dyUp * radius);
    }

    /// <summary>Centre angle and width (radians) of a slice's wedge.</summary>
    public static (double Center, double Width) SliceAngles(int index, int itemCount) =>
        itemCount <= 0 ? (0, TwoPi) : (TwoPi * index / itemCount, TwoPi / itemCount);

    /// <summary>Angles of the hairlines between slices (none for a single slice).</summary>
    public static IReadOnlyList<double> GuideAngles(int itemCount) =>
        itemCount <= 1 ? [] : Enumerable.Range(0, itemCount).Select(i => TwoPi * (i + 0.5) / itemCount).ToList();

    /// <summary>A point at <paramref name="radius"/> and clockwise-from-top <paramref name="angle"/>, y down.</summary>
    public static Point PointAt(double angle, double radius, Point center) =>
        new(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);

    /// <summary>
    /// The angle to animate to so the highlight takes the short way round (last slice to first
    /// crosses the top instead of unwinding the whole wheel). Results stay unwrapped.
    /// </summary>
    public static double ShortestTarget(double current, double target)
    {
        var delta = Math.IEEERemainder(target - current, TwoPi);
        return current + delta;
    }

    /// <summary>When a chip's turn comes in the opening sweep, clockwise from twelve o'clock.</summary>
    public static double OpenDelay(int index, int count) =>
        count <= 1 ? 0 : RadialLayout.SweepDuration * index / (count - 1);

    /// <summary>Where a click lands: past the wheel's edge dismisses, the hub steps back, the ring selects.</summary>
    public static RadialPointerZone Zone(double dx, double dyUp, double wheelDiameter = RadialLayout.WheelDiameter,
        double deadZoneRadius = RadialLayout.DeadZoneRadius)
    {
        var distance = Math.Sqrt(dx * dx + dyUp * dyUp);
        if (distance > wheelDiameter / 2) return RadialPointerZone.Outside;
        return distance < deadZoneRadius ? RadialPointerZone.Hub : RadialPointerZone.Ring;
    }

    /// <summary>Whether the pointer travelled far enough from where the wheel opened to own the highlight.</summary>
    public static bool PointerArmed(double dx, double dy, double threshold = RadialLayout.MoveActivationDistance) =>
        Math.Sqrt(dx * dx + dy * dy) >= threshold;

    /// <summary>
    /// Keeps the overlay (a square of <paramref name="panelSize"/>) inside <paramref name="area"/>,
    /// so a wheel summoned near an edge or the taskbar stays whole. Areas smaller than the panel
    /// leave the wanted centre alone, like upstream.
    /// </summary>
    public static Point ClampCenter(Point wanted, Rect area, double panelSize)
    {
        var half = panelSize / 2;
        if (area.Width <= panelSize || area.Height <= panelSize) return wanted;
        return new Point(
            Math.Clamp(wanted.X, area.Left + half, area.Right - half),
            Math.Clamp(wanted.Y, area.Top + half, area.Bottom - half));
    }

    /// <summary>Top-left of a card of <paramref name="size"/> centred on <paramref name="anchor"/>, kept
    /// <paramref name="margin"/> inside <paramref name="area"/> (the Now Playing card).</summary>
    public static Point PlaceCard(Point anchor, Size size, Rect area, double margin = 12)
    {
        var x = Math.Min(Math.Max(anchor.X - size.Width / 2, area.Left + margin), area.Right - size.Width - margin);
        var y = Math.Min(Math.Max(anchor.Y - size.Height / 2, area.Top + margin), area.Bottom - size.Height - margin);
        return new Point(x, y);
    }

    /// <summary>The slice a dragged chip would land on in the settings canvas (by pointer angle).</summary>
    public static int? DropSlot(Point pointer, Point center, int itemCount) =>
        IndexForAngle(Angle(pointer.X - center.X, center.Y - pointer.Y), itemCount);
}
