// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner.Messaging;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>Settings → Utilities → Cleaner (upstream CleanerSettings: the system cleaner and messaging downloads).</summary>
public static class CleanerSettingsPage
{
    /// <summary>Renders the whole page at full height (the settings window crops it in --render-ui).</summary>
    public static void RenderPreview(string path, bool dark, double height = 2200) => PreviewRenderer.Render(Build(), path, 860, height, dark);

    public static FrameworkElement Build() => Ui.Page(S.Name, S.IntroCaption,
        OpenGroup(),
        Ui.Group(null, ScheduleBody(compactCaption: false)),
        ScanOptionsGroup(),
        MessagingGroup(),
        OrganizerGroup());

    private static FrameworkElement OpenGroup()
    {
        var open = Ui.Button("Open Cleaner", CleanerModule.OpenCleaner, accent: true, glyph: CleanerGlyphs.Broom);
        var row = Ui.Split(Ui.Stack(2, Ui.Text(S.Name, 13.5, FontWeights.Medium), Ui.Caption(S.PanelCaption)), open);
        return Ui.Group(null, row);
    }

    // -------------------------------------------------------------- schedule

    /// <summary>The automatic-cleanup card of the cleaner's idle face.</summary>
    public static Border ScheduleCard(bool compactCaption)
    {
        var card = CleanerUi.SoftCard(ScheduleBody(compactCaption));
        return card;
    }

    /// <summary>Frequency, then (when on) weekday and time, notification, what it may clean and the
    /// next / last run lines. Rebuilds itself when any schedule setting changes.</summary>
    private static FrameworkElement ScheduleBody(bool compactCaption)
    {
        var holder = new Border();
        void Build()
        {
            var frequency = CleanerSchedule.Sanitized(Settings.GetString(CleanerKeys.ScheduleFrequency, "off"));
            var combo = Ui.Choice(CleanerKeys.ScheduleFrequency,
                [("off", S.ScheduleOff), ("daily", S.ScheduleDaily), ("weekly", S.ScheduleWeekly)], "off");
            combo.MinWidth = 110;
            var head = Ui.Split(Ui.Row(8, Ui.Icon(CleanerGlyphs.Clock, 14, "TextSecondary"), Ui.Text(S.ScheduleTitle, 12.5, FontWeights.Medium)), combo);
            var stack = Ui.Stack(8, head);
            if (frequency != CleanerSchedule.Frequency.Off)
            {
                Ui.Append(stack, 8, TimeRow(frequency));
                Ui.Append(stack, 8, Ui.Check(S.ScheduleNotifyToggle, CleanerKeys.ScheduleNotify, true));
                if (!compactCaption)
                {
                    Ui.Append(stack, 10, Ui.Text(S.ScheduleCategories, 12.5, FontWeights.Medium));
                    var checks = Ui.Columns(2, 8,
                        Ui.Check(S.CatTemp, CleanerKeys.ScheduleTemp, true),
                        Ui.Check(S.CatCaches, CleanerKeys.ScheduleCaches, true),
                        Ui.Check(S.CatLogs, CleanerKeys.ScheduleLogs, true),
                        Ui.Check(S.CatDeveloper, CleanerKeys.ScheduleDeveloper, true));
                    Ui.Append(stack, 4, checks);
                    Ui.Append(stack, 4, CleanerUi.Small(S.ScheduleCategoriesCaption));
                }
                var next = FeatureRuntime.Service<CleanerScheduleService>()?.NextFire;
                if (next is not null) Ui.Append(stack, 6, CleanerUi.Small(string.Format(S.ScheduleNextFormat, CleanerRules.RelativeDate(next.Value, DateTime.Now))));
                if (LastRunLine() is { } last) Ui.Append(stack, 4, CleanerUi.Small(last, "TextTertiary"));
            }
            Ui.Append(stack, 6, CleanerUi.Small(S.ScheduleCaption));
            holder.Child = stack;
        }
        Build();
        Ui.Track(holder, () =>
        {
            var settings = Settings.Observe(Build, CleanerKeys.ScheduleFrequency, CleanerKeys.LastAutoRun);
            void OnNext() => Ui.OnUi(Build);
            CleanerScheduleService.NextFireChanged += OnNext;
            return new ActionDisposable(() =>
            {
                settings.Dispose();
                CleanerScheduleService.NextFireChanged -= OnNext;
            });
        });
        return holder;
    }

    private static FrameworkElement TimeRow(CleanerSchedule.Frequency frequency)
    {
        var row = new WrapPanel();
        void Add(FrameworkElement e)
        {
            e.Margin = new Thickness(0, 0, 6, 4);
            row.Children.Add(e);
        }
        if (frequency == CleanerSchedule.Frequency.Weekly)
        {
            var names = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
            Add(CleanerUi.IntChoice(CleanerKeys.ScheduleWeekday,
                Enumerable.Range(1, 7).Select(d => (d, CultureInfo.CurrentCulture.TextInfo.ToTitleCase(names[d - 1]))).ToList(),
                CleanerKeys.DefaultWeekday));
        }
        var hour = Settings.GetInt(CleanerKeys.ScheduleHour, CleanerKeys.DefaultHour);
        if (CleanerSchedule.Uses12HourClock())
        {
            var (h12, _) = CleanerSchedule.Hour12Components(hour);
            Add(CleanerUi.Combo(Enumerable.Range(1, 12).Select(h => (h, h.ToString(CultureInfo.CurrentCulture))).ToList(), h12,
                v => Settings.Set(CleanerKeys.ScheduleHour, CleanerSchedule.Hour24(v, CleanerSchedule.Hour12Components(Settings.GetInt(CleanerKeys.ScheduleHour, CleanerKeys.DefaultHour)).IsPm))));
        }
        else
        {
            Add(CleanerUi.IntChoice(CleanerKeys.ScheduleHour, Enumerable.Range(0, 24).Select(h => (h, h.ToString(CultureInfo.CurrentCulture))).ToList(),
                CleanerKeys.DefaultHour));
        }
        var minute = Settings.GetInt(CleanerKeys.ScheduleMinute, CleanerKeys.DefaultMinute);
        var minutes = Enumerable.Range(0, 12).Select(i => i * 5).ToList();
        if (minute is >= 0 and <= 59 && !minutes.Contains(minute)) minutes = [.. minutes.Append(minute).Order()];
        Add(CleanerUi.IntChoice(CleanerKeys.ScheduleMinute, minutes.Select(m => (m, $":{m:00}")).ToList(), CleanerKeys.DefaultMinute));
        if (CleanerSchedule.Uses12HourClock())
        {
            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            var (_, pm) = CleanerSchedule.Hour12Components(hour);
            Add(CleanerUi.Combo([(false, string.IsNullOrEmpty(dtf.AMDesignator) ? "AM" : dtf.AMDesignator), (true, string.IsNullOrEmpty(dtf.PMDesignator) ? "PM" : dtf.PMDesignator)],
                pm, isPm => Settings.Set(CleanerKeys.ScheduleHour,
                    CleanerSchedule.Hour24(CleanerSchedule.Hour12Components(Settings.GetInt(CleanerKeys.ScheduleHour, CleanerKeys.DefaultHour)).Hour12, isPm))));
        }
        return row;
    }

    private static string? LastRunLine()
    {
        var last = CleanerSchedule.FromStamp(Settings.GetDouble(CleanerKeys.LastAutoRun, 0));
        if (last is null) return null;
        var freed = Settings.Get<long>(CleanerKeys.LastAutoFreed, 0);
        var line = freed > 0
            ? string.Format(S.ScheduleLastFormat, ByteFormat.Format(freed))
            : string.Format(S.ScheduleRanFormat, CleanerRules.RelativeDate(last.Value, DateTime.Now));
        return Settings.GetInt(CleanerKeys.LastAutoFailed, 0) > 0 ? line + " " + S.SomeFailed : line;
    }

    // ---------------------------------------------------------- scan options

    private static FrameworkElement ScanOptionsGroup()
    {
        var temp = Ui.Split(Ui.Stack(2, Ui.Text(S.CatTemp, 13.5, FontWeights.Medium),
                Ui.Caption("How long a file in your temporary folder must sit untouched before it is offered.")),
            CleanerUi.IntChoice(CleanerKeys.TempAgeDays,
                CleanerRules.TempAgeChoices.Select(d => (d, string.Format(S.ScreenshotsAfterFormat, d))).ToList(), CleanerRules.DefaultTempAgeDays));

        var screenshotChoices = new List<(int, string)> { (0, S.ScheduleOff) };
        screenshotChoices.AddRange(CleanerRules.ScreenshotAgeChoices.Select(d => (d, string.Format(S.ScreenshotsAfterFormat, d))));
        var screenshots = Ui.Split(Ui.Stack(2, Ui.Text(S.CatScreenshots, 13.5, FontWeights.Medium), Ui.Caption(S.ScreenshotsSettingCaption)),
            CleanerUi.IntChoice(CleanerKeys.ScreenshotAgeDays, screenshotChoices, CleanerRules.DefaultScreenshotAgeDays));

        var adminNote = Elevation.IsAdministrator
            ? "Windows Update can download these files again when it needs them."
            : "Fieldkit is not running as administrator, so these are skipped even when switched on.";
        var system = Ui.ToggleRow(S.CatSystemCaches, $"{S.SystemCachesCaption} {adminNote}", CleanerKeys.SystemCaches, false);
        return Ui.Group("Scan", temp, screenshots, system);
    }

    // ------------------------------------------------------------- messaging

    private static FrameworkElement MessagingGroup()
    {
        var holder = new ContentControl();
        void Build()
        {
            var enabled = Settings.GetBool(MessagingKeys.Enabled, false);
            var rows = new List<UIElement?>
            {
                Ui.ToggleRow(S.MsgTitle, enabled ? S.MsgIntro : S.MsgHubDescription, MessagingKeys.Enabled, false),
            };
            if (enabled)
            {
                rows.Add(Ui.Stack(8, Ui.Text(S.MsgSources, 13.5, FontWeights.Medium),
                    Ui.ToggleRow(S.MsgSourceWhatsApp, S.MsgSourceWhatsAppCaption, MessagingKeys.SourceWhatsApp, true),
                    Ui.ToggleRow(S.MsgSourceTelegram, S.MsgSourceTelegramCaption, MessagingKeys.SourceTelegram, true)));
                rows.Add(Ui.Split(Ui.Stack(2, Ui.Text(S.MsgAutomatic, 13.5, FontWeights.Medium), Ui.Caption(S.MsgAutomaticCaption)), AutomaticSwitch()));
                rows.Add(Ui.Split(Ui.Stack(2, Ui.Text(S.MsgRetention, 13.5, FontWeights.Medium), Ui.Caption(S.MsgRetentionCaption)),
                    CleanerUi.IntChoice(MessagingKeys.RetentionDays,
                        MessagingRules.AllowedRetentionDays.Select(d => (d, string.Format(S.MsgDaysFormat, d))).ToList(), 7, _ => RefreshResults())));
                rows.Add(CategoryChecks(MessagingKeys.Categories, MessagingKeys.DefaultCategories, RefreshResults));
                rows.Add(Ui.ToggleRow(S.ScheduleNotifyToggle, null, MessagingKeys.Notify, true));
                var review = Ui.Button("Review downloads…", MessagingWindow.Open, glyph: CleanerGlyphs.Download);
                rows.Add(Ui.Split(Ui.Stack(2, Ui.Text(S.MsgActivity, 13.5, FontWeights.Medium), Ui.Caption(MessagingWindow.LastRunLine())), review));
            }
            holder.Content = Ui.Group(S.MsgTitle, rows.ToArray());
        }
        Build();
        Ui.Track(holder, () => Settings.Observe(Build, MessagingKeys.Enabled));
        return holder;
    }

    /// <summary>The "Clean up automatically" switch. Turning it on first scans; when existing files
    /// already match, the user chooses whether automation may manage them (upstream alert).</summary>
    private static FrameworkElement AutomaticSwitch()
    {
        var cb = new CheckBox { IsChecked = Settings.GetBool(MessagingKeys.Automatic, false) };
        cb.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        cb.Click += async (_, _) =>
        {
            if (cb.IsChecked != true)
            {
                Settings.Set(MessagingKeys.Automatic, false);
                return;
            }
            cb.IsEnabled = false;
            try
            {
                var manager = MessagingDownloads.Shared;
                await manager.ScanAsync();
                if (manager.State == MessagingDownloads.Phase.Failed)
                {
                    cb.IsChecked = false;
                    return;
                }
                var includeExisting = false;
                if (manager.EligibleCount > 0)
                {
                    var answer = CleanerUi.Ask(Window.GetWindow(cb), S.MsgFirstTitle, string.Format(S.MsgFirstMessageFormat, manager.EligibleCount),
                        S.MsgFutureOnly, S.MsgIncludeExisting, S.Cancel);
                    if (answer is < 0 or 2)
                    {
                        cb.IsChecked = false;
                        return;
                    }
                    includeExisting = answer == 1;
                }
                Settings.Set(MessagingKeys.IncludeExisting, includeExisting);
                Settings.Set(MessagingKeys.AutomaticStart, CleanerSchedule.ToStamp(DateTime.Now));
                Settings.Set(MessagingKeys.Automatic, true);
                _ = manager.ScanAsync();
            }
            finally
            {
                cb.IsEnabled = true;
            }
        };
        return cb;
    }

    private static void RefreshResults()
    {
        var m = MessagingDownloads.Shared;
        if (m.State is MessagingDownloads.Phase.Results or MessagingDownloads.Phase.Done) _ = m.ScanAsync();
    }

    /// <summary>"All" plus the six file types in two columns, bound to an encoded category list.</summary>
    internal static FrameworkElement CategoryChecks(string key, string defaultRaw, Action? changed = null)
    {
        var holder = new ContentControl();
        void Build()
        {
            var enabled = MessagingRules.DecodeCategories(Settings.GetString(key, defaultRaw));
            var all = CleanerUi.Check(enabled.Count == Enum.GetValues<MessagingCategory>().Length, v =>
            {
                Settings.Set(key, MessagingRules.EncodeCategories(v ? Enum.GetValues<MessagingCategory>() : []));
                changed?.Invoke();
            }, S.MsgAllTypes);
            var checks = Enum.GetValues<MessagingCategory>().Select(c => (UIElement)CleanerUi.Check(enabled.Contains(c), v =>
            {
                var set = MessagingRules.DecodeCategories(Settings.GetString(key, defaultRaw));
                if (v) set.Add(c);
                else set.Remove(c);
                Settings.Set(key, MessagingRules.EncodeCategories(set));
                changed?.Invoke();
            }, MessagingRules.CategoryName(c))).ToArray();
            holder.Content = Ui.Stack(8, Ui.Text(S.MsgFileTypes, 13.5, FontWeights.Medium), all, Ui.Columns(2, 8, checks));
        }
        Build();
        Ui.Track(holder, () => Settings.Observe(Build, key));
        return holder;
    }

    // ------------------------------------------------------------- organizer

    private static FrameworkElement OrganizerGroup()
    {
        var holder = new ContentControl();
        void Build()
        {
            if (!Settings.GetBool(MessagingKeys.Enabled, false))
            {
                holder.Content = null;
                return;
            }
            var on = Settings.GetBool(MessagingKeys.OrganizerEnabled, false);
            var title = Ui.Row(8, Ui.Text(S.OrgTitle, 15, FontWeights.SemiBold), CleanerUi.ExperimentalBadge());
            title.Margin = new Thickness(2, 0, 0, 8);
            var rows = new List<UIElement?>
            {
                Ui.ToggleRow(S.OrgEnabled, S.OrgDescription, MessagingKeys.OrganizerEnabled, false,
                    v => { if (v) _ = MessagingDownloads.Shared.ScanAsync(); }),
            };
            if (on)
            {
                rows.Add(CleanerUi.Small(S.OrgEnabledCaption, "Orange", 12));
                rows.Add(DestinationRow());
                rows.Add(Ui.ChoiceRow(S.OrgOrganization, MessagingKeys.OrganizerLayout,
                    [("flat", S.OrgFlat), ("category", S.OrgByType), ("month", S.OrgByMonth)], "flat"));
                rows.Add(Ui.Split(Ui.Text(S.OrgDelay, 13.5, FontWeights.Medium),
                    CleanerUi.IntChoice(MessagingKeys.OrganizerDelay,
                        MessagingRules.AllowedOrganizerDelayMinutes.Select(m => (m, string.Format(S.OrgMinutesFormat, m))).ToList(), 5)));
                rows.Add(Ui.ChoiceRow(S.OrgDuplicateAction, MessagingKeys.OrganizerDuplicateAction,
                    [("trashNew", S.OrgTrashDuplicate), ("keepBoth", S.OrgKeepBoth), ("replaceExisting", S.OrgReplaceExisting)], "trashNew",
                    S.OrgDuplicateCaption));
                rows.Add(CategoryChecks(MessagingKeys.OrganizerCategories, MessagingKeys.AllCategories));
                rows.Add(MessagingWindow.OrganizerActions());
                rows.Add(CleanerUi.Small(S.OrgPrivacyNote, "TextSecondary", 12));
            }
            holder.Content = Ui.Stack(0, title, Ui.Group(null, rows.ToArray()));
        }
        Build();
        Ui.Track(holder, () => Settings.Observe(Build, MessagingKeys.Enabled, MessagingKeys.OrganizerEnabled, MessagingKeys.OrganizerDestination));
        return holder;
    }

    private static FrameworkElement DestinationRow()
    {
        var paths = MessagingPaths.Current();
        var destination = MessagingOrganizer.Destination(paths);
        var path = Ui.Text(CleanerRules.PrettyPath(destination, CleanerPaths.Current()), 12, wrap: false);
        path.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        var choose = Ui.Button(S.OrgChooseFolder, () =>
        {
            var dialog = new OpenFolderDialog { InitialDirectory = System.IO.Directory.Exists(destination) ? destination : paths.Downloads };
            if (dialog.ShowDialog() != true) return;
            if (!MessagingOrganizer.SetDestination(dialog.FolderName, paths))
                CleanerUi.Ask(null, S.OrgDestination, S.OrgInvalidDestination, "OK");
        });
        var reset = Settings.GetString(MessagingKeys.OrganizerDestination, string.Empty).Length > 0
            ? Ui.Button(S.OrgUseDefault, () => MessagingOrganizer.SetDestination(null, paths))
            : null;
        var reveal = Ui.IconButton(CleanerGlyphs.FolderOpen, S.Reveal, () => CleanerUi.OpenFolder(destination));
        var left = Ui.Stack(4, Ui.Text(S.OrgDestination, 13.5, FontWeights.Medium), Ui.Row(6, Ui.Icon(Glyphs.Folder, 13, "TextSecondary"), path));
        return Ui.Split(left, Ui.Row(6, choose, reset, reveal));
    }
}
