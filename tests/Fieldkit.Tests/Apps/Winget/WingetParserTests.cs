// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Tests.Apps.Winget;

public class WingetTextTests
{
    [Fact]
    public void KeepsTheLastRedrawOfEachLine()
    {
        var lines = WingetText.CleanLines("\r   - \r   \\ \r          \rName  Id\r\n\u001b[32mGit\u001b[0m   Git.Git\r\n\r\n");
        Assert.Equal(["Name  Id", "Git   Git.Git"], lines);
    }

    [Fact]
    public void SpinnerOnlyLinesBecomeEmpty()
    {
        var lines = WingetText.CleanLines("abc\n  |  \n/\ndef");
        Assert.Equal(["abc", "", "", "def"], lines);
    }

    [Theory]
    [InlineData("Git", 3)]
    [InlineData("メモ帳", 6)]
    [InlineData("秀丸 Editor", 11)]
    [InlineData("μTorrent", 8)]
    [InlineData("e\u0301", 1)]
    [InlineData("", 0)]
    public void MeasuresDisplayWidth(string text, int cells) => Assert.Equal(cells, WingetText.DisplayWidth(text));

    [Fact]
    public void SlicesByCellsNotCharacters()
    {
        var line = "メモ帳  Microsoft.Notepad";
        Assert.Equal("メモ帳", WingetText.Slice(line, 0, 8));
        Assert.Equal("Microsoft.Notepad", WingetText.Slice(line, 8, int.MaxValue));
    }

    [Theory]
    [InlineData("  ██████▒▒▒▒  12.0 MB / 40.0 MB", true)]
    [InlineData("  45%", true)]
    [InlineData("-----------", true)]
    [InlineData("Successfully installed", false)]
    [InlineData("Git   Git.Git   2.53.0", false)]
    public void RecognisesProgressBars(string line, bool expected) => Assert.Equal(expected, WingetText.IsProgressBar(line));
}

public class WingetTableParserTests
{
    [Fact]
    public void ParsesTheCapturedUpgradeTable()
    {
        var parsed = WingetTableParser.Parse(WingetCaptured.Upgrade);
        var table = Assert.Single(parsed.Tables);
        Assert.Equal(["Name", "Id", "Version", "Available", "Source"], table.Headers);
        Assert.Equal(10, table.Rows.Count);
        Assert.Contains("10 upgrades available.", parsed.Messages);
    }

    [Fact]
    public void FindsTheExplicitTargetingTableAndItsPreamble()
    {
        var parsed = WingetTableParser.Parse(WingetFixtures.UpgradeWithExplicit);
        Assert.Equal(2, parsed.Tables.Count);
        Assert.Equal(3, parsed.Tables[0].Rows.Count);
        Assert.Single(parsed.Tables[1].Rows);
        Assert.StartsWith("The following packages", Assert.Single(parsed.Tables[1].Preamble));
        Assert.Contains(parsed.Messages, m => m.Contains("--include-unknown"));
    }

    [Fact]
    public void MergesMultiWordTitlesWhenThereAreTooManyWords()
    {
        int[] w = [6, 9, 8, 7, 8];
        var header = WingetFixtures.Line(w, "Name", "Id", "Version", "Source", "Pin type");
        var row = WingetFixtures.Line(w, "Git", "Git.Git", "2.53.0", "winget", "Blocking");
        var (headers, starts) = WingetTableParser.HeaderColumns(header, 5, [row]);
        Assert.Equal(["Name", "Id", "Version", "Source", "Pin type"], headers);
        Assert.Equal([0, 6, 15, 23, 30], starts);
    }

    [Fact]
    public void SentencesAreNotRows()
    {
        int[] w = [8, 8, 6];
        var output = string.Join("\n",
            WingetFixtures.Line(w, "Name", "Id", "Ver"),
            WingetFixtures.Rule(w),
            WingetFixtures.Line(w, "Git", "Git.Git", "2.53"),
            "Several words reaching far beyond the first column here.");
        var table = Assert.Single(WingetTableParser.Parse(output, 5, 2).Tables);
        Assert.Single(table.Rows);
    }
}

public class WingetParserTests
{
    [Fact]
    public void ParsesUpgradesWithOddVersions()
    {
        var list = WingetParser.ParseUpgrade(WingetCaptured.Upgrade);
        Assert.Equal(10, list.Packages.Count);
        Assert.Empty(list.ExplicitOnly);
        var remote = list.Packages.Single(p => p.Id == "Contoso.RemoteDesk");
        Assert.Equal("cr 9.0.12", remote.Version);
        Assert.Equal("9.8.1", remote.Available);
        Assert.Equal("winget", remote.Source);
        Assert.Equal(WingetOrigin.Winget, remote.Origin);
        Assert.Equal("< 3.10.2", list.Packages.Single(p => p.Id == "Python.Launcher").Version);
        var redist = list.Packages.Single(p => p.Id == "Microsoft.VCRedist.2015+.x64");
        Assert.Equal("Microsoft Visual C++ v14 Redistributable (x64) - 14.40.33810", redist.Name);
        Assert.Equal("14.51.36247.0", redist.Available);
        Assert.All(list.Packages, p => Assert.True(p.HasUpdate));
    }

    [Fact]
    public void SeparatesPinnedUpdatesAndReadsFooterCounts()
    {
        var list = WingetParser.ParseUpgrade(WingetFixtures.UpgradeWithExplicit);
        Assert.Equal(["Git.Git", "9NCBCSZSJRSB", "Discord.Discord"], list.Packages.Select(p => p.Id));
        var pinned = Assert.Single(list.ExplicitOnly);
        Assert.Equal("Python.Python.3.12", pinned.Id);
        Assert.True(pinned.IsPinned);
        Assert.Equal(2, list.UnknownVersionCount);
        Assert.Equal(1, list.PinnedCount);
        Assert.Equal(WingetOrigin.Store, list.Packages[1].Origin);
    }

    [Fact]
    public void ParsesTheInstalledListWithLocalEntries()
    {
        var packages = WingetParser.ParsePackages(WingetCaptured.List);
        Assert.Equal(12, packages.Count);
        var builder = packages.Single(p => p.Name == "3D Builder");
        Assert.Equal(@"MSIX\Microsoft.3DBuilder_20.0.4.0_x64__8wekyb3d8bbwe", builder.Id);
        Assert.Equal("20.0.4.0", builder.Version);
        Assert.Equal(string.Empty, builder.Available);
        Assert.Equal(string.Empty, builder.Source);
        Assert.Equal(WingetOrigin.Local, builder.Origin);
        Assert.Equal(@"ARP\User\X64\Discord", packages.Single(p => p.Name == "Discord").Id);
        var reader = packages.Single(p => p.Id == "Fabrikam.Reader.Pro");
        Assert.Equal("25.011.20114", reader.Available);
        Assert.Equal("Café Médiathèque", packages.Single(p => p.Id == "Contoso.CafeMediatheque").Name);
        Assert.False(packages.Single(p => p.Id == "Microsoft.AppInstaller").HasUpdate);
    }

    [Fact]
    public void ParsesSearchResultsWithMatchColumn()
    {
        var results = WingetParser.ParsePackages(WingetCaptured.Search);
        Assert.Equal(6, results.Count);
        var code = results[0];
        Assert.Equal("Microsoft Visual Studio Code", code.Name);
        Assert.Equal("Microsoft.VisualStudioCode", code.Id);
        Assert.Equal("1.141.1", code.Version);
        Assert.Equal("Moniker: vscode", code.Match);
        Assert.Equal("winget", code.Source);
        Assert.Equal(string.Empty, code.Available);
    }

    [Fact]
    public void IgnoresSpinnerAndEscapeSequences()
    {
        var results = WingetParser.ParsePackages(WingetFixtures.SearchWithSpinner);
        Assert.Equal(2, results.Count);
        Assert.Equal("VLC media player", results[0].Name);
        Assert.Equal("Moniker: vlc", results[0].Match);
        Assert.Equal("msstore", results[1].Source);
        Assert.Equal(string.Empty, results[1].Match);
    }

    [Fact]
    public void FlagsTruncatedRows()
    {
        var results = WingetParser.ParsePackages(WingetFixtures.Truncated);
        Assert.True(results[0].Truncated);
        Assert.False(results[1].Truncated);
        Assert.Equal("winget", results[1].Source);
        Assert.False(WingetCommands.IsValidId(results[0].Id));
    }

    [Fact]
    public void UnderstandsLocalizedTitles()
    {
        var list = WingetParser.ParseUpgrade(WingetFixtures.GermanUpgrade);
        Assert.Equal(2, list.Packages.Count);
        Assert.Equal("1.25.2", list.Packages[0].Available);
        Assert.Equal("winget", list.Packages[0].Source);
        Assert.Equal("Notepad++.Notepad++", list.Packages[1].Id);
    }

    [Fact]
    public void AlignsWideCharacters()
    {
        var packages = WingetParser.ParsePackages(WingetFixtures.JapaneseList);
        Assert.Equal(3, packages.Count);
        Assert.Equal("メモ帳", packages[0].Name);
        Assert.Equal("Microsoft.Notepad", packages[0].Id);
        Assert.Equal("11.2507.26", packages[0].Version);
        Assert.Equal("秀丸エディタ", packages[1].Name);
        Assert.Equal("9.30", packages[1].Available);
        Assert.Equal("winget", packages[1].Source);
    }

    [Fact]
    public void TellsSourceFromAvailableByValues()
    {
        var packages = WingetParser.ParsePackages(WingetFixtures.ListWithoutAvailable);
        Assert.Equal(3, packages.Count);
        Assert.All(packages, p => Assert.Equal(string.Empty, p.Available));
        Assert.Equal("winget", packages[0].Source);
        Assert.Equal(string.Empty, packages[1].Source);
        Assert.Equal("msstore", packages[2].Source);
    }

    [Fact]
    public void EmptyOutputsGiveEmptyLists()
    {
        Assert.Empty(WingetParser.ParsePackages(WingetFixtures.NoPackage));
        Assert.Empty(WingetParser.ParsePackages(string.Empty));
        Assert.Empty(WingetParser.ParseUpgrade(string.Empty).Packages);
    }

    [Fact]
    public void ParsesShow()
    {
        var details = WingetParser.ParseShow(WingetCaptured.Show);
        Assert.NotNull(details);
        Assert.Equal("Git", details!.Name);
        Assert.Equal("Git.Git", details.Id);
        Assert.Equal("2.55.0.5", details.Version);
        Assert.Equal("The Git Development Community", details.Publisher);
        Assert.StartsWith("Git is a free and open source", details.Description);
        Assert.Contains("\nGit for Windows focuses", details.Description);
        Assert.Equal("https://gitforwindows.org/", details.Homepage);
        Assert.Equal("GPL-2.0", details.License);
        Assert.Equal("https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5", details.ReleaseNotesUrl);
        Assert.Equal(["git", "vcs"], details.Tags);
        Assert.Equal("inno", details.InstallerType);
        Assert.Equal("git", details.Moniker);
    }

    [Fact]
    public void ShowWithoutPackageIsNull() => Assert.Null(WingetParser.ParseShow(WingetFixtures.NoPackage));

    [Fact]
    public void ParsesSources()
    {
        var sources = WingetParser.ParseSources(WingetCaptured.Sources);
        Assert.Equal(["msstore", "winget", "winget-font"], sources.Select(s => s.Name));
        Assert.Equal("https://cdn.winget.microsoft.com/cache", sources[1].Argument);
        Assert.False(sources[1].Explicit);
        Assert.True(sources[2].Explicit);
    }

    [Fact]
    public void ParsesPins()
    {
        var pins = WingetParser.ParsePins(WingetFixtures.Pins);
        Assert.Equal(2, pins.Count);
        Assert.Equal("Python.Python.3.12", pins[0].Id);
        Assert.Equal("Pinning", pins[0].PinType);
        Assert.Equal("Blocking", pins[1].PinType);
        Assert.Empty(WingetParser.ParsePins(WingetFixtures.NoPins));
    }

    [Theory]
    [InlineData("Unknown", true)]
    [InlineData("Unbekannt", true)]
    [InlineData("< 3.11.8", false)]
    [InlineData("1.0", false)]
    public void DetectsUnknownVersions(string version, bool unknown) =>
        Assert.Equal(unknown, WingetPackageRules.IsUnknownVersion(version));
}

public class WingetProgressTests
{
    [Theory]
    [InlineData("  ██████▒▒▒▒  20.0 MB / 100 MB", 0.2)]
    [InlineData("  1.50 GB / 3.00 GB", 0.5)]
    [InlineData("  ████▒▒  40%", 0.4)]
    [InlineData("512 KB / 1.00 MB", 0.512)]
    public void ReadsProgress(string text, double expected) =>
        Assert.Equal(expected, WingetProgress.Fraction(text)!.Value, 3);

    [Fact]
    public void LatestProgressWins()
    {
        Assert.Equal(1.0, WingetProgress.Fraction(WingetFixtures.InstallTranscript));
        Assert.Null(WingetProgress.Fraction("Starting package install..."));
        Assert.Null(WingetProgress.Fraction("150%"));
    }

    [Theory]
    [InlineData("Downloading https://x/y.exe", WingetAction.Install, WingetPhase.Downloading)]
    [InlineData("Starting package install...", WingetAction.Install, WingetPhase.Installing)]
    [InlineData("Starting package install...", WingetAction.Upgrade, WingetPhase.Upgrading)]
    [InlineData("Starting package install...", WingetAction.UpgradeSelected, WingetPhase.Upgrading)]
    [InlineData("Starting package uninstall...", WingetAction.Uninstall, WingetPhase.Uninstalling)]
    [InlineData("Successfully installed", WingetAction.Install, WingetPhase.Finalizing)]
    [InlineData("Updating source: winget", WingetAction.UpdateSources, WingetPhase.Refreshing)]
    public void ReadsPhases(string line, WingetAction action, WingetPhase phase) =>
        Assert.Equal(phase, WingetProgress.Phase(line, action));

    [Fact]
    public void UnrelatedLinesHaveNoPhase() => Assert.Null(WingetProgress.Phase("This application is licensed to you by its owner.", WingetAction.Install));

    [Fact]
    public void ActivitySkipsNoise()
    {
        Assert.Null(WingetProgress.Activity("  ████▒▒  40%"));
        Assert.Null(WingetProgress.Activity("  \\ "));
        Assert.Equal("Successfully installed", WingetProgress.Activity("  Successfully installed  "));
    }

    [Fact]
    public void VisibleErrorIsTheLastMeaningfulLines()
    {
        var error = WingetProgress.VisibleError(WingetFixtures.FailedUpgrade);
        var lines = error.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Starting package install...", lines[0]);
        Assert.StartsWith("Another installation is already in progress", lines[2]);
    }

    [Fact]
    public void DetectsAgreementsAndTerminalNeeds()
    {
        Assert.True(WingetProgress.NeedsAgreements("Use --accept-source-agreements to accept them."));
        Assert.True(WingetProgress.NeedsAgreements("Package agreements were not agreed to. Operation cancelled."));
        Assert.False(WingetProgress.NeedsAgreements(WingetFixtures.InstallTranscript));
        Assert.True(WingetProgress.NeedsTerminal(string.Empty, WingetCommands.RequiresAdmin));
        Assert.True(WingetProgress.NeedsTerminal("This command requires administrator privileges to execute.", 1));
        Assert.False(WingetProgress.NeedsTerminal(WingetFixtures.FailedUpgrade, 1618));
    }

    [Fact]
    public void ClassifiesResults()
    {
        Assert.Equal(WingetResult.Succeeded, WingetProgress.Classify(0, WingetFixtures.InstallTranscript, false));
        Assert.Equal(WingetResult.Cancelled, WingetProgress.Classify(0, string.Empty, true));
        Assert.Equal(WingetResult.Failed, WingetProgress.Classify(1618, WingetFixtures.FailedUpgrade, false));
        Assert.Equal(WingetResult.NeedsTerminal, WingetProgress.Classify(WingetCommands.RequiresAdmin, string.Empty, false));
    }
}

public class WingetCommandsTests
{
    [Theory]
    [InlineData("Git.Git", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData(@"ARP\Machine\X86\Cutting Master 4_is1", true)]
    [InlineData("9NCBCSZSJRSB", true)]
    [InlineData("--all", false)]
    [InlineData("a\"b", false)]
    [InlineData("Microsoft.VisualStudioCode.Ins…", false)]
    [InlineData("", false)]
    [InlineData("a\nb", false)]
    public void ValidatesIds(string id, bool valid) => Assert.Equal(valid, WingetCommands.IsValidId(id));

    [Theory]
    [InlineData("Git.Git", "Git.Git")]
    [InlineData(@"ARP\Machine\X86\Cutting Master 4_is1", "\"ARP\\Machine\\X86\\Cutting Master 4_is1\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("", "\"\"")]
    public void QuotesForCommandLineToArgv(string arg, string expected) => Assert.Equal(expected, WingetCommands.Quote(arg));

    [Fact]
    public void InstallsExactlyWithAgreements()
    {
        var args = WingetCommands.Install("Git.Git", "winget")!;
        Assert.Equal(["install", "--id", "Git.Git", "--exact", "--source", "winget", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements"], args);
        Assert.Null(WingetCommands.Install("Git.Git", string.Empty));
        Assert.Null(WingetCommands.Install("-x", "winget"));
    }

    [Fact]
    public void UninstallsLocalEntriesWithoutSource()
    {
        var args = WingetCommands.Uninstall(@"ARP\User\X64\Discord", string.Empty)!;
        Assert.DoesNotContain("--source", args);
        Assert.Contains("--exact", args);
        Assert.DoesNotContain(WingetCommands.AcceptPackage, args);
    }

    [Fact]
    public void ReadOnlyListingsNeverActOnPackages()
    {
        var upgrade = WingetCommands.UpgradeList(includeUnknown: true, acceptSource: false);
        Assert.DoesNotContain("--all", upgrade);
        Assert.DoesNotContain("--id", upgrade);
        Assert.DoesNotContain(WingetCommands.AcceptSource, upgrade);
        Assert.Contains("--include-unknown", upgrade);
        Assert.Contains(WingetCommands.AcceptSource, WingetCommands.List(acceptSource: true));
        Assert.Contains(WingetCommands.DisableInteractivity, WingetCommands.List(acceptSource: false));
    }

    [Fact]
    public void SearchesSafely()
    {
        Assert.Equal(["search", "--query", "-v", "--source", "msstore", "--disable-interactivity"],
            WingetCommands.Search(" -v ", "msstore", false));
        Assert.Null(WingetCommands.Search("  ", null, false));
        Assert.Null(WingetCommands.Search("a\tb", null, false));
    }

    [Fact]
    public void TerminalCommandDropsDisableInteractivity()
    {
        var command = WingetCommands.TerminalCommand(WingetCommands.Upgrade("Git.Git", "winget")!);
        Assert.Equal("winget upgrade --id Git.Git --exact --source winget --accept-source-agreements --accept-package-agreements", command);
    }

    [Fact]
    public void DescribesExitCodes()
    {
        Assert.Equal("No package found matching input criteria.", WingetCommands.DescribeExitCode(WingetParser.NoPackagesFound));
        Assert.Equal("winget exited with code 0x80070005.", WingetCommands.DescribeExitCode(unchecked((int)0x80070005)));
        Assert.Equal(string.Empty, WingetCommands.DescribeExitCode(0));
    }

    [Fact]
    public void OperationCommands()
    {
        WingetTarget[] two = [new("Git", "Git.Git", "winget"), new("Spotify", "9NCBCSZSJRSB", "msstore")];
        var many = WingetOperationLane.Commands(WingetAction.UpgradeSelected, two)!;
        Assert.Equal(2, many.Count);
        Assert.Contains("msstore", many[1]);
        Assert.Null(WingetOperationLane.Commands(WingetAction.Install, two));
        Assert.Null(WingetOperationLane.Commands(WingetAction.Upgrade, [new("x", "-bad", "winget")]));
        Assert.Equal(["upgrade", "--all", "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements"],
            Assert.Single(WingetOperationLane.Commands(WingetAction.UpgradeAll, [])!));
        Assert.Equal(["source", "update", "--disable-interactivity"], Assert.Single(WingetOperationLane.Commands(WingetAction.UpdateSources, [])!));
    }
}
