// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Shell icons for apps, files, folders and AppsFolder items, resolved once and kept as small
/// frozen bitmaps (rows draw at 28 px, so 64 px is plenty). Call on an STA thread (the UI thread).
/// The cache is cleared when the feature stops.
/// </summary>
public static class CommandBarIcons
{
    private const int Side = 64;
    private const int CacheLimit = 400;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? parsingName)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(parsingName, out var cached)) return cached;
        }
        var image = Load(parsingName);
        lock (Cache)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[parsingName] = image;
        }
        return image;
    }

    public static void Clear()
    {
        lock (Cache) Cache.Clear();
    }

    private static ImageSource? Load(string parsingName)
    {
        var hr = CommandBarNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero,
            typeof(CommandBarNative.IShellItemImageFactory).GUID, out var factory);
        if (hr != 0 || factory is null) return null;
        var hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new CommandBarNative.SIZE { cx = Side, cy = Side },
                    CommandBarNative.SIIGBF_ICONONLY | CommandBarNative.SIIGBF_BIGGERSIZEOK, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return null;
            return FromDib(hbitmap);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) CommandBarNative.DeleteObject(hbitmap);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bit premultiplied DIB section keeping its alpha (CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromDib(IntPtr hbitmap)
    {
        if (CommandBarNative.GetObject(hbitmap, System.Runtime.InteropServices.Marshal.SizeOf<CommandBarNative.DIBSECTION>(), out var dib) == 0) return null;
        var bm = dib.dsBm;
        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        var stride = bm.bmWidthBytes;
        var source = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, bm.bmBits, stride * bm.bmHeight, stride);
        BitmapSource result = source;
        // A DIB with a positive height is stored bottom-up.
        if (dib.dsBmih.biHeight > 0)
        {
            var flipped = new TransformedBitmap(source, new ScaleTransform(1, -1));
            result = new WriteableBitmap(flipped);
        }
        else
        {
            result = new WriteableBitmap(source);
        }
        result.Freeze();
        return result;
    }

    /// <summary>The parsing name of an AppsFolder item (works for packaged and desktop apps).</summary>
    public static string AppsFolder(string appId) => "shell:AppsFolder\\" + appId;
}
