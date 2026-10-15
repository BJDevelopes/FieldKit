// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>Settings → Utilities → App updates (port of upstream's AppUpdatesSettings).</summary>
public static class AppUpdatesPage
{
    public static FrameworkElement Build() => Build(AppUpdatesController.Shared);

    public static FrameworkElement Build(AppUpdatesController controller)
    {
        SheetHost? sheet = null;
        var list = new AppUpdatesListView(controller, () => sheet);
        Ui.Track(list, () =>
        {
            controller.CheckIfNeeded();
            return null;
        });

        // Background schedule.
        var frequency = WingetUi.Segmented(
        [
            ("off", AppUpdateStrings.FrequencyOff),
            ("daily", AppUpdateStrings.FrequencyDaily),
            ("weekly", AppUpdateStrings.FrequencyWeekly),
        ], AppUpdateRules.FrequencyValue(AppUpdatesController.Frequency), value => Settings.Set(AppUpdatesController.FrequencyKey, value));
        var next = Ui.Caption(string.Empty, 12);
        var notify = Ui.ToggleRow(AppUpdateStrings.NotifyToggle, null, AppUpdatesController.NotifyKey, true);
        void SyncSchedule()
        {
            var on = AppUpdatesController.Frequency != CheckFrequency.Off;
            notify.IsEnabled = on;
            notify.Opacity = on ? 1 : 0.5;
            var when = controller.NextCheck;
            next.Text = on && when is { } w ? string.Format(AppUpdateStrings.NextCheckFormat, w.ToString("g", System.Globalization.CultureInfo.CurrentCulture)) : string.Empty;
            next.Visibility = next.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncSchedule();
        Ui.Track(next, () =>
        {
            var observer = Settings.Observe(SyncSchedule, AppUpdatesController.FrequencyKey);
            controller.Changed += SyncSchedule;
            return new ActionDisposable(() =>
            {
                observer.Dispose();
                controller.Changed -= SyncSchedule;
            });
        });

        // Sources: the last enabled one cannot be switched off.
        var winget = SourceToggle(AppUpdateStrings.IncludeWingetToggle, null, AppUpdatesController.IncludeWingetKey, controller);
        var store = SourceToggle(AppUpdateStrings.IncludeStoreToggle, AppUpdateStrings.IncludeStoreCaption, AppUpdatesController.IncludeStoreKey, controller);
        var unknown = SourceToggle(AppUpdateStrings.IncludeUnknownToggle, AppUpdateStrings.IncludeUnknownCaption, AppUpdatesController.IncludeUnknownKey, controller);

        var page = Ui.Page(AppUpdateStrings.PageTitle, null,
            Ui.Group(AppUpdateStrings.PageTitle, Ui.Caption(AppUpdateStrings.Caption, 12.5), list),
            Ui.Group(AppUpdateStrings.FrequencyLabel, Ui.Stack(8, frequency, next), notify),
            Ui.Group(AppUpdateStrings.SourcesTitle, winget, store, unknown),
            Ui.Group(AppUpdateStrings.ShortcutTitle, Ui.ShortcutRow(AppUpdateStrings.ShortcutRow, AppUpdatesController.ShortcutKey, string.Empty)));
        sheet = new SheetHost(page);
        return sheet;
    }

    private static readonly string[] SourceKeys =
        [AppUpdatesController.IncludeWingetKey, AppUpdatesController.IncludeStoreKey, AppUpdatesController.IncludeUnknownKey];

    private static FrameworkElement SourceToggle(string title, string? caption, string key, AppUpdatesController controller)
    {
        var row = Ui.ToggleRow(title, caption, key, true, _ => controller.SourceSelectionDidChange());
        var toggle = ((Grid)row).Children.OfType<CheckBox>().FirstOrDefault();
        void Sync()
        {
            var enabled = SourceKeys.Count(k => Settings.GetBool(k, true));
            if (toggle is not null) toggle.IsEnabled = !(Settings.GetBool(key, true) && enabled == 1);
        }
        Sync();
        Ui.Track(row, () => Settings.Observe(Sync, SourceKeys));
        return row;
    }
}
