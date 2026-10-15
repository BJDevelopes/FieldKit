// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Fieldkit.Features.WindowTools;

/// <summary>The eight drop areas around a screen. Ids are persisted (upstream raw values).</summary>
public enum EdgeZone { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

/// <summary>One screen for edge snapping: full bounds and work area, physical pixels.</summary>
public readonly record struct SnapScreen(Rect Bounds, Rect WorkArea);

/// <summary>Where a drag would place the window.</summary>
public readonly record struct SnapTarget(EdgeZone Zone, LayoutAction Action, Rect Frame, Rect WorkArea);

/// <summary>Edge-snap zone detection and the per-area placement layout (port of upstream WindowEdgeSnap*).</summary>
public static class EdgeSnap
{
    /// <summary>How close to an edge (px) the pointer must be.</summary>
    public const double ActivationDistance = 12;

    /// <summary>Pixels per second across a seam between displays above which the pointer is just passing.</summary>
    public const double CrossingSpeed = 400;

    public const int MaximumParts = 4;

    public static string Id(this EdgeZone zone)
    {
        var name = zone.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static EdgeZone? ZoneFromId(string? id)
    {
        foreach (var z in Enum.GetValues<EdgeZone>())
            if (string.Equals(z.Id(), id?.Trim(), StringComparison.Ordinal)) return z;
        return null;
    }

    public static string Title(this EdgeZone zone) => zone switch
    {
        EdgeZone.TopLeft => "Top left corner",
        EdgeZone.Top => "Top edge",
        EdgeZone.TopRight => "Top right corner",
        EdgeZone.Left => "Left edge",
        EdgeZone.Right => "Right edge",
        EdgeZone.BottomLeft => "Bottom left corner",
        EdgeZone.Bottom => "Bottom edge",
        _ => "Bottom right corner",
    };

    public static LayoutAction DefaultAction(this EdgeZone zone) => zone switch
    {
        EdgeZone.TopLeft => LayoutAction.TopLeft,
        EdgeZone.Top => LayoutAction.Maximize,
        EdgeZone.TopRight => LayoutAction.TopRight,
        EdgeZone.Left => LayoutAction.LeftHalf,
        EdgeZone.Right => LayoutAction.RightHalf,
        EdgeZone.BottomLeft => LayoutAction.BottomLeft,
        EdgeZone.Bottom => LayoutAction.BottomHalf,
        _ => LayoutAction.BottomRight,
    };

    public static bool IsHorizontalEdge(this EdgeZone zone) => zone is EdgeZone.Top or EdgeZone.Bottom;
    public static bool IsVerticalEdge(this EdgeZone zone) => zone is EdgeZone.Left or EdgeZone.Right;
    public static int MaximumPartsFor(EdgeZone zone) => zone.IsHorizontalEdge() || zone.IsVerticalEdge() ? MaximumParts : 1;

    public static HashSet<EdgeZone> DisabledZones(string? stored) =>
        (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ZoneFromId).OfType<EdgeZone>().ToHashSet();

    public static string DisabledZonesStorage(IEnumerable<EdgeZone> zones)
    {
        var set = zones.ToHashSet();
        return string.Join(",", Enum.GetValues<EdgeZone>().Where(set.Contains).Select(z => z.Id()));
    }

    /// <summary>
    /// The drop target under <paramref name="point"/>. Edges with another display beyond them
    /// (seams) only count while the pointer moves slowly across them; walls always count.
    /// </summary>
    public static SnapTarget? Target(Point point, IReadOnlyList<SnapScreen> screens, Vector velocity,
        ISet<EdgeZone> enabledZones, EdgeSnapLayout layout, Func<LayoutAction, Rect, Rect> frameFor,
        double distance = ActivationDistance)
    {
        var settledAcross = Math.Abs(velocity.X) <= CrossingSpeed;
        var settledUpDown = Math.Abs(velocity.Y) <= CrossingSpeed;
        var ordered = screens.Select((s, i) => (Screen: s, Index: i))
            .OrderBy(x => LayoutGeometry.DistanceSquared(point, x.Screen.Bounds))
            .ThenBy(x => Holds(x.Screen.Bounds, point) ? 0 : 1)
            .ThenBy(x => x.Index)
            .ToList();
        foreach (var (screen, _) in ordered)
        {
            var f = screen.Bounds;
            if (f.Width <= 0 || f.Height <= 0 || screen.WorkArea.Width <= 0 || screen.WorkArea.Height <= 0) continue;
            if (point.X < f.Left - distance || point.X > f.Right + distance ||
                point.Y < f.Top - distance || point.Y > f.Bottom + distance) continue;

            bool Open(Point probe) => screens.Any(s => s.Bounds != f && Holds(s.Bounds, probe));
            var nearLeft = Math.Abs(point.X - f.Left) <= distance && (settledAcross || !Open(new Point(f.Left - 0.5, point.Y)));
            var nearRight = Math.Abs(point.X - f.Right) <= distance && (settledAcross || !Open(new Point(f.Right + 0.5, point.Y)));
            var nearTop = Math.Abs(point.Y - f.Top) <= distance && (settledUpDown || !Open(new Point(point.X, f.Top - 0.5)));
            var nearBottom = Math.Abs(point.Y - f.Bottom) <= distance && (settledUpDown || !Open(new Point(point.X, f.Bottom + 0.5)));
            if (!(nearLeft || nearRight || nearTop || nearBottom)) continue;

            var hCorner = Math.Min(Math.Max(f.Width * 0.18, 96), 180);
            var vCorner = Math.Min(Math.Max(f.Height * 0.18, 80), 160);
            EdgeZone zone;
            if (nearTop) zone = point.X <= f.Left + hCorner ? EdgeZone.TopLeft : point.X >= f.Right - hCorner ? EdgeZone.TopRight : EdgeZone.Top;
            else if (nearBottom) zone = point.X <= f.Left + hCorner ? EdgeZone.BottomLeft : point.X >= f.Right - hCorner ? EdgeZone.BottomRight : EdgeZone.Bottom;
            else if (nearLeft) zone = point.Y <= f.Top + vCorner ? EdgeZone.TopLeft : point.Y >= f.Bottom - vCorner ? EdgeZone.BottomLeft : EdgeZone.Left;
            else zone = point.Y <= f.Top + vCorner ? EdgeZone.TopRight : point.Y >= f.Bottom - vCorner ? EdgeZone.BottomRight : EdgeZone.Right;
            if (!enabledZones.Contains(zone)) return null;

            double along = 0;
            if (zone.IsHorizontalEdge()) along = (point.X - f.Left - hCorner) / Math.Max(f.Width - 2 * hCorner, 1);
            else if (zone.IsVerticalEdge()) along = (point.Y - f.Top - vCorner) / Math.Max(f.Height - 2 * vCorner, 1);
            var action = layout.ActionAt(zone, along);
            return new SnapTarget(zone, action, frameFor(action, screen.WorkArea), screen.WorkArea);
        }
        return null;
    }

    /// <summary>
    /// A drag is a resize when the size changed while one horizontal and one vertical edge
    /// stayed put (native resizing anchors the opposite edges); a tiled window restoring its
    /// size as it starts moving is still a move.
    /// </summary>
    public static bool IsResize(Rect initial, Rect current, double tolerance = 2)
    {
        var sizeChanged = Math.Abs(current.Width - initial.Width) > tolerance || Math.Abs(current.Height - initial.Height) > tolerance;
        if (!sizeChanged) return false;
        var horizontalAnchored = Math.Abs(current.Left - initial.Left) <= tolerance || Math.Abs(current.Right - initial.Right) <= tolerance;
        var verticalAnchored = Math.Abs(current.Top - initial.Top) <= tolerance || Math.Abs(current.Bottom - initial.Bottom) <= tolerance;
        return horizontalAnchored && verticalAnchored;
    }

    private static bool Holds(Rect r, Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}

/// <summary>
/// What each drop area does: a corner holds one placement, an edge one per area it is split
/// into (left→right, top→bottom). Stored as "zone=action+action,..." for changed zones only.
/// </summary>
public sealed class EdgeSnapLayout
{
    private readonly Dictionary<EdgeZone, List<LayoutAction>> _changed = [];

    public static EdgeSnapLayout Parse(string? stored)
    {
        var layout = new EdgeSnapLayout();
        foreach (var entry in (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2 || EdgeSnap.ZoneFromId(pair[0]) is not { } zone) continue;
            var names = pair[1].Split('+');
            var actions = names.Select(LayoutActions.FromId).ToList();
            if (actions.Any(a => a is null)) continue;
            var list = actions.Select(a => a!.Value).ToList();
            if (!Accepts(list, zone)) continue;
            layout.Set(zone, list);
        }
        return layout;
    }

    public string Storage =>
        string.Join(",", Enum.GetValues<EdgeZone>()
            .Where(_changed.ContainsKey)
            .Select(z => z.Id() + "=" + string.Join("+", _changed[z].Select(a => a.Id()))));

    public IReadOnlyList<LayoutAction> Actions(EdgeZone zone) =>
        _changed.TryGetValue(zone, out var list) ? list : [zone.DefaultAction()];

    public LayoutAction ActionAt(EdgeZone zone, double along)
    {
        var actions = Actions(zone);
        var position = double.IsFinite(along) ? Math.Clamp(along, 0, 1) : 0;
        var index = (int)Math.Floor(position * actions.Count);
        return actions[Math.Min(index, actions.Count - 1)];
    }

    public void SetAction(EdgeZone zone, int part, LayoutAction action)
    {
        var actions = Actions(zone).ToList();
        if (part < 0 || part >= actions.Count) return;
        actions[part] = action;
        if (Accepts(actions, zone)) Set(zone, actions);
    }

    /// <summary>Splits an edge into 1–4 equal areas, each starting on the matching column/row.</summary>
    public void SetPartCount(EdgeZone zone, int count)
    {
        if (count == Actions(zone).Count) return;
        List<LayoutAction>? parts = (count, zone.IsHorizontalEdge()) switch
        {
            (1, _) => [zone.DefaultAction()],
            (2, true) => [LayoutAction.LeftHalf, LayoutAction.RightHalf],
            (3, true) => [LayoutAction.LeftThird, LayoutAction.CenterThird, LayoutAction.RightThird],
            (4, true) => [LayoutAction.LeftQuarter, LayoutAction.LeftMiddleQuarter, LayoutAction.RightMiddleQuarter, LayoutAction.RightQuarter],
            (2, false) => [LayoutAction.TopHalf, LayoutAction.BottomHalf],
            (3, false) => [LayoutAction.TopThird, LayoutAction.MiddleThird, LayoutAction.BottomThird],
            (4, false) => [LayoutAction.TopQuarter, LayoutAction.UpperMiddleQuarter, LayoutAction.LowerMiddleQuarter, LayoutAction.BottomQuarter],
            _ => null,
        };
        if (parts is null || parts.Count > EdgeSnap.MaximumPartsFor(zone)) return;
        Set(zone, parts);
    }

    private void Set(EdgeZone zone, List<LayoutAction> actions)
    {
        if (actions.Count == 1 && actions[0] == zone.DefaultAction()) _changed.Remove(zone);
        else _changed[zone] = actions;
    }

    private static bool Accepts(List<LayoutAction> actions, EdgeZone zone) =>
        actions.Count > 0 && actions.Count <= EdgeSnap.MaximumPartsFor(zone) && actions.All(a => a.IsPlacement());
}
