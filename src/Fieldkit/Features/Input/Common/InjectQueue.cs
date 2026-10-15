// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Common;

/// <summary>
/// Posts synthetic input from a worker thread, never from the low-level hook thread: an
/// injected event re-enters the same hooks, and calling SendInput from inside a hook callback
/// would make Windows wait on the very thread that is busy. Every event is tagged by
/// <see cref="InputSender"/> so our handlers recognise (and ignore) it.
///
/// The worker exists only while at least one feature holds a lease.
/// </summary>
internal static class InjectQueue
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<Native.INPUT[]> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static Thread? _thread;
    private static int _leases;
    private static volatile bool _stop;

    /// <summary>Keeps the worker alive until the returned token is disposed.</summary>
    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            _leases++;
            if (_thread is null)
            {
                _stop = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "Fieldkit input injection", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }
        return new ActionDisposableOnce(Release);
    }

    /// <summary>Queues events to be sent together, in order.</summary>
    public static void Post(params Native.INPUT[] inputs)
    {
        if (inputs.Length == 0 || _thread is null) return;
        Pending.Enqueue(inputs);
        Signal.Set();
    }

    public static void Wheel(int delta, bool horizontal) =>
        Post(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    mouseData = delta,
                    dwFlags = horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InputSender.Tag,
                },
            },
        });

    private static void Release()
    {
        Thread? toJoin = null;
        lock (Gate)
        {
            if (--_leases > 0) return;
            _leases = 0;
            _stop = true;
            toJoin = _thread;
            _thread = null;
        }
        Signal.Set();
        toJoin?.Join(500);
    }

    private static void Run()
    {
        while (!_stop)
        {
            Signal.WaitOne();
            while (Pending.TryDequeue(out var inputs))
            {
                try { InputSender.Send(inputs); }
                catch (Exception ex) { Log.Error("Injecting input failed", ex); }
            }
        }
        // Flush whatever is left (e.g. modifier releases queued by a stopping feature).
        while (Pending.TryDequeue(out var rest))
        {
            try { InputSender.Send(rest); } catch { }
        }
    }

    private sealed class ActionDisposableOnce(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
