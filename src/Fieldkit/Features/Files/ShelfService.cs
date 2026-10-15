// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>
/// Runs the shelf while it is enabled (upstream ShelfService): keeps the items (persisted to
/// <c>Shelf\shelf.json</c>), binds the shortcut, and while "Open by shaking" is on watches left-button
/// drags through the shared mouse hook for a shake. Window moves and resizes (seen through
/// EVENT_SYSTEM_MOVESIZESTART/END), drags that start over Fieldkit's own windows and drags that start
/// over an excepted app never summon it.
/// </summary>
public sealed class ShelfService : FeatureService
{
    public const string ShortcutDefault = "Ctrl+Alt+Shift+D";
    private static readonly TimeSpan SweepDelay = TimeSpan.FromMinutes(1);

    private readonly ShelfCollection _shelf = new();
    private readonly ShelfShakeDetector _detector = new();
    private ShelfWindow? _window;
    private IDisposable? _settingsObserver;
    private IDisposable? _hotkey;
    private IDisposable? _mouseHandler;
    private IntPtr _winEventHook;
    private Native.WinEventDelegate? _winEventProc;
    private DispatcherTimer? _sweepTimer;
    private bool _started;
    private int _shortcutTicket;

    // Input-hook thread state (written there; _moveSizeActive is also written on the UI thread).
    private volatile bool _leftDown;
    private volatile bool _moveSizeActive;
    private double _scale = 1;
    private int _gesture;

    // UI thread: where the current gesture started.
    private int _resolvedGesture = -1;
    private bool _resolvedExcluded;

    public override string FeatureId => "shelf";
    public override IReadOnlyList<string> EnableKeys => ["shelf.enabled"];
    public override bool EnabledByDefault => false;

    public ShelfCollection Shelf => _shelf;

    public bool IsShelfVisible => _window?.IsVisible == true;

    protected override void OnStart()
    {
        _shelf.Reset(ShelfStore.Load());
        _shelf.Changed += OnShelfChanged;
        _started = true;
        _settingsObserver = Settings.Observe(SyncInputs, "shelf.shortcutEnabled", "shelf.shakeToOpen");
        SyncInputs();
        _sweepTimer = new DispatcherTimer { Interval = SweepDelay };
        _sweepTimer.Tick += (_, _) => Sweep();
        _sweepTimer.Start();
    }

    protected override void OnStop()
    {
        _started = false;
        _shortcutTicket++;
        _settingsObserver?.Dispose();
        _settingsObserver = null;
        _hotkey?.Dispose();
        _hotkey = null;
        StopShakeWatch();
        _sweepTimer?.Stop();
        _sweepTimer = null;
        _shelf.Changed -= OnShelfChanged;
        ShelfStore.SaveNow(_shelf.Items);
        if (_window is not null)
        {
            _window.HidePanel();
            _window.Close();
            _window = null;
        }
    }

    // ------------------------------------------------------------ panel

    /// <summary>Shows the shelf under the pointer ("Open now", the shake).</summary>
    public void Summon()
    {
        if (!_started) return;
        _window ??= new ShelfWindow(_shelf);
        _window.ShowAtPointer();
    }

    public void Hide() => _window?.HidePanel();

    public void Toggle()
    {
        if (IsShelfVisible) Hide();
        else Summon();
    }

    /// <summary>
    /// The shortcut. With "Add the File Explorer selection" on and Explorer in front, the selected
    /// files come along (upstream handleShortcut); with nothing new selected it just toggles.
    /// </summary>
    public async void HandleShortcut()
    {
        try
        {
            var ticket = ++_shortcutTicket;
            var foreground = Native.GetForegroundWindow();
            if (!Settings.GetBool("shelf.shortcutAddsExplorerSelection", false)
                || !ExplorerShell.IsExplorerWindow(foreground, includeDesktop: false))
            {
                Toggle();
                return;
            }
            var location = await ExplorerShell.QueryAsync(foreground);
            if (ticket != _shortcutTicket || !_started) return;
            var shelved = _shelf.ShelvedPaths();
            var fresh = ShelfRules.Classify(new ShelfDropData(location?.Selection ?? []), shelved);
            if (fresh.Count == 0)
            {
                Toggle();
                return;
            }
            if (!_shelf.Add(fresh))
            {
                System.Media.SystemSounds.Beep.Play();
                Toggle();
                return;
            }
            Summon();
        }
        catch (Exception ex)
        {
            Log.Error("The shelf shortcut failed", ex);
        }
    }

    private void OnShelfChanged()
    {
        ShelfStore.SaveAsync(_shelf.Items);
        // Pictures that left the shelf are deleted a minute later: a destination may still be copying.
        _sweepTimer?.Stop();
        _sweepTimer?.Start();
    }

    private void Sweep()
    {
        _sweepTimer?.Stop();
        var snapshot = _shelf.Items.ToList();
        Task.Run(() => ShelfStore.SweepOrphans(snapshot, SweepDelay));
    }

    // ------------------------------------------------------------ inputs

    private void SyncInputs()
    {
        if (!_started) return;
        var wantShortcut = Settings.GetBool("shelf.shortcutEnabled", true);
        if (wantShortcut && _hotkey is null) _hotkey = Hotkeys.Bind("shelf.shortcut", ShortcutDefault, HandleShortcut);
        else if (!wantShortcut && _hotkey is not null)
        {
            _hotkey.Dispose();
            _hotkey = null;
        }

        var wantShake = Settings.GetBool("shelf.shakeToOpen", true);
        if (wantShake && _mouseHandler is null) StartShakeWatch();
        else if (!wantShake && _mouseHandler is not null) StopShakeWatch();
    }

    private void StartShakeWatch()
    {
        _winEventProc = OnWinEvent;
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_winEventHook == IntPtr.Zero) Log.Warn("The shelf could not watch window moves; a shaken window move may open it");
        _leftDown = false;
        _moveSizeActive = false;
        _mouseHandler = InputHooks.AddMouseHandler(OnMouse);
    }

    private void StopShakeWatch()
    {
        _mouseHandler?.Dispose();
        _mouseHandler = null;
        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;
        _winEventProc = null;
        _leftDown = false;
        _moveSizeActive = false;
    }

    // UI thread (out-of-context WinEvents arrive through this thread's message loop).
    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART) _moveSizeActive = true;
        else if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND) _moveSizeActive = false;
    }

    // Input-hook thread: bookkeeping only; every decision that needs a window is made on the UI thread.
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn) return false;
        switch (e.Action)
        {
            case MouseAction.LeftDown:
            {
                _leftDown = true;
                _moveSizeActive = false;
                _detector.Reset();
                _scale = Math.Max(0.5, WindowHelper.GetScaleAt(e.X, e.Y));
                var id = Interlocked.Increment(ref _gesture);
                int x = e.X, y = e.Y;
                Ui.OnUi(() => ResolveGestureSource(id, x, y));
                break;
            }
            case MouseAction.LeftUp:
                _leftDown = false;
                _detector.Reset();
                break;
            case MouseAction.Move when _leftDown && !_moveSizeActive:
                var seconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (_detector.AddSample(seconds, e.X / _scale))
                {
                    var gesture = Volatile.Read(ref _gesture);
                    Ui.OnUi(() => OnShake(gesture));
                }
                break;
        }
        return false;
    }

    private void ResolveGestureSource(int id, int x, int y)
    {
        _resolvedGesture = id;
        _resolvedExcluded = false;
        var root = WindowHelper.TopLevelAt(x, y);
        if (root == IntPtr.Zero) return;
        // Dragging taskbar buttons reorders them (upstream #2212), and in full-screen apps (games,
        // drawing, video) fast back-and-forth drags are ordinary input, not a request for the shelf.
        if (WindowHelper.GetClassName(root) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ||
            UI.TaskbarDock.ForegroundIsFullScreen(Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST)))
        {
            _resolvedExcluded = true;
            return;
        }
        var pid = WindowHelper.GetProcessId(root);
        // Upstream: drags from the app's own windows never reach the global monitor.
        if (pid == (uint)Environment.ProcessId)
        {
            _resolvedExcluded = true;
            return;
        }
        var exclusions = ShelfRules.NormalizeExclusions(Settings.Get<List<string>>("shelf.automaticExclusions", []));
        if (exclusions.Count == 0) return;
        _resolvedExcluded = !ShelfRules.AllowsAutomaticOpen(WindowHelper.GetProcessPath(pid), exclusions);
    }

    private void OnShake(int gesture)
    {
        if (!_started || _moveSizeActive || !_leftDown) return;
        if (_resolvedGesture == gesture && _resolvedExcluded) return;
        Summon();
    }
}
