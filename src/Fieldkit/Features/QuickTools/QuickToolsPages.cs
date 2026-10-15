// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>Settings → Quick tools, Scratchpad and Cleaning Mode.</summary>
public static class QuickToolsPages
{
    // ---- Quick tools -------------------------------------------------------------------

    public static FrameworkElement BuildQuickTools() => Ui.Page("Quick tools",
        "One-click system actions and a floating panel with your favorite tools.",
        FeatureCatalog.IsAvailable("quickLauncher") ? QuickPanelGroup() : null,
        FeatureCatalog.IsAvailable("quickToggles") ? QuickTogglesGroup() : null);

    private static FrameworkElement QuickPanelGroup()
    {
        var open = Ui.Button("Open quick panel", QuickPanelWindow.ToggleInstance, glyph: Glyphs.Grid);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        var reset = Ui.Button("Reset tiles", QuickPanelItems.Reset);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Group("Quick panel",
            Ui.Stack(8, Ui.Row(8, open, reset),
                Ui.Caption("A floating panel with your favorite tools, summoned by a shortcut from anywhere."),
                Ui.Text("Use the tune button to choose, hide and drag the tools around.", 12, brush: "TextTertiary")),
            EnergyUi.ShortcutToggle("Global shortcut", null, QuickPanelItems.ShortcutKey, QuickPanelItems.DefaultShortcut));
    }

    private static FrameworkElement QuickTogglesGroup()
    {
        var darkLabel = new ContentControl();
        void SyncDark() => darkLabel.Content = Ui.Button(QuickToggleActions.IsDarkMode() ? "Switch to light mode" : "Switch to dark mode", () =>
        {
            QuickToggleActions.ToggleDarkMode();
            SyncDark();
        }, glyph: QuickToggleActions.IsDarkMode() ? Glyphs.Brightness : Glyphs.Moon);
        SyncDark();

        var list = new ContentControl();
        void BuildList()
        {
            var hidden = QuickTogglesCatalog.Hidden();
            var order = QuickTogglesCatalog.Order();
            var byId = QuickTogglesCatalog.All.ToDictionary(t => t.Id);
            var rows = Ui.Stack(6);
            for (var i = 0; i < order.Count; i++)
            {
                var spec = byId[order[i]];
                var id = spec.Id;
                var visible = new CheckBox { IsChecked = !hidden.Contains(id), VerticalAlignment = VerticalAlignment.Center };
                visible.Click += (_, _) => QuickTogglesCatalog.SetHidden(id, visible.IsChecked != true);
                var icon = Ui.Icon(spec.Glyph, 14, hidden.Contains(id) ? "TextTertiary" : "Accent");
                icon.Width = 22;
                var text = Ui.Stack(0, Ui.Text(spec.Title, 13, FontWeights.Medium), Ui.Text(spec.Caption, 11.5, brush: "TextSecondary"));
                if (!QuickTogglesCatalog.IsAvailable(spec)) text.Opacity = 0.5;
                var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => QuickTogglesCatalog.Move(id, -1), 10);
                var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => QuickTogglesCatalog.Move(id, 1), 10);
                up.IsEnabled = i > 0;
                down.IsEnabled = i < order.Count - 1;
                Ui.Append(rows, 6, Ui.Split(Ui.Row(8, visible, icon, text), Ui.Row(0, up, down)));
            }
            list.Content = rows;
        }
        BuildList();
        Ui.Track(list, () => Settings.Observe(BuildList, QuickTogglesCatalog.OrderKey, QuickTogglesCatalog.HiddenKey));
        var reset = Ui.Button("Reset", QuickTogglesCatalog.Reset);
        reset.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Group("Quick toggles",
            darkLabel,
            Ui.Stack(8, Ui.Text("Shown in the panel", 13.5, FontWeights.Medium),
                Ui.Caption("Choose which toggles appear in the Quick toggles tab and in what order."), list, reset),
            ExcludedDrives(),
            Ui.Caption("One-click system actions in the notification area panel and in the quick panel."));
    }

    private static FrameworkElement ExcludedDrives()
    {
        var host = new ContentControl();
        void Build()
        {
            var excluded = QuickToggleActions.ExcludedDrives();
            var rows = Ui.Stack(6);
            foreach (var name in excluded)
            {
                Ui.Append(rows, 6, Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.HardDrive, 13, "TextSecondary"), Ui.Text(name, 13)),
                    Ui.IconButton(Glyphs.Remove, "Remove", () =>
                        Settings.Set(QuickTogglesCatalog.ExcludedDrivesKey, excluded.Where(e => e != name).ToList()), 11)));
            }
            Button? add = null;
            add = Ui.Button("Add drive…", () => ShowAddDriveMenu(add!), glyph: Glyphs.Add);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Append(rows, 8, add);
            Ui.Append(rows, 6, Ui.Caption("Drives in this list are never ejected when using Eject all drives."));
            host.Content = rows;
        }
        Build();
        Ui.Track(host, () => Settings.Observe(Build, QuickTogglesCatalog.ExcludedDrivesKey));
        var count = QuickToggleActions.ExcludedDrives().Count;
        return EnergyUi.Disclosure($"Excluded drives ({count})", () => host, Glyphs.HardDrive, expanded: count > 0, indent: 0);
    }

    private static void ShowAddDriveMenu(FrameworkElement anchor)
    {
        var excluded = QuickToggleActions.ExcludedDrives();
        void Add(string name)
        {
            var clean = name.Trim();
            if (clean.Length == 0 || excluded.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;
            Settings.Set(QuickTogglesCatalog.ExcludedDrivesKey, excluded.Append(clean).ToList());
        }
        var items = new List<(string, Action)>();
        foreach (var d in QuickToggleActions.EjectableDrives())
        {
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
            var key = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : d.VolumeLabel;
            items.Add((label, () => Add(key)));
        }
        if (items.Count > 0) items.Add(("-", () => { }));
        items.Add(("Other drive name…", () =>
        {
            if (TextPrompt.Ask("Add drive…", "Drive or volume name", string.Empty) is { } name) Add(name);
        }));
        EnergyUi.ShowMenu(anchor, items);
    }

    // ---- Scratchpad ----------------------------------------------------------------------

    public static FrameworkElement BuildScratchpad()
    {
        var open = Ui.Button("Open scratchpad", ScratchpadWindow.Open, glyph: Glyphs.Note);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        var background = Ui.SliderRow("Pad background", ScratchpadWindow.BackgroundKey, 0, 1, 0, v => $"{v * 100:0}%", 0.05);
        var ends = Ui.Split(Ui.Text("Translucent", 11, brush: "TextTertiary"), Ui.Text("Opaque", 11, brush: "TextTertiary"));
        return Ui.Page("Scratchpad", "Quick notes in separate tabs",
            Ui.Group(null,
                Ui.Stack(8, open, Ui.Caption("Quick notes in separate tabs. Everything is saved by itself as you type.")),
                Ui.ChoiceRow("Clear on its own", ScratchpadStore.RetentionKey,
                    [("never", "Never"), ("day", "After a day unused"), ("week", "After a week unused"), ("month", "After a month unused")], "never",
                    "The pad empties itself once the text goes that long without edits."),
                Ui.ToggleRow("Close when I click outside", null, ScratchpadWindow.CloseOnClickOutsideKey, true),
                Ui.SliderRow("Text size", ScratchpadWindow.TextSizeKey, ScratchpadLogic.MinTextSize, ScratchpadLogic.MaxTextSize, 13, v => $"{v:0} pt", 1),
                Ui.Stack(2, background, ends),
                EnergyUi.ShortcutToggle("Global shortcut", "Shows or hides the scratchpad from any app.", ScratchpadWindow.ShortcutKey, ScratchpadWindow.DefaultShortcut)),
            Ui.Group("Keyboard",
                Ui.Caption("Ctrl+T new tab · Ctrl+W close tab · Ctrl+F find · Ctrl+G next match · Esc hides the pad")));
    }

    // ---- Cleaning Mode -------------------------------------------------------------------

    public static FrameworkElement BuildCleaningMode()
    {
        var start = Ui.Button("Lock keyboard now", CleaningMode.Start, accent: true, glyph: Glyphs.Keyboard);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Page("Cleaning Mode", "Lock keyboard and screen for cleaning",
            Ui.Group(null,
                Ui.Stack(8, start, Ui.Caption("Locks the keyboard so you can clean safely. Press Escape 5 times or click Unlock to finish.")),
                Ui.ToggleRow("Keep screen visible", "Shows a discreet indicator in the corner of the screen instead of blacking out content.",
                    CleaningMode.KeepScreenVisibleKey, false),
                EnergyUi.IntChoiceRow("Unlock by itself after", "A safety net in case the keyboard cannot reach Escape.",
                    CleaningMode.MaxMinutesKey, CleaningMode.MaxMinuteChoices, m => $"{m} minutes",
                    CleaningMode.MaxMinuteChoices.Contains(Settings.GetInt(CleaningMode.MaxMinutesKey, 10)) ? Settings.GetInt(CleaningMode.MaxMinutesKey, 10) : 10),
                Ui.Text("Ctrl+Alt+Del always works: Windows keeps it out of reach of every app.", 11.5, brush: "TextTertiary")));
    }
}

/// <summary>A small themed text prompt.</summary>
public static class TextPrompt
{
    public static string? Ask(string title, string placeholder, string initial, Window? owner = null)
    {
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 260, ToolTip = placeholder };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        var ok = Ui.Button(title.TrimEnd('…'), () =>
        {
            result = box.Text;
            window.Close();
        }, accent: true);
        ok.IsDefault = true;
        var cancel = Ui.Button("Cancel", window.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(8, cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = Ui.Stack(12, Ui.Title(title, 14), Ui.Caption(placeholder), box, buttons);
        body.Margin = new Thickness(18);
        window.Content = body;
        window.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
