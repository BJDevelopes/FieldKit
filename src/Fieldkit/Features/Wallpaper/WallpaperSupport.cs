// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Fieldkit.Features.Wallpaper;

/// <summary>Where a gallery picture comes from (upstream: Apple stills vs. the user's own).</summary>
public enum WallpaperSource { System, Own }

/// <summary>The gallery filter (setting values <c>all</c>, <c>own</c>, <c>system</c>).</summary>
public enum WallpaperFilter { All, Own, System }

/// <summary>A gallery cell. <see cref="Id"/> is the normalized full path.</summary>
public sealed record WallpaperEntry(string Id, string Path, string Title, WallpaperSource Source);

/// <summary>
/// Catalog, filter and paging helpers (the counterpart of upstream <c>WallpaperSupport</c>).
/// No WPF and no settings, so everything here is unit-tested.
/// </summary>
public static class WallpaperSupport
{
    /// <summary>Paths compare case-insensitively on Windows.</summary>
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Upstream's still-image extensions plus <c>.jfif</c> (a common Windows JPEG name).</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".jpg", ".jpeg", ".jfif", ".png", ".tif", ".tiff", ".gif", ".bmp", ".webp",
    };

    /// <summary>The file dialog filter matching <see cref="ImageExtensions"/>.</summary>
    public static string DialogFilter =>
        "Images|" + string.Join(";", ImageExtensions.OrderBy(e => e, StringComparer.Ordinal).Select(e => "*" + e));

    /// <summary>Built-in Windows stills (the counterpart of /System/Library/Desktop Pictures).</summary>
    public static string SystemWallpaperRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");

    /// <summary>Upstream page size: 3 columns x 8 rows.</summary>
    public const int PageSize = 24;

    public static bool IsStillImage(string path) =>
        ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    public static string Title(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>Full path without a trailing separator (except for drive roots).</summary>
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
    }

    /// <summary>True when <paramref name="path"/> lies strictly inside <paramref name="folder"/>.</summary>
    public static bool IsInside(string path, string folder)
    {
        var f = Normalize(folder);
        var p = Normalize(path);
        if (!f.EndsWith('\\')) f += "\\";
        return p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>OneDrive "files on demand" placeholders: reading them downloads the whole file.</summary>
    public static bool IsCloudPlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */) != 0 ||
        (attributes & FileAttributes.Offline) != 0;

    /// <summary>
    /// Every still image under <paramref name="folder"/>, recursively. Like upstream it skips
    /// hidden entries and does not walk into links (symlinks / junctions, the Windows counterpart
    /// of skipping packages and symlinks). Sorted by file name, case-insensitive.
    /// Returns an empty list when <paramref name="shouldContinue"/> turns false.
    /// </summary>
    public static List<string> ImagesInFolder(string folder, Func<bool>? shouldContinue = null)
    {
        shouldContinue ??= () => true;
        var found = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            if (!shouldContinue()) return [];
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                {
                    if (!shouldContinue()) return [];
                    var name = System.IO.Path.GetFileName(file);
                    if (name.StartsWith('.') || !IsStillImage(file)) continue;
                    found.Add(file);
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                {
                    var info = new DirectoryInfo(sub);
                    if (info.Name.StartsWith('.')) continue;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(sub);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found
            .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Built-in stills under <paramref name="root"/> (C:\Windows\Web\Wallpaper\&lt;theme&gt;\*.jpg).
    /// Titles carry the theme folder ("Windows img0") since the file names repeat.
    /// </summary>
    public static List<WallpaperEntry> SystemEntries(string root, Func<bool>? shouldContinue = null)
    {
        if (!Directory.Exists(root)) return [];
        var entries = new List<WallpaperEntry>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var file in ImagesInFolder(root, shouldContinue))
        {
            var id = Normalize(file);
            if (!seen.Add(id)) continue;
            var parent = System.IO.Path.GetDirectoryName(id) ?? string.Empty;
            var title = PathComparer.Equals(parent, Normalize(root))
                ? Title(id)
                : $"{System.IO.Path.GetFileName(parent)} {Title(id)}";
            entries.Add(new WallpaperEntry(id, id, title, WallpaperSource.System));
        }
        return entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<WallpaperEntry> OwnEntries(IEnumerable<string> paths)
    {
        var entries = new List<WallpaperEntry>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var path in paths)
        {
            var id = Normalize(path);
            if (!seen.Add(id)) continue;
            entries.Add(new WallpaperEntry(id, id, Title(id), WallpaperSource.Own));
        }
        return entries;
    }

    /// <summary>System stills first win a duplicate path; the result is sorted by title.</summary>
    public static List<WallpaperEntry> Merge(IEnumerable<WallpaperEntry> system, IEnumerable<WallpaperEntry> own)
    {
        var merged = system.ToList();
        var seen = new HashSet<string>(merged.Select(e => e.Id), PathComparer);
        foreach (var entry in own)
        {
            if (seen.Add(entry.Id)) merged.Add(entry);
        }
        return merged.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<WallpaperEntry> Filtered(IEnumerable<WallpaperEntry> entries, WallpaperFilter filter) => filter switch
    {
        WallpaperFilter.Own => entries.Where(e => e.Source == WallpaperSource.Own).ToList(),
        WallpaperFilter.System => entries.Where(e => e.Source == WallpaperSource.System).ToList(),
        _ => entries.ToList(),
    };

    public static WallpaperFilter ParseFilter(string? value) => value switch
    {
        "own" => WallpaperFilter.Own,
        "system" => WallpaperFilter.System,
        _ => WallpaperFilter.All,
    };

    public static string FilterValue(WallpaperFilter filter) => filter switch
    {
        WallpaperFilter.Own => "own",
        WallpaperFilter.System => "system",
        _ => "all",
    };

    // ---------------------------------------------------------------- paging (upstream rules)

    public static int PageCount(int itemCount, int pageSize = PageSize)
    {
        if (pageSize <= 0 || itemCount <= 0) return itemCount == 0 ? 0 : 1;
        return (itemCount + pageSize - 1) / pageSize;
    }

    /// <summary>1-based, clamped into the valid range.</summary>
    public static int ClampedPage(int page, int itemCount, int pageSize = PageSize)
    {
        var pages = PageCount(itemCount, pageSize);
        return pages <= 0 ? 1 : Math.Clamp(page, 1, pages);
    }

    public static List<T> PageSlice<T>(IReadOnlyList<T> items, int page, int pageSize = PageSize)
    {
        if (items.Count == 0 || pageSize <= 0) return [];
        var safe = ClampedPage(page, items.Count, pageSize);
        var start = (safe - 1) * pageSize;
        if (start >= items.Count) return [];
        return items.Skip(start).Take(pageSize).ToList();
    }

    /// <summary>
    /// Gallery rows to reserve: several pages keep the full 8-row height so paging does not
    /// resize the panel; one page takes only the rows it fills (2 when empty).
    /// </summary>
    public static int GalleryRows(int itemCount, int columns = 3, int pageSize = PageSize)
    {
        if (PageCount(itemCount, pageSize) > 1) return (pageSize + columns - 1) / columns;
        if (itemCount == 0) return 2;
        return (itemCount + columns - 1) / columns;
    }
}

/// <summary>What the user added (files and folders) plus folder pictures they hid.</summary>
public sealed record WallpaperSourceState(IReadOnlyList<string> Sources, IReadOnlyList<string> Excluded)
{
    public static WallpaperSourceState Empty { get; } = new([], []);
}

/// <summary>A user source as shown on a chip / in Settings.</summary>
public sealed record WallpaperSourceInfo(string Path, string Title, bool IsFolder, bool IsReachable);

/// <summary>
/// Own-picture bookkeeping (upstream bookmarks + exclusions): add, remove, hide one picture of a
/// folder, and collect the pictures a scan should show.
/// </summary>
public static class WallpaperSources
{
    /// <summary>
    /// Adds picked files / folders. Files that are not still images are ignored; re-adding a path
    /// moves it to the end and clears a prior hide of it (and of its children for a folder).
    /// </summary>
    public static WallpaperSourceState Add(WallpaperSourceState state, IEnumerable<(string Path, bool IsFolder)> picked)
    {
        var sources = state.Sources.ToList();
        var excluded = state.Excluded.ToList();
        foreach (var (raw, isFolder) in picked)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = WallpaperSupport.Normalize(raw);
            if (!isFolder && !WallpaperSupport.IsStillImage(path)) continue;
            sources.RemoveAll(s => WallpaperSupport.PathComparer.Equals(s, path));
            sources.Add(path);
            excluded.RemoveAll(e => WallpaperSupport.PathComparer.Equals(e, path) ||
                                    (isFolder && WallpaperSupport.IsInside(e, path)));
        }
        return new WallpaperSourceState(sources, excluded);
    }

    /// <summary>Drops a source; a folder also forgets the pictures hidden inside it.</summary>
    public static WallpaperSourceState RemoveSource(WallpaperSourceState state, string path, bool isFolder)
    {
        var normalized = WallpaperSupport.Normalize(path);
        var sources = state.Sources.Where(s => !WallpaperSupport.PathComparer.Equals(s, normalized)).ToList();
        var excluded = state.Excluded.Where(e => isFolder
            ? !WallpaperSupport.IsInside(e, normalized)
            : !WallpaperSupport.PathComparer.Equals(e, normalized)).ToList();
        return new WallpaperSourceState(sources, excluded);
    }

    /// <summary>
    /// The gallery "x" on one of your pictures: drops it when it was added as a file, otherwise
    /// hides it (it came from a folder; the file stays on disk).
    /// </summary>
    public static WallpaperSourceState RemoveEntry(WallpaperSourceState state, string entryPath)
    {
        var path = WallpaperSupport.Normalize(entryPath);
        if (state.Sources.Any(s => WallpaperSupport.PathComparer.Equals(s, path)))
            return RemoveSource(state, path, isFolder: false);
        if (state.Excluded.Any(e => WallpaperSupport.PathComparer.Equals(e, path))) return state;
        return new WallpaperSourceState(state.Sources, [.. state.Excluded, path]);
    }

    /// <summary>Every picture the sources contribute, minus hidden ones, in source order.</summary>
    public static List<string> CollectImages(WallpaperSourceState state, Func<bool>? shouldContinue = null)
    {
        shouldContinue ??= () => true;
        var excluded = new HashSet<string>(state.Excluded, WallpaperSupport.PathComparer);
        var seenRoots = new HashSet<string>(WallpaperSupport.PathComparer);
        var images = new List<string>();
        foreach (var source in state.Sources)
        {
            if (!shouldContinue()) return [];
            if (!seenRoots.Add(source)) continue;
            if (Directory.Exists(source))
            {
                foreach (var file in WallpaperSupport.ImagesInFolder(source, shouldContinue))
                {
                    var id = WallpaperSupport.Normalize(file);
                    if (!excluded.Contains(id)) images.Add(id);
                }
            }
            else if (File.Exists(source) && WallpaperSupport.IsStillImage(source) && !excluded.Contains(source))
            {
                images.Add(source);
            }
        }
        return images;
    }

    public static WallpaperSourceInfo Describe(string path)
    {
        var isDir = Directory.Exists(path);
        var isFile = !isDir && File.Exists(path);
        var isFolder = isDir || (!isFile && !WallpaperSupport.IsStillImage(path));
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return new WallpaperSourceInfo(path, string.IsNullOrEmpty(name) ? path : name, isFolder, isDir || isFile);
    }

    /// <summary>
    /// Sources offered on the remove strip: folders, unreachable sources and file sources with no
    /// gallery cell. While a scan runs every file source would look orphaned, so only folders and
    /// unreachable ones are listed then (upstream rule).
    /// </summary>
    public static List<WallpaperSourceInfo> RemovableChips(IEnumerable<WallpaperSourceInfo> sources,
        IReadOnlySet<string> ownEntryIds, bool isLoading) =>
        sources.Where(s => s.IsFolder || !s.IsReachable || (!isLoading && !ownEntryIds.Contains(s.Path))).ToList();
}
