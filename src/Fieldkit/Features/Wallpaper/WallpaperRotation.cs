// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Wallpaper;

/// <summary>Picture position (IDesktopWallpaper DESKTOP_WALLPAPER_POSITION values).</summary>
public enum WallpaperFit { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

/// <summary>A monitor as IDesktopWallpaper reports it (physical pixels).</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>One display: its device path id, bounds and current wallpaper file (may be empty).</summary>
public sealed record WallpaperMonitor(string Id, MonitorBounds Bounds, string Wallpaper);

/// <summary>One picture on one display; a rotation step is a list of these.</summary>
public sealed record WallpaperAssignment(string MonitorId, string Path);

/// <summary>Which displays a change goes to, and which picture each gets.</summary>
public static class WallpaperTargets
{
    /// <summary>
    /// Upstream <c>targetScreenIDs</c>: every display when "all displays" is on, otherwise the one
    /// under the pointer, falling back to the primary (the display holding 0,0), then the first.
    /// </summary>
    public static List<string> TargetMonitors(bool allDisplays, IReadOnlyList<WallpaperMonitor> monitors,
        (int X, int Y)? pointer)
    {
        var active = monitors.Where(m => !m.Bounds.IsEmpty).ToList();
        if (active.Count == 0) return [];
        if (allDisplays) return active.Select(m => m.Id).ToList();
        if (pointer is { } p && active.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y)) is { } under)
            return [under.Id];
        var primary = active.FirstOrDefault(m => m.Bounds.Contains(0, 0)) ?? active[0];
        return [primary.Id];
    }

    /// <summary>
    /// Builds a step: the same picture everywhere, or (when <paramref name="differentPerDisplay"/>)
    /// one picture per display drawn in turn from <paramref name="picks"/>.
    /// </summary>
    public static List<WallpaperAssignment> Assign(IReadOnlyList<string> monitorIds, IReadOnlyList<string> picks,
        bool differentPerDisplay)
    {
        if (monitorIds.Count == 0 || picks.Count == 0) return [];
        return monitorIds
            .Select((id, i) => new WallpaperAssignment(id, differentPerDisplay ? picks[i % picks.Count] : picks[0]))
            .ToList();
    }

    /// <summary>The current state of the given displays as a step (for "Previous" back to it).</summary>
    public static List<WallpaperAssignment> Snapshot(IEnumerable<WallpaperMonitor> monitors) =>
        monitors.Where(m => !m.Bounds.IsEmpty && !string.IsNullOrEmpty(m.Wallpaper))
            .Select(m => new WallpaperAssignment(m.Id, m.Wallpaper))
            .ToList();
}

/// <summary>
/// Picks the next pictures of a rotation. Sequential order walks the pool after the current
/// picture (wrapping); shuffle draws at random without repeats until every picture of the
/// pool was shown, and never repeats the current picture right away.
/// </summary>
public sealed class RotationPicker
{
    private readonly HashSet<string> _shown = new(WallpaperSupport.PathComparer);
    private readonly Random _random;

    public RotationPicker(Random? random = null) => _random = random ?? new Random();

    /// <summary>Pictures already used in this shuffle cycle.</summary>
    public IReadOnlyCollection<string> ShownThisCycle => _shown;

    public void Reset() => _shown.Clear();

    /// <summary>Returns up to <paramref name="count"/> pictures (fewer only when the pool is empty).</summary>
    public List<string> Pick(IReadOnlyList<string> pool, string? current, int count, bool shuffle)
    {
        if (pool.Count == 0 || count <= 0) return [];
        return shuffle ? PickShuffled(pool, current, count) : PickSequential(pool, current, count);
    }

    public static List<string> PickSequential(IReadOnlyList<string> pool, string? current, int count)
    {
        if (pool.Count == 0 || count <= 0) return [];
        var index = -1;
        if (current is not null)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (WallpaperSupport.PathComparer.Equals(pool[i], current))
                {
                    index = i;
                    break;
                }
            }
        }
        var result = new List<string>(count);
        for (var n = 1; n <= count; n++) result.Add(pool[(index + n) % pool.Count]);
        return result;
    }

    private List<string> PickShuffled(IReadOnlyList<string> pool, string? current, int count)
    {
        // Forget pictures that left the pool (folder edits) so the cycle can still finish.
        var poolSet = new HashSet<string>(pool, WallpaperSupport.PathComparer);
        _shown.RemoveWhere(p => !poolSet.Contains(p));

        var result = new List<string>(count);
        var picked = new HashSet<string>(WallpaperSupport.PathComparer);
        for (var n = 0; n < count; n++)
        {
            var candidates = pool.Where(p => !_shown.Contains(p) && !picked.Contains(p) && !Same(p, current)).ToList();
            if (candidates.Count == 0)
            {
                // Cycle finished: start a new one, still avoiding an immediate repeat.
                _shown.Clear();
                candidates = pool.Where(p => !picked.Contains(p) && !Same(p, current)).ToList();
                if (candidates.Count == 0) candidates = pool.Where(p => !picked.Contains(p)).ToList();
                if (candidates.Count == 0) candidates = pool.ToList();
            }
            var choice = candidates[_random.Next(candidates.Count)];
            _shown.Add(choice);
            picked.Add(choice);
            result.Add(choice);
        }
        return result;
    }

    private static bool Same(string a, string? b) => b is not null && WallpaperSupport.PathComparer.Equals(a, b);
}

/// <summary>
/// Back / forward history of applied steps (like a browser): going back then choosing a new
/// picture drops the forward entries. Oldest entries fall off past <see cref="Capacity"/>.
/// </summary>
public sealed class WallpaperHistory<T>
{
    private readonly List<T> _items = [];
    private int _cursor = -1;

    public WallpaperHistory(int capacity = 50) => Capacity = Math.Max(2, capacity);

    public int Capacity { get; }
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;
    public bool CanGoBack => _cursor > 0;
    public bool CanGoForward => _cursor >= 0 && _cursor < _items.Count - 1;
    public T? Current => _cursor >= 0 ? _items[_cursor] : default;

    public void Push(T item)
    {
        if (_cursor < _items.Count - 1) _items.RemoveRange(_cursor + 1, _items.Count - _cursor - 1);
        _items.Add(item);
        if (_items.Count > Capacity) _items.RemoveRange(0, _items.Count - Capacity);
        _cursor = _items.Count - 1;
    }

    public bool TryBack(out T item)
    {
        if (!CanGoBack)
        {
            item = default!;
            return false;
        }
        item = _items[--_cursor];
        return true;
    }

    public bool TryForward(out T item)
    {
        if (!CanGoForward)
        {
            item = default!;
            return false;
        }
        item = _items[++_cursor];
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _cursor = -1;
    }
}

/// <summary>Rotation interval choices and timing.</summary>
public static class RotationSchedule
{
    public const int DefaultMinutes = 30;

    /// <summary>The Windows slideshow intervals (minutes, label).</summary>
    public static readonly IReadOnlyList<(int Minutes, string Label)> Intervals =
    [
        (1, "1 minute"),
        (10, "10 minutes"),
        (30, "30 minutes"),
        (60, "1 hour"),
        (360, "6 hours"),
        (1440, "1 day"),
    ];

    public static int ParseMinutes(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? Math.Min(minutes, 7 * 1440)
            : DefaultMinutes;

    /// <summary>
    /// Time until the next change: the interval counted from the last change, never negative.
    /// No last change, or one in the future (clock moved back), restarts a full interval.
    /// </summary>
    public static TimeSpan DueIn(DateTime? lastChangeUtc, TimeSpan interval, DateTime nowUtc)
    {
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromMinutes(DefaultMinutes);
        if (lastChangeUtc is not { } last || last > nowUtc) return interval;
        var due = last + interval - nowUtc;
        return due < TimeSpan.Zero ? TimeSpan.Zero : due;
    }

    public static DateTime? ParseTimestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : null;

    public static string FormatTimestamp(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static WallpaperFit ParseFit(string? value) => value switch
    {
        "center" => WallpaperFit.Center,
        "tile" => WallpaperFit.Tile,
        "stretch" => WallpaperFit.Stretch,
        "fit" => WallpaperFit.Fit,
        "span" => WallpaperFit.Span,
        _ => WallpaperFit.Fill,
    };
}
