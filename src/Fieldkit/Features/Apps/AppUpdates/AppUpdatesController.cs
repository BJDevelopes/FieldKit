// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>
/// Finds which installed apps have a newer version, in one list, and updates the ones the
/// person picks (port of upstream's AppUpdatesService). The scan is one read-only
/// <c>winget upgrade</c>; updates go through the shared <see cref="WingetOperationLane"/>.
/// Nothing runs at rest: scans happen when a surface opens, on request, or on the optional
/// schedule kept by <see cref="AppUpdatesService"/>. Call on the UI thread.
/// </summary>
public sealed class AppUpdatesController
{
    public const string FeatureId = "appUpdates";
    public const string Owner = FeatureId;
    public const string FrequencyKey = "appUpdates.checkFrequency";
    public const string NotifyKey = "appUpdates.notify";
    public const string IncludeWingetKey = "appUpdates.includeWinget";
    public const string IncludeStoreKey = "appUpdates.includeStore";
    public const string IncludeUnknownKey = "appUpdates.includeUnknown";
    public const string RulesKey = "appUpdates.rules";
    public const string LastCheckKey = "appUpdates.lastCheck";
    public const string LastCountKey = "appUpdates.lastCount";
    public const string NotifiedKey = "appUpdates.notifiedIds";
    public const string ShortcutKey = "appUpdates.shortcut";

    /// <summary>winget refreshes its source on the way, which can sit on a slow network.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(120);

    private static AppUpdatesController? _shared;
    public static AppUpdatesController Shared => _shared ??= new AppUpdatesController(WingetOperationLane.Shared, live: true);

    private readonly WingetOperationLane _lane;
    private readonly bool _live;
    private readonly Dictionary<string, InstalledApp?> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.Ordinal);
    private List<AppUpdateItem> _allItems = [];
    private HashSet<string> _knownIds = new(StringComparer.Ordinal);
    private CancellationTokenSource _cts = new();
    private int _generation;
    private bool _automaticPending;
    private bool _sourceRefreshPending;
    private bool _handoffPending;

    private AppUpdatesController(WingetOperationLane lane, bool live)
    {
        _lane = lane;
        _live = live;
        Rules = AppUpdateRules.DecodeRules(Settings.GetString(RulesKey, string.Empty));
        var stamp = Settings.GetDouble(LastCheckKey, 0);
        LastCheck = stamp > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)stamp).LocalDateTime : null;
        if (live) lane.Completed += OnOperationCompleted;
    }

    public event Action? Changed;

    /// <summary>A scan finished; the flag says whether it was the background pass.</summary>
    public event Action<bool>? CheckFinished;

    public WingetOperationLane Lane => _lane;
    public IReadOnlyList<AppUpdateItem> Items { get; private set; } = [];
    public IReadOnlyList<AppUpdateRule> Rules { get; private set; }
    public HashSet<string> Selection { get; private set; } = new(StringComparer.Ordinal);
    public bool IsChecking { get; private set; }
    public DateTime? LastCheck { get; private set; }
    /// <summary>Set by the service while a background check is scheduled.</summary>
    public DateTime? NextCheck { get; set; }
    public bool PackageManagerAvailable { get; private set; } = true;
    /// <summary>winget failed or timed out, so an empty list proves nothing.</summary>
    public bool CheckFailed { get; private set; }
    /// <summary>Store apps were wanted but not checked (terms not accepted).</summary>
    public bool StoreNotChecked { get; private set; }
    public bool HasCheckedThisSession { get; private set; }
    public string? LastError { get; private set; }

    public int SelectedCount => Items.Count(i => i.IsSelectable && Selection.Contains(i.Id));
    public int SelectableCount => Items.Count(i => i.IsSelectable);
    public bool IsBusy => IsChecking || _lane.IsRunning;

    public static bool IncludeWinget => Settings.GetBool(IncludeWingetKey, true);
    public static bool IncludeStore => Settings.GetBool(IncludeStoreKey, true);
    public static bool IncludeUnknown => Settings.GetBool(IncludeUnknownKey, true);
    public static CheckFrequency Frequency => AppUpdateRules.ParseFrequency(Settings.GetString(FrequencyKey, "off"));

    /// <summary>A controller with fixed rows (previews and tests).</summary>
    public static AppUpdatesController ForPreview(WingetOperationLane lane, IReadOnlyList<AppUpdateItem> items,
        IReadOnlyList<AppUpdateRule>? rules = null, DateTime? lastCheck = null)
    {
        var c = new AppUpdatesController(lane, live: false)
        {
            Items = items,
            Rules = rules ?? [],
            LastCheck = lastCheck,
            HasCheckedThisSession = true,
        };
        c.Selection = new HashSet<string>(items.Where(i => i.IsSelectable).Select(i => i.Id), StringComparer.Ordinal);
        c._knownIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
        return c;
    }

    public InstalledApp? AppFor(AppUpdateItem item) => _apps.TryGetValue(item.Id, out var a) ? a : null;

    /// <summary>The matched app's icon, extracted on first use (UI thread) and cached.</summary>
    public ImageSource? IconFor(AppUpdateItem item)
    {
        if (_icons.TryGetValue(item.Id, out var cached)) return cached;
        var icon = AppFor(item) is { } app ? InstalledAppsProvider.Icon(app) : null;
        _icons[item.Id] = icon;
        return icon;
    }

    // ------------------------------------------------------------ checking

    /// <summary>Scans with the enabled sources. <paramref name="automatic"/> marks the background
    /// pass, which alone may post a notification.</summary>
    public void Check(bool automatic = false)
    {
        if (!_live || !FeatureCatalog.IsAvailable(FeatureId)) return;
        if (IsChecking)
        {
            _automaticPending |= automatic;
            return;
        }
        ReloadRules();
        IsChecking = true;
        LastError = null;
        var generation = ++_generation;
        var includeWinget = IncludeWinget;
        var includeStore = IncludeStore;
        var includeUnknown = IncludeUnknown;
        var consent = WingetConsent.Accepted;
        var token = _cts.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Raise();
        _ = Task.Run(async () =>
        {
            ScanResult scan;
            try { scan = await ScanAsync(includeWinget, includeStore, includeUnknown, consent, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("App update check failed", ex);
                scan = new ScanResult([], true, true, ex.Message, []);
            }
            _ = dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                Finish(scan, automatic, includeStore && !consent);
            });
        });
    }

    private sealed record ScanResult(
        List<AppUpdateItem> Items,
        bool Available,
        bool Failed,
        string? Error,
        Dictionary<string, InstalledApp?> Apps);

    private static async Task<ScanResult> ScanAsync(bool includeWinget, bool includeStore, bool includeUnknown, bool consent, CancellationToken token)
    {
        if (!WingetCli.IsAvailable) return new ScanResult([], false, false, null, []);
        var result = await WingetCli.RunAsync(WingetCommands.UpgradeList(includeUnknown, consent), CheckTimeout, token).ConfigureAwait(false);
        if (result.Missing) return new ScanResult([], false, false, null, []);
        var parsed = WingetParser.ParseUpgrade(result.Output);
        var failed = result.TimedOut || (result.ExitCode != 0 && result.ExitCode != WingetParser.NoPackagesFound && parsed.Packages.Count == 0);
        string? error = null;
        if (failed)
        {
            error = result.TimedOut ? "winget took too long to answer." : WingetProgress.VisibleError(result.Output);
            if (string.IsNullOrWhiteSpace(error)) error = WingetCommands.DescribeExitCode(result.ExitCode);
        }
        var items = AppUpdateRules.Items(parsed, includeWinget, includeStore, includeUnknown);
        var apps = new Dictionary<string, InstalledApp?>(StringComparer.Ordinal);
        if (items.Count > 0)
        {
            List<InstalledApp> installed;
            try { installed = InstalledAppsProvider.Load(); }
            catch (Exception ex)
            {
                Log.Warn($"Reading installed apps failed: {ex.Message}");
                installed = [];
            }
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var app = InstalledAppMatcher.Match(item.Name, item.PackageId, item.InstalledVersion, installed);
                apps[item.Id] = app;
            }
        }
        return new ScanResult(items, true, failed, error, apps);
    }

    private void Finish(ScanResult scan, bool automatic, bool storeSkipped)
    {
        if (!FeatureCatalog.IsAvailable(FeatureId))
        {
            IsChecking = false;
            _sourceRefreshPending = false;
            _automaticPending = false;
            Raise();
            return;
        }
        var finishAutomatically = automatic || _automaticPending;
        _automaticPending = false;
        if (_sourceRefreshPending)
        {
            _sourceRefreshPending = false;
            IsChecking = false;
            Check(finishAutomatically);
            return;
        }
        _allItems = scan.Items;
        foreach (var (k, v) in scan.Apps)
        {
            _apps[k] = v;
            _icons.Remove(k);
        }
        var visible = AppUpdateRules.VisibleItems(scan.Items, Rules);
        var announced = new HashSet<string>(Settings.Get<string[]>(NotifiedKey, []), StringComparer.Ordinal);
        announced.IntersectWith(visible.Select(i => i.Id));
        var fresh = AppUpdateRules.Fresh(visible, announced);
        Selection = AppUpdateRules.ReconciledSelection(Selection, _knownIds, visible);
        _knownIds = new HashSet<string>(visible.Select(i => i.Id), StringComparer.Ordinal);
        Items = visible;
        PackageManagerAvailable = scan.Available;
        CheckFailed = scan.Failed;
        LastError = scan.Error;
        StoreNotChecked = storeSkipped;
        HasCheckedThisSession = true;
        IsChecking = false;
        _handoffPending = false;
        LastCheck = DateTime.Now;
        Settings.Set(LastCheckKey, (double)DateTimeOffset.Now.ToUnixTimeSeconds());
        Settings.Set(LastCountKey, visible.Count);
        if (finishAutomatically && fresh.Count > 0 && Settings.GetBool(NotifyKey, true))
        {
            Notifier.Show(AppUpdateStrings.PageTitle, AppUpdateRules.NotificationBody(visible.Count));
            announced = new HashSet<string>(visible.Select(i => i.Id), StringComparer.Ordinal);
        }
        Settings.Set(NotifiedKey, announced.OrderBy(i => i, StringComparer.Ordinal).ToArray());
        Raise();
        CheckFinished?.Invoke(finishAutomatically);
    }

    /// <summary>A source switch changes the answer: re-run, or re-run right after the current scan.</summary>
    public void SourceSelectionDidChange()
    {
        if (IsChecking) _sourceRefreshPending = true;
        else Check();
    }

    /// <summary>What a surface calls when it appears.</summary>
    public void CheckIfNeeded()
    {
        ReloadRules();
        if (AppUpdateRules.ShouldRecheck(HasCheckedThisSession, _handoffPending, LastCheck, DateTime.Now)) Check();
    }

    /// <summary>A window came back to the front: re-check if an update was handed to another app.</summary>
    public void SurfaceActivated()
    {
        if (_handoffPending) CheckIfNeeded();
    }

    /// <summary>Stops a running scan (feature switched off).</summary>
    public void CancelCheck()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _generation++;
        IsChecking = false;
        _automaticPending = false;
        _sourceRefreshPending = false;
    }

    // ------------------------------------------------------------ rules

    public void SkipVersion(AppUpdateItem item)
    {
        if (AppUpdateRules.IsUncomparable(item.LatestVersion)) return;
        SetRule(item, item.LatestVersion);
    }

    public void ExcludeApp(AppUpdateItem item) => SetRule(item, null);

    private void SetRule(AppUpdateItem item, string? version)
    {
        if (IsChecking || !Items.Contains(item)) return;
        SaveRules(AppUpdateRules.WithRule(Rules, item, version));
    }

    public void RemoveRule(AppUpdateRule rule)
    {
        if (IsChecking || !Rules.Contains(rule)) return;
        SaveRules(Rules.Where(r => r != rule).ToList());
        // An excluded app was not looked at since; Check now brings it back.
        if (rule.Version is null) HasCheckedThisSession = false;
        Raise();
    }

    private void SaveRules(List<AppUpdateRule> rules)
    {
        Rules = rules;
        Settings.Set(RulesKey, AppUpdateRules.EncodeRules(rules));
        ApplyRules();
    }

    private void ReloadRules()
    {
        var restored = AppUpdateRules.DecodeRules(Settings.GetString(RulesKey, string.Empty));
        if (restored.SequenceEqual(Rules)) return;
        Rules = restored;
        HasCheckedThisSession = false;
        if (IsChecking) _sourceRefreshPending = true;
        ApplyRules();
    }

    private void ApplyRules()
    {
        var visible = AppUpdateRules.VisibleItems(_live ? _allItems : Items, Rules);
        Selection = AppUpdateRules.ReconciledSelection(Selection, _knownIds, visible);
        _knownIds = new HashSet<string>(visible.Select(i => i.Id), StringComparer.Ordinal);
        Items = visible;
        if (_live)
        {
            Settings.Set(LastCountKey, visible.Count);
            var announced = Settings.Get<string[]>(NotifiedKey, []).Where(_knownIds.Contains).ToArray();
            Settings.Set(NotifiedKey, announced);
        }
        Raise();
    }

    // ------------------------------------------------------------ selection

    public void SelectAll()
    {
        Selection = new HashSet<string>(Items.Where(i => i.IsSelectable).Select(i => i.Id), StringComparer.Ordinal);
        Raise();
    }

    public void SelectNone()
    {
        Selection = new HashSet<string>(StringComparer.Ordinal);
        Raise();
    }

    public void Toggle(AppUpdateItem item)
    {
        if (!item.IsSelectable || !Items.Contains(item)) return;
        if (!Selection.Remove(item.Id)) Selection.Add(item.Id);
        Raise();
    }

    // ------------------------------------------------------------ acting

    /// <summary>Updates the ticked rows, one winget command each, in one operation.</summary>
    public bool UpdateSelected() => Start(AppUpdateRules.Targets(Items, Selection));

    public bool Update(AppUpdateItem item) => item.IsSelectable && Items.Contains(item) && Start([item.ToTarget()]);

    private bool Start(List<WingetTarget> targets)
    {
        if (!_live || targets.Count == 0 || _lane.IsRunning) return false;
        var started = _lane.Start(targets.Count == 1 ? WingetAction.Upgrade : WingetAction.UpgradeSelected, Owner, targets);
        Raise();
        return started;
    }

    /// <summary>Opens the app so its own updater can run; re-checks when the person comes back.</summary>
    public async Task<bool> OpenAppAsync(AppUpdateItem item)
    {
        if (AppFor(item) is not { } app) return false;
        var opened = await InstalledAppMatcher.OpenAsync(app);
        if (opened) _handoffPending = true;
        return opened;
    }

    /// <summary>The Store page of a Store row, or the Store's updates page.</summary>
    public void OpenStore(AppUpdateItem? item)
    {
        var uri = item is { Source: AppUpdateSource.Store } && WingetCommands.IsValidId(item.PackageId) && item.PackageId.All(char.IsLetterOrDigit)
            ? $"ms-windows-store://pdp/?ProductId={item.PackageId}"
            : WingetCommands.StoreUpdatesUri;
        if (WingetUi.OpenUri(uri)) _handoffPending = true;
    }

    private void OnOperationCompleted(WingetOperationStatus status)
    {
        // Any package change (here or in Packages) can change this list; re-read once it was shown.
        if (status.Owner == Owner || HasCheckedThisSession) Check();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("App updates view update failed", ex); }
    }
}
