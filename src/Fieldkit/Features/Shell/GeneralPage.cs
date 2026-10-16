// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Shell;

/// <summary>Settings → General: appearance, startup, panel layout and backup.</summary>
public static class GeneralPage
{
    public static FrameworkElement Build()
    {
        var startup = new CheckBox { IsChecked = LaunchAtLogin.IsEnabled };
        startup.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        startup.Click += (_, _) => LaunchAtLogin.Set(startup.IsChecked == true);

        return Ui.Page("General", null,
            Ui.Group("Appearance",
                Ui.ChoiceRow("Theme", ThemeManager.AppearanceKey,
                    [("system", "Match Windows"), ("dark", "Dark"), ("light", "Light")], "system")),
            Ui.Group("Startup and notifications",
                Ui.Split(Ui.Stack(2, Ui.Text("Start with Windows", 13.5, FontWeights.Medium),
                    Ui.Caption("Opens quietly in the notification area when you sign in.")), startup),
                AlwaysAdminRow(),
                Ui.ToggleRow("Show on the taskbar", "A Fieldkit button next to the notification area, so the panel is one click away. It steps aside for full-screen apps and auto-hiding taskbars.", TaskbarButton.EnabledKey, true),
                Ui.ToggleRow("Notifications", "Alerts and finished tasks appear as Windows notifications.", "general.notifications", true)),
            PanelLayout(),
            UtilitiesInPanel(),
            Ui.Group("Backup",
                Ui.Split(Ui.Stack(2, Ui.Text("Export settings", 13.5, FontWeights.Medium),
                        Ui.Caption("Save your preferences to a file to restore them or move them to another PC. Machine-specific state is left out.")),
                    Ui.Row(8, Ui.Button("Export…", Export), Ui.Button("Import…", Import)))));
    }

    /// <summary>The switch that makes Fieldkit ask for administrator rights every time it starts.</summary>
    private static FrameworkElement AlwaysAdminRow() => Ui.ToggleRow("Always run as administrator",
        "Fieldkit asks Windows for permission each time it starts, including when it starts with Windows, and restarts with administrator rights. " +
        "Windows hides keys and mouse input from normal apps while an administrator window is in front, so this makes the switcher, snippets, " +
        "scrolling and other input features work there too. If you decline the prompt, Fieldkit runs as a normal user. " +
        (Elevation.IsAdministrator ? "Fieldkit is running as administrator now." : "Fieldkit is running as a normal user now."),
        ElevationPolicy.AlwaysKey, false, on =>
        {
            if (!on || Elevation.IsAdministrator) return;
            var answer = MessageBox.Show(
                "Fieldkit will restart now and ask Windows for permission. Restart now?",
                "Always run as administrator", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.OK) FeatureHubPage.RestartElevated();
        });

    private static FrameworkElement PanelLayout()
    {
        var host = new ContentControl();
        void Render()
        {
            var sections = FeatureRuntime.Registry.PanelSections
                .Where(s => s.FeatureGate.Length == 0 || s.FeatureGate.Any(FeatureCatalog.IsAvailable))
                .OrderBy(s => Settings.GetInt($"panel.order.{s.Id}", s.Order))
                .ToList();
            var rows = sections.Select((s, index) =>
            {
                var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => Swap(sections, index, index - 1));
                var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => Swap(sections, index, index + 1));
                up.IsEnabled = index > 0;
                down.IsEnabled = index < sections.Count - 1;
                var left = Ui.Row(10, Ui.Icon(s.Glyph, 15), Ui.Text(s.Title, 13.5, FontWeights.Medium, wrap: false));
                return (UIElement)Ui.Split(left, Ui.Row(4, up, down, Ui.Toggle($"panel.show.{s.Id}", true)));
            }).ToArray();
            host.Content = Ui.Group("Panel sections", [Ui.Caption("Choose which sections the tray panel shows, and in which order."), .. rows]);
        }
        Render();
        Ui.Track(host, () => Settings.ObservePrefix("panel.order.", Render));
        return host;
    }

    private static FrameworkElement UtilitiesInPanel()
    {
        var entries = FeatureRuntime.Registry.Utilities
            .Where(u => FeatureCatalog.IsAvailable(u.FeatureId))
            .OrderBy(u => u.Order).ThenBy(u => u.Title, StringComparer.CurrentCulture)
            .ToList();
        if (entries.Count == 0) return new ContentControl();
        var rows = entries.Select(u => (UIElement)Ui.Split(
            Ui.Row(10, Ui.Icon(u.Glyph, 15), Ui.Text(u.Title, 13.5, FontWeights.Medium, wrap: false)),
            Ui.Toggle(u.VisibilityKey, true))).ToArray();
        return Ui.Group("Utilities in the panel",
            [Ui.Caption("Hidden tools stay installed and keep their shortcuts, the Command Bar and the quick panel."), .. rows]);
    }

    private static void Swap(List<PanelSectionDef> sections, int a, int b)
    {
        if (b < 0 || b >= sections.Count) return;
        // Persist a dense order for every section so the swap is stable.
        for (var i = 0; i < sections.Count; i++) Settings.Set($"panel.order.{sections[i].Id}", i * 10);
        Settings.Set($"panel.order.{sections[a].Id}", b * 10);
        Settings.Set($"panel.order.{sections[b].Id}", a * 10);
    }

    private static void Export()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{AppInfo.Name} settings {DateTime.Now:yyyy-MM-dd}.json",
            Filter = "Settings (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, Settings.ExportJson());
    }

    private static void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Settings (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Settings.ImportJson(File.ReadAllText(dialog.FileName));
            MessageBox.Show("Settings imported.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"This file could not be imported: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
