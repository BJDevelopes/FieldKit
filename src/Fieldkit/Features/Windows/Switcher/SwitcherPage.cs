// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>Settings → Switcher (upstream SwitcherSettings).</summary>
public static class SwitcherPage
{
    public static FrameworkElement Build() =>
        Ui.Page("Switcher", "Switch between apps and windows your way.",
            SwitcherCard(),
            WindowsUi.EnabledWhen(OptionsCard(), SwitcherService.EnabledKey, true),
            PreviewsCard(SwitcherService.PreviewSizeKey, SwitcherService.PreviewExcludedKey, SwitcherService.PreviewExcludedApps));

    private static FrameworkElement SwitcherCard()
    {
        var usage = Ui.Caption(string.Empty);
        var takeoverWarning = WindowsUi.Note(Glyphs.Warning,
            "Alt+Tab stays with Windows while “Replace Windows Alt+Tab” is off. Record another shortcut for Apps to open Fieldkit’s switcher.",
            "Orange");
        void Sync()
        {
            var apps = WindowsUi.ShortcutText(SwitcherService.ShortcutKey, SwitcherService.DefaultShortcut);
            usage.Text = $"Hold {apps} to navigate; release to activate the window. Shift or ← goes back; W closes the window; Q quits the app; M minimizes it; Esc cancels.";
            var system = SwitcherService.IsSystemCombo(Shortcut.FromSetting(SwitcherService.ShortcutKey, SwitcherService.DefaultShortcut));
            takeoverWarning.Visibility = system && !Settings.GetBool(SwitcherService.TakeOverKey, true) ? Visibility.Visible : Visibility.Collapsed;
        }
        Sync();
        Ui.Track(usage, () => Settings.Observe(Sync, SwitcherService.ShortcutKey, SwitcherService.TakeOverKey));

        var body = WindowsUi.EnabledWhen(Ui.Stack(12,
            Ui.Text("Layout", 13.5, FontWeights.Medium),
            LayoutChoices(),
            Ui.Divider(4),
            Ui.ShortcutRow("Apps", SwitcherService.ShortcutKey, SwitcherService.DefaultShortcut),
            Ui.ShortcutRow("Windows", SwitcherService.WindowShortcutKey, SwitcherService.DefaultWindowShortcut),
            Ui.Caption("Opens a switcher for the front app’s windows. While the switcher is open, jumps between the selected app’s windows."),
            Ui.ToggleRow("Replace Windows Alt+Tab",
                "Fieldkit’s switcher answers Alt+Tab and Alt+Shift+Tab instead of the Windows one while it is on. All running apps stay reachable.",
                SwitcherService.TakeOverKey, true),
            takeoverWarning,
            usage), SwitcherService.EnabledKey, true);

        return Ui.Group("Window switcher",
            Ui.ToggleRow("Use the Fieldkit switcher",
                "Switch between apps and windows, including minimized windows and multiple windows from the same app.",
                SwitcherService.EnabledKey, true),
            body);
    }

    private static FrameworkElement LayoutChoices()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        var choices = new List<(Border Card, TextBlock Title, SwitcherLayoutMode Mode)>();
        SwitcherLayoutMode Current() =>
            Settings.GetBool(SwitcherService.SimpleModeKey, false) ? SwitcherLayoutMode.Simple
            : Settings.GetBool(SwitcherService.IconRowModeKey, false) ? SwitcherLayoutMode.Icons
            : SwitcherLayoutMode.Windows;
        void Sync()
        {
            var current = Current();
            foreach (var (card, title, mode) in choices)
            {
                var selected = mode == current;
                card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
                card.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBackground");
                title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextPrimary");
            }
        }
        foreach (var (mode, title, caption) in new[]
                 {
                     (SwitcherLayoutMode.Windows, "Window previews", "One preview per window, minimized ones included."),
                     (SwitcherLayoutMode.Icons, "Large icons", "Shows one icon per app with that app’s window previews above it."),
                     (SwitcherLayoutMode.Simple, "Simple list", "Shows app icons and window titles, without previews."),
                 })
        {
            var titleText = Ui.Text(title, 12, FontWeights.SemiBold);
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = Ui.Stack(8, Sketch(mode), titleText, Ui.Caption(caption, 11.5)),
            };
            var m = mode;
            card.MouseLeftButtonUp += (_, _) =>
            {
                Settings.Set(SwitcherService.SimpleModeKey, m == SwitcherLayoutMode.Simple);
                if (m != SwitcherLayoutMode.Simple) Settings.Set(SwitcherService.IconRowModeKey, m == SwitcherLayoutMode.Icons);
            };
            choices.Add((card, titleText, mode));
            grid.Children.Add(card);
        }
        Sync();
        Ui.Track(grid, () => Settings.Observe(Sync, SwitcherService.SimpleModeKey, SwitcherService.IconRowModeKey));
        return grid;
    }

    /// <summary>The switcher drawn in miniature, like upstream's layout chooser.</summary>
    private static FrameworkElement Sketch(SwitcherLayoutMode mode)
    {
        var host = new Border { Height = 64, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xD1, 0, 0, 0)) };
        FrameworkElement Window(bool selected, double w, double h)
        {
            var b = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(selected ? 1.5 : 0),
                Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)0xE6 : (byte)0x66, 255, 255, 255)),
                Child = new Border { Height = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)) },
            };
            if (selected) b.SetResourceReference(Border.BorderBrushProperty, "Accent");
            return b;
        }
        FrameworkElement Icon(bool selected)
        {
            var b = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4) };
            if (selected) b.SetResourceReference(Border.BackgroundProperty, "Accent");
            else b.Background = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255));
            return b;
        }
        FrameworkElement content = mode switch
        {
            SwitcherLayoutMode.Windows => Ui.Row(6, Window(true, 34, 26), Window(false, 34, 26), Window(false, 34, 26)),
            SwitcherLayoutMode.Icons => Ui.Row(10, Enumerable.Range(0, 4).Select(i =>
                (UIElement)Ui.Stack(4, i == 0 ? Window(true, 26, 18) : new Border { Width = 26, Height = 18 }, Icon(i == 0))).ToArray()),
            _ => Ui.Stack(7,
                new Border { Width = 44, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) },
                Ui.Row(8, Icon(true), Icon(false), Icon(false), Icon(false))),
        };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        host.Child = content;
        return host;
    }

    private static FrameworkElement OptionsCard()
    {
        var hints = Ui.ToggleRow("Show shortcut hints", "Shows the app and window shortcuts below the icons.", SwitcherService.ShowHintsKey, true);
        void SyncHints() => hints.Visibility =
            Settings.GetBool(SwitcherService.SimpleModeKey, false) || Settings.GetBool(SwitcherService.IconRowModeKey, false)
                ? Visibility.Visible : Visibility.Collapsed;
        SyncHints();
        Ui.Track(hints, () => Settings.Observe(SyncHints, SwitcherService.SimpleModeKey, SwitcherService.IconRowModeKey));

        return Ui.Group("Options",
            Ui.SliderRow("Appearance delay", SwitcherService.AppearanceDelayKey, 0, 500, SwitcherLogic.DefaultAppearanceDelay,
                v => $"{v:0} ms", 25, "How long the shortcut must be held before the switcher appears."),
            Ui.SliderRow("Maximum columns", SwitcherService.MaxColumnsKey, SwitcherLogic.MinColumns - 1, SwitcherLogic.MaxColumns, 1,
                v => v < SwitcherLogic.MinColumns ? "Auto" : $"{v:0}", 1, "Wraps onto more rows after this many items. Useful on ultrawide displays."),
            Ui.ToggleRow("Pin search with S",
                "S starts a search and pins the switcher open, so you can let go of Alt while typing, and a search starting with Q, W or M no longer quits, closes or minimizes by mistake.",
                SwitcherService.SearchPinKey, false).WithBelow(hints),
            Ui.ToggleRow("Show one entry per app",
                "Collapses all of an app’s windows into one entry in the switcher, instead of one entry per window.",
                SwitcherService.MergeKey, false),
            WindowsUi.ChipRow("Minimized windows", null, SwitcherService.MinimizedPlacementKey, "normal",
                [("normal", "Normal ordering"), ("end", "Place at end"), ("hidden", "Hide")]),
            WindowsUi.ChipRow("Show on", "Which display the switcher opens on when more than one is connected.",
                SwitcherService.ScreenPlacementKey, "pointer",
                [("pointer", "Display with the pointer"), ("primary", "Primary display"), ("activeWindow", "Display with the active window")]),
            Ui.ToggleRow("Show only the current display",
                "Lists only windows on the display under the pointer. If that display has no windows, the switcher does not open.",
                SwitcherService.CurrentDisplayOnlyKey, false),
            Ui.ToggleRow("Show only the current desktop",
                "Lists windows from the current virtual desktop. When off, windows on other desktops appear too, and choosing one takes you there.",
                SwitcherService.CurrentDesktopOnlyKey, false),
            WindowsUi.ChipRow("Apps with no open window",
                "Chooses which running apps with no window at all show up in the switcher. “All apps” lists the apps you used since Fieldkit started; choosing one opens it again.",
                SwitcherService.WindowlessKey, "explorer",
                [("off", "Do not show"), ("explorer", "File Explorer only"), ("all", "All apps")]),
            RulesList());
    }

    private static FrameworkElement RulesList()
    {
        Dictionary<string, string> Load() => Settings.Get<Dictionary<string, string>>(SwitcherService.AppRulesKey, []) ?? [];
        void Save(Dictionary<string, string> rules) => Settings.Set(SwitcherService.AppRulesKey, rules);
        FrameworkElement RulePicker(string key)
        {
            var combo = new ComboBox { MinWidth = 170 };
            foreach (var (rule, label) in new[]
                     {
                         (SwitcherAppRule.ShowWithoutWindows, "Show without windows"),
                         (SwitcherAppRule.WindowsOnly, "Windows only"),
                         (SwitcherAppRule.Hidden, "Never show"),
                     })
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = SwitcherLogic.RuleId(rule) });
            var current = Load().TryGetValue(key, out var v) ? v : "windowsOnly";
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? combo.Items[1];
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not ComboBoxItem item) return;
                var rules = Load();
                rules[key] = (string)item.Tag;
                Save(rules);
            };
            return combo;
        }
        return WindowsUi.AppListEditor("Rules by app",
            "Choose how each app appears. Apps without a rule use the choice above.",
            "Add an app…", "Remove", SwitcherService.AppRulesKey,
            () => Load().Keys.Select(AppNames.Key).ToList(),
            path =>
            {
                AppNames.Remember(path);
                var rules = Load();
                rules[AppNames.Key(path)] = SwitcherLogic.RuleId(SwitcherAppRule.ShowWithoutWindows);
                Save(rules);
            },
            key =>
            {
                var rules = Load();
                rules.Remove(key);
                Save(rules);
            },
            RulePicker);
    }

    /// <summary>The window thumbnails card shared with the Taskbar page: size, minimal look, paused apps.</summary>
    public static FrameworkElement PreviewsCard(string sizeKey, string excludedKey, AppList excluded)
    {
        var sizes = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        var cards = new List<(Border Card, Border Sketch, TextBlock Label, string Value)>();
        void Sync()
        {
            var current = Settings.GetString(sizeKey, "normal");
            foreach (var (card, sketch, label, value) in cards)
            {
                var selected = value == current;
                card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
                card.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBackground");
                sketch.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "TextTertiary");
                label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextPrimary");
            }
        }
        foreach (var (value, title, scale) in new[] { ("small", "Small", 0.5), ("normal", "Normal", 0.66), ("large", "Large", 0.83), ("xlarge", "Extra large", 1.0) })
        {
            var sketch = new Border { Width = 60 * scale, Height = 42 * scale, CornerRadius = new CornerRadius(4), Opacity = 0.8 };
            var label = Ui.Text(title, 11.5, FontWeights.Medium, wrap: false);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var box = new Grid { Height = 42 };
            box.Children.Add(sketch);
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = Ui.Stack(8, box, label),
            };
            var v = value;
            card.MouseLeftButtonUp += (_, _) => Settings.Set(sizeKey, v);
            cards.Add((card, sketch, label, value));
            sizes.Children.Add(card);
        }
        Sync();
        Ui.Track(sizes, () => Settings.Observe(Sync, sizeKey));
        return Ui.Group("Window thumbnails",
            Ui.Stack(8, Ui.Text("Preview size", 13.5, FontWeights.Medium), sizes),
            Ui.ToggleRow("Minimal previews",
                "Hide titles, buttons and decorative details in taskbar and switcher previews. The selection stays visible.",
                SwitcherService.MinimalPreviewsKey, false),
            WindowsUi.AppListEditor("Pause in these apps", "Window thumbnails stop while one of these apps is in front.",
                "Add an app…", "Remove", excludedKey, () => excluded.Items, excluded.Add, excluded.Remove));
    }
}
