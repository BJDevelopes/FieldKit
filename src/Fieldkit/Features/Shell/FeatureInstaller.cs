// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Capture;
using Fieldkit.UI;

namespace Fieldkit.Features.Shell;

/// <summary>
/// Installs and uninstalls features one per idle turn of the UI thread, so the Features page can show how far a
/// bundle or "Install all" is instead of looking frozen, and says when it is done.
/// </summary>
public static class FeatureInstaller
{
    private static readonly Queue<(FeatureInfo Feature, bool Available)> Queue = new();
    private static readonly List<string> EnableKeys = [];
    private static int _installs;
    private static int _removals;
    private static string? _last;

    public static bool Busy { get; private set; }

    /// <summary>Whether a short note ("Screenshot installed") appears at the end; the self-test turns it off.</summary>
    public static bool ShowNotice { get; set; } = true;

    public static int Done { get; private set; }

    public static int Total { get; private set; }

    /// <summary>What is being done right now, such as "Installing Screenshot".</summary>
    public static string Status { get; private set; } = string.Empty;

    /// <summary>Raised after every step and when the work is finished (Busy is false then).</summary>
    public static event Action? Changed;

    public static void Install(string featureId) => Run([(featureId, true)]);

    public static void Uninstall(string featureId) => Run([(featureId, false)]);

    /// <summary>Applies the changes that differ from today's state; <paramref name="enableKeys"/> are switched on at the end.</summary>
    public static void Run(IEnumerable<(string Id, bool Available)> changes, IEnumerable<string>? enableKeys = null)
    {
        // What a feature will be once the queue is done: a second click must not queue it twice.
        var target = Queue.ToDictionary(q => q.Feature.Id, q => q.Available);
        var pending = changes
            .Select(c => (Feature: FeatureCatalog.Get(c.Id), c.Available))
            .Where(c => target.TryGetValue(c.Feature.Id, out var queued) ? queued != c.Available : c.Feature.IsAvailable != c.Available)
            .ToList();
        var keys = enableKeys?.ToList() ?? [];
        if (pending.Count == 0 && keys.Count == 0) return;

        foreach (var change in pending) Queue.Enqueue(change);
        EnableKeys.AddRange(keys);
        Total += pending.Count;
        var start = !Busy;
        Busy = true;
        if (start) Status = "Starting";
        Changed?.Invoke();
        if (start) Schedule();
    }

    private static void Schedule()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        // Below rendering, so the page paints its progress between two features.
        dispatcher.BeginInvoke(Step, DispatcherPriority.Background);
    }

    private static void Step()
    {
        if (Queue.Count > 0)
        {
            var (feature, available) = Queue.Dequeue();
            Status = $"{(available ? "Installing" : "Uninstalling")} {feature.Title}";
            Changed?.Invoke();
            try { feature.SetAvailable(available); }
            catch (Exception ex) { Log.Error($"Changing {feature.Id} failed", ex); }
            Done++;
            if (available) _installs++; else _removals++;
            _last = feature.Title;
            Changed?.Invoke();
            Schedule();
            return;
        }

        foreach (var key in EnableKeys) Settings.Set(key, true);
        EnableKeys.Clear();
        var message = Summary();
        Busy = false;
        Done = Total = _installs = _removals = 0;
        Status = string.Empty;
        _last = null;
        Changed?.Invoke();
        if (message.Length > 0 && ShowNotice) Hud.Show(Glyphs.Check, message);
    }

    private static string Summary() => (_installs, _removals) switch
    {
        (0, 0) => string.Empty,
        (1, 0) => $"{_last} installed",
        (0, 1) => $"{_last} uninstalled",
        (_, 0) => $"{_installs} features installed",
        (0, _) => $"{_removals} features uninstalled",
        _ => $"{_installs} installed, {_removals} uninstalled",
    };
}
