// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>
/// State of the Packages tool (port of upstream's HomebrewManager): the installed list with
/// update badges, search results, the selected package and its details. Reads run through
/// <see cref="WingetCli"/>; installs, updates and removals go through the shared
/// <see cref="WingetOperationLane"/>. Methods are called on the UI thread and
/// <see cref="Changed"/> is raised there.
/// </summary>
public sealed class PackageManager
{
    public const string Owner = "homebrew";
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    private static PackageManager? _shared;
    public static PackageManager Shared => _shared ??= new PackageManager(WingetOperationLane.Shared, live: true);

    private readonly WingetOperationLane _lane;
    private readonly bool _live;
    private CancellationTokenSource _reads = new();
    private int _installedGeneration;
    private int _searchGeneration;
    private int _detailsGeneration;
    private Task<List<InstalledApp>>? _apps;
    private PackageItem? _lastOperationTarget;
    private WingetAction? _lastOperationAction;

    private PackageManager(WingetOperationLane lane, bool live)
    {
        _lane = lane;
        _live = live;
        if (live) lane.Completed += OnOperationCompleted;
    }

    public event Action? Changed;

    public WingetOperationLane Lane => _lane;
    public bool WingetMissing { get; private set; }
    public bool HasLoaded { get; private set; }
    public IReadOnlyList<PackageItem> Installed { get; private set; } = [];
    public IReadOnlyList<PackageItem> SearchResults { get; private set; } = [];
    public PackageItem? Selected { get; private set; }
    public WingetDetails? Details { get; private set; }
    public InstalledApp? SelectedApp { get; private set; }
    public bool IsLoadingInstalled { get; private set; }
    public bool IsSearching { get; private set; }
    public bool IsLoadingDetails { get; private set; }
    public bool HasSearched { get; private set; }
    public string? ErrorMessage { get; private set; }
    /// <summary>The last listing said the Microsoft Store source needs its terms accepted.</summary>
    public bool StoreNeedsConsent { get; private set; }
    public IReadOnlyList<WingetSource> Sources { get; private set; } = [];
    public bool IsLoadingSources { get; private set; }
    public bool SourcesLoaded { get; private set; }

    public bool IsBusy => IsLoadingInstalled || IsSearching || IsLoadingDetails || _lane.IsRunning;
    public int OutdatedCount => Installed.Count(i => i.HasUpdate);

    /// <summary>A manager with fixed data and no winget behind it (window previews and tests).</summary>
    public static PackageManager ForPreview(WingetOperationLane lane, IReadOnlyList<PackageItem> installed,
        IReadOnlyList<PackageItem> results, PackageItem? selected, WingetDetails? details, IReadOnlyList<WingetSource>? sources = null) =>
        new(lane, live: false)
        {
            Installed = installed,
            SearchResults = results,
            Selected = selected,
            Details = details,
            HasLoaded = true,
            HasSearched = results.Count > 0,
            Sources = sources ?? [],
            SourcesLoaded = sources is not null,
        };

    /// <summary>Loads the installed list the first time a surface appears.</summary>
    public void EnsureLoaded()
    {
        if (!_live || HasLoaded || IsLoadingInstalled) return;
        RefreshInstalled();
    }

    /// <param name="clearingError">False right after a failed operation, so its reason stays visible.</param>
    public void RefreshInstalled(bool clearingError = true)
    {
        if (!_live) return;
        if (!WingetCli.IsAvailable)
        {
            WingetMissing = true;
            Installed = [];
            SearchResults = [];
            Selected = null;
            Details = null;
            if (clearingError) ErrorMessage = null;
            Raise();
            return;
        }
        WingetMissing = false;
        IsLoadingInstalled = true;
        if (clearingError) ErrorMessage = null;
        _apps = null;
        var generation = ++_installedGeneration;
        var consent = WingetConsent.Accepted;
        var token = _reads.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Raise();
        _ = Task.Run(async () =>
        {
            WingetRunResult list;
            WingetRunResult? pins = null;
            try
            {
                list = await WingetCli.RunAsync(WingetCommands.List(consent), ListTimeout, token).ConfigureAwait(false);
                if (list.Succeeded) pins = await WingetCli.RunAsync(WingetCommands.PinList(), ReadTimeout, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = dispatcher.BeginInvoke(() =>
            {
                if (generation != _installedGeneration) return;
                IsLoadingInstalled = false;
                HasLoaded = true;
                if (list.Missing)
                {
                    WingetMissing = true;
                    Installed = [];
                }
                else if (!list.Succeeded && list.ExitCode != WingetParser.NoPackagesFound)
                {
                    ErrorMessage = FailureText(list);
                }
                else
                {
                    var pinned = pins is { Succeeded: true }
                        ? new HashSet<string>(WingetParser.ParsePins(pins.Output).Select(p => p.Id), StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Installed = WingetParser.ParsePackages(list.Output).Select(p => PackageRules.FromInstalled(p, pinned)).ToList();
                    SearchResults = PackageRules.Reconcile(SearchResults, Installed);
                    StoreNeedsConsent = !consent && WingetProgress.NeedsAgreements(list.Output);
                    if (Selected is not null)
                    {
                        var fresh = Installed.FirstOrDefault(i => i.Key == Selected.Key);
                        Selected = fresh is not null ? fresh with { LatestVersion = Selected.LatestVersion } : Selected with { InstalledVersion = null, Available = null };
                    }
                }
                Raise();
            });
        });
    }

    public void Search(string query, string source)
    {
        if (!_live) return;
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            SearchResults = [];
            HasSearched = false;
            Raise();
            return;
        }
        var consent = WingetConsent.Accepted;
        var args = WingetCommands.Search(trimmed, source.Length == 0 ? null : source, consent);
        if (args is null) return;
        var generation = ++_searchGeneration;
        IsSearching = true;
        HasSearched = true;
        ErrorMessage = null;
        var token = _reads.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Raise();
        _ = Task.Run(async () =>
        {
            WingetRunResult result;
            try { result = await WingetCli.RunAsync(args, ReadTimeout, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            _ = dispatcher.BeginInvoke(() =>
            {
                if (generation != _searchGeneration) return;
                IsSearching = false;
                if (result.Succeeded)
                {
                    SearchResults = WingetParser.ParsePackages(result.Output).Select(p => PackageRules.FromSearch(p, Installed)).ToList();
                    if (!consent && WingetProgress.NeedsAgreements(result.Output)) StoreNeedsConsent = true;
                }
                else if (result.ExitCode == WingetParser.NoPackagesFound)
                {
                    SearchResults = [];
                }
                else
                {
                    SearchResults = [];
                    ErrorMessage = FailureText(result);
                    if (!consent && WingetProgress.NeedsAgreements(result.Output)) StoreNeedsConsent = true;
                }
                Raise();
            });
        });
    }

    public void Select(PackageItem item)
    {
        Selected = item;
        Details = null;
        SelectedApp = null;
        if (!_live)
        {
            Raise();
            return;
        }
        var generation = ++_detailsGeneration;
        var token = _reads.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var consent = WingetConsent.Accepted;
        var args = item.HasSource && item.CanAct ? WingetCommands.Show(item.Id, item.Source, consent) : null;
        IsLoadingDetails = args is not null;
        ErrorMessage = null;
        Raise();
        _ = Task.Run(async () =>
        {
            WingetDetails? details = null;
            string? error = null;
            InstalledApp? app = null;
            try
            {
                if (args is not null)
                {
                    var result = await WingetCli.RunAsync(args, ReadTimeout, token).ConfigureAwait(false);
                    if (result.Succeeded) details = WingetParser.ParseShow(result.Output);
                    else if (result.ExitCode != WingetParser.NoPackagesFound) error = FailureText(result);
                }
                if (item.IsInstalled) app = await FindAppAsync(item).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = dispatcher.BeginInvoke(() =>
            {
                if (generation != _detailsGeneration) return;
                IsLoadingDetails = false;
                Details = details;
                SelectedApp = app;
                if (error is not null) ErrorMessage = error;
                if (Selected is not null && details is not null && Selected.Key == item.Key && Selected.LatestVersion is null && details.Version.Length > 0)
                    Selected = Selected with { LatestVersion = details.Version };
                Raise();
            });
        });
    }

    public void ClearSelection()
    {
        _detailsGeneration++;
        Selected = null;
        Details = null;
        SelectedApp = null;
        IsLoadingDetails = false;
        Raise();
    }

    /// <summary>The installed app behind a package (registry/Store entry), loaded once per refresh.</summary>
    public async Task<InstalledApp?> FindAppAsync(PackageItem item)
    {
        if (!_live) return null;
        _apps ??= Task.Run(() => InstalledAppsProvider.Load());
        try
        {
            var apps = await _apps.ConfigureAwait(false);
            return InstalledAppMatcher.Match(item.Name, item.Id, item.InstalledVersion ?? string.Empty, apps);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading installed apps failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Starts an install, update, removal, update-all or source refresh (after the UI confirmed it).</summary>
    public bool Perform(WingetAction action, PackageItem? item)
    {
        if (!_live || _lane.IsRunning) return false;
        if (action is WingetAction.Install or WingetAction.Uninstall or WingetAction.Upgrade && item is not { CanAct: true }) return false;
        IReadOnlyList<WingetTarget> targets = item is null ? [] : [item.ToTarget()];
        _lastOperationTarget = item;
        _lastOperationAction = action;
        ErrorMessage = null;
        var started = _lane.Start(action, Owner, targets);
        Raise();
        return started;
    }

    public void LoadSources()
    {
        if (!_live || IsLoadingSources || !WingetCli.IsAvailable) return;
        IsLoadingSources = true;
        var token = _reads.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Raise();
        _ = Task.Run(async () =>
        {
            WingetRunResult result;
            try { result = await WingetCli.RunAsync(WingetCommands.SourceList(), ReadTimeout, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            _ = dispatcher.BeginInvoke(() =>
            {
                IsLoadingSources = false;
                SourcesLoaded = true;
                Sources = result.Succeeded ? WingetParser.ParseSources(result.Output) : [];
                Raise();
            });
        });
    }

    /// <summary>Stops every pending read (feature switched off).</summary>
    public void CancelReads()
    {
        try { _reads.Cancel(); } catch (ObjectDisposedException) { }
        _reads.Dispose();
        _reads = new CancellationTokenSource();
        _installedGeneration++;
        _searchGeneration++;
        _detailsGeneration++;
        IsLoadingInstalled = IsSearching = IsLoadingDetails = IsLoadingSources = false;
        HasLoaded = false;
    }

    private void OnOperationCompleted(WingetOperationStatus status)
    {
        _apps = null;
        if (status.Owner != Owner)
        {
            // Another feature (App updates) changed packages: keep the list honest if it was loaded.
            if (HasLoaded) RefreshInstalled(clearingError: false);
            return;
        }
        var action = _lastOperationAction ?? status.Action;
        var item = _lastOperationTarget;
        if (status.Result == WingetResult.Succeeded)
        {
            if (PackageRules.ClearsSelectionOnSuccess(action) && Selected is not null && item is not null && Selected.Key == item.Key)
                ClearSelection();
            RefreshInstalled();
            if (!PackageRules.ClearsSelectionOnSuccess(action) && item is not null) Select(Selected?.Key == item.Key ? Selected : item);
        }
        else
        {
            // winget reports partial runs as failures: re-read regardless.
            RefreshInstalled(clearingError: false);
        }
    }

    private static string FailureText(WingetRunResult result)
    {
        if (result.TimedOut) return "winget took too long to answer. Try again in a moment.";
        var visible = WingetProgress.VisibleError(result.Output);
        return visible.Length > 0 ? visible : WingetCommands.DescribeExitCode(result.ExitCode);
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Packages view update failed", ex); }
    }
}
