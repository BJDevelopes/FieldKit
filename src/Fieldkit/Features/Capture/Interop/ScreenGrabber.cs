// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Photographs the screen with GDI (BitBlt + CAPTUREBLT) in physical pixels. The process is
/// per-monitor DPI aware, so coordinates are the same virtual-screen pixels Windows uses.
/// Nothing leaves memory unless the user saves it.
/// </summary>
public static class ScreenGrabber
{
    public static PixelRect VirtualScreen() => new(
        CaptureNative.GetSystemMetrics(CaptureNative.SM_XVIRTUALSCREEN),
        CaptureNative.GetSystemMetrics(CaptureNative.SM_YVIRTUALSCREEN),
        CaptureNative.GetSystemMetrics(CaptureNative.SM_CXVIRTUALSCREEN),
        CaptureNative.GetSystemMetrics(CaptureNative.SM_CYVIRTUALSCREEN));

    /// <summary>Captures a rectangle of the virtual screen.</summary>
    /// <param name="hideOwnWindows">Keeps Fieldkit's own windows out of the picture (display affinity).</param>
    public static RasterImage Capture(PixelRect rect, bool includeCursor = false, bool hideOwnWindows = true)
    {
        if (rect.IsEmpty) throw new ArgumentException("Nothing to capture.");
        using var hidden = hideOwnWindows ? ExcludeOwnWindows() : null;
        var screen = CaptureNative.GetDC(IntPtr.Zero);
        var mem = CaptureNative.CreateCompatibleDC(screen);
        var header = new CaptureNative.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<CaptureNative.BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CaptureNative.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) throw new InvalidOperationException("The screen could not be captured");
            var old = CaptureNative.SelectObject(mem, dib);
            CaptureNative.BitBlt(mem, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, CaptureNative.SRCCOPY | CaptureNative.CAPTUREBLT);
            if (includeCursor) DrawCursor(mem, rect);
            CaptureNative.SelectObject(mem, old);
            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            PixelOps.MakeOpaque(pixels);
            return new RasterImage(rect.Width, rect.Height, pixels) { Scale = WindowHelper.GetScaleAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2) };
        }
        finally
        {
            if (dib != IntPtr.Zero) CaptureNative.DeleteObject(dib);
            CaptureNative.DeleteDC(mem);
            CaptureNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void DrawCursor(IntPtr dc, PixelRect rect)
    {
        var info = new CaptureNative.CURSORINFO { cbSize = Marshal.SizeOf<CaptureNative.CURSORINFO>() };
        if (!CaptureNative.GetCursorInfo(ref info) || (info.flags & CaptureNative.CURSOR_SHOWING) == 0) return;
        var icon = CaptureNative.CopyIcon(info.hCursor);
        if (icon == IntPtr.Zero) return;
        try
        {
            var hx = 0;
            var hy = 0;
            if (CaptureNative.GetIconInfo(icon, out var ii))
            {
                hx = ii.xHotspot;
                hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) CaptureNative.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) CaptureNative.DeleteObject(ii.hbmColor);
            }
            CaptureNative.DrawIconEx(dc, info.ptScreenPos.X - rect.X - hx, info.ptScreenPos.Y - rect.Y - hy, icon, 0, 0, 0, IntPtr.Zero, CaptureNative.DI_NORMAL);
        }
        finally
        {
            CaptureNative.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// Marks every visible top-level window of this process as excluded from capture until disposed
    /// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+). Windows that already had an affinity keep it.
    /// </summary>
    public static IDisposable ExcludeOwnWindows()
    {
        var changed = new List<IntPtr>();
        var pid = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (WindowHelper.GetProcessId(hwnd) != pid) return true;
            if (CaptureNative.GetWindowDisplayAffinity(hwnd, out var current) && current != CaptureNative.WDA_NONE) return true;
            if (CaptureNative.SetWindowDisplayAffinity(hwnd, CaptureNative.WDA_EXCLUDEFROMCAPTURE)) changed.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return new Fieldkit.UI.ActionDisposable(() =>
        {
            foreach (var hwnd in changed)
            {
                try { CaptureNative.SetWindowDisplayAffinity(hwnd, CaptureNative.WDA_NONE); }
                catch (Exception ex) { Log.Warn($"Restoring display affinity failed: {ex.Message}"); }
            }
        });
    }

    /// <summary>Keeps one of our windows (controls, frames, HUDs) out of every capture.</summary>
    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) CaptureNative.SetWindowDisplayAffinity(hwnd, CaptureNative.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>
    /// Visible app windows in Z-order (front first) with their visible frame, for picking.
    /// The desktop and cloaked or minimised windows are left out.
    /// </summary>
    public static List<PickableWindow> PickableWindows(bool includeOwnProcess, ISet<IntPtr> exclude)
    {
        var own = (uint)Environment.ProcessId;
        var result = new List<PickableWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (exclude.Contains(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || WindowHelper.IsCloaked(hwnd)) return true;
            if (!includeOwnProcess && WindowHelper.GetProcessId(hwnd) == own) return true;
            var cls = WindowHelper.GetClassName(hwnd);
            if (cls is "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow" or "ForegroundStaging") return true;
            var r = WindowHelper.GetVisibleBounds(hwnd);
            var rect = PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width < 8 || rect.Height < 8) return true;
            // Fully transparent overlay windows (layered, click-through) would swallow every pick.
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return true;
            result.Add(new PickableWindow(hwnd, rect, WindowHelper.GetTitle(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
