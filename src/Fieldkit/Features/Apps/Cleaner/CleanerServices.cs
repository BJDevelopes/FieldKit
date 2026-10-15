// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner.Messaging;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Interop;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>
/// Runs while the Cleaner is installed: binds the "Open Cleaner" shortcut and, when the feature
/// is uninstalled or switched off, closes every cleaner window and cancels a running scan.
/// </summary>
public sealed class CleanerShellService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "cleaner";

    protected override void OnStart()
    {
        _hotkey = Hotkeys.Bind(CleanerKeys.Shortcut, string.Empty, CleanerModule.OpenCleaner);
    }

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        ToolWindow.CloseSingle(CleanerWindow.Key);
        ToolWindow.CloseSingle(MessagingWindow.Key);
        if (JunkCleaner.Shared.State == JunkCleaner.Phase.Scanning) JunkCleaner.Shared.Reset();
    }
}

/// <summary>
/// The automatic cleanup (upstream CleanerScheduler): daily or weekly at a chosen time. Exists
/// only while a frequency is chosen; a one-minute wall-clock check catches sleep, clock and
/// time-zone changes, and a run missed while the PC was off fires two minutes after start.
/// </summary>
public sealed class CleanerScheduleService : FeatureService
{
    private DispatcherTimer? _timer;
    private IDisposable? _observer;
    private bool _running;

    public override string FeatureId => "cleaner";

    public override IReadOnlyList<string> EnableKeys => [CleanerKeys.ScheduleFrequency];

    public override bool ShouldRun() => Frequency != CleanerSchedule.Frequency.Off;

    /// <summary>When the next automatic pass fires (shown under the schedule so arming it gives visible confirmation).</summary>
    public DateTime? NextFire { get; private set; }

    public static event Action? NextFireChanged;

    private static CleanerSchedule.Frequency Frequency => CleanerSchedule.Sanitized(Settings.GetString(CleanerKeys.ScheduleFrequency, "off"));

    protected override void OnStart()
    {
        _observer = Settings.Observe(ScheduleNext, CleanerKeys.ScheduleHour, CleanerKeys.ScheduleMinute, CleanerKeys.ScheduleWeekday,
            CleanerKeys.ScheduleFrequency);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        ScheduleNext();
    }

    protected override void OnStop()
    {
        _timer?.Stop();
        _timer = null;
        _observer?.Dispose();
        _observer = null;
        NextFire = null;
        NextFireChanged?.Invoke();
    }

    private void ScheduleNext()
    {
        if (_timer is null) return;
        var now = DateTime.Now;
        var (frequency, hour, minute, weekday) = Current();
        var last = CleanerSchedule.FromStamp(Settings.GetDouble(CleanerKeys.LastAutoRun, 0));
        NextFire = CleanerSchedule.MissedRun(now, last, frequency, hour, minute, weekday)
            ? now.AddMinutes(2) // missed while the PC was off: soon, not the instant the app comes up
            : CleanerSchedule.NextFireDate(now, frequency, hour, minute, weekday);
        NextFireChanged?.Invoke();
    }

    private static (CleanerSchedule.Frequency, int, int, int) Current() =>
        (Frequency,
            Settings.GetInt(CleanerKeys.ScheduleHour, CleanerKeys.DefaultHour),
            Settings.GetInt(CleanerKeys.ScheduleMinute, CleanerKeys.DefaultMinute),
            Settings.GetInt(CleanerKeys.ScheduleWeekday, CleanerKeys.DefaultWeekday));

    private async void Tick()
    {
        if (_running || NextFire is null || DateTime.Now < NextFire) return;
        _running = true;
        try
        {
            var outcome = await JunkCleaner.Shared.RunAutomaticAsync();
            if (_timer is null) return; // stopped meanwhile
            if (outcome is null)
            {
                // The user is in the middle of a manual session: try again in ten minutes.
                NextFire = DateTime.Now.AddMinutes(10);
                NextFireChanged?.Invoke();
                return;
            }
            Settings.Set(CleanerKeys.LastAutoRun, CleanerSchedule.ToStamp(DateTime.Now));
            Settings.Set(CleanerKeys.LastAutoFreed, outcome.Freed);
            Settings.Set(CleanerKeys.LastAutoFailed, outcome.Failed);
            Notify(outcome);
            ScheduleNext();
        }
        catch (Exception ex)
        {
            Log.Error("Automatic cleanup failed", ex);
            NextFire = DateTime.Now.AddMinutes(10);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Even a run that found nothing reports, so a fresh schedule gives proof of life.</summary>
    private static void Notify(CleanerEngine.Outcome outcome)
    {
        if (!Settings.GetBool(CleanerKeys.ScheduleNotify, true)) return;
        Notifier.Show(S.ScheduleTitle, NotificationText(outcome.Freed, outcome.Failed));
    }

    public static string NotificationText(long freed, int failed)
    {
        var sentences = new List<string>();
        if (freed > 0) sentences.Add(string.Format(S.AutoNotificationFormat, ByteFormat.Format(freed)));
        if (failed > 0) sentences.Add(S.SomeFailed);
        if (sentences.Count == 0) sentences.Add(S.NothingFound);
        return string.Join(" ", sentences);
    }
}
