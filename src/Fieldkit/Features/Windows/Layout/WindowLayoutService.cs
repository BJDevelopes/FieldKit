// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Window layout background behaviour: global shortcuts for every placement, modifier-drag
/// move/resize anywhere in a window (mouse hook) and custom edge-snap areas (WinEvent
/// move/size tracking). Each part runs only while its switch is on.
/// </summary>
public sealed class WindowLayoutService : FeatureService
{
    public const string ShortcutsEnabledKey = "windowLayout.shortcutsEnabled";
    public const string GestureEnabledKey = "windowLayout.gestureEnabled";
    public const string GestureModifiersKey = "windowLayout.gestureModifiers";
    public const string GestureRaiseKey = "windowLayout.gestureRaiseWindow";
    public const string EdgeSnapEnabledKey = "windowLayout.edgeSnapEnabled";
    public const string EdgeSnapDisabledZonesKey = "windowLayout.edgeSnapDisabledZones";
    public const string EdgeSnapZoneActionsKey = "windowLayout.edgeSnapZoneActions";

    private readonly Dictionary<LayoutAction, IDisposable> _hotkeys = [];
    private readonly List<IDisposable> _subscriptions = [];
    private IDisposable? _mouseHook;
    private DragGesture? _gesture;
    private EdgeSnapTracker? _edgeSnap;

    public override string FeatureId => "windowLayout";

    public override IReadOnlyList<string> EnableKeys => [ShortcutsEnabledKey, GestureEnabledKey, EdgeSnapEnabledKey];

    public override bool ShouldRun() =>
        Settings.GetBool(ShortcutsEnabledKey, true) || Settings.GetBool(GestureEnabledKey, false) ||
        Settings.GetBool(EdgeSnapEnabledKey, false);

    protected override void OnStart()
    {
        Sync();
        _subscriptions.Add(Settings.Observe(Sync, ShortcutsEnabledKey, GestureEnabledKey, EdgeSnapEnabledKey));
        _subscriptions.Add(Settings.Observe(() =>
        {
            StopGesture();
            Sync();
        }, GestureModifiersKey, GestureRaiseKey));
        _subscriptions.Add(Settings.ObservePrefix("windowLayout.shortcut.", () =>
        {
            if (Settings.GetBool(ShortcutsEnabledKey, true)) RebindShortcuts();
        }));
    }

    protected override void OnStop()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        UnbindShortcuts();
        StopGesture();
        _edgeSnap?.Dispose();
        _edgeSnap = null;
    }

    private void Sync()
    {
        if (Settings.GetBool(ShortcutsEnabledKey, true)) RebindShortcuts();
        else UnbindShortcuts();

        if (Settings.GetBool(GestureEnabledKey, false))
        {
            if (_mouseHook is null)
            {
                // A modifier-drag move snaps like a title-bar drag when edge snapping is on (upstream PR #3057).
                _gesture = new DragGesture(
                    hwnd => Ui.OnUi(() => _edgeSnap?.Begin(hwnd)),
                    hwnd => Ui.OnUi(() => _edgeSnap?.End(hwnd)));
                _mouseHook = InputHooks.AddMouseHandler(_gesture.Handle, priority: 40);
            }
        }
        else
        {
            StopGesture();
        }

        if (Settings.GetBool(EdgeSnapEnabledKey, false)) _edgeSnap ??= new EdgeSnapTracker();
        else
        {
            _edgeSnap?.Dispose();
            _edgeSnap = null;
        }
    }

    private void StopGesture()
    {
        _mouseHook?.Dispose();
        _mouseHook = null;
        _gesture = null;
    }

    private void RebindShortcuts()
    {
        UnbindShortcuts();
        foreach (var action in LayoutActions.All)
        {
            var shortcut = Shortcut.FromSetting(action.ShortcutKey(), action.DefaultShortcut());
            if (shortcut.IsEmpty) continue;
            var a = action;
            var registration = Hotkeys.Register(shortcut, () => OnShortcut(a, shortcut));
            if (registration is not null) _hotkeys[action] = registration;
        }
    }

    private void UnbindShortcuts()
    {
        foreach (var h in _hotkeys.Values) h.Dispose();
        _hotkeys.Clear();
    }

    private void OnShortcut(LayoutAction action, Shortcut shortcut)
    {
        var fg = WindowEnumerator.RootOwner(Native.GetForegroundWindow());
        if (WindowLayoutEngine.IsIgnored(fg))
        {
            PassThrough(action, shortcut);
            return;
        }
        WindowLayoutEngine.Apply(action, fg);
    }

    /// <summary>In an ignored app the combination belongs to the app: re-send it without our hotkey.</summary>
    private void PassThrough(LayoutAction action, Shortcut shortcut)
    {
        if (_hotkeys.Remove(action, out var registration)) registration.Dispose();
        InputSender.Send(InputSender.Key(shortcut.Key, true), InputSender.Key(shortcut.Key, false));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsRunning || !Settings.GetBool(ShortcutsEnabledKey, true) || _hotkeys.ContainsKey(action)) return;
            var again = Hotkeys.Register(shortcut, () => OnShortcut(action, shortcut));
            if (again is not null) _hotkeys[action] = again;
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ drag gesture

    /// <summary>Modifier-drag move/resize, run on the hook thread. Never blocks: windows move with async SetWindowPos.</summary>
    private sealed class DragGesture(Action<IntPtr> moveStarted, Action<IntPtr> moveEnded)
    {
        private GestureState _state = GestureState.Idle;
        private bool _right;
        private bool _resize;
        private Point _start;
        private Rect _original;
        private ResizeEdges _edges;
        private IntPtr _hwnd;
        // Read once per start; the service restarts the gesture when these settings change.
        private readonly Modifiers _modifiers = DragGestureMath.ParseModifiers(Settings.GetString(GestureModifiersKey, "ctrl+win"));
        private readonly bool _raise = Settings.GetBool(GestureRaiseKey, false);

        public bool Handle(ref MouseEvent e)
        {
            if (e.IsOwn) return false;
            var point = new Point(e.X, e.Y);
            switch (_state)
            {
                case GestureState.Idle:
                    return TryArm(e, point);
                case GestureState.Pending:
                    return Pending(e, point);
                default:
                    return Active(e, point);
            }
        }

        private bool TryArm(MouseEvent e, Point point)
        {
            if (e.Action is not (MouseAction.LeftDown or MouseAction.RightDown)) return false;
            var held = Shortcut.CurrentModifiers();
            var withoutShift = held & ~Modifiers.Shift;
            if (withoutShift != _modifiers) return false;
            var right = e.Action == MouseAction.RightDown;
            var resize = right || held.HasFlag(Modifiers.Shift);
            var hwnd = WindowHelper.TopLevelAt(e.X, e.Y);
            if (!Eligible(hwnd)) return false;
            _hwnd = hwnd;
            _right = right;
            _resize = resize;
            _start = point;
            Native.GetWindowRect(hwnd, out var r);
            _original = new Rect(r.Left, r.Top, r.Width, r.Height);
            _edges = DragGestureMath.EdgesAt(point, _original);
            _state = GestureState.Pending;
            // Alt or Win released after a swallowed click would open the menu bar or Start.
            if (_modifiers.HasFlag(Modifiers.Win) || _modifiers.HasFlag(Modifiers.Alt)) InputSender.Tap(0xE8);
            return true;
        }

        private static bool Eligible(IntPtr hwnd)
        {
            if (!WindowLayoutEngine.IsArrangeable(hwnd)) return false;
            if (WindowHelper.GetProcessId(hwnd) == (uint)Environment.ProcessId) return false;
            if (WindowActions.IsFullScreen(hwnd)) return false;
            return !WindowLayoutEngine.IsIgnored(WindowEnumerator.RootOwner(Native.GetForegroundWindow())) &&
                   !WindowLayoutEngine.IsIgnored(hwnd);
        }

        private bool IsTrackedUp(MouseEvent e) => e.Action == (_right ? MouseAction.RightUp : MouseAction.LeftUp);
        private bool IsTrackedDown(MouseEvent e) => e.Action == (_right ? MouseAction.RightDown : MouseAction.LeftDown);

        private bool Pending(MouseEvent e, Point point)
        {
            GestureInput input = e.Action switch
            {
                MouseAction.Move => DragGestureMath.ExceedsSlop(_start, point) ? GestureInput.DragPastSlop : GestureInput.DragWithinSlop,
                _ when IsTrackedUp(e) => GestureInput.TrackedUp,
                _ when IsTrackedDown(e) => GestureInput.SameButtonDown,
                MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown or MouseAction.XDown => GestureInput.OtherButtonDown,
                _ => GestureInput.OtherEvent,
            };
            switch (DragGestureMath.Decide(GestureState.Pending, input))
            {
                case GestureDecision.Promote:
                    Promote();
                    Apply(point);
                    return false;
                case GestureDecision.Hold:
                    return false;
                case GestureDecision.ReplayClick:
                    _state = GestureState.Idle;
                    var (down, up) = Buttons();
                    InputSender.Send(InputSender.MouseButton(down), InputSender.MouseButton(up));
                    return true;
                case GestureDecision.Restart:
                    _state = GestureState.Idle;
                    return TryArm(e, point);
                default:
                    _state = GestureState.Idle;
                    InputSender.Send(InputSender.MouseButton(Buttons().Down));
                    return false;
            }
        }

        private (uint Down, uint Up) Buttons() => _right
            ? (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP)
            : (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP);

        private void Promote()
        {
            _state = GestureState.Active;
            var hwnd = _hwnd;
            var before = WindowLayoutEngine.Frame(hwnd);
            if (Native.IsZoomed(hwnd))
            {
                // A maximized window comes back at its normal size under the pointer.
                var placement = new WinApi.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
                WinApi.GetWindowPlacement(hwnd, ref placement);
                var normal = placement.rcNormalPosition;
                var fraction = (_start.X - _original.Left) / Math.Max(1, _original.Width);
                _original = new Rect(_start.X - normal.Width * fraction, _original.Top, normal.Width, normal.Height);
                Native.ShowWindowAsync(hwnd, Native.SW_RESTORE);
                _edges = DragGestureMath.EdgesAt(_start, _original);
            }
            var raise = _raise;
            Ui.OnUi(() =>
            {
                WindowLayoutEngine.Remember(hwnd, before);
                if (raise) WindowActions.Activate(hwnd);
            });
            if (!_resize) moveStarted(hwnd);
        }

        private bool Active(MouseEvent e, Point point)
        {
            if (e.Action == MouseAction.Move)
            {
                Apply(point);
                return false;
            }
            if (IsTrackedUp(e))
            {
                Apply(point);
                _state = GestureState.Idle;
                if (!_resize) moveEnded(_hwnd);
                return true;
            }
            return false;
        }

        private void Apply(Point point)
        {
            var flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | WinApi.SWP_ASYNCWINDOWPOS;
            if (_resize)
            {
                var r = DragGestureMath.Resized(_original, _start, point, _edges);
                Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, flags);
            }
            else
            {
                var r = DragGestureMath.Moved(_original, _start, point);
                Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)r.X, (int)r.Y, 0, 0, flags | Native.SWP_NOSIZE);
            }
        }
    }

    // ------------------------------------------------------------------ edge snapping

    /// <summary>
    /// Follows title-bar drags (EVENT_SYSTEM_MOVESIZESTART/END) and shows where the window will
    /// land when the pointer reaches an enabled edge area; places it on release.
    /// </summary>
    private sealed class EdgeSnapTracker : IDisposable
    {
        private readonly Native.WinEventDelegate _callback;
        private readonly IntPtr _hook;
        private readonly DispatcherTimer _timer;
        private SnapPreviewWindow? _preview;
        private IntPtr _hwnd;
        private Rect _initial;
        private Point _pointerStart;
        private Point _lastPoint;
        private long _lastTicks;
        private Vector _velocity;
        private bool _resizing;
        private SnapTarget? _target;

        public EdgeSnapTracker()
        {
            _callback = OnEvent;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero,
                _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Track();
        }

        private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW) return;
            if (type == Native.EVENT_SYSTEM_MOVESIZESTART) Begin(hwnd);
            else if (type == Native.EVENT_SYSTEM_MOVESIZEEND) End(hwnd);
        }

        public void Begin(IntPtr hwnd)
        {
            if (!WindowLayoutEngine.IsArrangeable(hwnd) || WindowLayoutEngine.IsIgnored(hwnd)) return;
            _hwnd = hwnd;
            _initial = WindowLayoutEngine.Frame(hwnd);
            Native.GetCursorPos(out var p);
            _pointerStart = _lastPoint = new Point(p.X, p.Y);
            _lastTicks = Stopwatch.GetTimestamp();
            _velocity = default;
            _resizing = false;
            _target = null;
            _timer.Start();
        }

        private void Track()
        {
            if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
            {
                Reset();
                return;
            }
            Native.GetCursorPos(out var p);
            var point = new Point(p.X, p.Y);
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Max(1e-3, (now - _lastTicks) / (double)Stopwatch.Frequency);
            _velocity = (point - _lastPoint) / seconds;
            _lastPoint = point;
            _lastTicks = now;

            if (!_resizing && EdgeSnap.IsResize(_initial, WindowLayoutEngine.Frame(_hwnd))) _resizing = true;
            if (_resizing || point == _pointerStart)
            {
                _target = null;
                _preview?.Hide();
                return;
            }

            var screens = WindowHelper.GetMonitors()
                .Select(m => new SnapScreen(new Rect(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height),
                    new Rect(m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Width, m.WorkArea.Height)))
                .ToList();
            var enabled = Enum.GetValues<EdgeZone>().ToHashSet();
            enabled.ExceptWith(EdgeSnap.DisabledZones(Settings.GetString(EdgeSnapDisabledZonesKey, string.Empty)));
            var layout = EdgeSnapLayout.Parse(Settings.GetString(EdgeSnapZoneActionsKey, string.Empty));
            _target = EdgeSnap.Target(point, screens, _velocity, enabled, layout,
                (action, work) => WindowLayoutEngine.TargetRect(action, work, work));
            if (_target is { } t)
            {
                _preview ??= new SnapPreviewWindow();
                _preview.ShowAt(t.Frame);
            }
            else
            {
                _preview?.Hide();
            }
        }

        public void End(IntPtr hwnd)
        {
            if (hwnd != _hwnd) return;
            Track();
            var target = _target;
            var initial = _initial;
            Reset();
            if (target is not { } t) return;
            // Let Windows finish its own move (and Snap, if it is on) before placing.
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            delay.Tick += (_, _) =>
            {
                delay.Stop();
                WindowLayoutEngine.PlaceSnapped(hwnd, t.Action, t.Frame, initial);
            };
            delay.Start();
        }

        private void Reset()
        {
            _timer.Stop();
            _hwnd = IntPtr.Zero;
            _target = null;
            _preview?.Hide();
        }

        public void Dispose()
        {
            Reset();
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
            _preview?.Close();
            _preview = null;
            GC.KeepAlive(_callback);
        }
    }

    /// <summary>Whether Windows Snap ("Snap windows" in Settings › System › Multitasking) is on.</summary>
    public static bool IsWindowsSnapOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("WindowArrangementActive") is not string v || v != "0";
        }
        catch
        {
            return true;
        }
    }
}
