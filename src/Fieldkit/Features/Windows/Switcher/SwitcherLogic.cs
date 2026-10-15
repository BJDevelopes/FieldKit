// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Windows;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>One entry of the switcher: a window, or an app with no window (windowless entry).</summary>
public sealed record SwitcherItem(
    IntPtr Handle,
    string Title,
    uint ProcessId,
    string AppPath,
    string AppName,
    bool IsMinimized,
    bool OnOtherDesktop,
    Rect Frame)
{
    public bool IsAppEntry => Handle == IntPtr.Zero;
    public string AppKey => AppNames.Key(AppPath);

    public static SwitcherItem From(AppWindow w, Rect frame) =>
        new(w.Handle, w.Title, w.ProcessId, w.ProcessPath, w.AppName, w.IsMinimized, w.OnOtherDesktop, frame);

    public static SwitcherItem AppEntry(string path, string name, uint pid) =>
        new(IntPtr.Zero, name, pid, path, name, false, false, Rect.Empty);
}

/// <summary>Where minimized windows go in the list.</summary>
public enum MinimizedPlacement { Normal, End, Hidden }

/// <summary>Per-app switcher rule (upstream SwitcherAppRule).</summary>
public enum SwitcherAppRule { ShowWithoutWindows, WindowsOnly, Hidden }

/// <summary>Which running apps with no window get an entry.</summary>
public enum WindowlessApps { Off, Explorer, All }

/// <summary>What a letter typed with the switcher open does (when no search is under way).</summary>
public enum SwitcherLetterAction { CloseWindow, QuitApp, MinimizeWindow, PinSearch }

/// <summary>A group of items of the same app (large-icons mode).</summary>
public sealed record SwitcherAppGroup(string AppKey, uint ProcessId, string AppName, IReadOnlyList<int> ItemIndices);

/// <summary>
/// The switcher's decisions as plain functions: ordering, filtering, rules, search and
/// selection movement. No windows, hooks or settings in here.
/// </summary>
public static class SwitcherLogic
{
    public const int DefaultAppearanceDelay = 100;
    public const int MinColumns = 2;
    public const int MaxColumns = 8;

    public static int SanitizedDelay(int ms) => Math.Clamp(ms, 0, 500);

    /// <summary>0 or out of range means Auto (null).</summary>
    public static int? SanitizedMaxColumns(int value) => value is >= MinColumns and <= MaxColumns ? value : null;

    public static double PreviewScale(string? size) => size switch
    {
        "small" => 0.75,
        "large" => 1.4,
        "xlarge" => 1.8,
        _ => 1.0,
    };

    public static MinimizedPlacement ParsePlacement(string? s) => s switch
    {
        "end" => MinimizedPlacement.End,
        "hidden" => MinimizedPlacement.Hidden,
        _ => MinimizedPlacement.Normal,
    };

    public static WindowlessApps ParseWindowless(string? s) => s switch
    {
        "off" => WindowlessApps.Off,
        "all" => WindowlessApps.All,
        _ => WindowlessApps.Explorer,
    };

    public static SwitcherAppRule? ParseRule(string? s) => s switch
    {
        "showWithoutWindows" => SwitcherAppRule.ShowWithoutWindows,
        "windowsOnly" => SwitcherAppRule.WindowsOnly,
        "hidden" => SwitcherAppRule.Hidden,
        _ => null,
    };

    public static string RuleId(SwitcherAppRule rule) => rule switch
    {
        SwitcherAppRule.ShowWithoutWindows => "showWithoutWindows",
        SwitcherAppRule.Hidden => "hidden",
        _ => "windowsOnly",
    };

    /// <summary>
    /// Orders windows most-recently-used first (unknown windows keep their Z-order after the
    /// known ones) and applies the minimized placement.
    /// </summary>
    public static List<SwitcherItem> Order(IReadOnlyList<SwitcherItem> zOrder, IReadOnlyList<IntPtr> mru, MinimizedPlacement placement)
    {
        var rank = new Dictionary<IntPtr, int>();
        for (var i = 0; i < mru.Count; i++) rank.TryAdd(mru[i], i);
        var ordered = zOrder
            .Select((item, z) => (item, z))
            .OrderBy(x => rank.TryGetValue(x.item.Handle, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.z)
            .Select(x => x.item)
            .ToList();
        return placement switch
        {
            MinimizedPlacement.Hidden => ordered.Where(i => !i.IsMinimized).ToList(),
            MinimizedPlacement.End => ordered.Where(i => !i.IsMinimized).Concat(ordered.Where(i => i.IsMinimized)).ToList(),
            _ => ordered,
        };
    }

    /// <summary>Drops windows of apps whose rule is Hidden.</summary>
    public static List<SwitcherItem> ApplyRules(IEnumerable<SwitcherItem> items, IReadOnlyDictionary<string, SwitcherAppRule> rules) =>
        items.Where(i => !(rules.TryGetValue(i.AppKey, out var r) && r == SwitcherAppRule.Hidden)).ToList();

    /// <summary>
    /// Which running apps without a window earn an entry: the mode decides by default, a rule
    /// overrides per app (Show without windows / Windows only / Never show).
    /// </summary>
    public static bool ShowsWindowlessApp(string appKey, WindowlessApps mode, IReadOnlyDictionary<string, SwitcherAppRule> rules)
    {
        if (rules.TryGetValue(appKey, out var rule))
            return rule == SwitcherAppRule.ShowWithoutWindows;
        return mode switch
        {
            WindowlessApps.All => true,
            WindowlessApps.Explorer => appKey == "explorer.exe",
            _ => false,
        };
    }

    /// <summary>One entry per app, at the position of its most recent window.</summary>
    public static List<SwitcherItem> MergeByApp(IEnumerable<SwitcherItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SwitcherItem>();
        foreach (var item in items)
            if (seen.Add(GroupKey(item))) result.Add(item);
        return result;
    }

    /// <summary>Items grouped by app, groups in the order of each app's first item.</summary>
    public static List<SwitcherAppGroup> GroupByApp(IReadOnlyList<SwitcherItem> items)
    {
        var groups = new List<(string Key, SwitcherItem First, List<int> Indices)>();
        for (var i = 0; i < items.Count; i++)
        {
            var key = GroupKey(items[i]);
            var group = groups.FirstOrDefault(g => g.Key == key);
            if (group.Indices is null) groups.Add((key, items[i], [i]));
            else group.Indices.Add(i);
        }
        return groups.Select(g => new SwitcherAppGroup(g.First.AppKey, g.First.ProcessId, g.First.AppName, g.Indices)).ToList();
    }

    private static string GroupKey(SwitcherItem item) => item.AppKey.Length > 0 ? item.AppKey : $"pid:{item.ProcessId}";

    /// <summary>
    /// Search: every word of the query must appear in the window title or app name
    /// (case and accent insensitive), or the query must match the app name's initials.
    /// </summary>
    public static bool Matches(SwitcherItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = Fold(item.Title + " " + item.AppName);
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(haystack.Contains)) return true;
        var initials = new string(Fold(item.AppName).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
        return words.Length == 1 && initials.StartsWith(words[0], StringComparison.Ordinal);
    }

    public static string Fold(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Q quits, W closes, M minimizes, S pins search (when enabled); anything else searches.</summary>
    public static SwitcherLetterAction? LetterAction(char typed, bool pinSearchEnabled) => char.ToLowerInvariant(typed) switch
    {
        'w' => SwitcherLetterAction.CloseWindow,
        'q' => SwitcherLetterAction.QuitApp,
        'm' => SwitcherLetterAction.MinimizeWindow,
        's' when pinSearchEnabled => SwitcherLetterAction.PinSearch,
        _ => null,
    };

    /// <summary>Next index moving by <paramref name="delta"/>; wraps when asked, otherwise stops at the ends.</summary>
    public static int Advance(int index, int delta, int count, bool wrap = true)
    {
        if (count <= 0) return -1;
        var next = index + delta;
        if (wrap) return ((next % count) + count) % count;
        return Math.Clamp(next, 0, count - 1);
    }

    /// <summary>Up/down in a grid: same column of the previous/next row, clamped to the list.</summary>
    public static int MoveRow(int index, int rowDelta, int columns, int count)
    {
        if (count <= 0) return -1;
        if (columns <= 0) columns = 1;
        var next = index + rowDelta * columns;
        if (next < 0 || next >= count) return index;
        return next;
    }

    /// <summary>The first selection: the window after the current one, or the last one going backwards.</summary>
    public static int InitialSelection(int count, bool backwards)
    {
        if (count <= 0) return -1;
        if (count == 1) return 0;
        return backwards ? count - 1 : 1;
    }

    /// <summary>The next window of the same app as <paramref name="index"/> (Alt+` while open).</summary>
    public static int NextOfSameApp(IReadOnlyList<SwitcherItem> items, int index, int delta)
    {
        if (index < 0 || index >= items.Count) return index;
        var key = GroupKey(items[index]);
        var same = Enumerable.Range(0, items.Count).Where(i => GroupKey(items[i]) == key).ToList();
        if (same.Count < 2) return index;
        var position = same.IndexOf(index);
        return same[Advance(position, delta, same.Count)];
    }

    /// <summary>Columns for the grid: as many cards as fit, capped by the user's maximum.</summary>
    public static int Columns(int itemCount, double availableWidth, double cardWidth, int? maxColumns)
    {
        if (itemCount <= 0) return 1;
        var fit = Math.Max(1, (int)Math.Floor(availableWidth / Math.Max(1, cardWidth)));
        if (maxColumns is { } cap) fit = Math.Min(fit, cap);
        return Math.Min(fit, itemCount);
    }

    /// <summary>Keeps only windows that are mostly on <paramref name="display"/>.</summary>
    public static List<SwitcherItem> OnDisplay(IEnumerable<SwitcherItem> items, Rect display) =>
        items.Where(i => i.IsAppEntry || i.Frame.IsEmpty || ContainsMostOf(display, i.Frame)).ToList();

    private static bool ContainsMostOf(Rect display, Rect frame)
    {
        var overlap = Rect.Intersect(display, frame);
        if (overlap.IsEmpty) return false;
        return overlap.Width * overlap.Height >= frame.Width * frame.Height / 2;
    }

    /// <summary>Whether a key event with these modifiers matches a shortcut (extra Shift allowed for "backwards").</summary>
    public static bool MatchesShortcut(Shortcut shortcut, int vk, Modifiers held, bool allowExtraShift = true, Modifiers tolerate = Modifiers.None)
    {
        if (shortcut.IsEmpty || shortcut.Key != vk) return false;
        var expected = shortcut.Modifiers | tolerate;
        var actual = held;
        if (allowExtraShift && !shortcut.Modifiers.HasFlag(Modifiers.Shift)) actual &= ~Modifiers.Shift;
        return (actual & ~tolerate) == (shortcut.Modifiers & ~tolerate) && (actual | expected) == expected;
    }
}
