// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>Settings → Files → Clipboard (upstream ClipboardSettings).</summary>
public static class ClipboardPage
{
    public static FrameworkElement Build()
    {
        var historyOn = FeatureCatalog.IsAvailable(ClipboardHistory.FeatureId);
        var content = new List<UIElement?>();
        if (historyOn)
        {
            var status = Ui.Row(5, Ui.Icon(Glyphs.Check, 11, "Green"), Ui.Text("Saving new text", 12, brush: "Green", wrap: false));
            void SyncStatus() => status.Visibility = Settings.GetBool(ClipboardHistory.EnabledKey) && FeatureRuntime.Service<ClipboardHistoryService>()?.IsRunning == true
                ? Visibility.Visible : Visibility.Collapsed;
            SyncStatus();
            Ui.Track(status, () => Settings.Observe(() => status.Dispatcher.BeginInvoke(SyncStatus), ClipboardHistory.EnabledKey));
            content.Add(Ui.Group(null,
                Ui.ToggleRow("Save clipboard history",
                    "Stores copied text so you can reuse it later. Everything stays local and can be cleared anytime.",
                    ClipboardHistory.EnabledKey, false),
                Ui.Stack(4, Ui.Text("Everything stays on this PC. Very large items are ignored.", 11.5, brush: "TextTertiary"), status)));

            content.Add(ShortcutGroup());
            content.Add(CaptureGroup());
            content.Add(AutoClearGroup());
        }
        if (FeatureCatalog.IsAvailable("finderCutPaste"))
        {
            content.Add(Ui.Group(null, Ui.ToggleRow("Paste copied images as files",
                "When File Explorer is active, Ctrl+V saves a copied image as a PNG in the current folder.",
                "finderCutPaste.pasteImageAsFile", false)));
        }
        if (FeatureCatalog.IsAvailable(PastePlainService.Id))
        {
            content.Add(Ui.Group("Paste as plain text",
                Ui.ToggleRow("Paste as plain text",
                    "Pastes what you copied without colors, fonts or formatting. The original stays on the clipboard.",
                    PastePlainService.EnabledKey, false),
                Ui.ShortcutRow("Shortcut", PastePlainService.ShortcutKey, PastePlainService.DefaultShortcut)));
        }
        if (historyOn) content.Add(StatsGroup());
        return Ui.Page("Clipboard", "Clipboard history, plain-text paste and automatic clearing.", content.ToArray());
    }

    private static FrameworkElement ShortcutGroup()
    {
        var layout = Ui.ChoiceRow("History layout", ClipboardHistory.LayoutKey, [("list", "List"), ("cards", "Cards")], "list");
        var open = Ui.Button("Open clipboard history", ClipboardHistoryWindow.Open, glyph: Glyphs.Clipboard);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        void SyncOpen() => open.IsEnabled = ClipboardHistory.Entries.Count > 0;
        SyncOpen();
        Ui.Track(open, () =>
        {
            ClipboardHistory.Changed += SyncOpen;
            return new ActionDisposable(() => ClipboardHistory.Changed -= SyncOpen);
        });
        return Ui.Group("History shortcut",
            Ui.ToggleRow("History shortcut", null, ClipboardHistory.ShortcutEnabledKey, true),
            Ui.ShortcutRow("Shortcut", ClipboardHistory.ShortcutKey, ClipboardHistory.DefaultShortcut,
                "Opens a quick window with search, pinned items and Ctrl+1 to Ctrl+9 shortcuts for pasting into the previous app."),
            layout,
            Ui.Stack(6, Ui.Caption("Click a row to paste it into the previous app. Ctrl-click selects several; Shift+Enter copies without pasting.", 12), open));
    }

    private static string AgeLabel(int days) => days switch
    {
        0 => "Forever",
        1 => "1 day",
        7 => "1 week",
        30 => "1 month",
        90 => "3 months",
        365 => "1 year",
        _ => $"{days} days",
    };

    private static FrameworkElement CaptureGroup()
    {
        var limit = new ComboBox { MinWidth = 120 };
        foreach (var value in ClipboardHistoryLogic.AllowedLimits)
            limit.Items.Add(new ComboBoxItem { Content = value == 0 ? "Unlimited" : value.ToString(CultureInfo.CurrentCulture), Tag = value });
        var current = ClipboardHistoryLogic.SanitizedLimit(Settings.GetInt(ClipboardHistory.LimitKey, ClipboardHistoryLogic.DefaultLimit));
        limit.SelectedItem = limit.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == current);
        limit.SelectionChanged += (_, _) =>
        {
            if (limit.SelectedItem is ComboBoxItem item)
            {
                Settings.Set(ClipboardHistory.LimitKey, (int)item.Tag);
                ClipboardHistory.TrimToLimit();
            }
        };
        var limitRow = Ui.Split(Ui.Text("Limit", 13.5, FontWeights.Medium), limit);

        var age = new ComboBox { MinWidth = 120 };
        foreach (var days in ClipboardHistoryLogic.AllowedAgeDays)
            age.Items.Add(new ComboBoxItem { Content = AgeLabel(days), Tag = days });
        var currentAge = ClipboardHistoryLogic.SanitizedAgeDays(Settings.GetInt(ClipboardHistory.MaxAgeKey, 0));
        age.SelectedItem = age.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == currentAge);
        age.SelectionChanged += (_, _) =>
        {
            if (age.SelectedItem is ComboBoxItem item)
            {
                Settings.Set(ClipboardHistory.MaxAgeKey, (int)item.Tag);
                ClipboardHistory.TrimToLimit();
            }
        };
        var ageRow = Ui.Split(Ui.Stack(2, Ui.Text("Keep items for", 13.5, FontWeights.Medium),
            Ui.Text("Older items are removed. Pinned items stay.", 12, brush: "TextSecondary")), age);

        var group = Ui.Group(null,
            Ui.ToggleRow("Also save copied images and files",
                "Images join the history and files are remembered as links to their location. Pin and paste them like any text item.",
                ClipboardHistory.IncludeImagesFilesKey, true),
            Ui.ToggleRow("Skip text that looks sensitive",
                "Avoids saving short no-space strings that look like passwords, tokens or keys. Copies that password managers mark as private are never saved.",
                ClipboardHistory.SkipSensitiveKey, true),
            IgnoredAppsList(),
            limitRow,
            ageRow);
        void Sync() => group.IsEnabled = Settings.GetBool(ClipboardHistory.EnabledKey);
        Sync();
        Ui.Track((FrameworkElement)group, () => Settings.Observe(Sync, ClipboardHistory.EnabledKey));
        return group;
    }

    /// <summary>"Apps to skip": nothing copied in these apps is saved (upstream ClipboardIgnoredAppsList).</summary>
    private static FrameworkElement IgnoredAppsList()
    {
        var list = Ui.Stack(6);
        var add = Ui.Button("Add an app…", () => { }, glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => ShowAddMenu(add, name =>
        {
            ClipboardHistory.IgnoredApps = [.. ClipboardHistory.IgnoredApps, name];
        });
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var app in ClipboardHistory.IgnoredApps)
            {
                var name = app;
                var remove = Ui.IconButton(Glyphs.Remove, "Remove", () =>
                    ClipboardHistory.IgnoredApps = ClipboardHistory.IgnoredApps.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList(), 11);
                Ui.Append(list, 6, Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Grid, 13, "TextSecondary"), Ui.Text(name, 13, wrap: false)), remove));
            }
        }
        Rebuild();
        Ui.Track(list, () => Settings.Observe(Rebuild, ClipboardHistory.IgnoredAppsKey));
        return Ui.Stack(6,
            Ui.Text("Apps to skip", 13.5, FontWeights.Medium),
            Ui.Caption("Nothing you copy in these apps is saved to the history."),
            list,
            add);
    }

    /// <summary>A menu of running apps (plus Browse…) that hands back an executable file name.</summary>
    internal static void ShowAddMenu(FrameworkElement anchor, Action<string> add)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in WindowHelper.GetAltTabWindows().OrderBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase))
        {
            var exe = Path.GetFileName(window.ProcessPath);
            if (string.IsNullOrEmpty(exe) || !seen.Add(exe)) continue;
            var item = new MenuItem
            {
                Header = exe,
                Icon = AppIcons.Element(AppIcons.ForPath(window.ProcessPath), 16),
            };
            item.Click += (_, _) => add(exe);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose an app" };
            if (dialog.ShowDialog() == true) add(Path.GetFileName(dialog.FileName));
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    private static FrameworkElement AutoClearGroup()
    {
        var delay = new TextBox
        {
            Width = 64,
            Text = AutoClearLogic.SanitizedDelay(Settings.GetInt(ClipboardAutoClearService.DelayKey, AutoClearLogic.DefaultDelaySeconds)).ToString(CultureInfo.CurrentCulture),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(4, 3, 4, 3),
        };
        delay.LostFocus += (_, _) =>
        {
            var value = int.TryParse(delay.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v)
                ? Math.Clamp(v, AutoClearLogic.MinDelaySeconds, AutoClearLogic.MaxDelaySeconds)
                : AutoClearLogic.DefaultDelaySeconds;
            delay.Text = value.ToString(CultureInfo.CurrentCulture);
            Settings.Set(ClipboardAutoClearService.DelayKey, value);
        };
        var delayRow = Ui.Split(Ui.Text("Auto clear clipboard with a delay of", 13.5, FontWeights.Medium),
            Ui.Row(8, delay, Ui.Text("seconds", 13, brush: "TextSecondary", wrap: false), Ui.Toggle(ClipboardAutoClearService.OnDelayKey, false)));
        void SyncDelay() => delay.IsEnabled = Settings.GetBool(ClipboardAutoClearService.OnDelayKey);
        SyncDelay();
        Ui.Track(delay, () => Settings.Observe(SyncDelay, ClipboardAutoClearService.OnDelayKey));
        return Ui.Group("Auto clear",
            delayRow,
            Ui.ToggleRow("Clear clipboard on computer sleep", null, ClipboardAutoClearService.OnSleepKey, false),
            Ui.ToggleRow("Clear clipboard on display sleep", null, ClipboardAutoClearService.OnDisplaySleepKey, false),
            Ui.ToggleRow("Clear clipboard on screen lock", null, ClipboardAutoClearService.OnLockKey, false),
            Ui.Caption("Clears the system clipboard only. Items already saved stay in the history."));
    }

    private static FrameworkElement StatsGroup()
    {
        var pinned = Ui.Text(string.Empty, 13.5, FontWeights.Medium, wrap: false);
        var recent = Ui.Text(string.Empty, 13.5, FontWeights.Medium, wrap: false);
        var clear = Ui.Button("Clear unpinned", () =>
        {
            var snapshot = ClipboardHistoryLogic.RecentSnapshot(ClipboardHistory.Entries);
            if (snapshot.Count == 0) return;
            if (ConfirmDialog.Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                    $"Clear unpinned ({snapshot.Count})?",
                    "Pinned items stay, and so does anything copied after this. This can’t be undone.", "Clear unpinned"))
                ClipboardHistory.ClearRecent(snapshot);
        });
        void Sync()
        {
            pinned.Text = ClipboardHistory.Pinned.Count().ToString(CultureInfo.CurrentCulture);
            recent.Text = ClipboardHistory.Recent.Count().ToString(CultureInfo.CurrentCulture);
            clear.IsEnabled = ClipboardHistory.Recent.Any();
        }
        Sync();
        var row = Ui.Split(Ui.Row(6, pinned, Ui.Text("Pinned", 13.5, brush: "TextSecondary", wrap: false),
            Ui.Text("·", 13.5, brush: "TextTertiary", wrap: false), recent, Ui.Text("Recent", 13.5, brush: "TextSecondary", wrap: false)), clear);
        Ui.Track(row, () =>
        {
            ClipboardHistory.Changed += Sync;
            return new ActionDisposable(() => ClipboardHistory.Changed -= Sync);
        });
        return Ui.Group(null, row);
    }
}
