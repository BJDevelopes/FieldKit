// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.UI;
using Fieldkit.UI.Panel;

namespace Fieldkit.Features.Shell;

/// <summary>Settings → About: version, credits, license and the parity summary.</summary>
public static class AboutPage
{
    public static FrameworkElement Build()
    {
        var (full, partial, native, planned) = ParityReport.Counts();
        var brand = PanelWindow.BrandMark(48);
        brand.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Page("About", null,
            Ui.Card(Ui.Stack(10,
                Ui.Row(16, brand, Ui.Stack(2,
                    Ui.Text(AppInfo.Name, 22, FontWeights.SemiBold),
                    Ui.Caption($"Version {AppInfo.Version}"))),
                Ui.Caption(AppInfo.Tagline, 13.5)), new Thickness(18)),
            Ui.Group("Feature parity",
                Ui.Caption($"{AppInfo.Name} brings the feature set of Vorssaint for macOS to Windows. " +
                           $"Of {FeatureCatalog.Upstream.Count} features: {full} match, {partial} are partial, {native} are built into Windows " +
                           $"and {planned} are not built yet. Settings → Features marks each one."),
                Ui.Row(8,
                    Ui.Button("Open parity map", () => FeatureHubPage.OpenUri($"{AppInfo.RepositoryUrl}/blob/main/docs/PARITY.md")),
                    Ui.Button("Original project", () => FeatureHubPage.OpenUri(AppInfo.UpstreamUrl)))),
            Ui.Group("License",
                Ui.Caption("Free software under the GNU General Public License v3.0 or later. Inspired by and feature-mapped from " +
                           "Vorssaint (GPL-3.0-or-later). Not affiliated with or endorsed by the Vorssaint project."),
                Ui.Row(8, Ui.Button("Open log folder", () => FeatureHubPage.OpenUri(AppInfo.DataDirectory)))));
    }
}
