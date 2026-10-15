// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

public enum UninstallerPhase { Pick, Scanning, Review, Uninstalling, Removing, Done }

/// <summary>
/// The uninstaller flow (upstream AppUninstaller's phases) for one window: pick → scan → review →
/// run the app's uninstaller → review what it left → Recycle Bin → done. Lives on the UI thread;
/// background work is cancelled by <see cref="Dispose"/>.
/// </summary>
public sealed class UninstallerSession : IDisposable
{
    public const string PreferQuietKey = "uninstaller.preferQuiet";
    public const string ShowStoreAppsKey = "uninstaller.showStoreApps";

    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _step;
    private AppIdentity? _identity;
    private long _uninstalledBytes;
    private int _generation;

    public UninstallerPhase Phase { get; private set; } = UninstallerPhase.Pick;
    public ReviewStage Stage { get; private set; } = ReviewStage.BeforeUninstall;
    public List<AppRow>? Apps { get; private set; }
    public InstalledApp? Target { get; private set; }
    public ImageSource? TargetIcon { get; private set; }
    public UninstallPlan? Plan { get; private set; }
    public List<Leftover> Items { get; private set; } = [];
    public string? Notice { get; private set; }
    public string? PickerNotice { get; private set; }
    public string BusyMessage { get; private set; } = UninstallerText.Scanning;
    public string? BusyDetail { get; private set; }
    /// <summary>True while waiting for an uninstaller that already exited but whose entry is still registered
    /// (installers that hand off to a copy of themselves).</summary>
    public bool WaitingForHandOff { get; private set; }
    public long Freed { get; private set; }
    public IReadOnlyList<(Leftover Item, string Reason)> Failed { get; private set; } = [];
    public IReadOnlyList<string> Backups { get; private set; } = [];
    public bool IsAdmin { get; } = Elevation.IsAdministrator;
    public string SearchQuery { get; set; } = string.Empty;

    /// <summary>Raised on the UI thread whenever the phase or its data changes.</summary>
    public event Action? Changed;

    public bool IsBusy => Phase is UninstallerPhase.Uninstalling or UninstallerPhase.Removing;

    private CancellationToken Life => _life.Token;

    private void Raise() => Changed?.Invoke();

    // ------------------------------------------------------------------ picker

    /// <summary>Loads the installed apps (once), their icons and missing sizes in the background.</summary>
    public async void LoadApps(bool force = false)
    {
        if (Apps is not null && !force) return;
        Apps = null;
        Raise();
        try
        {
            var includeStore = Settings.GetBool(ShowStoreAppsKey, true);
            var self = AppInfo.ExecutablePath;
            var rows = await Task.Run(() => InstalledAppsProvider.Load(includeStore)
                .Where(a => !UninstallRules.IsSelf(a, self))
                .Select(a => new AppRow(a, InstalledAppsProvider.Icon(a)))
                .ToList(), Life);
            if (Life.IsCancellationRequested) return;
            Apps = rows;
            Raise();
            _ = MeasureSizesAsync(rows, Life);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Uninstaller: loading apps failed", ex);
            Apps = [];
            Raise();
        }
    }

    private static async Task MeasureSizesAsync(List<AppRow> rows, CancellationToken ct)
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (var row in rows.Where(r => r.Size == 0 && LeftoverRules.IsAppSpecificFolder(r.App.InstallLocation)))
                {
                    ct.ThrowIfCancellationRequested();
                    row.Size = DiskUsage.SizeOf(row.App.InstallLocation, ct);
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A program or shortcut chosen with Browse… or dropped on the window.</summary>
    public async void SelectFile(string path)
    {
        if (IsBusy) return;
        try
        {
            var target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.ResolveTarget(path) ?? path : path;
            string? product = null;
            try
            {
                if (File.Exists(target)) product = FileVersionInfo.GetVersionInfo(target).ProductName;
            }
            catch
            {
            }
            var apps = Apps?.Select(r => r.App).ToList()
                       ?? await Task.Run(() => InstalledAppsProvider.Load(Settings.GetBool(ShowStoreAppsKey, true)), Life);
            var self = AppInfo.ExecutablePath;
            var app = UninstallRules.Resolve(target, apps.Where(a => !UninstallRules.IsSelf(a, self)), product);
            if (app is null)
            {
                PickerNotice = UninstallerText.SelectionUnavailable;
                Raise();
                return;
            }
            Select(app);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // -------------------------------------------------------------------- scan

    /// <summary>Selects an app and scans what removing it would take (upstream select(appURL:)).</summary>
    public async void Select(InstalledApp app)
    {
        if (IsBusy) return;
        var gen = NewStep(out var ct);
        Target = app;
        TargetIcon = Apps?.FirstOrDefault(r => r.App.Id == app.Id)?.Icon;
        Plan = UninstallRules.Plan(app, Settings.GetBool(PreferQuietKey, false));
        Stage = ReviewStage.BeforeUninstall;
        Notice = Plan.CanRun ? null : Plan.Problem;
        PickerNotice = null;
        Items = [];
        _identity = null;
        _uninstalledBytes = 0;
        Phase = UninstallerPhase.Scanning;
        BusyMessage = UninstallerText.Scanning;
        BusyDetail = null;
        Raise();
        try
        {
            TargetIcon ??= await Task.Run(() => InstalledAppsProvider.Icon(app), ct);
            var (identity, items, size) = await Task.Run(() =>
            {
                var env = ScanEnvironment.Current();
                var id = BuildIdentity(app, env);
                var others = OthersThan(app);
                var found = LeftoverScanner.Scan(id, others, env, afterUninstall: false, ct);
                var appSize = app.EstimatedSizeBytes > 0 ? app.EstimatedSizeBytes
                    : id.InstallLocation.Length > 0 ? DiskUsage.SizeOf(id.InstallLocation, ct) : 0;
                return (id, found, appSize);
            }, ct);
            if (gen != _generation) return;
            _identity = identity;
            var uninstallerRow = new Leftover
            {
                Path = identity.InstallLocation.Length > 0 ? identity.InstallLocation : app.Id,
                Kind = LeftoverKind.Uninstaller,
                Category = LeftoverCategory.App,
                Size = size,
                Include = Plan.CanRun,
                DisplayName = app.Name,
                Detail = Plan.CanRun ? Plan.CommandText : Plan.Problem,
            };
            Items = [uninstallerRow, .. items];
            Phase = UninstallerPhase.Review;
            Raise();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Uninstaller: scan failed", ex);
            if (gen != _generation) return;
            Notice = $"The scan failed: {ex.Message}";
            Items = [];
            Phase = UninstallerPhase.Review;
            Raise();
        }
    }

    private static AppIdentity BuildIdentity(InstalledApp app, ScanEnvironment env)
    {
        var exes = new List<string>();
        if (InstalledAppRules.IconFile(app.IconPath) is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exes.Add(icon);
        if (InstalledAppRules.MainExecutableGuess(LeftoverRules.InferInstallLocation(app)) is { } main) exes.Add(main);
        return LeftoverRules.Identity(app, exes, env.ProgramFolders);
    }

    private static OtherApps OthersThan(InstalledApp app)
    {
        var all = InstalledAppsProvider.Load(includeStore: true);
        return new OtherApps(all.Where(a => a.Id != app.Id
                                            && !(app.IsStore && a.PackageFamilyName == app.PackageFamilyName)
                                            && !(a.RegistryKeyName.Length > 0 && a.RegistryKeyName == app.RegistryKeyName && a.Name == app.Name)));
    }

    // --------------------------------------------------------------- actions

    /// <summary>Upstream reset(): back to the picker (not while something is being removed).</summary>
    public void Reset()
    {
        if (IsBusy) return;
        NewStep(out _);
        Target = null;
        TargetIcon = null;
        Plan = null;
        Items = [];
        Notice = null;
        PickerNotice = null;
        Freed = 0;
        Failed = [];
        Backups = [];
        _identity = null;
        Phase = UninstallerPhase.Pick;
        Raise();
        LoadApps();
    }

    /// <summary>Whether the primary action runs the app's uninstaller (otherwise it only recycles leftovers).</summary>
    public bool PrimaryRunsUninstaller =>
        Stage == ReviewStage.BeforeUninstall && Items.Any(i => i.Kind == LeftoverKind.Uninstaller && i.Include) && Plan?.CanRun == true;

    /// <summary>Runs the app's own uninstaller (after the window's confirmation), then rescans what it left.</summary>
    public async void Uninstall()
    {
        if (Phase != UninstallerPhase.Review || Target is null || Plan is null || !Plan.CanRun) return;
        var app = Target;
        var plan = Plan;
        var gen = NewStep(out var ct);
        var reviewed = Items.Where(i => i.Kind != LeftoverKind.Uninstaller).ToDictionary(i => i.Path, i => i.Include, StringComparer.OrdinalIgnoreCase);
        var appSize = Items.FirstOrDefault(i => i.Kind == LeftoverKind.Uninstaller)?.Size ?? 0;
        Phase = UninstallerPhase.Uninstalling;
        BusyMessage = $"Running {app.Name}’s uninstaller…";
        BusyDetail = plan.Elevate && !IsAdmin
            ? "Windows asks for administrator permission because the app is installed for all users. Follow its uninstaller, then come back here."
            : "Follow the app’s uninstaller, then come back here to review what it left behind.";
        WaitingForHandOff = false;
        Notice = null;
        Raise();
        UninstallResult result;
        try
        {
            result = await UninstallRunner.RunAsync(plan, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (gen != _generation) return;
        switch (result.Outcome)
        {
            case UninstallOutcome.Declined:
                BackToReview("The uninstaller didn’t start: administrator permission was declined.");
                return;
            case UninstallOutcome.Failed:
                BackToReview($"The uninstaller couldn’t run: {result.Error}");
                return;
        }

        if (await Task.Run(() => UninstallRunner.IsStillInstalled(app), ct))
        {
            if (plan.Method != UninstallMethod.Command)
            {
                BackToReview($"{app.Name} is still installed. Its uninstaller may have been cancelled.");
                return;
            }
            // Many uninstallers relaunch a copy of themselves and exit at once: wait for the entry to go.
            WaitingForHandOff = true;
            BusyMessage = "Waiting for the uninstaller to finish…";
            Raise();
            try
            {
                while (await Task.Run(() => UninstallRunner.IsStillInstalled(app), ct))
                    await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (gen != _generation) return;
        }
        await AfterUninstallAsync(app, appSize, reviewed, gen, ct);
    }

    /// <summary>The person says the uninstaller is done (or was closed) while we wait for its hand-off.</summary>
    public void UninstallerFinished()
    {
        if (Phase != UninstallerPhase.Uninstalling || !WaitingForHandOff || Target is null) return;
        var app = Target;
        if (UninstallRunner.IsStillInstalled(app))
        {
            NewStep(out _);
            BackToReview($"{app.Name} is still installed. Its uninstaller may have been cancelled.");
            return;
        }
        // The polling loop notices on its next tick; nothing else to do.
    }

    private void BackToReview(string notice)
    {
        WaitingForHandOff = false;
        Notice = notice;
        Phase = UninstallerPhase.Review;
        Raise();
    }

    private async Task AfterUninstallAsync(InstalledApp app, long appSize, Dictionary<string, bool> reviewed, int gen, CancellationToken ct)
    {
        _uninstalledBytes = appSize;
        WaitingForHandOff = false;
        Stage = ReviewStage.AfterUninstall;
        Phase = UninstallerPhase.Scanning;
        BusyMessage = UninstallerText.Scanning;
        BusyDetail = null;
        Raise();
        try
        {
            var identity = _identity;
            var items = await Task.Run(() =>
            {
                var env = ScanEnvironment.Current();
                var id = identity ?? BuildIdentity(app, env);
                return LeftoverScanner.Scan(id, OthersThan(app), env, afterUninstall: true, ct);
            }, ct);
            if (gen != _generation) return;
            foreach (var item in items)
            {
                if (reviewed.TryGetValue(item.Path, out var include) && LeftoverRules.CanSelect(item, IsAdmin)) item.Include = include;
            }
            Items = items;
            Notice = null;
            if (items.Count == 0)
            {
                Finish(_uninstalledBytes, [], []);
                return;
            }
            Phase = UninstallerPhase.Review;
            Raise();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Moves the selected leftovers to the Recycle Bin and deletes the selected keys (after the window's confirmation).</summary>
    public async void RemoveSelected()
    {
        if (Phase != UninstallerPhase.Review) return;
        var chosen = Items.Where(i => i.Include && i.Kind != LeftoverKind.Uninstaller).ToList();
        if (chosen.Count == 0) return;
        var gen = NewStep(out var ct);
        Phase = UninstallerPhase.Removing;
        BusyMessage = UninstallerText.Removing;
        BusyDetail = null;
        Raise();
        try
        {
            var install = _identity?.InstallLocation ?? string.Empty;
            var result = await Task.Run(() => LeftoverRemover.RemoveAsync(chosen, ScanEnvironment.Current(), install, ct), ct);
            if (gen != _generation) return;
            Finish(_uninstalledBytes + result.Freed, result.Failed, result.Backups);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Uninstaller: removal failed", ex);
            if (gen != _generation) return;
            Finish(_uninstalledBytes, chosen.Select(c => (c, ex.Message)).ToList(), []);
        }
    }

    /// <summary>After the uninstaller ran, the person keeps every leftover.</summary>
    public void KeepLeftovers()
    {
        if (Phase != UninstallerPhase.Review || Stage != ReviewStage.AfterUninstall) return;
        Finish(_uninstalledBytes, [], []);
    }

    private void Finish(long freed, IReadOnlyList<(Leftover, string)> failed, IReadOnlyList<string> backups)
    {
        Freed = freed;
        Failed = failed;
        Backups = backups;
        Items = [];
        Phase = UninstallerPhase.Done;
        Raise();
        if (Stage == ReviewStage.AfterUninstall) Apps = null; // the list changed; reload next time
    }

    /// <summary>Cancels a running scan (upstream: closing or Cancel while scanning).</summary>
    public void CancelScan()
    {
        if (Phase != UninstallerPhase.Scanning) return;
        if (Stage == ReviewStage.AfterUninstall)
        {
            NewStep(out _);
            Finish(_uninstalledBytes, [], []);
            return;
        }
        Reset();
    }

    private int NewStep(out CancellationToken ct)
    {
        _step?.Cancel();
        _step =CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        ct = _step.Token;
        return ++_generation;
    }

    /// <summary>Stops every scan, wait and removal of this window (the token sources are only cancelled,
    /// not disposed, so continuations still running can observe it safely).</summary>
    public void Dispose()
    {
        _generation++;
        _life.Cancel();
        Changed = null;
    }
}
