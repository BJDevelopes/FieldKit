// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fieldkit.Features.Wallpaper;

/// <summary>
/// Small gallery thumbnails decoded off the UI thread (DecodePixelWidth, frozen), cached for the
/// process lifetime and cleared on uninstall. A new generation drops pending work of a closed
/// page. Cloud-only OneDrive files are not read (that would download them); the cell keeps its
/// placeholder, like upstream does for iCloud placeholders.
/// </summary>
public static class WallpaperThumbnails
{
    private const int MaxEntries = 400;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();
    private static readonly SemaphoreSlim Workers = new(2);
    private static int _generation;

    /// <summary>Cancels queued decodes (page change, panel closed).</summary>
    public static void BeginGeneration() => Interlocked.Increment(ref _generation);

    public static ImageSource? Cached(string path, int width)
    {
        lock (Gate) return Cache.TryGetValue(Key(path, width), out var image) ? image : null;
    }

    /// <summary>Calls <paramref name="done"/> on the calling (UI) thread with the thumbnail, or null.</summary>
    public static void Load(string path, int width, Action<ImageSource?> done)
    {
        if (Cached(path, width) is { } hit)
        {
            done(hit);
            return;
        }
        var generation = Volatile.Read(ref _generation);
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            ImageSource? image = null;
            await Workers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _generation) != generation) return;
                image = Cached(path, width) ?? Decode(path, width);
                if (image is not null) Store(path, width, image);
            }
            finally
            {
                Workers.Release();
            }
            if (Volatile.Read(ref _generation) != generation) return;
            if (dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _generation) == generation) done(image);
            });
        });
    }

    public static void Clear()
    {
        BeginGeneration();
        lock (Gate)
        {
            Cache.Clear();
            Order.Clear();
        }
    }

    /// <summary>Decodes a downscaled, frozen bitmap; null when unreadable or cloud-only.</summary>
    public static BitmapSource? Decode(string path, int width)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || WallpaperSupport.IsCloudPlaceholder(info.Attributes)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = Math.Max(16, width);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // missing codec (HEIC/WebP extensions), corrupt file, locked file
        }
    }

    private static void Store(string path, int width, ImageSource image)
    {
        lock (Gate)
        {
            var key = Key(path, width);
            if (Cache.ContainsKey(key)) return;
            Cache[key] = image;
            Order.Enqueue(key);
            while (Order.Count > MaxEntries) Cache.Remove(Order.Dequeue());
        }
    }

    private static string Key(string path, int width) => $"{path}#{width}";
}
