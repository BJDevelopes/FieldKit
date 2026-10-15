// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Shell;

/// <summary>
/// App-level surfaces that aggregate what feature modules declare: the Utilities, Controls
/// and Quick toggles panel sections, and the General / Features / Shortcuts / About pages.
/// </summary>
public sealed class CoreModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.PanelSection(new PanelSectionDef("utilities", "Utilities", Glyphs.Tools, 80, [], BuildUtilities));
        r.PanelSection(new PanelSectionDef("controls", "Controls", Glyphs.Toggles, 90, [], BuildControls));
        r.PanelSection(new PanelSectionDef("toggles", "Quick toggles", Glyphs.Toggles, 100, ["quickToggles"], BuildQuickToggles));

        r.SettingsPage(new SettingsPageDef("general", SettingsCategory.Essentials, "General", Glyphs.Settings, 0, [],
            GeneralPage.Build, ["appearance", "startup", "launch", "backup", "export", "import", "panel", "theme", "dark"]));
        r.SettingsPage(new SettingsPageDef("features", SettingsCategory.Essentials, "Features", Glyphs.Grid, 1, [],
            FeatureHubPage.Build, ["install", "uninstall", "bundle", "permissions"]));
        r.SettingsPage(new SettingsPageDef("shortcuts", SettingsCategory.Advanced, "Keyboard shortcuts", Glyphs.Keyboard, 0, [],
            BuildShortcutsPage, ["hotkey", "keys"]));
        r.SettingsPage(new SettingsPageDef("about", SettingsCategory.Advanced, "About", Glyphs.Info, 100, [],
            AboutPage.Build, ["version", "license", "parity", "credits"]));
    }

    private static bool AnyEntries<T>(IEnumerable<T> entries, Func<T, string> feature) =>
        entries.Any(e => FeatureCatalog.IsAvailable(feature(e)));

    private static FrameworkElement BuildUtilities()
    {
        var entries = FeatureRuntime.Registry.Utilities
            .Where(u => FeatureCatalog.IsAvailable(u.FeatureId) && u.IsShownInPanel)
            .OrderBy(u => u.Order).ThenBy(u => u.Title, StringComparer.CurrentCulture)
            .ToList();
        if (entries.Count == 0) return Ui.Caption("No utilities are installed.");
        return Ui.Stack(10, entries.Select(u =>
            (UIElement)Ui.NavRow(u.Glyph, u.Title, u.Description, () => { PanelHost.Hide(); u.Open(); },
                u.ShortcutKey is null ? null : EffectiveShortcut(u.ShortcutKey))).ToArray());
    }

    /// <summary>The shortcut text in use for a setting key: the user's choice, else the registering action's default.</summary>
    public static string EffectiveShortcut(string shortcutKey)
    {
        var fallback = FeatureRuntime.Registry.Actions.FirstOrDefault(a => a.ShortcutKey == shortcutKey)?.DefaultShortcut ?? string.Empty;
        return Shortcut.FromSetting(shortcutKey, fallback).ToString();
    }

    private static string GroupTitle(ControlGroup g) => g switch
    {
        ControlGroup.Windows => "Windows",
        ControlGroup.MouseKeyboard => "Mouse and keyboard",
        ControlGroup.Files => "Files",
        _ => g.ToString(),
    };

    private static FrameworkElement BuildControls()
    {
        var all = FeatureRuntime.Registry.Controls.Where(c => FeatureCatalog.IsAvailable(c.FeatureId)).ToList();
        if (all.Count == 0) return Ui.Caption("No controls are installed.");
        var root = Ui.Stack(6);
        foreach (var group in all.GroupBy(c => c.Group).OrderBy(g => g.Key))
        {
            var items = group.OrderBy(c => c.Order).ToList();
            var collapsedKey = $"panel.controls.collapsed.{group.Key}";
            var body = Ui.Stack(10, items.Select(c =>
                (UIElement)Ui.ToggleCard(c.Glyph, c.Title, c.Description, c.SettingKey, c.DefaultValue, c.SubToggles)).ToArray());
            body.Margin = new Thickness(0, 6, 0, 8);

            var chevron = Ui.Icon(Glyphs.ChevronDown, 11, "TextSecondary");
            var count = Ui.Text(string.Empty, 12.5, FontWeights.SemiBold, "Accent", wrap: false);
            void Refresh()
            {
                var collapsed = Settings.GetBool(collapsedKey, false);
                body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                chevron.Text = collapsed ? Glyphs.ChevronRight : Glyphs.ChevronDown;
                count.Text = $"{items.Count(c => Settings.GetBool(c.SettingKey, c.DefaultValue))}/{items.Count}";
            }
            var header = Ui.Split(Ui.Row(8, chevron, Ui.SectionHeader(GroupTitle(group.Key))), count);
            header.Background = System.Windows.Media.Brushes.Transparent;
            header.Cursor = Cursors.Hand;
            header.Margin = new Thickness(0, 6, 0, 0);
            header.MouseLeftButtonUp += (_, _) => Settings.Set(collapsedKey, !Settings.GetBool(collapsedKey, false));
            Refresh();
            var section = Ui.Stack(0, header, body);
            Ui.Track(section, () => Settings.Observe(Refresh, [collapsedKey, .. items.Select(i => i.SettingKey)]));
            root.Children.Add(section);
        }
        return root;
    }

    private static FrameworkElement BuildQuickToggles()
    {
        var toggles = FeatureRuntime.Registry.QuickToggles
            .Where(t => t.FeatureId is null || FeatureCatalog.IsAvailable(t.FeatureId))
            .OrderBy(t => t.Order).ToList();
        if (toggles.Count == 0) return Ui.Caption("No quick toggles yet.");
        var tiles = toggles.Select(t => (UIElement)QuickToggleTile(t)).ToArray();
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var tile in tiles)
        {
            ((FrameworkElement)tile).Margin = new Thickness(4);
            grid.Children.Add(tile);
        }
        return grid;
    }

    public static FrameworkElement QuickToggleTile(QuickToggleDef t)
    {
        var icon = Ui.Icon(t.Glyph, 20);
        var label = Ui.Text(t.Title, 12, FontWeights.Medium);
        label.TextAlignment = TextAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.Stack(6, icon, label);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        var card = Ui.Card(stack, new Thickness(6, 12, 6, 10));
        card.Cursor = Cursors.Hand;
        void Refresh()
        {
            var on = t.IsOn?.Invoke() ?? false;
            card.SetResourceReference(Border.BackgroundProperty, on ? "AccentSubtle" : "CardBackground");
            icon.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "TextPrimary");
        }
        Refresh();
        card.MouseLeftButtonUp += (_, _) =>
        {
            Ui.Guard(t.Toggle);
            Refresh();
        };
        if (t.IsOn is not null) Ui.Every(card, TimeSpan.FromSeconds(2), Refresh);
        return card;
    }

    private static FrameworkElement BuildShortcutsPage()
    {
        var rows = FeatureRuntime.Registry.Actions
            .Where(a => a.ShortcutKey is not null && FeatureCatalog.IsAvailable(a.FeatureId))
            .GroupBy(a => FeatureCatalog.Get(a.FeatureId).Group)
            .OrderBy(g => g.Key)
            .Select(g => (UIElement)Ui.Group(FeatureCatalog.GroupTitle(g.Key),
                g.Select(a => (UIElement)Ui.ShortcutRow(a.Title, a.ShortcutKey!, a.DefaultShortcut,
                    FeatureCatalog.Get(a.FeatureId).Title)).ToArray()))
            .ToArray();
        return Ui.Page("Keyboard shortcuts",
            "Every global shortcut in one place. Click a shortcut to record a new one, press Backspace to clear it.",
            rows.Length == 0 ? [Ui.Caption("No installed feature has a shortcut yet.")] : rows);
    }
}
