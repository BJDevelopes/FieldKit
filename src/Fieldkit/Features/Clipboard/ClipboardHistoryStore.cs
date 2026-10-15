// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Fieldkit.Core;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// The private files behind the clipboard history: <c>Clipboard\history.json</c> and the PNGs in
/// <c>Clipboard\Images</c> under the data directory. Kept out of the settings store on purpose, so a
/// settings export never carries anything the user copied.
/// </summary>
public static class ClipboardHistoryStore
{
    private const long MaxFileBytes = 96L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static int _saveGeneration;

    public static string Directory
    {
        get
        {
            var dir = Path.Combine(AppInfo.DataDirectory, "Clipboard");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ImageDirectory
    {
        get
        {
            var dir = Path.Combine(Directory, "Images");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Directory, "history.json");

    public static string ImagePath(string fileName) => Path.Combine(ImageDirectory, Path.GetFileName(fileName));

    public static List<ClipboardEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length > MaxFileBytes) return [];
                var entries = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(FilePath), Json) ?? [];
                return ClipboardHistoryLogic.Normalized(entries.Where(e => e.Kind != ClipboardEntryKind.Image || e.ImageFile is not null));
            }
            catch (Exception ex)
            {
                Log.Error("Reading the clipboard history failed", ex);
                // The next save replaces the file; keep the unreadable one (like the shelf) so pins can be recovered.
                if (ex is JsonException)
                {
                    try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
                }
                return [];
            }
        }
    }

    /// <summary>Writes a snapshot in the background (latest call wins) and sweeps orphaned images.</summary>
    public static void SaveAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        var snapshot = entries.ToList();
        var generation = Interlocked.Increment(ref _saveGeneration);
        Task.Run(() =>
        {
            if (generation != Volatile.Read(ref _saveGeneration)) return;
            SaveNow(snapshot);
        });
    }

    public static void SaveNow(IReadOnlyList<ClipboardEntry> entries)
    {
        lock (Gate)
        {
            try
            {
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
                File.Move(temp, FilePath, overwrite: true);
                SweepImages(entries);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the clipboard history failed", ex);
            }
        }
    }

    /// <summary>Stores a PNG under a new name. Returns the file name, or null on failure.</summary>
    public static string? StoreImage(byte[] png)
    {
        try
        {
            var name = $"{Guid.NewGuid():N}.png";
            File.WriteAllBytes(ImagePath(name), png);
            return name;
        }
        catch (Exception ex)
        {
            Log.Error("Storing a copied image failed", ex);
            return null;
        }
    }

    public static byte[]? ReadImage(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            var path = ImagePath(fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void SweepImages(IReadOnlyList<ClipboardEntry> entries)
    {
        var keep = new HashSet<string>(entries.Select(e => e.ImageFile).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in System.IO.Directory.EnumerateFiles(ImageDirectory, "*.png"))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            // Only files older than a minute: a capture may be storing its picture right now.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(1)) continue;
            try { File.Delete(file); } catch { }
        }
    }
}
