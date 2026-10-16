// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Core.Updates;
using Fieldkit.UI;
using Fieldkit.UI.Panel;

namespace Fieldkit.Features.Shell;

/// <summary>Settings → About: version, credits, license and the parity summary.</summary>
public static class AboutPage
{
    private static FrameworkElement UpdatesGroup()
    {
        var status = Ui.Caption(StatusText());
        var check = new Button();
        check.Content = "Check for updates";
        async Task CheckAsync()
        {
            check.IsEnabled = false;
            status.Text = "Checking GitHub…";
            UpdateService.ForgetSkipped();
            var result = await UpdateService.CheckAsync();
            status.Text = result.Status switch
            {
                UpdateStatus.Available => $"Version {result.Release!.Version} is available.",
                UpdateStatus.UpToDate => $"Fieldkit {AppInfo.Version} is up to date.",
                _ => result.Error ?? "The check did not work.",
            };
            check.IsEnabled = true;
            if (result is { Status: UpdateStatus.Available, Release: { } release }) UpdatePromptWindow.ShowFor(release);
        }
        check.Click += async (_, _) =>
        {
            try { await CheckAsync(); }
            catch (Exception ex) { Log.Warn($"The update check failed: {ex.Message}"); status.Text = "The check did not work."; check.IsEnabled = true; }
        };
        return Ui.Group("Updates",
            Ui.ToggleRow("Check for updates when Fieldkit starts",
                "Asks GitHub for the latest release a few seconds after start-up and offers to update. Nothing about you or your PC is sent.",
                UpdateService.CheckKey, true),
            Ui.Split(status, check));
    }

    private static string StatusText() =>
        UpdateService.Pending is { } pending ? $"Version {pending.Version} is available."
        : UpdateService.LastCheck is { } last ? $"Fieldkit is up to date. Last checked {last.ToLocalTime():g}."
        : "Fieldkit has not checked yet this session.";

    public static FrameworkElement Build()
    {
        var (full, partial, native, planned) = ParityReport.Counts();
        var brand = PanelWindow.BrandMark(48);
        brand.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Page("About", null,
            Ui.Card(Ui.Stack(10,
                Ui.Row(16, brand, Ui.Stack(2,
                    Ui.Text(AppInfo.Name, 22, FontWeights.SemiBold),
                    Ui.Caption($"Version {AppInfo.Version} · {(UpdateService.Kind == InstallKind.Installed ? "installed" : "portable")}"))),
                Ui.Caption(AppInfo.Tagline, 13.5)), new Thickness(18)),
            UpdatesGroup(),
            Ui.Group("Feature parity",
                Ui.Caption($"{AppInfo.Name} brings the feature set of Vorssaint for macOS to Windows. " +
                           $"Of {FeatureCatalog.Upstream.Count} features: {full} match, {partial} are partial, {native} are built into Windows " +
                           $"and {planned} are not built yet. Settings → Features marks each one."),
                Ui.Row(8,
                    Ui.Button("Open parity map", () => FeatureHubPage.OpenUri($"{AppInfo.RepositoryUrl}/blob/main/docs/PARITY.md")),
                    Ui.Button("Original project", () => FeatureHubPage.OpenUri(AppInfo.UpstreamUrl)))),
            Ui.Group("Support",
                Ui.Caption("Fieldkit is free and open source, made by one person in their spare time. A coffee helps keep it going; " +
                           "starring the project or reporting a bug helps just as much."),
                Ui.Row(8, Ui.Button("Buy me a coffee", () => FeatureHubPage.OpenUri(AppInfo.SupportUrl), glyph: Glyphs.Smile))),
            Ui.Group("License",
                Ui.Caption("Free software under the GNU General Public License v3.0 or later. Inspired by and feature-mapped from " +
                           "Vorssaint (GPL-3.0-or-later). Not affiliated with or endorsed by the Vorssaint project."),
                Ui.Row(8, Ui.Button("Open log folder", () => FeatureHubPage.OpenUri(AppInfo.DataDirectory)))));
    }
}
