// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>What the window features do to other apps' windows. Call on the UI thread.</summary>
public static class WindowActions
{
    /// <summary>Brings a window forward (switching virtual desktop if it lives on another one).</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        try { WindowHelper.Activate(hwnd); }
        catch (Exception ex) { Log.Error("Activating a window failed", ex); }
    }

    public static void Minimize(IntPtr hwnd)
    {
        if (Native.IsWindow(hwnd)) Native.ShowWindowAsync(hwnd, Native.SW_MINIMIZE);
    }

    public static void Restore(IntPtr hwnd)
    {
        if (Native.IsWindow(hwnd)) Native.ShowWindowAsync(hwnd, Native.SW_RESTORE);
    }

    /// <summary>Asks one window to close, as its close button would.</summary>
    public static void Close(IntPtr hwnd)
    {
        if (Native.IsWindow(hwnd)) Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(WinApi.SC_CLOSE), IntPtr.Zero);
    }

    /// <summary>
    /// Asks an app to quit by closing every top-level window it owns. Apps still get to
    /// show "Save changes?" prompts. File Explorer only loses the window: the shell stays.
    /// </summary>
    public static void QuitApp(uint pid, IntPtr fallbackWindow, bool includeHidden = false)
    {
        var path = WindowHelper.GetProcessPath(pid);
        if (AppNames.IsSystemProcess(path) || pid == (uint)Environment.ProcessId)
        {
            if (fallbackWindow != IntPtr.Zero) Close(fallbackWindow);
            return;
        }
        var windows = TopLevelWindowsOf(pid, includeHidden);
        foreach (var hwnd in windows) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (windows.Count == 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                process.CloseMainWindow();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    /// <summary>Top-level windows of a process (visible only unless asked), titled or not.</summary>
    public static List<IntPtr> TopLevelWindowsOf(uint pid, bool includeHidden)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (WindowHelper.GetProcessId(hwnd) != pid) return true;
            var visible = Native.IsWindowVisible(hwnd);
            if (!includeHidden && !visible) return true;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (!visible)
            {
                // Hidden windows: only an app's own titled windows (its tray/main window),
                // never the per-thread IME, COM or GDI+ helpers Windows creates for it.
                if (Native.GetWindowTextLength(hwnd) == 0) return true;
                var cls = WindowHelper.GetClassName(hwnd);
                if (cls is "IME" or "MSCTFIME UI" or "OleMainThreadWndClass" or "GDI+ Hook Window Class" ||
                    cls.StartsWith(".NET-BroadcastEventWindow", StringComparison.Ordinal)) return true;
            }
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Moves a window's visible frame onto <paramref name="target"/> (physical pixels).</summary>
    public static void Place(IntPtr hwnd, Native.RECT target)
    {
        if (!Native.IsWindow(hwnd)) return;
        WindowHelper.MoveVisibleFrame(hwnd, target);
    }

    /// <summary>Whether a window can be resized by the user (has a sizing border).</summary>
    public static bool IsResizable(IntPtr hwnd) => (WinApi.Style(hwnd) & Native.WS_THICKFRAME) != 0;

    /// <summary>Whether a window covers its whole monitor without a frame (a game or video in full screen).</summary>
    public static bool IsFullScreen(IntPtr hwnd)
    {
        if ((WinApi.Style(hwnd) & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
        Native.GetWindowRect(hwnd, out var rect);
        var mon = WindowHelper.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        return rect.Left <= mon.Left && rect.Top <= mon.Top && rect.Right >= mon.Right && rect.Bottom >= mon.Bottom;
    }
}
