// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner.Messaging;

/// <summary>
/// Messaging downloads automation (upstream WhatsAppDownloadScheduler + organizer monitoring):
/// one lightweight pass per day at 9:00 while automatic cleanup is on, and the organizer's folder
/// watch while organization is on. Nothing stays alive when the feature, messaging downloads or
/// both automations are off.
/// </summary>
public sealed class MessagingService : FeatureService
{
    private DispatcherTimer? _timer;
    private IDisposable? _observer;
    private bool _running;

    public override string FeatureId => "cleaner";

    public override IReadOnlyList<string> EnableKeys => [MessagingKeys.Enabled, MessagingKeys.Automatic, MessagingKeys.OrganizerEnabled];

    public override bool ShouldRun() => ShouldRunFor(
        Settings.GetBool(MessagingKeys.Enabled, false),
        Settings.GetBool(MessagingKeys.Automatic, false),
        Settings.GetBool(MessagingKeys.OrganizerEnabled, false));

    public static bool ShouldRunFor(bool enabled, bool automatic, bool organizer) => enabled && (automatic || organizer);

    public static DateTime? NextFire { get; private set; }

    protected override void OnStart()
    {
        _observer = Settings.Observe(Sync, MessagingKeys.Automatic, MessagingKeys.OrganizerEnabled, MessagingKeys.OrganizerDestination,
            MessagingKeys.OrganizerDelay, MessagingKeys.OrganizerLayout, MessagingKeys.OrganizerCategories, MessagingKeys.OrganizerDuplicateAction);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Sync();
    }

    protected override void OnStop()
    {
        _timer?.Stop();
        _timer = null;
        _observer?.Dispose();
        _observer = null;
        NextFire = null;
        MessagingOrganizer.Shared.Sync(false);
    }

    private void Sync()
    {
        if (_timer is null) return;
        var automatic = Settings.GetBool(MessagingKeys.Automatic, false);
        if (automatic && Settings.GetDouble(MessagingKeys.AutomaticStart, 0) <= 0 && !Settings.GetBool(MessagingKeys.IncludeExisting, false))
            Settings.Set(MessagingKeys.AutomaticStart, CleanerSchedule.ToStamp(DateTime.Now));
        ScheduleNext();
        MessagingOrganizer.Shared.Sync(Settings.GetBool(MessagingKeys.OrganizerEnabled, false));
    }

    private static void ScheduleNext()
    {
        if (!Settings.GetBool(MessagingKeys.Automatic, false))
        {
            NextFire = null;
            return;
        }
        var now = DateTime.Now;
        var last = CleanerSchedule.FromStamp(Settings.GetDouble(MessagingKeys.LastAutoRun, 0));
        NextFire = MessagingRules.MissedAutomaticCheck(now, last) ? now.AddMinutes(2) : MessagingRules.NextAutomaticCheck(now);
    }

    private async void Tick()
    {
        if (_running || NextFire is null || DateTime.Now < NextFire) return;
        var manager = MessagingDownloads.Shared;
        var userIsReviewing = manager.State == MessagingDownloads.Phase.Results && manager.ReviewVisible;
        if (manager.IsBusy || MessagingOrganizer.Shared.IsBusy || userIsReviewing)
        {
            NextFire = DateTime.Now.AddMinutes(10);
            return;
        }
        _running = true;
        try
        {
            await manager.ScanAsync();
            if (_timer is null) return;
            if (manager.State == MessagingDownloads.Phase.Failed)
            {
                Finish(0, 0, 1, notify: false);
                return;
            }
            // The person may have opened the review while the scan ran: never rewrite their
            // checkboxes or clean under their eyes.
            if (manager.ReviewVisible)
            {
                NextFire = DateTime.Now.AddMinutes(10);
                return;
            }
            manager.SelectAutomaticRules();
            if (manager.SelectedCount > 0) await manager.CleanSelectedAsync(automatic: true);
            else manager.CompleteAutomaticWithoutCleaning();
            var (moved, bytes, failed) = manager.LastResult;
            Finish(moved, bytes, failed, notify: true);
        }
        catch (Exception ex)
        {
            Log.Error("Messaging cleanup failed", ex);
            NextFire = DateTime.Now.AddMinutes(10);
        }
        finally
        {
            _running = false;
        }
    }

    private static void Finish(int moved, long bytes, int failed, bool notify)
    {
        Settings.Set(MessagingKeys.LastAutoRun, CleanerSchedule.ToStamp(DateTime.Now));
        if (notify && Settings.GetBool(MessagingKeys.Notify, true))
            Notifier.Show(S.MsgNotificationTitle, string.Format(S.MsgNotificationFormat, moved, ByteFormat.Format(bytes), failed));
        if (!MessagingDownloads.Shared.ReviewVisible) MessagingDownloads.Shared.Reset();
        ScheduleNext();
    }
}
