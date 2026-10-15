// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

public enum LayoutResult { Done, Restored, NoWindow, NoRestore, Failed }

/// <summary>
/// Applies layout actions to windows (shortcuts, the panel grid, Settings "try it" buttons,
/// edge snapping). Keeps a per-window history for Restore and the last placement for repeated
/// keys. UI thread only.
/// </summary>
public static class WindowLayoutEngine
{
    public const string WindowGapKey = "windowLayout.windowGap";
    public const string ScreenGapKey = "windowLayout.screenGap";
    public const string MarginKey = "windowLayout.marginPercent";
    public const string SideCycleKey = "windowLayout.sideRepeatCyclesThirds";
    public const string IgnoredAppsKey = "windowLayout.ignoredApps";

    private static readonly LayoutHistory History = new();
    private static readonly Dictionary<IntPtr, (LayoutAction Action, Rect Requested, Rect Actual)> Settled = [];

    public static AppList IgnoredApps { get; } = new(IgnoredAppsKey);

    public static LayoutResult? LastResult { get; private set; }

    /// <summary>Raised on the UI thread after every apply.</summary>
    public static event Action<LayoutResult>? ResultChanged;

    public static string ResultText(LayoutResult result) => result switch
    {
        LayoutResult.Done => "Window arranged.",
        LayoutResult.Restored => "Window restored.",
        LayoutResult.NoWindow => "No active window found.",
        LayoutResult.NoRestore => "No previous layout to restore.",
        _ => "Could not move this window.",
    };

    /// <summary>
    /// The window to act on: the foreground window, or when that is one of ours (Settings,
    /// panel), the front-most other app window.
    /// </summary>
    public static IntPtr TargetWindow()
    {
        var fg = WindowEnumerator.RootOwner(Native.GetForegroundWindow());
        if (fg != IntPtr.Zero && WindowHelper.GetProcessId(fg) != (uint)Environment.ProcessId && IsArrangeable(fg)) return fg;
        return WindowEnumerator.List(includeOtherDesktops: false).FirstOrDefault(w => !w.IsMinimized)?.Handle ?? IntPtr.Zero;
    }

    public static bool IsArrangeable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        var cls = WindowHelper.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return (WinApi.Style(hwnd) & Native.WS_CHILD) == 0;
    }

    public static bool IsIgnored(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IgnoredApps.Contains(WindowHelper.GetProcessPath(WindowEnumerator.ResolveProcessId(hwnd)));

    public static Rect Frame(IntPtr hwnd)
    {
        var r = WindowHelper.GetVisibleBounds(hwnd);
        return new Rect(r.Left, r.Top, r.Width, r.Height);
    }

    public static Native.RECT ToRect(Rect r) =>
        new((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));

    public static IReadOnlyList<Rect> WorkAreas() =>
        WindowHelper.GetMonitors().Select(m => new Rect(m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Width, m.WorkArea.Height)).ToList();

    public static Rect TargetRect(LayoutAction action, Rect current, Rect workArea) =>
        LayoutGeometry.RectFor(action, current, workArea,
            Settings.GetInt(WindowGapKey, 0), Settings.GetInt(ScreenGapKey, 0),
            Settings.GetDouble(MarginKey, LayoutGeometry.DefaultMarginPercent));

    /// <summary>Applies an action to <paramref name="hwnd"/> (default: <see cref="TargetWindow"/>).</summary>
    public static LayoutResult Apply(LayoutAction action, IntPtr hwnd = default)
    {
        var result = ApplyCore(action, hwnd == IntPtr.Zero ? TargetWindow() : hwnd);
        LastResult = result;
        ResultChanged?.Invoke(result);
        return result;
    }

    private static LayoutResult ApplyCore(LayoutAction action, IntPtr hwnd)
    {
        if (!IsArrangeable(hwnd)) return LayoutResult.NoWindow;
        try
        {
            History.RemoveStale(Native.IsWindow);
            foreach (var stale in Settled.Keys.Where(k => !Native.IsWindow(k)).ToList()) Settled.Remove(stale);
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            var current = Frame(hwnd);

            switch (action)
            {
                case LayoutAction.Restore:
                {
                    var previous = History.PopPrevious(hwnd, current);
                    if (previous is null) return LayoutResult.NoRestore;
                    WindowActions.Place(hwnd, ToRect(previous.Value));
                    Settled.Remove(hwnd);
                    return LayoutResult.Restored;
                }
                case LayoutAction.FullScreen:
                    // Windows has no system full screen for arbitrary windows; F11 is the
                    // convention browsers, Explorer, editors and media players share.
                    WindowActions.Activate(hwnd);
                    InputSender.ReleaseModifiers();
                    InputSender.Tap(0x7A);
                    return LayoutResult.Done;
            }

            var areas = WorkAreas();
            if (areas.Count == 0) return LayoutResult.Failed;
            var index = LayoutGeometry.DisplayIndexFor(current, areas);
            var source = areas[index];
            var cycle = Settings.GetBool(SideCycleKey, false);
            LayoutAction? previousAction = Settled.TryGetValue(hwnd, out var settled) &&
                                           (LayoutGeometry.IsClose(current, settled.Actual, LayoutGeometry.FrameTolerance) ||
                                            LayoutGeometry.IsClose(current, settled.Requested, LayoutGeometry.FrameTolerance))
                ? settled.Action
                : null;

            Rect target;
            LayoutAction applied;
            if (action is LayoutAction.NextDisplay or LayoutAction.PreviousDisplay)
            {
                var next = LayoutGeometry.AdjacentDisplayIndex(index, areas, action == LayoutAction.NextDisplay);
                if (next is null) return LayoutResult.Done;
                var zoomed = Native.IsZoomed(hwnd);
                target = zoomed ? areas[next.Value] : LayoutGeometry.RectForDisplay(current, source, areas[next.Value]);
                applied = action;
            }
            else if (LayoutGeometry.DisplayCrossing(action, previousAction, cycle) is { } crossing &&
                     LayoutGeometry.NeighbourIndex(index, areas, crossing.Direction) is { } neighbour)
            {
                applied = crossing.Action;
                target = TargetRect(applied, current, areas[neighbour]);
            }
            else
            {
                applied = LayoutGeometry.EffectiveAction(action, previousAction, cycle);
                target = TargetRect(applied, current, source);
            }

            if (!LayoutGeometry.IsClose(current, target, 1)) History.Record(hwnd, current);
            WindowActions.Place(hwnd, ToRect(target));
            Settled[hwnd] = (applied, target, Frame(hwnd));
            return LayoutResult.Done;
        }
        catch (Exception ex)
        {
            Log.Error($"Window layout {action} failed", ex);
            return LayoutResult.Failed;
        }
    }

    /// <summary>Places a window on an exact frame (edge snapping) keeping Restore working.</summary>
    public static void PlaceSnapped(IntPtr hwnd, LayoutAction action, Rect target, Rect frameBeforeDrag)
    {
        if (!IsArrangeable(hwnd)) return;
        History.Record(hwnd, frameBeforeDrag);
        WindowActions.Place(hwnd, ToRect(target));
        Settled[hwnd] = (action, target, Frame(hwnd));
    }

    /// <summary>Remembers a frame for Restore (e.g. before a drag gesture moves the window).</summary>
    public static void Remember(IntPtr hwnd, Rect frame) => History.Record(hwnd, frame);
}
