// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Core.Updates;

namespace Fieldkit.Features.Shell;

/// <summary>
/// Looks for a newer Fieldkit when it starts (unless switched off in Settings → About) and asks whether to
/// update. The check is one request to GitHub's public releases list: nothing about you or your PC is sent.
/// </summary>
public static class UpdateService
{
    public const string CheckKey = "update.check";
    public const string LastCheckKey = "update.lastCheck";
    public const string LatestKey = "update.latest";
    public const string SkippedKey = "update.skipped";

    private static readonly AppUpdater Updater = new();
    private static bool _checking;

    /// <summary>Closes Fieldkit the same way Quit does (set by the app); used once an installer has started.</summary>
    public static Action QuitAction { get; set; } = () => System.Windows.Application.Current?.Shutdown();

    public static AppVersion Current => AppVersion.TryParse(AppInfo.Version, out var v) ? v : new AppVersion(0, 0, 0);

    public static InstallKind Kind => UpdateLogic.DetectInstall(AppContext.BaseDirectory, File.Exists);

    public static bool CheckEnabled => Settings.GetBool(CheckKey, true);

    /// <summary>The newer release found by the latest check, if there is one.</summary>
    public static UpdateRelease? Pending { get; private set; }

    public static DateTime? LastCheck =>
        DateTime.TryParse(Settings.GetString(LastCheckKey, string.Empty), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : null;

    public static event Action? Changed;

    /// <summary>Checks shortly after start-up, off the start-up path, and shows the prompt when there is an update.</summary>
    public static void ScheduleStartupCheck(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                AppUpdater.CleanUp(); // an installer left by the update that just ran
                await StartupCheckAsync();
            }
            catch (Exception ex) { Log.Warn($"The update check failed: {ex.Message}"); }
        };
        timer.Start();
    }

    private static async Task StartupCheckAsync()
    {
        if (!CheckEnabled || _checking) return;
        var skipped = Settings.GetString(SkippedKey, string.Empty);
        // A known newer version keeps asking each start until it is installed or skipped; otherwise ask GitHub at most every few hours.
        var knownNewer = AppVersion.TryParse(Settings.GetString(LatestKey, string.Empty), out var latest) && latest > Current && latest.ToString() != skipped;
        if (!knownNewer && !UpdateLogic.NeedsNetworkCheck(DateTime.UtcNow, LastCheck)) return;

        var result = await CheckAsync();
        if (result is { Status: UpdateStatus.Available, Release: { } release } && release.Version.ToString() != skipped)
            UpdatePromptWindow.ShowFor(release);
    }

    /// <summary>Asks GitHub now and remembers the answer.</summary>
    public static async Task<UpdateCheck> CheckAsync()
    {
        _checking = true;
        try
        {
            var result = await Task.Run(() => Updater.CheckAsync(Current, includeBeta: false));
            if (result.Status != UpdateStatus.Failed)
            {
                Settings.Set(LastCheckKey, DateTime.UtcNow.ToString("o"));
                Settings.Set(LatestKey, result.Release?.Version.ToString() ?? Current.ToString());
                Pending = result.Release;
            }
            Log.Info($"Update check: {result.Status}{(result.Release is { } r ? " " + r.Version : string.Empty)}{(result.Error is { } e ? " (" + e + ")" : string.Empty)}");
            Changed?.Invoke();
            return result;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>"Skip this version": it is not offered again; a later one is.</summary>
    public static void Skip(UpdateRelease release) => Settings.Set(SkippedKey, release.Version.ToString());

    /// <summary>A person asked for an update (Check now): a version skipped earlier is offered again.</summary>
    public static void ForgetSkipped() => Settings.Remove(SkippedKey);

    internal static AppUpdater Service => Updater;
}
