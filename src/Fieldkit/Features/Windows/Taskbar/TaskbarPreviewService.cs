// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Taskbar Preview: resting the pointer on a running app's taskbar button shows larger live
/// previews of all its windows (every virtual desktop by default); click one to switch to it.
/// </summary>
public sealed class TaskbarPreviewService : FeatureService
{
    public const string EnabledKey = "dockPreview.enabled";
    public const string CurrentDesktopOnlyKey = "dockPreview.currentSpaceOnly";
    public const string OpenDelayKey = "dockPreview.openDelay";
    public const string QuitAppOnCloseKey = "dockPreview.quitAppOnClose";
    public const string PreviewSizeKey = "dockPreview.previewSize";
    public const string PreviewExcludedKey = "dockPreview.previewExcludedApps";
    public const int DefaultOpenDelay = 200;

    public static AppList PreviewExcludedApps { get; } = new(PreviewExcludedKey);

    public static int SanitizedOpenDelay(int ms) => Math.Clamp(ms, 200, 900);

    private TaskbarHoverTracker? _tracker;
    private TaskbarPreviewWindow? _window;
    private DispatcherTimer? _openTimer;
    private DispatcherTimer? _hideTimer;
    private TaskbarHover? _pending;

    public override string FeatureId => "dockPreview";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        _tracker = TaskbarHoverTracker.Acquire();
        _tracker.HoverChanged += OnHover;
        _tracker.MouseFilter += OnMouse;
        _tracker.KeepAlive = p => _window is { IsVisible: true } w && Inflate(w.ScreenRect, 12).Contains(p);
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SanitizedOpenDelay(Settings.GetInt(OpenDelayKey, DefaultOpenDelay))) };
        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            if (_pending is not null) Show(_pending);
        };
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hideTimer.Tick += (_, _) => HideIfAway();
    }

    protected override void OnStop()
    {
        _openTimer?.Stop();
        _hideTimer?.Stop();
        if (_tracker is not null)
        {
            _tracker.HoverChanged -= OnHover;
            _tracker.MouseFilter -= OnMouse;
            _tracker.KeepAlive = null;
            TaskbarHoverTracker.Release(_tracker);
        }
        _tracker = null;
        _window?.Close();
        _window = null;
        _pending = null;
    }

    private static Rect Inflate(Rect r, double by) => r.IsEmpty ? r : new Rect(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    // Hook thread: a click anywhere but the preview closes it (taskbar clicks included).
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.Action is not (MouseAction.LeftDown or MouseAction.RightDown)) return false;
        var p = new Point(e.X, e.Y);
        if (_window is not null && _window.ScreenRect.Contains(p)) return false;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pending = null;
            _openTimer?.Stop();
            _window?.Dismiss();
        });
        return false;
    }

    private void OnHover(TaskbarHover? hover)
    {
        if (!IsRunning) return;
        if (hover is null || hover.Windows.Count == 0)
        {
            _pending = null;
            _openTimer?.Stop();
            if (_window is { IsVisible: true }) _hideTimer?.Start();
            return;
        }
        _hideTimer?.Stop();
        if (_window is { IsVisible: true })
        {
            // Moving along the taskbar with the panel open switches at once.
            if (_window.Shown?.Button.AppId != hover.Button.AppId || _window.Shown?.Button.Bounds != hover.Button.Bounds) Show(hover);
            return;
        }
        _pending = hover;
        _openTimer!.Interval = TimeSpan.FromMilliseconds(SanitizedOpenDelay(Settings.GetInt(OpenDelayKey, DefaultOpenDelay)));
        _openTimer.Stop();
        _openTimer.Start();
    }

    private void HideIfAway()
    {
        _hideTimer?.Stop();
        if (_window is null || !_window.IsVisible) return;
        Native.GetCursorPos(out var c);
        var p = new Point(c.X, c.Y);
        if (Inflate(_window.ScreenRect, 12).Contains(p)) return;
        if (_tracker?.Current is { } current && current.Button.Bounds.Contains(p)) return;
        _window.Dismiss();
    }

    private void Show(TaskbarHover hover)
    {
        _window ??= CreateWindow();
        var fg = WindowHelper.GetProcessPath(WindowHelper.GetProcessId(Native.GetForegroundWindow()));
        _window.Show(hover,
            SwitcherLogic.PreviewScale(Settings.GetString(PreviewSizeKey, "normal")),
            Settings.GetBool(SwitcherService.MinimalPreviewsKey, false),
            !PreviewExcludedApps.Contains(fg),
            Settings.GetBool(QuitAppOnCloseKey, false) ? "Quit app" : "Close window");
    }

    private TaskbarPreviewWindow CreateWindow()
    {
        var window = new TaskbarPreviewWindow();
        window.WindowChosen += w =>
        {
            window.Dismiss();
            WindowActions.Activate(w.Handle);
        };
        window.CloseRequested += w =>
        {
            if (Settings.GetBool(QuitAppOnCloseKey, false))
            {
                WindowActions.QuitApp(w.ProcessId, w.Handle);
                window.Dismiss();
            }
            else
            {
                WindowActions.Close(w.Handle);
                window.RemoveWindow(w.Handle);
            }
        };
        window.MinimizeRequested += w =>
        {
            if (Native.IsIconic(w.Handle)) WindowActions.Restore(w.Handle);
            else WindowActions.Minimize(w.Handle);
            window.Dismiss();
        };
        window.MouseLeave += (_, _) => _hideTimer?.Start();
        window.MouseEnter += (_, _) => _hideTimer?.Stop();
        return window;
    }
}
