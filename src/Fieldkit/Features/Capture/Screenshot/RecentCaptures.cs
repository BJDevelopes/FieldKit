// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Fieldkit.Core;

namespace Fieldkit.Features.Capture;

public enum RecentCaptureKind { Screenshot, Recording }

/// <summary>One entry of the capture history. Screenshots keep a private copy so they can be restored.</summary>
public sealed record RecentCapture(Guid Id, RecentCaptureKind Kind, DateTime CreatedAt, string? ImageFile, string? RecordingPath);

/// <summary>
/// The capture history (upstream RecentCaptureStore): the last 12 screenshots and recordings, kept
/// only on this PC in the app's data folder, capped at 256 MB of screenshots.
/// </summary>
public static class RecentCaptures
{
    public const int Limit = 12;
    public const long MaximumBytes = 256L * 1024 * 1024;
    private static readonly object Gate = new();
    private static List<RecentCapture>? _entries;

    public static event Action? Changed;

    private static string Folder => CaptureSettings.PrivateFolder("Recent");
    private static string IndexPath => Path.Combine(Folder, "recent.json");

    public static IReadOnlyList<RecentCapture> Entries
    {
        get
        {
            lock (Gate) return Load().Where(Visible).ToList();
        }
    }

    private static bool Visible(RecentCapture e) =>
        e.Kind == RecentCaptureKind.Screenshot ? FeatureCatalog.IsAvailable("screenshot") : FeatureCatalog.IsAvailable("screenRecorder");

    /// <summary>The newest screenshot, for "Edit latest screenshot".</summary>
    public static RecentCapture? LatestScreenshot => Entries.FirstOrDefault(e => e.Kind == RecentCaptureKind.Screenshot && e.ImageFile is not null && File.Exists(e.ImageFile));

    public static void AddScreenshot(RasterImage image)
    {
        try
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(Folder, id + ".png");
            File.WriteAllBytes(path, image.EncodePng());
            Add(new RecentCapture(id, RecentCaptureKind.Screenshot, DateTime.Now, path, null));
        }
        catch (Exception ex)
        {
            Log.Error("Keeping the screenshot in the history failed", ex);
        }
    }

    public static void AddRecording(string savedPath) =>
        Add(new RecentCapture(Guid.NewGuid(), RecentCaptureKind.Recording, DateTime.Now, null, savedPath));

    public static void Remove(RecentCapture entry)
    {
        lock (Gate)
        {
            Load().RemoveAll(e => e.Id == entry.Id);
            DeletePrivate(entry);
            Save();
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var e in Load().Where(Visible).ToList())
            {
                DeletePrivate(e);
                _entries!.Remove(e);
            }
            Save();
        }
        Changed?.Invoke();
    }

    public static BitmapSource? Thumbnail(RecentCapture entry)
    {
        var path = entry.ImageFile;
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 208;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Keeps the newest entries within the count and size budget (pure, tested).</summary>
    public static List<RecentCapture> Capped(IReadOnlyList<RecentCapture> newestFirst, Func<RecentCapture, long> screenshotBytes)
    {
        var kept = new List<RecentCapture>();
        long bytes = 0;
        var keptScreenshot = false;
        foreach (var e in newestFirst.Take(Limit))
        {
            if (e.Kind != RecentCaptureKind.Screenshot)
            {
                kept.Add(e);
                continue;
            }
            var size = Math.Max(0, screenshotBytes(e));
            if (keptScreenshot && bytes + size > MaximumBytes) continue;
            kept.Add(e);
            keptScreenshot = true;
            bytes += size;
        }
        return kept;
    }

    private static void Add(RecentCapture entry)
    {
        lock (Gate)
        {
            var list = Load();
            list.Insert(0, entry);
            var capped = Capped(list, e => e.ImageFile is { } f && File.Exists(f) ? new FileInfo(f).Length : 0);
            foreach (var dropped in list.Except(capped).ToList()) DeletePrivate(dropped);
            _entries = capped;
            Save();
        }
        Changed?.Invoke();
    }

    private static void DeletePrivate(RecentCapture e)
    {
        // Only the private copy goes; saved files stay where the person put them.
        if (e.ImageFile is { } f && f.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(f); }
            catch (Exception ex) { Log.Warn($"Removing a history image failed: {ex.Message}"); }
        }
    }

    private static List<RecentCapture> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<List<RecentCapture>>(File.ReadAllText(IndexPath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the capture history failed: {ex.Message}");
            _entries = [];
        }
        _entries.RemoveAll(e => e.Kind == RecentCaptureKind.Screenshot && (e.ImageFile is null || !File.Exists(e.ImageFile)));
        return _entries;
    }

    private static void Save()
    {
        try
        {
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries ?? []));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the capture history failed", ex);
        }
    }
}
