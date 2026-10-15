// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Wallpaper;

/// <summary>
/// Runs while the feature is installed: binds the optional next / previous / shuffle hotkeys
/// (none by default, upstream has no wallpaper shortcuts). Stopping it (uninstall) drops the
/// live catalog, history and thumbnails; the user's sources stay for a reinstall, like upstream.
/// </summary>
public sealed class WallpaperService : FeatureService
{
    private readonly List<IDisposable> _bindings = [];

    public override string FeatureId => WallpaperKeys.FeatureId;

    protected override void OnStart()
    {
        _bindings.Add(Hotkeys.Bind(WallpaperKeys.NextShortcut, string.Empty, () => _ = WallpaperController.NextAsync()));
        _bindings.Add(Hotkeys.Bind(WallpaperKeys.PreviousShortcut, string.Empty, () => _ = WallpaperController.PreviousAsync()));
        _bindings.Add(Hotkeys.Bind(WallpaperKeys.ShuffleShortcut, string.Empty, () => _ = WallpaperController.ShuffleAsync()));
    }

    protected override void OnStop()
    {
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        if (!WallpaperController.IsAvailable) WallpaperController.Reset();
    }
}

/// <summary>
/// "Change picture automatically" (a Windows addition; upstream only picks stills). Runs only
/// while <c>wallpaper.rotate</c> is on: one timer for the next change (counted from the last
/// change, so restarts do not reset it) and file watchers on your folders so new pictures join
/// the rotation. Stopping releases both and leaves the current wallpaper as it is.
/// </summary>
public sealed class WallpaperRotationService : FeatureService
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _timer;
    private IDisposable? _intervalObserver;
    private IDisposable? _sourcesObserver;
    private bool _changing;

    public override string FeatureId => WallpaperKeys.FeatureId;
    public override IReadOnlyList<string> EnableKeys => [WallpaperKeys.Rotate];
    public override bool EnabledByDefault => false;

    /// <summary>When the next automatic change happens (UTC), for the settings page.</summary>
    public DateTime? NextChangeUtc { get; private set; }

    protected override void OnStart()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += OnTick;
        WallpaperController.Applied += Reschedule;
        _intervalObserver = Settings.Observe(Reschedule, WallpaperKeys.RotateInterval);
        _sourcesObserver = Settings.Observe(RestartWatchers, WallpaperKeys.Sources);
        RestartWatchers();
        WallpaperController.FoldersWatched = true;
        Reschedule();
    }

    protected override void OnStop()
    {
        WallpaperController.Applied -= Reschedule;
        _intervalObserver?.Dispose();
        _intervalObserver = null;
        _sourcesObserver?.Dispose();
        _sourcesObserver = null;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        StopWatchers();
        WallpaperController.FoldersWatched = false;
        NextChangeUtc = null;
    }

    private void Reschedule()
    {
        if (_timer is null) return;
        _timer.Stop();
        var interval = TimeSpan.FromMinutes(RotationSchedule.ParseMinutes(Settings.GetString(WallpaperKeys.RotateInterval, "30")));
        var last = RotationSchedule.ParseTimestamp(Settings.GetString(WallpaperKeys.LastChange, string.Empty));
        var now = DateTime.UtcNow;
        var due = RotationSchedule.DueIn(last, interval, now);
        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);
        NextChangeUtc = now + due;
        _timer.Interval = due;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        if (_changing) return;
        _changing = true;
        try
        {
            await WallpaperController.NextAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Automatic wallpaper change failed", ex);
        }
        finally
        {
            _changing = false;
        }
        // A failed or skipped change still waits a full interval before trying again.
        if (_timer is not null && !_timer.IsEnabled)
        {
            Settings.Set(WallpaperKeys.LastChange, RotationSchedule.FormatTimestamp(DateTime.UtcNow));
            Reschedule();
        }
    }

    private void RestartWatchers()
    {
        StopWatchers();
        if (_timer is null) return;
        foreach (var source in WallpaperController.SourceState.Sources)
        {
            if (!Directory.Exists(source)) continue;
            try
            {
                var watcher = new FileSystemWatcher(source)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.Error += (_, _) => WallpaperController.InvalidateOwn();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warn($"Watching {source} failed: {ex.Message}");
            }
        }
        WallpaperController.InvalidateOwn();
    }

    private static void OnFolderChanged(object sender, FileSystemEventArgs e) => WallpaperController.InvalidateOwn();

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFolderChanged;
            watcher.Deleted -= OnFolderChanged;
            watcher.Renamed -= OnFolderChanged;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
