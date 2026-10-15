// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Energy;

/// <summary>
/// Holds a SetThreadExecutionState request on a dedicated thread. The request is
/// per-thread, so a thread that does nothing else keeps it alive no matter what the UI
/// thread does, and it ends exactly when this thread clears it or exits.
/// </summary>
public sealed class ExecutionStateKeeper : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private Native.EXECUTION_STATE _wanted = Native.EXECUTION_STATE.ES_CONTINUOUS;
    private bool _exit;

    /// <summary>Asks the system to stay awake (and the display on when <paramref name="display"/>).</summary>
    public void Hold(bool display)
    {
        var flags = Native.EXECUTION_STATE.ES_CONTINUOUS | Native.EXECUTION_STATE.ES_SYSTEM_REQUIRED;
        if (display) flags |= Native.EXECUTION_STATE.ES_DISPLAY_REQUIRED;
        lock (_gate)
        {
            _wanted = flags;
            EnsureThread();
        }
        _signal.Set();
    }

    /// <summary>Lets the system follow its normal sleep rules again.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_thread is null) return;
            _wanted = Native.EXECUTION_STATE.ES_CONTINUOUS;
        }
        _signal.Set();
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        _exit = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Fieldkit keep awake" };
        _thread.Start();
    }

    private void Run()
    {
        var applied = (Native.EXECUTION_STATE)0;
        while (true)
        {
            Native.EXECUTION_STATE wanted;
            bool exit;
            lock (_gate)
            {
                wanted = _wanted;
                exit = _exit;
            }
            if (exit) break;
            if (wanted != applied)
            {
                if (Native.SetThreadExecutionState(wanted) == 0) Log.Warn($"SetThreadExecutionState({wanted}) failed");
                applied = wanted;
            }
            // Refresh periodically as a belt-and-braces measure; ES_CONTINUOUS keeps it anyway.
            _signal.WaitOne(TimeSpan.FromMinutes(1));
        }
        Native.SetThreadExecutionState(Native.EXECUTION_STATE.ES_CONTINUOUS);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _exit = true;
            _wanted = Native.EXECUTION_STATE.ES_CONTINUOUS;
            _thread = null;
        }
        _signal.Set();
        thread?.Join(2000);
    }
}
