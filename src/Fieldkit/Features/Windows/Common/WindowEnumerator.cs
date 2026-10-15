// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>An app window the window features can show, switch to or arrange.</summary>
public sealed record AppWindow(
    IntPtr Handle,
    string Title,
    uint ProcessId,
    string ProcessPath,
    bool IsMinimized,
    bool OnOtherDesktop)
{
    /// <summary>"chrome.exe" — the app identity used by rules and lists.</summary>
    public string AppKey => AppNames.Key(ProcessPath);

    /// <summary>Friendly app name ("Google Chrome").</summary>
    public string AppName => ProcessPath.Length > 0 ? AppNames.DisplayName(ProcessPath) : Title;
}

/// <summary>Wraps the documented IVirtualDesktopManager (null-safe; everything degrades to "current desktop").</summary>
internal static class VirtualDesktops
{
    [ThreadStatic] private static WinApi.IVirtualDesktopManager? _manager;
    [ThreadStatic] private static bool _failed;

    private static WinApi.IVirtualDesktopManager? Manager
    {
        get
        {
            if (_manager is not null || _failed) return _manager;
            try
            {
                var type = Type.GetTypeFromCLSID(WinApi.VirtualDesktopManagerClsid);
                _manager = type is null ? null : (WinApi.IVirtualDesktopManager?)Activator.CreateInstance(type);
            }
            catch
            {
                _manager = null;
            }
            _failed = _manager is null;
            return _manager;
        }
    }

    /// <summary>True when the window sits on a virtual desktop other than the current one.</summary>
    public static bool IsOnOtherDesktop(IntPtr hwnd)
    {
        try
        {
            var m = Manager;
            if (m is null) return false;
            if (m.GetWindowDesktopId(hwnd, out var id) != 0 || id == Guid.Empty) return false;
            return m.IsWindowOnCurrentVirtualDesktop(hwnd, out var current) == 0 && !current;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Lists app windows like Alt+Tab does, optionally including windows parked on other
/// virtual desktops (which DWM cloaks). Read-only.
/// </summary>
public static class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "EdgeUiInputTopWndClass", "NotifyIconOverflowWindow",
    };

    /// <summary>App windows in Z-order (front-most first).</summary>
    public static List<AppWindow> List(bool includeOtherDesktops, bool includeOwnProcess = false)
    {
        var own = (uint)Environment.ProcessId;
        var result = new List<AppWindow>();
        var paths = new Dictionary<uint, string>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd, includeOtherDesktops, out var otherDesktop)) return true;
            var pid = ResolveProcessId(hwnd);
            if (!includeOwnProcess && pid == own) return true;
            if (!paths.TryGetValue(pid, out var path))
            {
                paths[pid] = path = WindowHelper.GetProcessPath(pid);
                AppNames.Remember(path);
            }
            result.Add(new AppWindow(hwnd, WindowHelper.GetTitle(hwnd), pid, path, Native.IsIconic(hwnd), otherDesktop));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>One window, if it still qualifies.</summary>
    public static AppWindow? Describe(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return null;
        var pid = ResolveProcessId(hwnd);
        var path = WindowHelper.GetProcessPath(pid);
        return new AppWindow(hwnd, WindowHelper.GetTitle(hwnd), pid, path, Native.IsIconic(hwnd),
            WindowHelper.IsCloaked(hwnd) && VirtualDesktops.IsOnOtherDesktop(hwnd));
    }

    /// <summary>Whether a top-level window is a user-facing app window.</summary>
    public static bool IsCandidate(IntPtr hwnd, bool includeOtherDesktops, out bool onOtherDesktop)
    {
        onOtherDesktop = false;
        if (!Native.IsWindowVisible(hwnd)) return false;
        var ex = WinApi.ExStyle(hwnd);
        var appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (Native.GetWindowTextLength(hwnd) == 0) return false;
        if (ExcludedClasses.Contains(WindowHelper.GetClassName(hwnd))) return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            // Shell-cloaked windows live on another virtual desktop; anything else cloaked
            // (suspended Store apps, hidden frames) is not really there.
            if (!includeOtherDesktops || (cloaked & WinApi.DWM_CLOAKED_SHELL) == 0) return false;
            if (!VirtualDesktops.IsOnOtherDesktop(hwnd)) return false;
            onOtherDesktop = true;
        }
        return true;
    }

    /// <summary>The process that owns a window, looking through the UWP frame host to the app inside.</summary>
    public static uint ResolveProcessId(IntPtr hwnd)
    {
        var pid = WindowHelper.GetProcessId(hwnd);
        if (WindowHelper.GetClassName(hwnd) != "ApplicationFrameWindow") return pid;
        var child = IntPtr.Zero;
        while ((child = Native.FindWindowEx(hwnd, child, "Windows.UI.Core.CoreWindow", null)) != IntPtr.Zero)
        {
            var inner = WindowHelper.GetProcessId(child);
            if (inner != pid && inner != 0) return inner;
        }
        return pid;
    }

    /// <summary>The top-level app window that owns <paramref name="hwnd"/> (its root owner when owned).</summary>
    public static IntPtr RootOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return hwnd;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
        return root == IntPtr.Zero ? hwnd : root;
    }

    /// <summary>File name of a path without throwing.</summary>
    internal static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); }
        catch { return path; }
    }
}
