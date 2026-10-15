// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fieldkit.Core;

namespace Fieldkit.Features.Files;

/// <summary>
/// The private files behind the shelf: <c>Shelf\shelf.json</c> and the pictures in <c>Shelf\Items</c>
/// under the data directory. Kept out of the settings store, so a settings export never carries what
/// the user shelved. Only files inside <see cref="ItemsDirectory"/> are ever deleted: the shelf
/// forgets user files, it never touches them.
/// </summary>
public static class ShelfStore
{
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private static readonly object Gate = new();
    private static int _saveGeneration;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(AppInfo.DataDirectory, "Shelf");

    public static string FilePath => Path.Combine(Directory, "shelf.json");

    /// <summary>Where pictures dropped as pixels are kept.</summary>
    public static string ItemsDirectory => Path.Combine(Directory, "Items");

    /// <summary>Whether <paramref name="path"/> is a file the shelf itself wrote (and so may delete).</summary>
    public static bool IsOwned(string? path, string? itemsDirectory = null)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var dir = Path.GetFullPath(itemsDirectory ?? ItemsDirectory).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- pure parts

    public static string Serialize(IEnumerable<ShelfItem> items) => JsonSerializer.Serialize(items.ToList(), Json);

    /// <summary>Parses a saved shelf; unreadable data gives an empty list (and is logged by the caller).</summary>
    public static List<ShelfItem>? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<ShelfItem>>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Drops entries that can no longer be honoured (missing files, empty notes, bad links, duplicate
    /// ids), cuts notes to the limit and keeps at most <see cref="ShelfRules.MaxItems"/>.
    /// <paramref name="fileExists"/> must answer true for files on drives that are merely absent right
    /// now (see <see cref="FileSurvives"/>), or a late USB/network drive would lose its items.
    /// </summary>
    public static List<ShelfItem> Sanitize(IEnumerable<ShelfItem?> items, Func<string, bool> fileExists)
    {
        var result = new List<ShelfItem>();
        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (result.Count >= ShelfRules.MaxItems) break;
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id)) continue;
            switch (item.Kind)
            {
                case ShelfItemKind.File or ShelfItemKind.Image:
                    if (string.IsNullOrEmpty(item.Path) || !fileExists(item.Path)) continue;
                    result.Add(item with { Text = null, Title = string.IsNullOrEmpty(item.Title) ? ShelfRules.FileTitle(item.Path) : item.Title });
                    break;
                case ShelfItemKind.Text:
                    if (ShelfRules.BoundedText(item.Text) is not { } text) continue;
                    result.Add(item with { Path = null, Text = text, Title = string.IsNullOrEmpty(item.Title) ? ShelfRules.TextTitle(text) : item.Title });
                    break;
                case ShelfItemKind.Link:
                    if (ShelfRules.AsWebLink(item.Text) is not { } url) continue;
                    result.Add(item with { Path = null, Text = url, Title = string.IsNullOrEmpty(item.Title) ? ShelfRules.LinkTitle(url) : item.Title });
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Whether a saved file item should be kept: it exists, or its drive (or network share) is not
    /// reachable right now, which says nothing about the file. UNC paths are never probed, so a dead
    /// share cannot stall startup.
    /// </summary>
    public static bool FileSurvives(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            if (File.Exists(path) || System.IO.Directory.Exists(path)) return true;
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Files in the items folder that no item refers to any more.</summary>
    public static List<string> Orphans(IEnumerable<string> storedFiles, IEnumerable<ShelfItem> items)
    {
        var referenced = new HashSet<string>(
            items.Where(i => i.Path is not null).Select(i => Path.GetFullPath(i.Path!)), StringComparer.OrdinalIgnoreCase);
        return storedFiles.Where(f => !referenced.Contains(Path.GetFullPath(f))).ToList();
    }

    // ---------------------------------------------------------------- disk

    public static List<ShelfItem> Load() => Load(FilePath);

    public static List<ShelfItem> Load(string file)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileBytes) return [];
                var parsed = Deserialize(File.ReadAllText(file));
                if (parsed is null)
                {
                    Log.Warn("The saved shelf could not be read; keeping a copy and starting empty");
                    try { File.Copy(file, file + ".corrupt", overwrite: true); } catch { }
                    return [];
                }
                return Sanitize(parsed, FileSurvives);
            }
            catch (Exception ex)
            {
                Log.Error("Reading the shelf failed", ex);
                return [];
            }
        }
    }

    /// <summary>Writes a snapshot in the background (latest call wins).</summary>
    public static void SaveAsync(IReadOnlyList<ShelfItem> items)
    {
        var snapshot = items.ToList();
        var generation = Interlocked.Increment(ref _saveGeneration);
        Task.Run(() => SaveIfCurrent(generation, snapshot));
    }

    /// <summary>Writes now, and makes any older background save that has not written yet a no-op.</summary>
    public static void SaveNow(IReadOnlyList<ShelfItem> items) =>
        SaveIfCurrent(Interlocked.Increment(ref _saveGeneration), items.ToList());

    private static void SaveIfCurrent(int generation, IReadOnlyList<ShelfItem> items)
    {
        lock (Gate)
        {
            if (generation != Volatile.Read(ref _saveGeneration)) return;
            Save(FilePath, items);
        }
    }

    public static void Save(string file, IReadOnlyList<ShelfItem> items)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                File.WriteAllText(temp, Serialize(items));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the shelf failed", ex);
            }
        }
    }

    /// <summary>Saves PNG bytes as a new picture in the items folder; null on failure.</summary>
    public static string? WriteImage(byte[] png, string? itemsDirectory = null)
    {
        try
        {
            var dir = itemsDirectory ?? ItemsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            var path = ExplorerShell.UniquePath(dir, $"Image {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Saving a shelf picture failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Deletes pictures in the items folder that no item refers to and that are older than
    /// <paramref name="minimumAge"/> (a destination may still be copying one that just left the shelf).
    /// Never looks outside the items folder.
    /// </summary>
    public static void SweepOrphans(IReadOnlyList<ShelfItem> items, TimeSpan minimumAge, string? itemsDirectory = null)
    {
        try
        {
            var dir = itemsDirectory ?? ItemsDirectory;
            if (!System.IO.Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - minimumAge;
            var candidates = System.IO.Directory.EnumerateFiles(dir).Where(f => File.GetLastWriteTimeUtc(f) <= cutoff);
            foreach (var orphan in Orphans(candidates, items))
            {
                try { File.Delete(orphan); }
                catch (Exception ex) { Log.Warn($"Could not delete an old shelf picture: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Sweeping the shelf pictures failed: {ex.Message}");
        }
    }
}
