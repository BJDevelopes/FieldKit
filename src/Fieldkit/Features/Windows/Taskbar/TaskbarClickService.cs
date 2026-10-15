// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Taskbar clicks: clicking the active app's taskbar button minimizes all its windows (click
/// again to bring them back), or cycles through its windows like Alt+`.
/// </summary>
public sealed class TaskbarClickService : FeatureService
{
    public const string MinimizeKey = "dockClick.minimize";
    public const string CycleKey = "dockClick.cycleWindows";

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private TaskbarHoverTracker? _tracker;
    private readonly ConcurrentDictionary<string, IntPtr[]> _minimizedByUs = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _swallowUp;
    private string? _lastAppId;
    private long _lastClick;

    public override string FeatureId => "dockClick";
    public override IReadOnlyList<string> EnableKeys => [MinimizeKey, CycleKey];
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        _tracker = TaskbarHoverTracker.Acquire();
        _tracker.MouseFilter += OnMouse;
    }

    protected override void OnStop()
    {
        if (_tracker is not null)
        {
            _tracker.MouseFilter -= OnMouse;
            TaskbarHoverTracker.Release(_tracker);
        }
        _tracker = null;
        _minimizedByUs.Clear();
        _swallowUp = false;
    }

    // Hook thread: only cheap window-state reads here; the work is posted to the UI thread.
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn) return false;
        if (e.Action == MouseAction.LeftUp && _swallowUp)
        {
            _swallowUp = false;
            return true;
        }
        if (e.Action != MouseAction.LeftDown || _tracker?.Current is not { } hover) return false;
        if (!hover.Button.Bounds.Contains(new Point(e.X, e.Y)) || hover.Windows.Count == 0) return false;

        var now = Environment.TickCount64;
        if (_lastAppId == hover.Button.AppId && now - _lastClick < GetDoubleClickTime())
        {
            // A double-click would toggle twice and look like the click bounced.
            _swallowUp = true;
            return true;
        }

        var fg = WindowEnumerator.RootOwner(Native.GetForegroundWindow());
        var current = hover.Windows.Where(w => !w.OnOtherDesktop && Native.IsWindow(w.Handle)).ToList();
        var unminimized = current.Where(w => !Native.IsIconic(w.Handle)).ToList();
        var minimized = current.Count - unminimized.Count;
        var action = TaskbarClickLogic.Action(
            appIsFrontmost: hover.Handles.Contains(fg),
            hasUnminimizedWindows: unminimized.Count > 0,
            hasMinimizedWindows: minimized > 0,
            hasFullScreenWindows: unminimized.Any(w => WindowActions.IsFullScreen(w.Handle)),
            hasModifiers: Shortcut.CurrentModifiers() != Modifiers.None,
            minimizeEnabled: Settings.GetBool(MinimizeKey, false),
            cycleEnabled: Settings.GetBool(CycleKey, false),
            cycleCandidateCount: unminimized.Count,
            ownsMinimize: _minimizedByUs.ContainsKey(hover.Button.AppId));
        if (action == TaskbarClickAction.PassThrough) return false;

        _lastAppId = hover.Button.AppId;
        _lastClick = now;
        _swallowUp = true;
        var appId = hover.Button.AppId;
        Application.Current?.Dispatcher.BeginInvoke(() => Run(action, appId, unminimized));
        return true;
    }

    private void Run(TaskbarClickAction action, string appId, List<AppWindow> unminimized)
    {
        switch (action)
        {
            case TaskbarClickAction.Minimize:
                foreach (var w in unminimized) WindowActions.Minimize(w.Handle);
                _minimizedByUs[appId] = unminimized.Select(w => w.Handle).ToArray();
                break;
            case TaskbarClickAction.Restore:
                if (_minimizedByUs.TryRemove(appId, out var handles))
                {
                    // Restore back to front so the most recent window ends on top.
                    foreach (var h in handles.Reverse().Where(Native.IsWindow)) WindowActions.Restore(h);
                    var first = handles.FirstOrDefault(Native.IsWindow);
                    if (first != IntPtr.Zero) WindowActions.Activate(first);
                }
                break;
            case TaskbarClickAction.CycleWindows:
                // Front to back: raising the back-most window rotates through them all.
                var z = WindowEnumerator.List(includeOtherDesktops: false).Select(w => w.Handle).ToList();
                var last = unminimized.Where(w => z.Contains(w.Handle)).OrderBy(w => z.IndexOf(w.Handle)).LastOrDefault();
                if (last is not null) WindowActions.Activate(last.Handle);
                break;
        }
    }
}
