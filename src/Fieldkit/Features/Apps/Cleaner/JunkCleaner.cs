// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>
/// The cleaner's state (upstream JunkCleaner): idle → scanning → results → cleaning → done.
/// Scans and cleans run off the UI thread and are cancellable; state changes are raised on the
/// UI thread. Nothing runs until the user opens the tool or the schedule fires.
/// </summary>
public sealed class JunkCleaner
{
    public static JunkCleaner Shared { get; } = new();

    private JunkCleaner() { }

    public enum Phase { Idle, Scanning, Results, Cleaning, Done }

    public Phase State { get; private set; } = Phase.Idle;
    public List<CleanerItem> Items { get; private set; } = [];
    public CleanerCategory? ScanningCategory { get; private set; }
    public long LastFreed { get; private set; }
    public int LastFailed { get; private set; }

    public ICleanerDeleter Deleter { get; set; } = SystemCleanerDeleter.Instance;
    public Func<CleanerPaths> PathsFactory { get; set; } = CleanerPaths.Current;

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event Action? Changed;

    private CancellationTokenSource? _scanCts;
    private int _token;

    public long SelectedSize => Items.Where(i => i.Include).Sum(i => i.Size);
    public long TotalSize => Items.Sum(i => i.Size);
    public int SelectedCount => Items.Count(i => i.Include);

    public IEnumerable<CleanerItem> ItemsIn(DisplayGroup group) => Items.Where(i => i.Group == group);

    public void SetInclude(CleanerItem item, bool include)
    {
        if (item.IsBlocked) return;
        item.Include = include;
        Raise();
    }

    public void SetInclude(DisplayGroup group, bool include)
    {
        foreach (var item in ItemsIn(group).Where(i => !i.IsBlocked)) item.Include = include;
        Raise();
    }

    public void Reset()
    {
        _token++;
        _scanCts?.Cancel();
        _scanCts = null;
        Items = [];
        ScanningCategory = null;
        if (State != Phase.Cleaning) State = Phase.Idle;
        Raise();
    }

    /// <summary>Scans everything. <paramref name="attended"/> false skips the screenshot search.</summary>
    public async Task ScanAsync(bool attended)
    {
        if (State is Phase.Scanning or Phase.Cleaning) return;
        var token = ++_token;
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Items = [];
        State = Phase.Scanning;
        Raise();

        var dispatcher = Application.Current?.Dispatcher;
        var paths = PathsFactory();
        var options = new CleanerScanOptions
        {
            Attended = attended,
            TempAgeDays = Settings.GetInt(CleanerKeys.TempAgeDays, CleanerRules.DefaultTempAgeDays),
            ScreenshotAgeDays = Settings.GetInt(CleanerKeys.ScreenshotAgeDays, CleanerRules.DefaultScreenshotAgeDays),
            IncludeSystemCaches = Settings.GetBool(CleanerKeys.SystemCaches, false) && Elevation.IsAdministrator,
        };
        try
        {
            await Task.Run(() => CleanerScanner.Scan(paths, options, cts.Token,
                category => OnUi(dispatcher, () =>
                {
                    if (token != _token) return;
                    ScanningCategory = category;
                    Raise();
                }),
                (_, found) => OnUi(dispatcher, () =>
                {
                    if (token != _token) return;
                    Items.AddRange(found);
                    Raise();
                })), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Cleaner scan failed", ex);
        }
        if (token != _token) return;
        _scanCts = null;
        ScanningCategory = null;
        State = Phase.Results;
        Raise();
    }

    /// <summary>Cleans the checked items. Returns null when nothing was selected.</summary>
    public async Task<CleanerEngine.Outcome?> CleanSelectedAsync()
    {
        var chosen = Items.Where(i => i.Include && !i.IsBlocked).ToList();
        if (chosen.Count == 0 || State != Phase.Results) return null;
        State = Phase.Cleaning;
        Raise();
        var paths = PathsFactory();
        var deleter = Deleter;
        CleanerEngine.Outcome outcome;
        try
        {
            outcome = await Task.Run(() =>
            {
                // The installed-apps oracle only when a leftover row asks for it: building it
                // walks the registry and packages, and only leftovers are re-checked against it.
                IReadOnlySet<string>? tokens = chosen.Any(i => i.Category == CleanerCategory.Leftovers)
                    ? CleanerRules.OwnerTokens(InstalledAppsProvider.Load(), CleanerScanner.RunningProcessNames())
                    : null;
                return CleanerEngine.Clean(chosen, deleter, paths, tokens);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Cleaner clean failed", ex);
            outcome = new CleanerEngine.Outcome(0, chosen.Count);
        }
        Items = [];
        LastFreed = outcome.Freed;
        LastFailed = outcome.Failed;
        State = Phase.Done;
        Raise();
        return outcome;
    }

    /// <summary>
    /// One unattended pass for the schedule: scan, keep only what the scan pre-checked in the
    /// groups the user enabled for the schedule, clean, go idle again. Null when the user is in
    /// the middle of a manual session (their review wins).
    /// </summary>
    public async Task<CleanerEngine.Outcome?> RunAutomaticAsync()
    {
        if (State != Phase.Idle) return null;
        await ScanAsync(attended: false).ConfigureAwait(true);
        if (State != Phase.Results) return null; // interrupted by the user
        foreach (var item in Items)
            item.Include = CleanerRules.AutomaticMayClean(item.Category, item.Recommended, item.IsBlocked, k => Settings.GetBool(k, true));
        Raise();
        var outcome = Items.Any(i => i.Include)
            ? await CleanSelectedAsync().ConfigureAwait(true)
            : new CleanerEngine.Outcome(0, 0);
        Reset();
        return outcome;
    }

    private static void OnUi(Dispatcher? dispatcher, Action action)
    {
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Cleaner observer failed", ex); }
    }

    /// <summary>Puts the state machine in a given phase with given items (previews only).</summary>
    internal static JunkCleaner ForPreview(Phase phase, List<CleanerItem> items, CleanerCategory? scanning = null, long freed = 0, int failed = 0) =>
        new() { State = phase, Items = items, ScanningCategory = scanning, LastFreed = freed, LastFailed = failed };
}
