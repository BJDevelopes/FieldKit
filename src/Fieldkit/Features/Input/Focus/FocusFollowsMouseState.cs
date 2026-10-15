// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Input.Focus;

/// <summary>Constants and small rules of focus follows mouse (upstream FocusFollowsMouseSupport).</summary>
public static class FocusFollowsMouseRules
{
    public const int DefaultDelayMs = 250;
    public const int MinDelayMs = 100;
    public const int MaxDelayMs = 3000;

    public static int SanitizedDelay(int ms) => Math.Clamp(ms, MinDelayMs, MaxDelayMs);

    /// <summary>Window classes hover never focuses: the taskbar, the desktop, menus, Start, Task View, tooltips.</summary>
    public static bool IsShellClass(string className) => className is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "#32768" or "#32770_tooltip"
        or "tooltips_class32" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
        or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame"
        or "ForegroundStaging" or "TaskListThumbnailWnd" or "Xaml_WindowedPopupClass" or "SysShadow"
        or "DropDown" or "ComboLBox";
}

/// <summary>An evaluation handed out by <see cref="FocusFollowsMouseState"/>.</summary>
public readonly record struct FocusEvaluation(int X, int Y, ulong Generation);

/// <summary>
/// The pointer-settle state machine (port of upstream FocusFollowsMouseState). Movement restarts
/// the delay; with a window id (no-raise + don't wait for stop) the delay counts time over that
/// window so moving inside it keeps a pending focus. Times are milliseconds.
/// </summary>
public sealed class FocusFollowsMouseState
{
    private enum Phase { Pending, Evaluating, Completed, Cancelled }

    private (int X, int Y)? _point;
    private long _movedAt;
    private long _lastMovementAt;
    private Phase _phase = Phase.Pending;
    private IntPtr? _window;
    private bool _movedDuringEvaluation;

    public ulong Generation { get; private set; }

    public bool HasPendingEvaluation => _point is not null && _phase == Phase.Pending;

    public void RecordMovement(int x, int y, long timeMs, IntPtr? window = null)
    {
        try
        {
            if (window is { } w && _window == w && _point is not null)
            {
                if (_phase == Phase.Evaluating) _movedDuringEvaluation = true;
                if (_phase != Phase.Cancelled) return;
            }
            _window = window;
            _movedAt = timeMs;
            Generation++;
            _phase = Phase.Pending;
            _movedDuringEvaluation = false;
        }
        finally
        {
            _point = (x, y);
            _lastMovementAt = timeMs;
        }
    }

    public void Reset()
    {
        _point = null;
        Generation++;
        _phase = Phase.Pending;
        _movedDuringEvaluation = false;
    }

    public FocusEvaluation? NextEvaluation(long timeMs, int delayMs)
    {
        if (_point is not { } p || !HasPendingEvaluation) return null;
        if (timeMs - _movedAt < FocusFollowsMouseRules.SanitizedDelay(delayMs)) return null;
        _phase = Phase.Evaluating;
        _movedDuringEvaluation = false;
        return new FocusEvaluation(p.X, p.Y, Generation);
    }

    /// <summary>A failed attempt waits for movement, or keeps movement that arrived meanwhile.</summary>
    public void FinishEvaluation(FocusEvaluation evaluation, bool succeeded)
    {
        if (!IsCurrent(evaluation)) return;
        _phase = succeeded ? Phase.Completed : Phase.Cancelled;
        if (!succeeded && _movedDuringEvaluation)
        {
            _movedAt = _lastMovementAt;
            Generation++;
            _phase = Phase.Pending;
        }
        _movedDuringEvaluation = false;
    }

    public bool IsCurrent(FocusEvaluation evaluation) => evaluation.Generation == Generation && _phase == Phase.Evaluating;
}
