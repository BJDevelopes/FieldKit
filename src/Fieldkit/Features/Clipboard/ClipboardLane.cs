// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Interop;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>Why Fieldkit itself wrote the clipboard (so listeners can tell their own writes apart).</summary>
public enum OwnWriteKind
{
    /// <summary>Copied out of the clipboard history: the entry was already moved to the top.</summary>
    HistoryCopy,
    /// <summary>A temporary write that is put back shortly (paste as plain text, snippets).</summary>
    Transient,
    /// <summary>The previous content restored after a transient write.</summary>
    Restore,
    /// <summary>A cleaned link replacing what another app copied (keeps that app as the source).</summary>
    Rewrite,
    /// <summary>The clipboard was emptied (auto clear).</summary>
    Clear,
    /// <summary>Anything else Fieldkit copies on the user's behalf (Clean URL, shelf, …).</summary>
    Other,
}

/// <summary>One clipboard change as listeners see it.</summary>
/// <param name="Sequence">GetClipboardSequenceNumber at the time of the change.</param>
/// <param name="OwnWrite">Set when Fieldkit made this change.</param>
/// <param name="OwnWriteSource">For a <see cref="OwnWriteKind.Rewrite"/>, the app the original copy came from.</param>
/// <param name="SourcePath">The executable that owns the clipboard (falls back to the foreground app).</param>
/// <param name="ForegroundPath">The executable of the foreground window when the change arrived.</param>
public sealed record ClipboardChange(uint Sequence, OwnWriteKind? OwnWrite, string? OwnWriteSource, string? SourcePath, string? ForegroundPath);

/// <summary>
/// The single serial lane every clipboard read and write goes through: a background STA thread with
/// its own message loop that also hosts the clipboard format listener. Reading can block behind an app
/// that promised data and stopped answering; that must never freeze the UI thread or the input hooks,
/// so nothing touches the clipboard anywhere else (the counterpart of upstream GeneralPasteboardAccess).
///
/// The thread starts on first use and ends again once nothing listens and nothing is queued.
/// </summary>
public static class ClipboardLane
{
    private static readonly object Gate = new();
    private static readonly List<(int Priority, Action<ClipboardChange, ClipboardAccess> Handler)> Subscribers = [];
    private static readonly Dictionary<uint, (OwnWriteKind Kind, string? Source)> OwnWrites = [];
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static HwndSource? _window;
    private static bool _listening;
    private static DispatcherTimer? _debounce;
    private static DispatcherTimer? _idle;
    private static int _pending;

    /// <summary>Handle of the lane's hidden window (the owner of everything Fieldkit writes).</summary>
    internal static IntPtr WindowHandle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>Runs <paramref name="work"/> on the lane and returns its result.</summary>
    public static Task<T> Run<T>(Func<ClipboardAccess, T> work)
    {
        Dispatcher dispatcher;
        lock (Gate)
        {
            // Counted before the thread is looked up, so an idle shutdown cannot race this call.
            Interlocked.Increment(ref _pending);
            dispatcher = EnsureThread();
        }
        return dispatcher.InvokeAsync(() =>
        {
            try
            {
                return work(new ClipboardAccess());
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
                ScheduleIdleCheck();
            }
        }, DispatcherPriority.Normal).Task;
    }

    public static Task Run(Action<ClipboardAccess> work) => Run(a =>
    {
        work(a);
        return true;
    });

    /// <summary>
    /// Calls <paramref name="handler"/> on the lane for every clipboard change, lowest priority first,
    /// while the returned token is alive. The format listener exists only while someone subscribes.
    /// </summary>
    public static IDisposable Subscribe(Action<ClipboardChange, ClipboardAccess> handler, int priority = 100)
    {
        lock (Gate)
        {
            Subscribers.Add((priority, handler));
            Subscribers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
        var dispatcher = EnsureThread();
        dispatcher.BeginInvoke(SyncListener);
        return new ActionToken(() =>
        {
            lock (Gate) Subscribers.RemoveAll(s => s.Handler == handler);
            var d = _dispatcher;
            d?.BeginInvoke(() =>
            {
                SyncListener();
                ScheduleIdleCheck();
            });
        });
    }

    /// <summary>Records that the clipboard now holds a write of ours (called on the lane after CloseClipboard).</summary>
    internal static void NoteOwnWrite(OwnWriteKind kind, string? source = null)
    {
        var seq = Native.GetClipboardSequenceNumber();
        lock (Gate)
        {
            OwnWrites[seq] = (kind, source);
            if (OwnWrites.Count > 64)
            {
                foreach (var old in OwnWrites.Keys.OrderBy(k => k).Take(OwnWrites.Count - 32).ToList()) OwnWrites.Remove(old);
            }
        }
    }

    private static Dispatcher EnsureThread()
    {
        lock (Gate)
        {
            if (_dispatcher is not null && !_dispatcher.HasShutdownStarted) return _dispatcher;
            using var ready = new ManualResetEventSlim(false);
            Dispatcher? created = null;
            _thread = new Thread(() =>
            {
                created = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(created));
                var parameters = new HwndSourceParameters("FieldkitClipboard")
                {
                    Width = 0,
                    Height = 0,
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                    WindowStyle = 0,
                };
                _window = new HwndSource(parameters);
                _window.AddHook(WndProc);
                ready.Set();
                Dispatcher.Run();
                _window?.Dispose();
                _window = null;
            })
            {
                IsBackground = true,
                Name = "Fieldkit clipboard lane",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(5000);
            _dispatcher = created ?? throw new InvalidOperationException("Clipboard lane did not start.");
            return _dispatcher;
        }
    }

    // Lane thread only.
    private static void SyncListener()
    {
        if (_window is null) return;
        bool want;
        lock (Gate) want = Subscribers.Count > 0;
        if (want && !_listening)
        {
            _listening = Native.AddClipboardFormatListener(_window.Handle);
            if (!_listening) Log.Warn("AddClipboardFormatListener failed");
        }
        else if (!want && _listening)
        {
            Native.RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
            _debounce?.Stop();
        }
    }

    // Lane thread only: shuts the thread down once it has nothing left to do.
    private static void ScheduleIdleCheck()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ScheduleIdleCheck);
            return;
        }
        _idle ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _idle.Tick -= OnIdle;
        _idle.Tick += OnIdle;
        _idle.Stop();
        _idle.Start();
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        _idle?.Stop();
        lock (Gate)
        {
            if (Subscribers.Count > 0 || Volatile.Read(ref _pending) > 0) return;
            if (_listening && _window is not null) Native.RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
            _debounce?.Stop();
            _debounce = null;
            _idle = null;
            var d = _dispatcher;
            _dispatcher = null;
            d?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        handled = true;
        var foreground = Native.GetForegroundWindow();
        var fgPath = foreground == IntPtr.Zero ? null : WindowHelper.GetProcessPath(WindowHelper.GetProcessId(foreground));
        // Apps often write a copy in several steps; let them finish before anyone reads it.
        _debounce ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(60) };
        _debounce.Stop();
        _debounce.Tag = fgPath;
        _debounce.Tick -= OnDebounced;
        _debounce.Tick += OnDebounced;
        _debounce.Start();
        return IntPtr.Zero;
    }

    private static void OnDebounced(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        var fgPath = _debounce?.Tag as string;
        var seq = Native.GetClipboardSequenceNumber();
        OwnWriteKind? own = null;
        string? ownSource = null;
        lock (Gate)
        {
            if (OwnWrites.TryGetValue(seq, out var w))
            {
                own = w.Kind;
                ownSource = w.Source;
            }
        }
        string? sourcePath = null;
        if (own is null)
        {
            var owner = Native.GetClipboardOwner();
            if (owner != IntPtr.Zero)
            {
                var pid = WindowHelper.GetProcessId(owner);
                if (pid != (uint)Environment.ProcessId) sourcePath = WindowHelper.GetProcessPath(pid);
            }
            if (string.IsNullOrEmpty(sourcePath)) sourcePath = fgPath;
            // Our own windows (the history, Settings) are not "an app the copy came from".
            if (string.Equals(sourcePath, AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase)) sourcePath = null;
        }
        var change = new ClipboardChange(seq, own, ownSource, sourcePath, fgPath);
        (int, Action<ClipboardChange, ClipboardAccess>)[] subscribers;
        lock (Gate) subscribers = [.. Subscribers];
        var access = new ClipboardAccess();
        foreach (var (_, handler) in subscribers)
        {
            try { handler(change, access); }
            catch (Exception ex) { Log.Error("Clipboard listener failed", ex); }
        }
    }

    private sealed class ActionToken(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
