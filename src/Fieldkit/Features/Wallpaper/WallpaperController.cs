// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Wallpaper;

/// <summary>Settings keys of the wallpaper feature.</summary>
public static class WallpaperKeys
{
    public const string FeatureId = "wallpaper";
    public const string Filter = "wallpaper.filter";
    public const string ApplyAllDisplays = "wallpaper.applyAllDisplays";
    public const string Fit = "wallpaper.fit";
    public const string Sources = "wallpaper.sources";
    public const string Excluded = "wallpaper.excluded";
    public const string Rotate = "wallpaper.rotate";
    public const string RotateInterval = "wallpaper.rotateInterval";
    public const string Shuffle = "wallpaper.shuffle";
    public const string RotateSource = "wallpaper.rotateSource";
    public const string DifferentPerDisplay = "wallpaper.differentPerDisplay";
    public const string LastChange = "wallpaper.lastChange";
    public const string NextShortcut = "wallpaper.shortcut.next";
    public const string PreviousShortcut = "wallpaper.shortcut.previous";
    public const string ShuffleShortcut = "wallpaper.shortcut.shuffle";
}

/// <summary>Upstream <c>WallpaperFeatureStrings.enUS</c>, adapted to Windows.</summary>
public static class WallpaperText
{
    public const string PageTitle = "Wallpaper";
    public const string PanelDescription = "Pick a still wallpaper without opening Windows Settings.";
    public const string FilterAll = "All";
    public const string FilterOwn = "Your pictures";
    public const string FilterSystem = "Windows";
    public const string ApplyAllDisplays = "Show on all displays";
    public const string AddImage = "Add image";
    public const string AddFolder = "Add folder";
    public const string RemoveAdded = "Remove";
    public const string DoneRemoving = "Done";
    public const string SourceUnavailable = "Unavailable";
    public const string AddImagePrompt = "Choose images to keep in Fieldkit’s wallpaper list";
    public const string AddFolderPrompt = "Choose a folder of images to keep in Fieldkit’s wallpaper list";
    public const string OpenSystemSettings = "Open Wallpaper settings";
    public const string EmptyAll = "No wallpapers found";
    public const string EmptyOwn = "No pictures added yet";
    public const string EmptySystem = "No Windows wallpapers found";
    public const string ApplyFailed = "Could not set the wallpaper";
    public const string PreviousPage = "Previous";
    public const string NextPage = "Next";

    // Windows additions (rotation, history).
    public const string NextWallpaper = "Next wallpaper";
    public const string PreviousWallpaper = "Previous wallpaper";
    public const string ShuffleWallpaper = "Shuffle wallpaper";

    public static string Empty(WallpaperFilter filter) => filter switch
    {
        WallpaperFilter.Own => EmptyOwn,
        WallpaperFilter.System => EmptySystem,
        _ => EmptyAll,
    };
}

/// <summary>
/// The wallpaper catalog and every change of picture (the counterpart of upstream
/// <c>WallpaperService</c>, plus next / previous / shuffle). UI-thread state; scans and the
/// COM apply run off the UI thread. Nothing here runs on its own: the panel, actions and the
/// rotation service call in, and <see cref="Reset"/> drops everything when the feature goes away.
/// </summary>
public static class WallpaperController
{
    private static readonly WallpaperHistory<IReadOnlyList<WallpaperAssignment>> History = new();
    private static readonly RotationPicker Picker = new();
    private static List<WallpaperEntry>? _systemCache;
    private static int _scanGeneration;
    private static int _resetGeneration;
    private static volatile bool _entriesFresh;
    private static bool _dialogOpen;

    /// <summary>Raised on the UI thread whenever anything the panel shows changed.</summary>
    public static event Action? Changed;

    /// <summary>Raised on the UI thread after a picture change succeeded.</summary>
    public static event Action? Applied;

    public static IReadOnlyList<WallpaperEntry> Entries { get; private set; } = [];
    public static IReadOnlyList<WallpaperMonitor> Monitors { get; private set; } = [];
    public static bool IsLoading { get; private set; }
    public static bool IsApplying { get; private set; }
    public static string? LastError { get; private set; }

    public static bool IsAvailable => FeatureCatalog.IsAvailable(WallpaperKeys.FeatureId);

    public static bool CanGoBack => History.CanGoBack;

    public static WallpaperFilter Filter => WallpaperSupport.ParseFilter(Settings.GetString(WallpaperKeys.Filter, "all"));

    public static IReadOnlyList<WallpaperEntry> VisibleEntries => WallpaperSupport.Filtered(Entries, Filter);

    public static bool ApplyAllDisplays => Settings.GetBool(WallpaperKeys.ApplyAllDisplays, true);

    public static WallpaperSourceState SourceState => new(
        Settings.Get<string[]>(WallpaperKeys.Sources, []),
        Settings.Get<string[]>(WallpaperKeys.Excluded, []));

    public static IEnumerable<WallpaperSourceInfo> SourceInfos => SourceState.Sources.Select(WallpaperSources.Describe);

    /// <summary>True when a display currently shows <paramref name="path"/>.</summary>
    public static bool IsShowing(string path) =>
        Monitors.Any(m => WallpaperSupport.PathComparer.Equals(m.Wallpaper, path));

    // ------------------------------------------------------------------ catalog

    /// <summary>Rescans your folders (the Windows stills are cached after the first scan).</summary>
    public static async Task RefreshAsync(bool forceSystemRescan = false)
    {
        if (!IsAvailable)
        {
            Entries = [];
            IsLoading = false;
            Raise();
            return;
        }
        var token = Interlocked.Increment(ref _scanGeneration);
        var state = SourceState;
        var systemCache = forceSystemRescan ? null : _systemCache;
        if (Entries.Count == 0) IsLoading = true;
        RefreshMonitors();
        Raise();

        bool Go() => Volatile.Read(ref _scanGeneration) == token;
        (List<WallpaperEntry> System, List<WallpaperEntry> Merged)? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                var system = systemCache ?? WallpaperSupport.SystemEntries(WallpaperSupport.SystemWallpaperRoot, Go);
                var own = WallpaperSupport.OwnEntries(WallpaperSources.CollectImages(state, Go));
                return (system, WallpaperSupport.Merge(system, own));
            });
        }
        catch (Exception ex)
        {
            Log.Error("Scanning wallpapers failed", ex);
        }
        if (!Go()) return;
        IsLoading = false;
        if (result is { } r)
        {
            _systemCache ??= r.System;
            Entries = r.Merged;
            _entriesFresh = true;
        }
        Raise();
    }

    /// <summary>Marks your folders as changed on disk (file watchers; any thread).</summary>
    public static void InvalidateOwn() => _entriesFresh = false;

    /// <summary>
    /// True while the rotation service watches your folders. Without watchers nothing tells us
    /// about new pictures, so next / shuffle rescan first.
    /// </summary>
    public static bool FoldersWatched { get; set; }

    private static async Task EnsureEntriesAsync()
    {
        if (!_entriesFresh || !FoldersWatched || Entries.Count == 0) await RefreshAsync();
    }

    public static void RefreshMonitors()
    {
        try
        {
            Monitors = DesktopWallpaperApi.Monitors();
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the current wallpaper failed: {ex.Message}");
            Monitors = [];
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>A gallery click: the picture on every display, or on the one under the pointer.</summary>
    public static void Apply(WallpaperEntry entry) => _ = ApplyStepAsync(monitors =>
        WallpaperTargets.Assign(WallpaperTargets.TargetMonitors(ApplyAllDisplays, monitors, Pointer()), [entry.Path], false),
        pushHistory: true);

    /// <summary>Next picture: forward through history after going back, otherwise a new pick.</summary>
    public static async Task NextAsync()
    {
        if (!IsAvailable || IsApplying) return;
        if (History.TryForward(out var forward))
        {
            if (!await ApplyStepAsync(m => Restorable(forward, m), pushHistory: false)) History.TryBack(out _);
            return;
        }
        await PickAndApplyAsync(Settings.GetBool(WallpaperKeys.Shuffle, true));
    }

    /// <summary>A random picture of the rotation pictures, whatever the order setting.</summary>
    public static async Task ShuffleAsync()
    {
        if (!IsAvailable || IsApplying) return;
        await PickAndApplyAsync(shuffle: true);
    }

    /// <summary>Back to what the displays showed before the last change.</summary>
    public static async Task PreviousAsync()
    {
        if (!IsAvailable || IsApplying) return;
        if (!History.TryBack(out var step)) return;
        if (!await ApplyStepAsync(m => Restorable(step, m), pushHistory: false)) History.TryForward(out _);
    }

    /// <summary>Pictures rotation and next / shuffle draw from.</summary>
    public static List<string> RotationPool() =>
        WallpaperSupport.Filtered(Entries, WallpaperSupport.ParseFilter(Settings.GetString(WallpaperKeys.RotateSource, "all")))
            .Select(e => e.Path)
            .ToList();

    private static async Task PickAndApplyAsync(bool shuffle)
    {
        await EnsureEntriesAsync();
        if (!IsAvailable) return;
        var pool = RotationPool();
        if (pool.Count == 0)
        {
            LastError = WallpaperText.Empty(WallpaperSupport.ParseFilter(Settings.GetString(WallpaperKeys.RotateSource, "all")));
            Raise();
            return;
        }
        await ApplyStepAsync(monitors =>
        {
            var all = ApplyAllDisplays;
            var targets = WallpaperTargets.TargetMonitors(all, monitors, Pointer());
            var different = all && targets.Count > 1 && Settings.GetBool(WallpaperKeys.DifferentPerDisplay, false);
            var current = monitors.FirstOrDefault(m => targets.Count > 0 && m.Id == targets[0])?.Wallpaper;
            var picks = Picker.Pick(pool, current, different ? targets.Count : 1, shuffle);
            return WallpaperTargets.Assign(targets, picks, different);
        }, pushHistory: true);
    }

    private static List<WallpaperAssignment> Restorable(IReadOnlyList<WallpaperAssignment> step, IReadOnlyList<WallpaperMonitor> monitors) =>
        step.Where(a => monitors.Any(m => m.Id == a.MonitorId) && System.IO.File.Exists(a.Path)).ToList();

    private static async Task<bool> ApplyStepAsync(Func<IReadOnlyList<WallpaperMonitor>, List<WallpaperAssignment>> build, bool pushHistory)
    {
        if (!IsAvailable || IsApplying) return false;
        LastError = null;
        RefreshMonitors();
        var monitors = Monitors;
        var step = build(monitors);
        if (step.Count == 0)
        {
            LastError = WallpaperText.ApplyFailed;
            Raise();
            return false;
        }
        var before = WallpaperTargets.Snapshot(monitors);
        var reset = _resetGeneration;
        var fit = RotationSchedule.ParseFit(Settings.GetString(WallpaperKeys.Fit, "fill"));
        IsApplying = true;
        Raise();

        var ok = false;
        try
        {
            ok = await DesktopWallpaperApi.RunSta(() =>
            {
                DesktopWallpaperApi.Apply(step, fit);
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Setting the wallpaper failed", ex);
        }
        if (reset != _resetGeneration) return false; // uninstalled while applying
        IsApplying = false;
        RefreshMonitors();
        if (!ok)
        {
            LastError = WallpaperText.ApplyFailed;
            Raise();
            return false;
        }
        if (pushHistory)
        {
            // The first change remembers what was there, so Previous can go back to it.
            if (History.IsEmpty && before.Count > 0) History.Push(before);
            History.Push(step);
        }
        Settings.Set(WallpaperKeys.LastChange, RotationSchedule.FormatTimestamp(DateTime.UtcNow));
        Raise();
        try { Applied?.Invoke(); }
        catch (Exception ex) { Log.Error("Wallpaper observer failed", ex); }
        return true;
    }

    private static (int X, int Y)? Pointer() =>
        Native.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    // ------------------------------------------------------------------ your pictures

    public static void AddImages()
    {
        if (!IsAvailable || _dialogOpen) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = WallpaperSupport.DialogFilter,
            Title = WallpaperText.AddImagePrompt,
        };
        _dialogOpen = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
        }
        finally
        {
            _dialogOpen = false;
        }
        Remember(dialog.FileNames.Select(f => (f, false)));
    }

    public static void AddFolder()
    {
        if (!IsAvailable || _dialogOpen) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Multiselect = false,
            Title = WallpaperText.AddFolderPrompt,
        };
        _dialogOpen = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
        }
        finally
        {
            _dialogOpen = false;
        }
        Remember([(dialog.FolderName, true)]);
    }

    private static void Remember(IEnumerable<(string Path, bool IsFolder)> picked)
    {
        Save(WallpaperSources.Add(SourceState, picked));
        _entriesFresh = false;
        _ = RefreshAsync();
    }

    public static void RemoveSource(WallpaperSourceInfo source)
    {
        if (!IsAvailable) return;
        Save(WallpaperSources.RemoveSource(SourceState, source.Path, source.IsFolder));
        _entriesFresh = false;
        _ = RefreshAsync();
    }

    /// <summary>The gallery "x": forget a picture added as a file, or hide one from a folder.</summary>
    public static void RemoveEntry(WallpaperEntry entry)
    {
        if (!IsAvailable || entry.Source != WallpaperSource.Own) return;
        Save(WallpaperSources.RemoveEntry(SourceState, entry.Path));
        Interlocked.Increment(ref _scanGeneration); // an in-flight scan must not bring it back
        Entries = Entries.Where(e => !WallpaperSupport.PathComparer.Equals(e.Id, entry.Id)).ToList();
        if (IsLoading) _ = RefreshAsync();
        Raise();
    }

    private static void Save(WallpaperSourceState state)
    {
        Settings.Set(WallpaperKeys.Sources, state.Sources.ToArray());
        Settings.Set(WallpaperKeys.Excluded, state.Excluded.ToArray());
    }

    public static void OpenSystemSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:personalization-background") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening Windows wallpaper settings failed", ex);
        }
    }

    /// <summary>Feature uninstalled: drop the live catalog, history and thumbnails (sources stay).</summary>
    public static void Reset()
    {
        Interlocked.Increment(ref _scanGeneration);
        _resetGeneration++;
        _systemCache = null;
        _entriesFresh = false;
        Entries = [];
        Monitors = [];
        IsLoading = false;
        IsApplying = false;
        LastError = null;
        History.Clear();
        Picker.Reset();
        WallpaperThumbnails.Clear();
        Raise();
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Wallpaper observer failed", ex); }
    }
}
