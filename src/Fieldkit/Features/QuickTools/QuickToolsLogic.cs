// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.QuickTools;

/// <summary>Pure rules for Quick toggles and the Quick panel (unit-tested).</summary>
public static class QuickToolsLogic
{
    /// <summary>Bus types that mean "external" for a fixed disk: 1394, USB, SD, MMC.</summary>
    private static readonly HashSet<int> ExternalBuses = [4, 7, 12, 13];

    /// <summary>
    /// Upstream shouldOfferEject: not the system volume, either removable or on an external bus,
    /// and not excluded by name (label, letter "E:", "E:\" or "E", case-insensitive).
    /// </summary>
    public static bool ShouldOfferEject(bool removable, bool fixedDisk, int busType, bool isSystem,
        string root, string label, IEnumerable<string> excluded)
    {
        if (isSystem) return false;
        if (!removable && !(fixedDisk && ExternalBuses.Contains(busType))) return false;
        var letter = root.TrimEnd('\\', '/');
        foreach (var raw in excluded)
        {
            var e = raw.Trim().TrimEnd('\\', '/');
            if (e.Length == 0) continue;
            if (string.Equals(e, label, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(e, letter, StringComparison.OrdinalIgnoreCase)) return false;
            if (e.Length == 1 && letter.Length >= 1 && char.ToUpperInvariant(e[0]) == char.ToUpperInvariant(letter[0])) return false;
        }
        return true;
    }

    /// <summary>
    /// Upstream order sanitizing: keeps known ids once each in the stored order, then appends
    /// every missing known id in default order. Empty means the default order.
    /// </summary>
    public static List<string> SanitizedOrder(string? stored, IReadOnlyList<string> defaults)
    {
        var known = new HashSet<string>(defaults, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (known.Contains(id) && seen.Add(id)) result.Add(id);
        }
        foreach (var id in defaults)
        {
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>Moves <paramref name="id"/> to just before <paramref name="beforeId"/> (or to the end).</summary>
    public static List<string> Moved(IReadOnlyList<string> order, string id, string? beforeId)
    {
        var list = order.Where(x => x != id).ToList();
        var index = beforeId is null ? -1 : list.IndexOf(beforeId);
        if (index < 0) list.Add(id);
        else list.Insert(index, id);
        return list;
    }

    public static string Joined(IEnumerable<string> ids) => string.Join(",", ids);

    public static HashSet<string> ParseSet(string? stored) =>
        new((stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    public static string JoinedSet(IEnumerable<string> ids) => string.Join(",", ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>
    /// Arrow-key move in a grid of <paramref name="count"/> tiles and <paramref name="columns"/> columns.
    /// No wrapping: left/right stop at row edges, up/down stop at the ends.
    /// </summary>
    public static int GridMove(int index, int count, int columns, int dx, int dy)
    {
        if (count <= 0) return 0;
        index = Math.Clamp(index, 0, count - 1);
        if (dx != 0)
        {
            var column = index % columns;
            var target = column + dx;
            if (target < 0 || target >= columns) return index;
            var next = index + dx;
            return next >= 0 && next < count ? next : index;
        }
        if (dy != 0)
        {
            var next = index + dy * columns;
            return next >= 0 && next < count ? next : index;
        }
        return index;
    }

    /// <summary>Digit key (1–9) to a tile index, or -1.</summary>
    public static int DigitIndex(int digit, int count) => digit is >= 1 and <= 9 && digit <= count ? digit - 1 : -1;
}
