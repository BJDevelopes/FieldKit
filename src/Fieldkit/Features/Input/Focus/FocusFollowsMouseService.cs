// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Focus;

/// <summary>
/// Focuses the window under the pointer once it has rested for the hover delay (upstream
/// FocusFollowsMouseService). Our own implementation rather than Windows' "activate a window by
/// hovering" setting, so the taskbar, desktop, menus, Start / Task View, our own windows and the
/// apps on the exception list are never focused, and nothing changes while a button or a
/// modifier is held. Without "Bring the window to the front" the previous Z-order is put back
/// right after activation.
///
/// The mouse hook only stores the latest position; all window work runs on a 50 ms UI timer
/// that exists only while a movement is waiting to settle.
/// </summary>
internal sealed class FocusFollowsMouseService : FeatureService
{
    public override string FeatureId => "focusFollowsMouse";
    public override IReadOnlyList<string> EnableKeys => ["focusFollowsMouse.enabled"];
    public override bool EnabledByDefault => false;

    private readonly FocusFollowsMouseState _state = new();
    private readonly PointerTargetResolver _resolver = new();
    private IDisposable? _hook;
    private DispatcherTimer? _timer;
    private int _timerRequested;
    private long _sample;      // packed x/y of the latest move
    private long _sampleTime;  // ms
    private long _sampleSeq;
    private long _seenSeq;
    private HashSet<string> _exceptions = [];
    private IDisposable? _exceptionsObserver;

    protected override void OnStart()
    {
        _exceptions = AppExceptions.Snapshot(FeatureId);
        _exceptionsObserver = Settings.Observe(() => _exceptions = AppExceptions.Snapshot(FeatureId), AppExceptions.Key(FeatureId));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Ui_Tick();
        _hook = InputHooks.AddMouseHandler(OnMouse, priority: 200);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        _timer?.Stop();
        _timer = null;
        _exceptionsObserver?.Dispose();
        _exceptionsObserver = null;
        _state.Reset();
        Interlocked.Exchange(ref _timerRequested, 0);
    }

    // Hook thread: store the sample, wake the UI timer if it sleeps. Never swallows.
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.Action != MouseAction.Move || e.IsOwn) return false;
        Volatile.Write(ref _sample, ((long)e.X << 32) | (uint)e.Y);
        Volatile.Write(ref _sampleTime, Environment.TickCount64);
        Interlocked.Increment(ref _sampleSeq);
        if (Interlocked.CompareExchange(ref _timerRequested, 1, 0) == 0)
            Ui.OnUi(() => { if (IsRunning) _timer?.Start(); });
        return false;
    }

    private void Ui_Tick()
    {
        if (!IsRunning || _timer is null) return;
        var seq = Interlocked.Read(ref _sampleSeq);
        if (seq != _seenSeq)
        {
            _seenSeq = seq;
            var packed = Volatile.Read(ref _sample);
            int x = (int)(packed >> 32), y = (int)(uint)packed;
            var raise = Settings.GetBool("focusFollowsMouse.raise", true);
            var waitsForStop = raise || Settings.GetBool("focusFollowsMouse.waitForStop", true);
            _state.RecordMovement(x, y, Volatile.Read(ref _sampleTime), waitsForStop ? null : WindowHelper.TopLevelAt(x, y));
        }

        if (NothingHeld())
        {
            var delay = Settings.GetInt("focusFollowsMouse.delay", FocusFollowsMouseRules.DefaultDelayMs);
            if (_state.NextEvaluation(Environment.TickCount64, delay) is { } evaluation)
                _state.FinishEvaluation(evaluation, TryFocus(evaluation));
        }

        if (_state.HasPendingEvaluation) return;
        _timer.Stop();
        Interlocked.Exchange(ref _timerRequested, 0);
        // A move that landed between the read above and the flag reset must not be lost.
        if (Interlocked.Read(ref _sampleSeq) != _seenSeq && Interlocked.CompareExchange(ref _timerRequested, 1, 0) == 0)
            _timer.Start();
    }

    private static bool NothingHeld()
    {
        foreach (var vk in HeldKeys)
        {
            if (InputHooks.IsKeyDown(vk)) return false;
        }
        return true;
    }

    private static readonly int[] HeldKeys =
    [
        InputNative.VK_LBUTTON, InputNative.VK_RBUTTON, InputNative.VK_MBUTTON, InputNative.VK_XBUTTON1, InputNative.VK_XBUTTON2,
        InputNative.VK_SHIFT, InputNative.VK_CONTROL, InputNative.VK_MENU, InputNative.VK_LWIN, InputNative.VK_RWIN,
    ];

    /// <returns>True when nothing more needs doing for this rest (focused, or deliberately left alone).</returns>
    private bool TryFocus(FocusEvaluation evaluation)
    {
        var root = WindowHelper.TopLevelAt(evaluation.X, evaluation.Y);
        if (root == IntPtr.Zero || !IsFocusable(root)) return false;
        if (AppExceptions.Matches(_exceptions, _resolver.NameOf(root))) return true;
        if (WindowHelper.GetProcessId(root) == (uint)Environment.ProcessId) return false;

        var foreground = Native.GetForegroundWindow();
        if (foreground == root) return true;
        // "Only between displays" (upstream #2142): hovering within the display that has focus changes nothing.
        if (Settings.GetBool("focusFollowsMouse.betweenDisplaysOnly") && foreground != IntPtr.Zero &&
            Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST) ==
            Native.MonitorFromPoint(new Native.POINT(evaluation.X, evaluation.Y), Native.MONITOR_DEFAULTTONEAREST)) return true;
        // A modal dialog keeps focus: Windows would hand it straight back.
        if (!InputNative.IsWindowEnabled(root)) return true;
        if (ForegroundIsBusy()) return false;

        var raise = Settings.GetBool("focusFollowsMouse.raise", true);
        var above = raise ? IntPtr.Zero : WindowAbove(root);
        Activate(root);
        if (!raise && above != IntPtr.Zero)
        {
            RestoreBehind(root, above);
            // Some apps raise themselves while handling activation; put them back once more.
            var again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                if (Native.GetForegroundWindow() == root) RestoreBehind(root, above);
            };
            again.Start();
        }
        return true;
    }

    private static bool IsFocusable(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || WindowHelper.IsCloaked(hwnd)) return false;
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return false;
        return !FocusFollowsMouseRules.IsShellClass(WindowHelper.GetClassName(hwnd));
    }

    /// <summary>A menu, a drag or a capture in the foreground app must not lose focus mid-gesture.</summary>
    private static bool ForegroundIsBusy()
    {
        var info = new InputNative.GUITHREADINFO { cbSize = Marshal.SizeOf<InputNative.GUITHREADINFO>() };
        if (!InputNative.GetGUIThreadInfo(0, ref info)) return false;
        return (info.flags & (InputNative.GUI_INMENUMODE | InputNative.GUI_POPUPMENUMODE | InputNative.GUI_INMOVESIZE)) != 0
               || info.hwndCapture != IntPtr.Zero;
    }

    /// <summary>The nearest visible, non-topmost window above <paramref name="hwnd"/> in Z-order.</summary>
    private static IntPtr WindowAbove(IntPtr hwnd)
    {
        var current = Native.GetWindow(hwnd, InputNative.GW_HWNDPREV);
        for (var guard = 0; current != IntPtr.Zero && guard < 512; guard++)
        {
            var ex = Native.GetWindowLongPtr(current, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOPMOST) != 0) return IntPtr.Zero;
            if (Native.IsWindowVisible(current) && !WindowHelper.IsCloaked(current)) return current;
            current = Native.GetWindow(current, InputNative.GW_HWNDPREV);
        }
        return IntPtr.Zero;
    }

    private static void RestoreBehind(IntPtr hwnd, IntPtr above) =>
        Native.SetWindowPos(hwnd, above, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

    private static void Activate(IntPtr hwnd)
    {
        if (Native.SetForegroundWindow(hwnd)) return;
        // A zero-length pointer move makes this process the last to receive input, which
        // Windows requires before it lets SetForegroundWindow through. Harmless to every app.
        InputSender.Send(InputSender.MouseButton(Native.MOUSEEVENTF_MOVE));
        if (Native.SetForegroundWindow(hwnd)) return;
        var fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var current = Native.GetCurrentThreadId();
        if (fgThread == 0 || fgThread == current) return;
        Native.AttachThreadInput(current, fgThread, true);
        Native.SetForegroundWindow(hwnd);
        Native.AttachThreadInput(current, fgThread, false);
    }
}
