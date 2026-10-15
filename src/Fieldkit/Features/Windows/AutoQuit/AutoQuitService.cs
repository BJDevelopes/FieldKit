// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>Which apps "Quit on close" applies to.</summary>
public enum AutoQuitScope { AllExceptExceptions, OnlyListed }

/// <summary>The quit decision. Pure, for tests.</summary>
public static class AutoQuitLogic
{
    public static AutoQuitScope ParseScope(string? s) => s == "listed" ? AutoQuitScope.OnlyListed : AutoQuitScope.AllExceptExceptions;

    /// <summary>
    /// Whether an app whose windows just closed should be asked to quit: it must have had a
    /// window (apps that normally run without one are never quit), have none left, not be a
    /// Windows component or Fieldkit, and pass the scope (exceptions or the chosen list).
    /// </summary>
    public static bool ShouldQuit(string appKey, bool isSystem, bool hadWindow, int remainingWindows, AutoQuitScope scope,
        IReadOnlyCollection<string> exceptions, IReadOnlyCollection<string> listed)
    {
        if (!hadWindow || remainingWindows > 0 || isSystem || appKey.Length == 0) return false;
        return scope == AutoQuitScope.OnlyListed ? listed.Contains(appKey) : !exceptions.Contains(appKey);
    }
}

/// <summary>
/// Quit on close: follows app windows with a WinEvent hook (show, hide, destroy). When an
/// app's last window closes or hides (e.g. to the tray), the app is asked to quit by closing
/// its remaining hidden windows; "Save changes?" prompts still appear. It is only ended by
/// force when the user chose that.
/// </summary>
public sealed class AutoQuitService : FeatureService
{
    public const string EnabledKey = "autoQuit.enabled";
    public const string ScopeKey = "autoQuit.scope";
    public const string ExceptionsKey = "autoQuit.exceptions";
    public const string AppsKey = "autoQuit.apps";
    public const string ForceKey = "autoQuit.forceQuit";

    public static AppList Exceptions { get; } = new(ExceptionsKey);
    public static AppList Apps { get; } = new(AppsKey);

    private Native.WinEventDelegate? _callback;
    private IntPtr _hook;
    private readonly Dictionary<IntPtr, uint> _windows = [];
    private readonly HashSet<uint> _pending = [];

    public override string FeatureId => "autoQuit";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        foreach (var w in WindowEnumerator.List(includeOtherDesktops: true)) _windows[w.Handle] = w.ProcessId;
        _callback = OnEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE, IntPtr.Zero, _callback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    protected override void OnStop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
        _windows.Clear();
        _pending.Clear();
    }

    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        switch (type)
        {
            case Native.EVENT_OBJECT_SHOW:
                if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
                // Titles often arrive after the window shows; check again shortly.
                Track(hwnd);
                break;
            case Native.EVENT_OBJECT_HIDE:
            case Native.EVENT_OBJECT_DESTROY:
                if (_windows.Remove(hwnd, out var pid)) Schedule(pid);
                break;
        }
    }

    private void Track(IntPtr hwnd)
    {
        if (WindowEnumerator.IsCandidate(hwnd, includeOtherDesktops: true, out _))
        {
            _windows[hwnd] = WindowEnumerator.ResolveProcessId(hwnd);
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsRunning && WindowEnumerator.IsCandidate(hwnd, includeOtherDesktops: true, out _))
                _windows[hwnd] = WindowEnumerator.ResolveProcessId(hwnd);
        };
        timer.Start();
    }

    private void Schedule(uint pid)
    {
        if (pid == 0 || !_pending.Add(pid)) return;
        // A short grace: apps that swap a splash for their main window, or reopen one, are left alone.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pending.Remove(pid);
            if (IsRunning) Check(pid);
        };
        timer.Start();
    }

    private void Check(uint pid)
    {
        using var process = WindowHelper.TryGetProcess(pid);
        if (process is null || process.HasExited) return;
        var path = WindowHelper.GetProcessPath(pid);
        var remaining = WindowEnumerator.List(includeOtherDesktops: true).Count(w => w.ProcessId == pid);
        if (!AutoQuitLogic.ShouldQuit(AppNames.Key(path), AppNames.IsSystemProcess(path), hadWindow: true, remaining,
                AutoQuitLogic.ParseScope(Settings.GetString(ScopeKey, "all")), Exceptions.Items, Apps.Items))
            return;

        Log.Info($"Quit on close: asking {AppNames.Key(path)} ({pid}) to quit");
        WindowActions.QuitApp(pid, IntPtr.Zero, includeHidden: true);
        if (!Settings.GetBool(ForceKey, false)) return;
        var force = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        force.Tick += (_, _) =>
        {
            force.Stop();
            if (!IsRunning || !Settings.GetBool(ForceKey, false)) return;
            using var p = WindowHelper.TryGetProcess(pid);
            if (p is null || p.HasExited) return;
            // Never end an app that opened a window again (a "Save changes?" prompt, say).
            if (WindowActions.TopLevelWindowsOf(pid, includeHidden: false).Count > 0) return;
            if (!string.Equals(WindowHelper.GetProcessPath(pid), path, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Log.Info($"Quit on close: ending {AppNames.Key(path)} ({pid})");
                p.Kill();
            }
            catch (Exception ex)
            {
                Log.Error("Ending an app failed", ex);
            }
        };
        force.Start();
    }
}
