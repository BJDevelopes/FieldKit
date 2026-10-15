// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Finds taskbars and the app button under a point with UI Automation (Windows 11:
/// Taskbar.TaskListButtonAutomationPeer under Taskbar.TaskbarFrame; Windows 10: buttons of
/// MSTaskListWClass). Call off the UI and hook threads: UIA calls can take tens of ms.
/// </summary>
internal static class TaskbarLocator
{
    private static readonly ConcurrentDictionary<Guid, string?> KnownFolders = new();
    private static readonly ConcurrentDictionary<IntPtr, WindowAppIdentity> Identities = new();

    /// <summary>Screen rectangles (px) of the primary and secondary taskbars.</summary>
    public static List<Rect> TaskbarRects()
    {
        var list = new List<Rect>();
        Native.EnumWindows((hwnd, _) =>
        {
            var cls = WindowHelper.GetClassName(hwnd);
            if (cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd") || !Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowRect(hwnd, out var r);
            if (r.Width > 0 && r.Height > 0) list.Add(new Rect(r.Left, r.Top, r.Width, r.Height));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The app button under a screen point, or null.</summary>
    public static TaskbarButtonInfo? ButtonAt(Point point)
    {
        try
        {
            var element = AutomationElement.FromPoint(point);
            var walker = TreeWalker.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 6; depth++)
            {
                var current = element.Current;
                if (current.ClassName == "Taskbar.TaskListButtonAutomationPeer" ||
                    (current.ControlType == ControlType.Button && IsWin10TaskList(walker.GetParent(element))))
                {
                    var r = current.BoundingRectangle;
                    return new TaskbarButtonInfo(TaskbarAppMatcher.ParseAutomationId(current.AutomationId), current.Name ?? string.Empty,
                        new Rect(r.X, r.Y, r.Width, r.Height));
                }
                if (current.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
                element = walker.GetParent(element);
            }
        }
        catch (Exception)
        {
            // Explorer restarting, element gone: no button.
        }
        return null;
    }

    private static bool IsWin10TaskList(AutomationElement? parent)
    {
        try
        {
            return parent?.Current.ClassName == "MSTaskListWClass";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every app window (all desktops unless asked) that a button stands for, front-most first.</summary>
    public static List<AppWindow> WindowsFor(TaskbarButtonInfo button, bool currentDesktopOnly)
    {
        var windows = WindowEnumerator.List(includeOtherDesktops: !currentDesktopOnly);
        foreach (var stale in Identities.Keys.Where(k => !Native.IsWindow(k)).ToList()) Identities.TryRemove(stale, out _);
        return windows.Where(w => TaskbarAppMatcher.Matches(button, Identity(w), KnownFolder)).ToList();
    }

    public static WindowAppIdentity Identity(AppWindow w) =>
        Identities.GetOrAdd(w.Handle, _ => new WindowAppIdentity(
            WindowAppId(w.Handle), ProcessAppId(w.ProcessId), w.ProcessPath, w.AppName, WindowHelper.GetClassName(w.Handle)));

    private static string? KnownFolder(Guid id) =>
        KnownFolders.GetOrAdd(id, static guid =>
        {
            if (WinApi.SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        });

    /// <summary>The explicit AppUserModelID a window carries (PKEY_AppUserModel_ID), if any.</summary>
    private static string? WindowAppId(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(WinApi.IPropertyStore).GUID;
            if (WinApi.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
            try
            {
                var key = WinApi.AppUserModelIdKey;
                if (store.GetValue(ref key, out var value) != 0) return null;
                try
                {
                    const ushort VT_LPWSTR = 31;
                    return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.pointer) : null;
                }
                finally
                {
                    WinApi.PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The AppUserModelID of a packaged (Store/MSIX) process, if any.</summary>
    private static string? ProcessAppId(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var length = 0;
            if (WinApi.GetApplicationUserModelId(handle, ref length, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || length <= 0) return null;
            var sb = new StringBuilder(length);
            return WinApi.GetApplicationUserModelId(handle, ref length, sb) == 0 ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
