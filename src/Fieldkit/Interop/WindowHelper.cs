// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Fieldkit.Interop;

/// <summary>A top-level window the user can switch to.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, uint ProcessId, string ProcessPath, string ClassName)
{
    public string ProcessName => Path.GetFileNameWithoutExtension(ProcessPath);
    public bool IsMinimized => Native.IsIconic(Handle);
}

/// <summary>Window enumeration, geometry and focus helpers shared by window features.</summary>
public static class WindowHelper
{
    public static string GetTitle(IntPtr hwnd)
    {
        var len = Native.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint GetProcessId(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Full image path of a process, or empty if inaccessible.</summary>
    public static string GetProcessPath(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return Native.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Executable file name (e.g. "chrome.exe") of the window's process.</summary>
    public static string GetProcessFileName(IntPtr hwnd) => Path.GetFileName(GetProcessPath(GetProcessId(hwnd)));

    public static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Whether a window is one the user thinks of as an app window (what Alt+Tab shows):
    /// visible, not cloaked (other virtual desktop / suspended UWP), unowned or app-window styled,
    /// not a tool window, with a title.
    /// </summary>
    public static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;
        if (IsCloaked(hwnd)) return false;
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        var owner = Native.GetWindow(hwnd, Native.GW_OWNER);
        if (owner != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if (Native.GetWindowTextLength(hwnd) == 0) return false;
        var cls = GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return true;
    }

    /// <summary>App windows in Z-order (front-most first).</summary>
    public static List<WindowInfo> GetAltTabWindows(bool includeOwnProcess = false)
    {
        var own = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();
        var paths = new Dictionary<uint, string>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd)) return true;
            var pid = GetProcessId(hwnd);
            if (!includeOwnProcess && pid == own) return true;
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = GetProcessPath(pid);
            result.Add(new WindowInfo(hwnd, GetTitle(hwnd), pid, path, GetClassName(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The visible frame (without the invisible resize borders Windows 10/11 add).</summary>
    public static Native.RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame,
                System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0)
            return frame;
        Native.GetWindowRect(hwnd, out var rect);
        return rect;
    }

    /// <summary>
    /// Moves/resizes a window so its VISIBLE frame lands exactly on <paramref name="target"/>
    /// (compensates for invisible borders). Restores maximized windows first.
    /// </summary>
    public static void MoveVisibleFrame(IntPtr hwnd, Native.RECT target)
    {
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.GetWindowRect(hwnd, out var outer);
        var visible = GetVisibleBounds(hwnd);
        var left = visible.Left - outer.Left;
        var top = visible.Top - outer.Top;
        var right = outer.Right - visible.Right;
        var bottom = outer.Bottom - visible.Bottom;
        Native.SetWindowPos(hwnd, IntPtr.Zero,
            target.Left - left, target.Top - top,
            target.Width + left + right, target.Height + top + bottom,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public static Native.MONITORINFOEX GetMonitorInfo(IntPtr hMonitor)
    {
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(hMonitor, ref info);
        return info;
    }

    /// <summary>Work area (screen minus taskbar) of the monitor holding the window, in physical pixels.</summary>
    public static Native.RECT GetWorkArea(IntPtr hwnd) =>
        GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)).rcWork;

    public static Native.RECT GetWorkAreaAt(int x, int y) =>
        GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST)).rcWork;

    /// <summary>All monitors (handle, bounds, work area), primary first.</summary>
    public static List<(IntPtr Handle, Native.RECT Bounds, Native.RECT WorkArea, bool Primary)> GetMonitors()
    {
        var list = new List<(IntPtr, Native.RECT, Native.RECT, bool)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = GetMonitorInfo(h);
            list.Add((h, info.rcMonitor, info.rcWork, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Item4).ThenBy(m => m.Item2.Left).ToList();
    }

    /// <summary>Scale factor (1.0 = 96 dpi) of the monitor containing a physical point.</summary>
    public static double GetScaleAt(int x, int y)
    {
        var mon = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(mon, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    /// <summary>
    /// Brings a window to the foreground reliably, working around the foreground lock
    /// (restores minimized windows).
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return;

        // A synthetic Alt tap grants this process the right to set the foreground window.
        InputSender.Send(InputSender.Key(0x12, true), InputSender.Key(0x12, false));
        if (Native.SetForegroundWindow(hwnd)) return;

        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var current = Native.GetCurrentThreadId();
        if (fgThread != current)
        {
            Native.AttachThreadInput(current, fgThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.AttachThreadInput(current, fgThread, false);
        }
    }

    /// <summary>Asks a window to close as if the user clicked its close button.</summary>
    public static void Close(IntPtr hwnd) => Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The top-level window under a screen point.</summary>
    public static IntPtr TopLevelAt(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT(x, y));
        return hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, Native.GA_ROOT);
    }

    /// <summary>Process name (without .exe) of the foreground window, lower-cased.</summary>
    public static string ForegroundProcessName()
    {
        var path = GetProcessPath(GetProcessId(Native.GetForegroundWindow()));
        return Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    }

    public static Process? TryGetProcess(uint pid)
    {
        try { return Process.GetProcessById((int)pid); }
        catch { return null; }
    }
}
