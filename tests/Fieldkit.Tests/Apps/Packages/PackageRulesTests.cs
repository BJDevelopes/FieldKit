// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Packages;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.Tests.Apps.Winget;

namespace Fieldkit.Tests.Apps.Packages;

public class PackageRulesTests
{
    private static List<PackageItem> Installed()
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Python.Launcher" };
        return WingetParser.ParsePackages(WingetCaptured.List).Select(p => PackageRules.FromInstalled(p, pinned)).ToList();
    }

    [Fact]
    public void InstalledItemsCarryUpdatesAndPins()
    {
        var items = Installed();
        var git = items.Single(i => i.Id == "Git.Git");
        Assert.True(git.IsInstalled);
        Assert.True(git.HasUpdate);
        Assert.Equal("2.50.1 → 2.55.0.5", git.VersionSummary);
        Assert.True(items.Single(i => i.Id == "Python.Launcher").IsPinned);
        var installer = items.Single(i => i.Id == "Microsoft.AppInstaller");
        Assert.False(installer.HasUpdate);
        Assert.Equal("1.29.380.0", installer.VersionText);
    }

    [Fact]
    public void SearchHitsShowTheirInstalledState()
    {
        var installed = Installed();
        var results = WingetParser.ParsePackages(WingetCaptured.Search).Select(p => PackageRules.FromSearch(p, installed)).ToList();
        var code = results.Single(r => r.Id == "Microsoft.VisualStudioCode");
        Assert.True(code.IsInstalled);
        Assert.Equal("1.139.2", code.InstalledVersion);
        Assert.Equal("1.141.1", code.LatestVersion);
        Assert.Equal("Moniker: vscode", code.Match);
        var codium = results.Single(r => r.Id == "Alex313031.Codium");
        Assert.False(codium.IsInstalled);
        Assert.Equal("1.93.1.24277", codium.VersionText);
    }

    [Fact]
    public void ReconcileDropsUninstalledState()
    {
        var installed = Installed();
        var results = WingetParser.ParsePackages(WingetCaptured.Search).Select(p => PackageRules.FromSearch(p, installed)).ToList();
        var after = PackageRules.Reconcile(results, installed.Where(i => i.Id != "Microsoft.VisualStudioCode").ToList());
        var code = after.Single(r => r.Id == "Microsoft.VisualStudioCode");
        Assert.False(code.IsInstalled);
        Assert.False(code.HasUpdate);
        Assert.Equal("1.141.1", code.VersionText);
    }

    [Fact]
    public void FiltersBySource()
    {
        var items = Installed();
        Assert.Equal(12, PackageRules.Count(items, InstalledFilter.All));
        Assert.Equal(8, PackageRules.Count(items, InstalledFilter.Winget));
        Assert.Equal(3, PackageRules.Count(items, InstalledFilter.Store));
        Assert.Equal(1, PackageRules.Count(items, InstalledFilter.Other));
        Assert.Equal(PackageRules.Count(items, InstalledFilter.All),
            PackageRules.Count(items, InstalledFilter.Winget) + PackageRules.Count(items, InstalledFilter.Store) + PackageRules.Count(items, InstalledFilter.Other));
    }

    [Fact]
    public void UpdatesComeFirstAndUpdateAllSkipsPins()
    {
        var visible = PackageRules.Visible(Installed(), InstalledFilter.All);
        var firstWithout = visible.FindIndex(i => !i.HasUpdate);
        Assert.True(firstWithout > 0);
        Assert.All(visible.Skip(firstWithout), i => Assert.False(i.HasUpdate));
        var targets = PackageRules.UpgradeAllTargets(Installed());
        Assert.DoesNotContain(targets, t => t.Id == "Python.Launcher");
        Assert.Contains(targets, t => t.Id == "Git.Git");
    }

    [Fact]
    public void ConfirmationWording()
    {
        var git = Installed().Single(i => i.Id == "Git.Git");
        Assert.Equal("Uninstall with winget?", PackageRules.ConfirmTitle(WingetAction.Uninstall));
        Assert.Equal("winget will uninstall Git. Configuration files may remain on the system.", PackageRules.ConfirmBody(WingetAction.Uninstall, git));
        Assert.Equal("Update all", PackageRules.ActionTitle(WingetAction.UpgradeAll));
        Assert.True(PackageRules.ClearsSelectionOnSuccess(WingetAction.Uninstall));
        Assert.False(PackageRules.ClearsSelectionOnSuccess(WingetAction.Upgrade));
    }

    [Fact]
    public void ActionsNeedFullIds()
    {
        var truncated = WingetParser.ParsePackages(WingetFixtures.Truncated).Select(p => PackageRules.FromInstalled(p)).ToList();
        Assert.False(truncated[0].CanAct);
        Assert.True(truncated[1].CanAct);
    }

    [Theory]
    [InlineData("winget", false)]
    [InlineData("msstore", true)]
    [InlineData("", true)]
    public void StoreSearchesNeedConsent(string source, bool needs) => Assert.Equal(needs, PackageRules.SearchNeedsConsent(source));

    [Theory]
    [InlineData("winget", "winget")]
    [InlineData("msstore", "Microsoft Store")]
    [InlineData("", "Installed on this PC")]
    [InlineData("contoso", "Source: contoso")]
    public void LabelsSources(string source, string label) => Assert.Equal(label, PackageRules.SourceLabel(source));
}

public class PackagesPreviewTests
{
    [Fact]
    public void RendersThePackagesWindow()
    {
        var dark = StaRunner.PreviewPath("packages-dark.png");
        var light = StaRunner.PreviewPath("packages-light.png");
        var review = StaRunner.PreviewPath("packages-uninstall-review.png");
        StaRunner.Run(() =>
        {
            PackagesWindow.RenderPreview(dark, true);
            PackagesWindow.RenderPreview(light, false);
            PackagesWindow.RenderUninstallPreview(review, true);
        });
        Assert.True(new FileInfo(dark).Length > 10_000);
        Assert.True(new FileInfo(light).Length > 10_000);
        Assert.True(new FileInfo(review).Length > 5_000);
    }

    [Fact]
    public void RendersTheSettingsPageWithoutWinget()
    {
        var path = StaRunner.PreviewPath("packages-page.png");
        StaRunner.Run(() =>
        {
            var lane = new WingetOperationLane();
            var manager = PackageManager.ForPreview(lane, [], [], null, null, WingetParser.ParseSources(WingetCaptured.Sources));
            Fieldkit.Features.Apps.Common.PreviewRenderer.Render(PackagesPage.Build(manager), path, 760, 760, true);
        });
        Assert.True(new FileInfo(path).Length > 5_000);
    }
}
