// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// "Maximize windows": when a window gets maximized (maximize button, title-bar double-click,
/// Win+Up) it instead fills the work area as a normal window, so it stays movable and
/// resizable and keeps its rounded frame. Maximizing a filled window again puts it back.
/// Optionally only while Shift is held. Excluded apps keep the Windows behaviour.
/// </summary>
public sealed class WindowMaximizerService : FeatureService
{
    public const string EnabledKey = "windowMaximizer.enabled";
    public const string ModeKey = "windowMaximizer.mode";
    public const string ExcludedAppsKey = "windowMaximizer.excludedApps";

    public static AppList ExcludedApps { get; } = new(ExcludedAppsKey);

    private Native.WinEventDelegate? _callback;
    private IntPtr _hook;
    private readonly HashSet<IntPtr> _zoomed = [];
    private readonly Dictionary<IntPtr, (Rect Filled, Rect Normal)> _filled = [];
    private readonly Dictionary<IntPtr, long> _recent = [];

    public override string FeatureId => "windowMaximizer";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        _callback = OnEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        // Windows that are already maximized keep that state until the user maximizes again.
        foreach (var w in WindowEnumerator.List(includeOtherDesktops: true))
            if (Native.IsZoomed(w.Handle)) _zoomed.Add(w.Handle);
    }

    protected override void OnStop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
        _zoomed.Clear();
        _filled.Clear();
        _recent.Clear();
    }

    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (!Native.IsZoomed(hwnd))
        {
            _zoomed.Remove(hwnd);
            return;
        }
        if (!_zoomed.Add(hwnd)) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
        if (Settings.GetString(ModeKey, "always") == "shift" && !InputHooks.IsKeyDown(0x10)) return;
        var now = Environment.TickCount64;
        if (_recent.TryGetValue(hwnd, out var last) && now - last < 1000) return; // an app re-maximizing itself
        _recent[hwnd] = now;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Convert(hwnd));
    }

    private void Convert(IntPtr hwnd)
    {
        if (!IsRunning || !Native.IsWindow(hwnd) || !Native.IsZoomed(hwnd)) return;
        if (!WindowActions.IsResizable(hwnd) || !WindowEnumerator.IsCandidate(hwnd, false, out _)) return;
        if (ExcludedApps.Contains(WindowHelper.GetProcessPath(WindowEnumerator.ResolveProcessId(hwnd)))) return;

        var work = WindowHelper.GetWorkArea(hwnd);

        Native.ShowWindow(hwnd, Native.SW_RESTORE);
        _zoomed.Remove(hwnd);
        var normal = WindowLayoutEngine.Frame(hwnd);

        // Maximizing a window we filled toggles it back to where it was.
        if (_filled.TryGetValue(hwnd, out var record) && LayoutGeometry.IsClose(record.Filled, normal, LayoutGeometry.FrameTolerance))
        {
            _filled.Remove(hwnd);
            WindowActions.Place(hwnd, WindowLayoutEngine.ToRect(record.Normal));
            return;
        }
        WindowLayoutEngine.Remember(hwnd, normal);
        WindowActions.Place(hwnd, work);
        _filled[hwnd] = (WindowLayoutEngine.Frame(hwnd), normal);
        foreach (var stale in _filled.Keys.Where(k => !Native.IsWindow(k)).ToList()) _filled.Remove(stale);
        foreach (var stale in _recent.Keys.Where(k => !Native.IsWindow(k)).ToList()) _recent.Remove(stale);
    }
}
