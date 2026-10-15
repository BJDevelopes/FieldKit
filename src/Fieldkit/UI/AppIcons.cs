// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>Cached app icons (from an executable path or a window) as frozen WPF images.</summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private const int WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int GCLP_HICON = -14;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>The large icon of an executable (or any file), cached. Null if none.</summary>
    public static ImageSource? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Cache.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                if (icon is null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>The icon a window shows in the taskbar, falling back to its executable's icon.</summary>
    public static ImageSource? ForWindow(IntPtr hwnd)
    {
        try
        {
            Native.SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero, 0x0002, 50, out var hIcon);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICON);
            if (hIcon != IntPtr.Zero)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
        }
        catch
        {
            // fall through
        }
        return ForPath(WindowHelper.GetProcessPath(WindowHelper.GetProcessId(hwnd)));
    }

    /// <summary>An Image element of a given size, or a neutral tile if no icon is available.</summary>
    public static FrameworkElement Element(ImageSource? source, double size = 32, string fallbackGlyph = Glyphs.Grid)
    {
        if (source is null) return Ui.IconTile(fallbackGlyph, size);
        var img = new System.Windows.Controls.Image { Source = source, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }
}
