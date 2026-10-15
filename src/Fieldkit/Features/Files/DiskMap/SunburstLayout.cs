// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Files.DiskMap;

/// <summary>One wedge: <paramref name="Ring"/> 0 is the innermost ring around the centre; angles in degrees, 0 at the top, clockwise.</summary>
public readonly record struct SunburstArc(DiskNode Node, int Ring, double Start, double Sweep, int Branch);

/// <summary>Pure sunburst geometry (unit tested).</summary>
public static class SunburstLayout
{
    public const int DefaultRings = 4;
    /// <summary>Wedges narrower than this are left out (too thin to see or click).</summary>
    public const double MinSweep = 0.6;

    /// <summary>
    /// The wedges around <paramref name="center"/>: each child takes an angle in proportion to its size,
    /// largest first, and its own children sit in the next ring inside that angle. <c>Branch</c> is the index
    /// of the first-ring ancestor, so a colour can follow a whole branch outwards.
    /// </summary>
    public static List<SunburstArc> Layout(DiskNode center, int rings = DefaultRings, double minSweep = MinSweep)
    {
        var arcs = new List<SunburstArc>();
        if (center.Size <= 0) return arcs;
        var branch = 0;
        var start = 0.0;
        foreach (var child in center.Largest())
        {
            var sweep = 360.0 * child.Size / center.Size;
            if (sweep < minSweep) break; // sorted: everything after is smaller still
            Add(arcs, child, 0, start, sweep, branch++, rings, minSweep);
            start += sweep;
        }
        return arcs;
    }

    private static void Add(List<SunburstArc> arcs, DiskNode node, int ring, double start, double sweep, int branch, int rings, double minSweep)
    {
        arcs.Add(new SunburstArc(node, ring, start, sweep, branch));
        if (ring + 1 >= rings || node.Size <= 0) return;
        var childStart = start;
        foreach (var child in node.Largest())
        {
            var childSweep = sweep * child.Size / node.Size;
            if (childSweep < minSweep) break;
            Add(arcs, child, ring + 1, childStart, childSweep, branch, rings, minSweep);
            childStart += childSweep;
        }
    }

    /// <summary>The point at <paramref name="degrees"/> (0 = top, clockwise) on a circle.</summary>
    public static (double X, double Y) PointAt(double cx, double cy, double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180;
        return (cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
    }

    /// <summary>"12%" style share of the parent, or "&lt;1%".</summary>
    public static string Share(long size, long total)
    {
        if (total <= 0) return string.Empty;
        var percent = 100.0 * size / total;
        return percent < 1 ? "<1%" : $"{percent:0}%";
    }
}
