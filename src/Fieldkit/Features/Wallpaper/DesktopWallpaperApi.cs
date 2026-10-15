// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Fieldkit.Features.Wallpaper;

/// <summary>
/// Thin wrapper over the shell's <c>IDesktopWallpaper</c> (Windows 8+). Wallpapers are per
/// monitor; Windows has no per-virtual-desktop API here. Each call creates and releases its
/// own COM object, so it can be used from any thread (apply runs on a short-lived STA thread).
/// </summary>
public static class DesktopWallpaperApi
{
    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    /// <summary>Connected displays with their current wallpaper. Detached monitors are left out.</summary>
    public static List<WallpaperMonitor> Monitors()
    {
        var result = new List<WallpaperMonitor>();
        Use(api =>
        {
            var count = api.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                string id;
                try { id = api.GetMonitorDevicePathAt(i); }
                catch (COMException) { continue; }
                if (string.IsNullOrEmpty(id)) continue;

                MonitorBounds bounds;
                try
                {
                    var r = api.GetMonitorRECT(id);
                    bounds = new MonitorBounds(r.Left, r.Top, r.Right, r.Bottom);
                }
                catch (COMException) { continue; } // not attached to the desktop
                if (bounds.IsEmpty) continue;

                string wallpaper;
                try { wallpaper = api.GetWallpaper(id) ?? string.Empty; }
                catch (COMException) { wallpaper = string.Empty; }
                result.Add(new WallpaperMonitor(id, bounds, wallpaper));
            }
        });
        return result;
    }

    /// <summary>Sets the picture position (global in Windows) then each display's picture.</summary>
    public static void Apply(IReadOnlyList<WallpaperAssignment> assignments, WallpaperFit fit)
    {
        Use(api =>
        {
            api.SetPosition((DesktopWallpaperPosition)fit);
            foreach (var a in assignments) api.SetWallpaper(a.MonitorId, a.Path);
        });
    }

    private static void Use(Action<IDesktopWallpaper> action)
    {
        var type = Type.GetTypeFromCLSID(ClsidDesktopWallpaper, throwOnError: true)!;
        var obj = Activator.CreateInstance(type) ?? throw new COMException("IDesktopWallpaper is unavailable.");
        try
        {
            action((IDesktopWallpaper)obj);
        }
        finally
        {
            Marshal.FinalReleaseComObject(obj);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a fresh STA thread (shell COM objects want one).</summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Fieldkit wallpaper",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    private enum DesktopSlideshowDirection
    {
        Forward = 0,
        Backward = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Vtable order matters: keep every method, even the ones Fieldkit never calls.
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        Rect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        void SetBackgroundColor(uint color);

        uint GetBackgroundColor();

        void SetPosition(DesktopWallpaperPosition position);

        DesktopWallpaperPosition GetPosition();

        void SetSlideshow(IntPtr items);

        IntPtr GetSlideshow();

        void SetSlideshowOptions(uint options, uint slideshowTick);

        [PreserveSig]
        int GetSlideshowOptions(out uint options, out uint slideshowTick);

        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, DesktopSlideshowDirection direction);

        uint GetStatus();

        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
