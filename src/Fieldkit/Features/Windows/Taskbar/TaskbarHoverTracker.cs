// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>The taskbar button under the pointer and the windows it stands for.</summary>
public sealed record TaskbarHover(TaskbarButtonInfo Button, IReadOnlyList<AppWindow> Windows)
{
    public HashSet<IntPtr> Handles { get; } = Windows.Select(w => w.Handle).ToHashSet();
}

/// <summary>
/// Shared by Taskbar Preview and Taskbar clicks: watches pointer moves (mouse hook, cheap
/// rectangle test only) and, while the pointer is over a taskbar, asks UI Automation on a
/// worker thread which app button it is on. Reference counted: runs only while a taskbar
/// feature is on.
/// </summary>
internal sealed class TaskbarHoverTracker
{
    private static TaskbarHoverTracker? _instance;
    private static int _users;

    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private IDisposable? _hook;
    private volatile bool _stopping;
    private Rect[] _taskbars = [];
    private long _taskbarsRefreshed;
    private Point _point;
    private bool _overTaskbar;
    private volatile TaskbarHover? _current;

    /// <summary>Raised on the UI thread when the hovered button changes (null = none).</summary>
    public event Action<TaskbarHover?>? HoverChanged;

    /// <summary>Extra mouse handling run on the hook thread before the hover test (clicks).</summary>
    public event MouseHandler? MouseFilter;

    /// <summary>Pointer position test supplied by the preview window (px rect), so leaving the taskbar for the preview keeps it.</summary>
    public Func<Point, bool>? KeepAlive { get; set; }

    /// <summary>The latest hover (read from any thread).</summary>
    public TaskbarHover? Current => _current;

    public bool OverTaskbar => _overTaskbar;

    private TaskbarHoverTracker()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "Fieldkit taskbar hover" };
        _worker.SetApartmentState(ApartmentState.MTA);
    }

    public static TaskbarHoverTracker Acquire()
    {
        if (_instance is null)
        {
            _instance = new TaskbarHoverTracker();
            _instance.Start();
        }
        _users++;
        return _instance;
    }

    public static void Release(TaskbarHoverTracker tracker)
    {
        if (tracker != _instance) return;
        if (--_users > 0) return;
        _instance.Stop();
        _instance = null;
        _users = 0;
    }

    private void Start()
    {
        RefreshTaskbars();
        _worker.Start();
        _hook = InputHooks.AddMouseHandler(OnMouse, priority: 60);
    }

    private void Stop()
    {
        _hook?.Dispose();
        _hook = null;
        _stopping = true;
        _signal.Set();
        HoverChanged = null;
        MouseFilter = null;
    }

    private void RefreshTaskbars()
    {
        try
        {
            _taskbars = TaskbarLocator.TaskbarRects().ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("Finding taskbars failed", ex);
        }
        _taskbarsRefreshed = Environment.TickCount64;
    }

    // Hook thread: only rectangle tests here.
    private bool OnMouse(ref MouseEvent e)
    {
        if (MouseFilter is { } filters)
        {
            foreach (var filter in filters.GetInvocationList().Cast<MouseHandler>())
                if (filter(ref e)) return true;
        }
        if (e.Action != MouseAction.Move) return false;
        var p = new Point(e.X, e.Y);
        var over = false;
        foreach (var r in _taskbars)
            if (r.Contains(p)) { over = true; break; }
        lock (_gate)
        {
            _point = p;
            var changed = over != _overTaskbar;
            _overTaskbar = over;
            if (over || changed) _signal.Set();
        }
        return false;
    }

    private void Work()
    {
        TaskbarButtonInfo? lastButton = null;
        while (!_stopping)
        {
            _signal.WaitOne(1000);
            if (_stopping) break;
            if (Environment.TickCount64 - _taskbarsRefreshed > 3000) RefreshTaskbars();
            Point p;
            bool over;
            lock (_gate)
            {
                p = _point;
                over = _overTaskbar;
            }
            TaskbarHover? hover = null;
            if (over)
            {
                var button = TaskbarLocator.ButtonAt(p);
                if (button is not null)
                {
                    if (lastButton is not null && button.AppId == lastButton.AppId && button.Bounds == lastButton.Bounds && _current is { } same)
                    {
                        // Same button: refresh its windows now and then, keep the rest.
                        hover = same;
                    }
                    else
                    {
                        try
                        {
                            hover = new TaskbarHover(button, TaskbarLocator.WindowsFor(button, CurrentDesktopOnly()));
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Matching taskbar windows failed", ex);
                        }
                    }
                }
                lastButton = button;
            }
            else
            {
                if (KeepAlive?.Invoke(p) == true && _current is not null) continue;
                lastButton = null;
            }
            if (!ReferenceEquals(hover, _current))
            {
                _current = hover;
                var h = hover;
                Application.Current?.Dispatcher.BeginInvoke(() => HoverChanged?.Invoke(h));
            }
            if (over) Thread.Sleep(40); // UIA at most ~25 times a second while moving over the taskbar
        }
        _signal.Dispose();
    }

    private static bool CurrentDesktopOnly() => Settings.GetBool(TaskbarPreviewService.CurrentDesktopOnlyKey, false);

    /// <summary>Forces a fresh window list for the hovered button (after closing/minimizing from the preview).</summary>
    public void Invalidate()
    {
        if (_stopping) return;
        _current = null;
        _signal.Set();
    }
}
