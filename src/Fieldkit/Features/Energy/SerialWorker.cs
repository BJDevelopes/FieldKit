// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Fieldkit.Core;

namespace Fieldkit.Features.Energy;

/// <summary>
/// One background thread running work items in order. <see cref="PostLatest"/> coalesces
/// per key so a slider drag only ever applies the newest value.
/// </summary>
public sealed class SerialWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ConcurrentDictionary<string, Action> _latest = new(StringComparer.Ordinal);
    private readonly Thread _thread;

    public SerialWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Post(Action work)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(work);
    }

    /// <summary>Runs <paramref name="work"/> once, replacing any not-yet-started work for the same key.</summary>
    public void PostLatest(string key, Action work)
    {
        lock (_latest)
        {
            var scheduled = _latest.ContainsKey(key);
            _latest[key] = work;
            if (scheduled) return;
        }
        Post(() =>
        {
            Action? latest;
            lock (_latest)
            {
                _latest.TryRemove(key, out latest);
            }
            latest?.Invoke();
        });
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Log.Error($"{_thread.Name} work failed", ex); }
        }
    }

    /// <summary>Finishes queued work (up to <paramref name="timeout"/>) and stops the thread.</summary>
    public void Dispose() => Dispose(TimeSpan.FromSeconds(3));

    public void Dispose(TimeSpan timeout)
    {
        _queue.CompleteAdding();
        _thread.Join(timeout);
    }
}
