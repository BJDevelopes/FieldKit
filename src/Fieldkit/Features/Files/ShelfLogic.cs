// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Files;

/// <summary>What a shelf tile holds.</summary>
public enum ShelfItemKind
{
    /// <summary>A file or folder somewhere on disk (never owned by the shelf).</summary>
    File,
    /// <summary>A note: plain text dropped onto the shelf.</summary>
    Text,
    /// <summary>A web address.</summary>
    Link,
    /// <summary>A picture dropped as pixels, saved as a PNG in the shelf's own store.</summary>
    Image,
}

/// <summary>One tile of the shelf. Immutable; the shelf swaps whole items when one changes.</summary>
public sealed record ShelfItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ShelfItemKind Kind { get; init; }

    /// <summary>The file (File) or the stored PNG (Image).</summary>
    public string? Path { get; init; }

    /// <summary>The note (Text) or the address (Link).</summary>
    public string? Text { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Pinned items survive "Clear all" and stay after being dragged out.</summary>
    public bool IsPinned { get; init; }

    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Whether <see cref="Path"/> points at a file on disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HoldsFile => Kind is ShelfItemKind.File or ShelfItemKind.Image && !string.IsNullOrEmpty(Path);
}

/// <summary>
/// What a drop (or the Explorer selection) offers, reduced to plain values so classification can be
/// tested without OLE. <see cref="Bitmap"/> is only a flag: the window saves the pixels itself.
/// </summary>
public sealed record ShelfDropData(
    IReadOnlyList<string>? Files = null,
    string? Text = null,
    string? Url = null,
    bool HasBitmap = false);

/// <summary>
/// Detects a quick side-to-side shake of the pointer while the left button is held (upstream
/// ShelfService.handleDrag): within the last 0.5 s, at least 5 samples, 3 direction reversals of more
/// than 6 px and 220 px of horizontal travel, and at least 1 s since the previous shake. Distances are
/// in DIPs; the caller divides physical pixels by the monitor scale. Not thread-safe: the service
/// feeds it from the input-hook thread only.
/// </summary>
public sealed class ShelfShakeDetector
{
    public const double WindowSeconds = 0.5;
    public const int MinSamples = 5;
    public const int MinReversals = 3;
    public const double ReversalThreshold = 6;
    public const double MinTravel = 220;
    public const double CooldownSeconds = 1.0;

    // A ring would avoid the shifts, but at most a few hundred samples live in 0.5 s.
    private readonly List<(double T, double X)> _samples = [];
    private double _lastShake = double.NegativeInfinity;

    public int SampleCount => _samples.Count;

    /// <summary>Forgets the samples (button pressed or released). The cooldown is kept.</summary>
    public void Reset() => _samples.Clear();

    /// <summary>Adds a pointer sample (seconds, DIPs) and answers whether it completes a shake.</summary>
    public bool AddSample(double t, double x)
    {
        _samples.Add((t, x));
        var drop = 0;
        while (drop < _samples.Count && t - _samples[drop].T > WindowSeconds) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
        if (_samples.Count < MinSamples) return false;

        var reversals = 0;
        var travel = 0.0;
        var lastDirection = 0;
        for (var i = 1; i < _samples.Count; i++)
        {
            var dx = _samples[i].X - _samples[i - 1].X;
            travel += Math.Abs(dx);
            var direction = dx > ReversalThreshold ? 1 : dx < -ReversalThreshold ? -1 : 0;
            if (direction == 0) continue;
            if (lastDirection != 0 && direction != lastDirection) reversals++;
            lastDirection = direction;
        }
        if (reversals < MinReversals || travel <= MinTravel || t - _lastShake <= CooldownSeconds) return false;
        _lastShake = t;
        _samples.Clear();
        return true;
    }
}

/// <summary>The shelf's decision rules (upstream ShelfInteractionSupport / ShelfPersistenceSupport).</summary>
public static class ShelfRules
{
    /// <summary>The shelf is a hand-curated surface, not an archive.</summary>
    public const int MaxItems = 200;

    public const int MaxTextLength = 200_000;

    /// <summary>Seconds of idleness before an empty, unpinned shelf hides itself.</summary>
    public static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".jxr", ".wdp",
    };

    /// <summary>Whether <paramref name="newItems"/> more fit next to <paramref name="existing"/>.</summary>
    public static bool CanAdd(int existing, int newItems) =>
        existing >= 0 && newItems > 0 && existing <= MaxItems - newItems;

    /// <summary>Text cut to the limit, or null when there is nothing but whitespace.</summary>
    public static string? BoundedText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var bounded = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        return string.IsNullOrWhiteSpace(bounded) ? null : bounded;
    }

    /// <summary>A tile title for a note: its first non-empty line, at most 48 characters.</summary>
    public static string TextTitle(string text)
    {
        var trimmed = text.Trim();
        var firstLine = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? trimmed;
        return firstLine.Length > 48 ? firstLine[..48] : firstLine;
    }

    /// <summary>A tile title for a link: its host, or the address itself.</summary>
    public static string LinkTitle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : url;

    /// <summary>The http(s) address <paramref name="text"/> consists of, or null.</summary>
    public static string? AsWebLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length > 4096 || trimmed.Any(char.IsWhiteSpace)) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? trimmed
            : null;
    }

    public static bool IsImageFile(string? path) =>
        !string.IsNullOrEmpty(path) && ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>
    /// Turns dropped data into items, in upstream's order of preference: files, then a picture, then a
    /// web address, then text (text that is just an http(s) address becomes a link). Files the shelf
    /// already holds are left out. The returned Image item has no path yet: the caller stores the pixels.
    /// </summary>
    public static List<ShelfItem> Classify(ShelfDropData data, IReadOnlySet<string>? shelvedPaths = null)
    {
        var result = new List<ShelfItem>();
        if (data.Files is { Count: > 0 } files)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in files)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path;
                try { path = System.IO.Path.GetFullPath(raw); }
                catch { continue; }
                if (!seen.Add(path) || shelvedPaths?.Contains(path) == true) continue;
                result.Add(new ShelfItem { Kind = ShelfItemKind.File, Path = path, Title = FileTitle(path) });
            }
            return result;
        }
        if (data.HasBitmap)
        {
            result.Add(new ShelfItem { Kind = ShelfItemKind.Image, Title = "Image" });
            return result;
        }
        if (AsWebLink(data.Url) is { } url)
        {
            result.Add(new ShelfItem { Kind = ShelfItemKind.Link, Text = url, Title = LinkTitle(url) });
            return result;
        }
        if (AsWebLink(data.Text) is { } textUrl)
        {
            result.Add(new ShelfItem { Kind = ShelfItemKind.Link, Text = textUrl, Title = LinkTitle(textUrl) });
            return result;
        }
        if (BoundedText(data.Text) is { } text)
            result.Add(new ShelfItem { Kind = ShelfItemKind.Text, Text = text, Title = TextTitle(text) });
        return result;
    }

    /// <summary>Whether the drop carries anything <see cref="Classify"/> can turn into an item.</summary>
    public static bool CanAccept(ShelfDropData data) =>
        data.Files is { Count: > 0 } || data.HasBitmap || AsWebLink(data.Url) is not null || BoundedText(data.Text) is not null;

    /// <summary>The name a file tile shows ("C:\" for a drive root).</summary>
    public static string FileTitle(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>"long file na…me.txt": keeps the start and the end (the extension) of a long title.</summary>
    public static string MiddleTruncate(string text, int maxLength)
    {
        if (maxLength < 3 || text.Length <= maxLength) return text;
        var tail = (maxLength - 1) / 2;
        var head = maxLength - 1 - tail;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }

    /// <summary>Upstream shouldHoldOpen: what keeps the shelf from hiding itself after 5 s.</summary>
    public static bool ShouldHoldOpen(bool pinned, int itemCount, bool pointerInside, bool dropTargeted, bool interacting) =>
        pinned || itemCount > 0 || pointerInside || dropTargeted || interacting;

    /// <summary>App exceptions only stop automatic appearances; the shortcut and Open now always work.</summary>
    public static bool AllowsAutomaticOpen(string? sourceExe, IEnumerable<string> excludedExes)
    {
        if (string.IsNullOrEmpty(sourceExe)) return true;
        var name = System.IO.Path.GetFileName(sourceExe);
        return !excludedExes.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A successful drop into another app closes the shelf, unless it is pinned.</summary>
    public static bool ShouldCloseAfterDrag(bool dropAccepted, int draggedCount, bool closeAfterDrop, bool pinned) =>
        dropAccepted && draggedCount > 0 && closeAfterDrop && !pinned;

    /// <summary>A successful drop removes the dragged items when the option is on.</summary>
    public static bool ShouldRemoveAfterDrag(bool dropAccepted, int draggedCount, bool removeAfterDrop) =>
        dropAccepted && draggedCount > 0 && removeAfterDrop;

    /// <summary>The dragged items that may leave: pinned ones always stay.</summary>
    public static List<Guid> RemovableAfterDrag(IEnumerable<ShelfItem> dragged) =>
        dragged.Where(i => !i.IsPinned).Select(i => i.Id).ToList();

    /// <summary>The ids a Shift+click selects: from the anchor to the target, inclusive, either direction.</summary>
    public static List<Guid> RangeSelection(IReadOnlyList<Guid> all, Guid? anchor, Guid target)
    {
        var to = IndexOf(all, target);
        if (to < 0) return [];
        var from = anchor is { } a && IndexOf(all, a) is var i and >= 0 ? i : to;
        var (lo, hi) = from <= to ? (from, to) : (to, from);
        return all.Skip(lo).Take(hi - lo + 1).ToList();
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == id) return i;
        return -1;
    }

    /// <summary>
    /// What an action acts on: the selection when the clicked tile is part of it, otherwise just that tile
    /// (upstream fileURLsForActions(startingAt:)). Keeps shelf order.
    /// </summary>
    public static List<ShelfItem> ActionTargets(IReadOnlyList<ShelfItem> items, IReadOnlySet<Guid> selection, ShelfItem clicked) =>
        selection.Contains(clicked.Id) ? items.Where(i => selection.Contains(i.Id)).ToList() : [clicked];

    /// <summary>What the footer's buttons act on: the selection, or everything when nothing is selected.</summary>
    public static List<ShelfItem> SelectionOrEverything(IReadOnlyList<ShelfItem> items, IReadOnlySet<Guid> selection) =>
        selection.Count == 0 ? items.ToList() : items.Where(i => selection.Contains(i.Id)).ToList();

    /// <summary>The text a set of text/link items carries when dragged or copied together.</summary>
    public static string? JoinedText(IEnumerable<ShelfItem> items)
    {
        var parts = items.Where(i => i.Kind is ShelfItemKind.Text or ShelfItemKind.Link && !string.IsNullOrEmpty(i.Text))
            .Select(i => i.Text!).ToList();
        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    /// <summary>The tooltip of a tile (upstream ShelfTooltipSupport, capped at 500 characters).</summary>
    public static string Tooltip(ShelfItem item)
    {
        const int cap = 500;
        var text = item.Kind switch
        {
            ShelfItemKind.File => item.Path ?? item.Title,
            ShelfItemKind.Image => item.Title,
            _ => item.Text?.Trim() ?? item.Title,
        };
        return text.Length > cap ? text[..cap] + "…" : text;
    }

    /// <summary>Exe file names from settings, cleaned: trimmed, file name only, no duplicates, sorted.</summary>
    public static List<string> NormalizeExclusions(IEnumerable<string>? names) =>
        (names ?? [])
            .Select(n => System.IO.Path.GetFileName(n?.Trim() ?? string.Empty))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// The live list of items plus the selection (upstream ShelfService's items / selection / pinnedIDs).
/// Plain C#, so the rules are testable; the service persists it and the window draws it. Every change
/// raises <see cref="Changed"/>. Single-threaded (UI thread).
/// </summary>
public sealed class ShelfCollection
{
    private readonly List<ShelfItem> _items = [];
    private readonly HashSet<Guid> _selection = [];
    private Guid? _anchor;

    public event Action? Changed;

    public IReadOnlyList<ShelfItem> Items => _items;
    public IReadOnlySet<Guid> Selection => _selection;
    public int Count => _items.Count;

    /// <summary>Paths of the files on the shelf (for skipping duplicates).</summary>
    public HashSet<string> ShelvedPaths() =>
        new(_items.Where(i => i.Kind == ShelfItemKind.File && i.Path is not null).Select(i => i.Path!), StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces everything (restoring from disk).</summary>
    public void Reset(IEnumerable<ShelfItem> items)
    {
        _items.Clear();
        _items.AddRange(items.Take(ShelfRules.MaxItems));
        _selection.Clear();
        _anchor = null;
        Changed?.Invoke();
    }

    /// <summary>Adds all of <paramref name="items"/> or, when they do not fit, none (upstream refuses the drop).</summary>
    public bool Add(IReadOnlyList<ShelfItem> items)
    {
        if (!ShelfRules.CanAdd(_items.Count, items.Count)) return false;
        _items.AddRange(items);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes the given items; returns what left.</summary>
    public List<ShelfItem> Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = _items.Where(i => set.Contains(i.Id)).ToList();
        if (removed.Count == 0) return removed;
        _items.RemoveAll(i => set.Contains(i.Id));
        _selection.ExceptWith(set);
        if (_anchor is { } a && set.Contains(a)) _anchor = null;
        Changed?.Invoke();
        return removed;
    }

    /// <summary>"Clear all": everything except pinned items.</summary>
    public List<ShelfItem> Clear() => Remove(_items.Where(i => !i.IsPinned).Select(i => i.Id).ToList());

    /// <summary>The trash button: the selection when there is one, otherwise "Clear all".</summary>
    public List<ShelfItem> RemoveSelectedOrClear() => _selection.Count == 0 ? Clear() : Remove(_selection.ToList());

    public void TogglePin(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return;
        _items[index] = _items[index] with { IsPinned = !_items[index].IsPinned };
        Changed?.Invoke();
    }

    /// <summary>Swaps an item for an updated copy with the same id.</summary>
    public void Replace(ShelfItem item)
    {
        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0) return;
        _items[index] = item;
        Changed?.Invoke();
    }

    /// <summary>A plain click toggles the tile; Shift extends from the last tile touched.</summary>
    public void Click(Guid id, bool extend)
    {
        if (_items.All(i => i.Id != id)) return;
        if (extend)
        {
            _selection.UnionWith(ShelfRules.RangeSelection(_items.Select(i => i.Id).ToList(), _anchor, id));
        }
        else if (!_selection.Remove(id))
        {
            _selection.Add(id);
        }
        _anchor = id;
        Changed?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0 && _anchor is null) return;
        _selection.Clear();
        _anchor = null;
        Changed?.Invoke();
    }

    public ShelfItem? Find(Guid id) => _items.FirstOrDefault(i => i.Id == id);
}
