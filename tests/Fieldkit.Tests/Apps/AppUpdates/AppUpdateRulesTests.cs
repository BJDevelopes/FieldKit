// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.AppUpdates;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.Tests.Apps.Winget;

namespace Fieldkit.Tests.Apps.AppUpdates;

public class AppUpdateRulesTests
{
    private static WingetUpgradeList Explicit => WingetParser.ParseUpgrade(WingetFixtures.UpgradeWithExplicit);

    [Fact]
    public void BuildsRowsPerSourceAndLeavesPinnedOut()
    {
        var items = AppUpdateRules.Items(Explicit, includeWinget: true, includeStore: true, includeUnknown: true);
        Assert.Equal(["Git", "Spotify Music", "Discord"], items.Select(i => i.Name));
        Assert.Equal([AppUpdateSource.Winget, AppUpdateSource.Store, AppUpdateSource.Unknown], items.Select(i => i.Source));
        Assert.DoesNotContain(items, i => i.PackageId == "Python.Python.3.12");
        Assert.True(items[0].IsSelectable);
        Assert.False(items[2].IsSelectable);
        Assert.Equal("2.53.0 → 2.55.0.5", items[0].VersionSummary);
        Assert.Equal(new WingetTarget("Spotify Music", "9NCBCSZSJRSB", "msstore"), items[1].ToTarget());
    }

    [Fact]
    public void SourceTogglesFilterRows()
    {
        Assert.Equal(["Git"], AppUpdateRules.Items(Explicit, true, false, false).Select(i => i.Name));
        Assert.Equal(["Spotify Music"], AppUpdateRules.Items(Explicit, false, true, false).Select(i => i.Name));
        Assert.Equal(["Discord"], AppUpdateRules.Items(Explicit, false, false, true).Select(i => i.Name));
    }

    [Fact]
    public void CapturedUpgradesAreAllWingetRowsSortedByName()
    {
        var items = AppUpdateRules.Items(WingetParser.ParseUpgrade(WingetCaptured.Upgrade), true, true, true);
        Assert.Equal(10, items.Count);
        Assert.All(items, i => Assert.Equal(AppUpdateSource.Winget, i.Source));
        Assert.Equal(items.Select(i => i.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), items.Select(i => i.Name));
    }

    [Theory]
    [InlineData("130", "129", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.1", "1.2", 1)]
    [InlineData("5", "5beta", 1)]
    [InlineData("9a", "10", -1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2.55.0.5", "2.53.0", 1)]
    [InlineData("02.08.02.61", "02.07.01.62", 1)]
    [InlineData("1.0.0-RC1", "1.0.0-rc1", 0)]
    [InlineData("99999999999999999999", "99999999999999999998", 1)]
    public void ComparesVersions(string a, string b, int expected)
    {
        Assert.Equal(expected, AppUpdateRules.Compare(a, b));
        Assert.Equal(-expected, AppUpdateRules.Compare(b, a));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("3.5.262,260717d", "3.5.262")]
    [InlineData(" 1.0 ", "1.0")]
    [InlineData("version", "version")]
    public void ExtractsVersionCore(string raw, string core) => Assert.Equal(core, AppUpdateRules.VersionCore(raw));

    [Theory]
    [InlineData("latest", true)]
    [InlineData("Unknown", true)]
    [InlineData("", true)]
    [InlineData("1.0", false)]
    public void DetectsUncomparableVersions(string v, bool expected) => Assert.Equal(expected, AppUpdateRules.IsUncomparable(v));

    [Fact]
    public void IsNewerUsesTheCore() => Assert.True(AppUpdateRules.IsNewer("v2.0,abc", "1.9"));

    [Fact]
    public void RulesRoundTripAndValidate()
    {
        var rules = new List<AppUpdateRule> { new("Git.Git", "Git", "2.55.0.5"), new("Discord.Discord", "Discord", null) };
        var decoded = AppUpdateRules.DecodeRules(AppUpdateRules.EncodeRules(rules));
        Assert.Equal(rules, decoded);
        Assert.Empty(AppUpdateRules.DecodeRules("not json"));
        Assert.Empty(AppUpdateRules.DecodeRules(null));
        var messy = "[{\"PackageId\":\"Git.Git\",\"Name\":\"Git\",\"Version\":null},{\"PackageId\":\"git.git\",\"Name\":\"Dup\",\"Version\":null}," +
                    "{\"PackageId\":\"-bad\",\"Name\":\"x\",\"Version\":null},{\"PackageId\":\"A.B\",\"Name\":\" \",\"Version\":null}," +
                    "{\"PackageId\":\"C.D\",\"Name\":\"C\",\"Version\":\"latest\"}]";
        Assert.Equal(["Git.Git"], AppUpdateRules.DecodeRules(messy).Select(r => r.PackageId));
    }

    [Fact]
    public void RulesHideSkippedVersionsAndExcludedApps()
    {
        var items = AppUpdateRules.Items(Explicit, true, true, true);
        var skipGit = new AppUpdateRule("git.git", "Git", "v2.55.0.5");
        var skipOldGit = new AppUpdateRule("Git.Git", "Git", "2.54");
        var excludeSpotify = new AppUpdateRule("9NCBCSZSJRSB", "Spotify", null);
        Assert.True(skipGit.Hides(items[0]));
        Assert.False(skipOldGit.Hides(items[0]));
        Assert.Equal(["Discord"], AppUpdateRules.VisibleItems(items, [skipGit, excludeSpotify]).Select(i => i.Name));
        var replaced = AppUpdateRules.WithRule([skipOldGit, excludeSpotify], items[0], null);
        Assert.Equal(2, replaced.Count);
        Assert.Null(replaced.Single(r => r.PackageId == "Git.Git").Version);
    }

    [Fact]
    public void NewRowsArriveTickedAndGoneRowsDropOut()
    {
        var items = AppUpdateRules.Items(Explicit, true, true, true);
        var previous = new HashSet<string> { items[0].Id, "Winget:gone" };
        var known = new HashSet<string> { items[0].Id, items[1].Id };
        var next = AppUpdateRules.ReconciledSelection(previous, known, items);
        Assert.Equal([items[0].Id], next.OrderBy(s => s));
        var fresh = AppUpdateRules.ReconciledSelection(new HashSet<string>(), new HashSet<string>(), items);
        Assert.Equal(2, fresh.Count);
        Assert.DoesNotContain(items[2].Id, fresh);
        Assert.Equal(["Git.Git"], AppUpdateRules.Targets(items, next).Select(t => t.Id));
    }

    [Fact]
    public void SchedulesChecks()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        Assert.Null(AppUpdateRules.NextCheckDate(now.AddDays(-3), CheckFrequency.Off, now));
        Assert.Equal(now + AppUpdateRules.CatchUpDelay, AppUpdateRules.NextCheckDate(null, CheckFrequency.Daily, now));
        Assert.Equal(now + AppUpdateRules.CatchUpDelay, AppUpdateRules.NextCheckDate(now.AddDays(-2), CheckFrequency.Daily, now));
        Assert.Equal(now.AddHours(-2).AddDays(1), AppUpdateRules.NextCheckDate(now.AddHours(-2), CheckFrequency.Daily, now));
        Assert.Equal(now.AddDays(-1).AddDays(7), AppUpdateRules.NextCheckDate(now.AddDays(-1), CheckFrequency.Weekly, now));
        Assert.Equal(CheckFrequency.Weekly, AppUpdateRules.ParseFrequency("weekly"));
        Assert.Equal(CheckFrequency.Off, AppUpdateRules.ParseFrequency("hourly"));
        Assert.Equal("daily", AppUpdateRules.FrequencyValue(CheckFrequency.Daily));
    }

    [Fact]
    public void RechecksWhenStaleOrAfterAHandoff()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        Assert.True(AppUpdateRules.ShouldRecheck(false, false, now, now));
        Assert.True(AppUpdateRules.ShouldRecheck(true, true, now, now));
        Assert.False(AppUpdateRules.ShouldRecheck(true, false, now.AddMinutes(-3), now));
        Assert.True(AppUpdateRules.ShouldRecheck(true, false, now.AddMinutes(-11), now));
        Assert.True(AppUpdateRules.ShouldRecheck(true, false, null, now));
    }

    [Fact]
    public void AnnouncesOnlyFreshRows()
    {
        var items = AppUpdateRules.Items(Explicit, true, true, true);
        var fresh = AppUpdateRules.Fresh(items, new HashSet<string> { items[0].Id });
        Assert.Equal(2, fresh.Count);
        Assert.Equal("One app has a newer version.", AppUpdateRules.NotificationBody(1));
        Assert.Equal("3 apps have a newer version.", AppUpdateRules.NotificationBody(3));
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(61, "1 hour ago")]
    [InlineData(180, "3 hours ago")]
    public void FormatsRecentTimes(int minutesAgo, string expected)
    {
        var now = new DateTime(2026, 10, 14, 18, 0, 0);
        Assert.Equal(expected, AppUpdateRules.RelativeTime(now.AddMinutes(-minutesAgo), now));
    }

    [Fact]
    public void FormatsOlderTimes()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0);
        Assert.Equal("yesterday", AppUpdateRules.RelativeTime(new DateTime(2026, 10, 13, 22, 0, 0), now));
        Assert.Equal("3 days ago", AppUpdateRules.RelativeTime(now.AddDays(-3), now));
        Assert.Equal("last week", AppUpdateRules.RelativeTime(now.AddDays(-9), now));
        Assert.Equal("2 weeks ago", AppUpdateRules.RelativeTime(now.AddDays(-15), now));
        Assert.Equal("Not checked yet", AppUpdateRules.LastCheckLine(null, now));
        Assert.Equal("Last checked now", AppUpdateRules.LastCheckLine(now, now));
    }
}

public class AppUpdatesPreviewTests
{
    [Fact]
    public void RendersTheAppUpdatesWindow()
    {
        var dark = StaRunner.PreviewPath("appupdates-dark.png");
        var light = StaRunner.PreviewPath("appupdates-light.png");
        StaRunner.Run(() =>
        {
            AppUpdatesWindow.RenderPreview(dark, true);
            AppUpdatesWindow.RenderPreview(light, false);
        });
        Assert.True(new FileInfo(dark).Length > 10_000);
        Assert.True(new FileInfo(light).Length > 10_000);
    }

    [Fact]
    public void RendersOperationStates()
    {
        var path = StaRunner.PreviewPath("winget-operation-states.png");
        StaRunner.Run(() =>
        {
            var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
            foreach (var (result, progress) in new (WingetResult, double?)[]
                     { (WingetResult.Running, null), (WingetResult.Succeeded, 1), (WingetResult.Failed, 0.4), (WingetResult.NeedsTerminal, null) })
            {
                var lane = new WingetOperationLane();
                lane.SetPreview(new WingetOperationStatus
                {
                    Action = WingetAction.Install,
                    Owner = "homebrew",
                    Target = new WingetTarget("VLC media player", "VideoLAN.VLC", "winget"),
                    Phase = WingetPhase.Installing,
                    Result = result,
                    Progress = progress,
                    StartedAt = DateTime.Now.AddSeconds(-75),
                    FinishedAt = result == WingetResult.Running ? null : DateTime.Now,
                    LastActivity = result == WingetResult.Failed ? "Installer failed with exit code: 1618" : "Starting package install...",
                }, "Found VLC media player [VideoLAN.VLC]\n", result == WingetResult.NeedsTerminal ? "winget install --id VideoLAN.VLC" : null);
                var card = Fieldkit.UI.Ui.Card(new WingetOperationView(lane));
                card.Margin = new System.Windows.Thickness(0, 0, 0, 12);
                stack.Children.Add(card);
            }
            Fieldkit.Features.Apps.Common.PreviewRenderer.Render(stack, path, 560, 720, true);
        });
        Assert.True(new FileInfo(path).Length > 10_000);
    }

    [Fact]
    public void RendersTheMissingWingetState()
    {
        var path = StaRunner.PreviewPath("winget-missing.png");
        StaRunner.Run(() => Fieldkit.Features.Apps.Common.PreviewRenderer.Render(WingetUi.MissingState(() => { }), path, 600, 420, true));
        Assert.True(new FileInfo(path).Length > 5_000);
    }
}
