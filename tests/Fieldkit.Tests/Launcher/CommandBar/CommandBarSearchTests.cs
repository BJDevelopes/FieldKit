// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Launcher.CommandBar;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarSearchTests
{
    [Theory]
    [InlineData("Café  Brûlé", "cafe brule")]
    [InlineData("ＦＵＬＬ　width", "full width")]
    [InlineData("\u200EGoogle Chrome", "google chrome")]
    [InlineData("INSTA", "insta")]
    [InlineData("  a\tb  ", "a b")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, CommandBarSearch.Normalize(input));

    [Fact]
    public void ScoresBetterMatchesHigher()
    {
        var exact = CommandBarSearch.Score("Brightness", "", "brightness")!.Value;
        var prefix = CommandBarSearch.Score("Brightness up", "", "bright")!.Value;
        var inside = CommandBarSearch.Score("Screen brightness", "", "bright")!.Value;
        var keyword = CommandBarSearch.Score("Display", "brightness", "bright")!.Value;
        Assert.True(exact > prefix);
        Assert.True(prefix > inside);
        Assert.True(inside > keyword);
    }

    [Fact]
    public void ForgivesTypingMistakes()
    {
        Assert.True(CommandBarSearch.Matches("brilho", "", "brlho")); // subsequence
        Assert.True(CommandBarSearch.Matches("brilho", "", "birlho")); // transposition
        Assert.True(CommandBarSearch.Matches("calendar", "", "calendr")); // missing letter
        Assert.True(CommandBarSearch.Matches("calendar", "", "calrndar")); // one substitution
        Assert.False(CommandBarSearch.Matches("calendar", "", "xyz"));
        Assert.False(CommandBarSearch.Matches("code 1234", "", "1243")); // digits are values, not typos
        Assert.False(CommandBarSearch.Matches("ab", "", "ba")); // too short to rescue
    }

    [Fact]
    public void EveryTokenMustMatch()
    {
        Assert.True(CommandBarSearch.Matches("Annual report", "", "report annual"));
        Assert.False(CommandBarSearch.Matches("Annual report", "", "report monthly"));
    }

    [Fact]
    public void RanksPriorityThenTierThenScoreThenOrder()
    {
        var candidates = new List<CommandBarCandidate>
        {
            CommandBarCandidate.Create(0, "Firefox"),
            CommandBarCandidate.Create(1, "Fire"),
            CommandBarCandidate.Create(2, "Campfire"),
            CommandBarCandidate.Create(3, "Spitfire", priority: 2400),
            CommandBarCandidate.Create(4, "Notes"),
        };
        Assert.Equal([3, 1, 0, 2], CommandBarSearch.RankedIndexes(candidates, "fire"));
    }

    [Fact]
    public void TiesKeepCatalogOrder()
    {
        var candidates = new List<CommandBarCandidate> { CommandBarCandidate.Create(7, "Alpha one"), CommandBarCandidate.Create(3, "Alpha two") };
        Assert.Equal([7, 3], CommandBarSearch.RankedIndexes(candidates, "alpha"));
    }

    [Fact]
    public void BoostsBreakTiesWithinATier()
    {
        var candidates = new List<CommandBarCandidate>
        {
            CommandBarCandidate.Create(0, "Notepad"),
            CommandBarCandidate.Create(1, "Notes", boost: 100),
        };
        Assert.Equal([1, 0], CommandBarSearch.RankedIndexes(candidates, "note"));
    }

    [Fact]
    public void HighlightsLiteralMatchesPreferringWordStarts()
    {
        Assert.Equal([0, 1, 2], CommandBarSearch.HighlightOffsets("Settings reset", "set").OrderBy(i => i));
        Assert.Equal([9, 10, 11], CommandBarSearch.HighlightOffsets("Reset my settings", "set").Where(i => i >= 9).OrderBy(i => i));
        Assert.Equal([0, 1, 2], CommandBarSearch.HighlightOffsets("Café", "caf").OrderBy(i => i));
        Assert.Empty(CommandBarSearch.HighlightOffsets("brilho", "brlho"));
    }

    [Fact]
    public void SplitsTrailingNumbers()
    {
        Assert.Equal(new CommandBarNumberSplit("volume", 40), CommandBarSearch.SplitTrailingNumber("volume 40"));
        Assert.Equal(new CommandBarNumberSplit("volume", 40), CommandBarSearch.SplitTrailingNumber("volume 40%"));
        Assert.Equal(new CommandBarNumberSplit("40", null), CommandBarSearch.SplitTrailingNumber("40"));
        Assert.Equal(new CommandBarNumberSplit("code abc", null), CommandBarSearch.SplitTrailingNumber("code abc"));
    }

    [Fact]
    public void ClampsArguments()
    {
        Assert.Equal(100, CommandBarSearch.ArgumentValue("150", 0, 100));
        Assert.Equal(40, CommandBarSearch.ArgumentValue("40%", 0, 100));
        Assert.Null(CommandBarSearch.ArgumentValue("forty", 0, 100));
        Assert.Null(CommandBarSearch.ArgumentValue("12345", 0, 100));
    }

    [Fact]
    public void ReadsTheEmojiColon()
    {
        Assert.Equal("fire", CommandBarSearch.EmojiQuery(":fire"));
        Assert.Equal(string.Empty, CommandBarSearch.EmojiQuery(" : "));
        Assert.Null(CommandBarSearch.EmojiQuery("fire"));
    }

    [Fact]
    public void MatchesVerbs()
    {
        Assert.True(CommandBarSearch.MatchesVerb("quit chrome", "Quit {0}"));
        Assert.True(CommandBarSearch.MatchesVerb("qu", "Quit {0}"));
        Assert.False(CommandBarSearch.MatchesVerb("chrome", "Quit {0}"));
    }

    [Fact]
    public void KeepsFirstOccurrences() => Assert.Equal([0, 1, 3], CommandBarSearch.FirstOccurrences(["a", "b", "a", "c", "b"]));

    [Fact]
    public void PlacesColourPreview()
    {
        Assert.Equal(0, CommandBarSearch.ColorPreviewIndex(["Notes"], "#cafe"));
        Assert.Equal(1, CommandBarSearch.ColorPreviewIndex(["Issue #cafe"], "#cafe"));
    }

    [Fact]
    public void EditDistanceHelpers()
    {
        Assert.True(CommandBarSearch.IsAdjacentTransposition("ab", "ba"));
        Assert.False(CommandBarSearch.IsAdjacentTransposition("abc", "abc"));
        Assert.True(CommandBarSearch.WithinOneEdit("kitten", "sitten"));
        Assert.False(CommandBarSearch.WithinOneEdit("kitten", "sittin"));
        Assert.True(CommandBarSearch.IsSubsequence("ntpd", "notepad"));
    }
}

public class CommandBarLinksTests
{
    [Fact]
    public void RoundTripsAndDropsIncompleteLinks()
    {
        var links = new List<CommandBarLink>
        {
            new() { Name = "gh", Destination = "https://github.com/search?q={query}" },
            new() { Name = "", Destination = "x" },
            new() { Name = "docs", Kind = CommandBarLinkKind.Place, Destination = "~\\Documents" },
        };
        var decoded = CommandBarLinks.Decode(CommandBarLinks.Encode(links));
        Assert.Equal(2, decoded.Count);
        Assert.Equal(CommandBarLinkKind.Place, decoded[1].Kind);
        Assert.Equal(links[0].Id, decoded[0].Id);
        Assert.Empty(CommandBarLinks.Decode("not json"));
        Assert.Empty(CommandBarLinks.Decode(null));
    }

    [Fact]
    public void ReadsMissingFieldsWithDefaults()
    {
        var decoded = CommandBarLinks.Decode("[{\"name\":\"x\",\"destination\":\"example.com\"}]");
        Assert.Single(decoded);
        Assert.Equal(CommandBarLinkKind.Link, decoded[0].Kind);
        Assert.False(decoded[0].RunsDirectly);
    }

    [Fact]
    public void ExpandsPlaceholders()
    {
        Assert.Equal("https://x.com/?q=a%20b%26c", CommandBarLinks.Expand("https://x.com/?q={query}", CommandBarLinkKind.Link, query: "a b&c"));
        Assert.Equal("C:\\Notes\\a b", CommandBarLinks.Expand("C:\\Notes\\{query}", CommandBarLinkKind.Place, query: "a b"));
        Assert.Equal("d=2026-10-14 c=", CommandBarLinks.Expand("d={date} c={clipboard}", CommandBarLinkKind.Place, date: "2026-10-14"));
    }

    [Fact]
    public void FindsTheArgumentAfterTheName()
    {
        Assert.Equal("Fieldkit App", CommandBarLinks.TrailingArgument("gh Fieldkit App", "gh"));
        Assert.Null(CommandBarLinks.TrailingArgument("ghost", "gh"));
        Assert.Null(CommandBarLinks.TrailingArgument("gh", "gh"));
        Assert.Equal("x", CommandBarLinks.TrailingArgument("My Site x", "my site"));
        Assert.Equal("gh foo", CommandBarLinks.RankingTitle("gh", "gh foo"));
        Assert.Equal("gh", CommandBarLinks.RankingTitle("gh", "gh"));
    }

    [Fact]
    public void PicksTheMostSpecificScript()
    {
        var run = new CommandBarLink { Name = "run", Kind = CommandBarLinkKind.Script, Destination = "a.ps1" };
        var report = new CommandBarLink { Name = "run report", Kind = CommandBarLinkKind.Script, Destination = "b.ps1" };
        var match = CommandBarLinks.MatchingScriptLink([run, report], "run report now");
        Assert.NotNull(match);
        Assert.Same(report, match.Value.Link);
        Assert.Equal("now", match.Value.Argument);
        Assert.Null(CommandBarLinks.ScriptArgument(run, "run"));
        run.RunsWithoutArgument = true;
        Assert.Equal(string.Empty, CommandBarLinks.ScriptArgument(run, "run"));
        Assert.Null(CommandBarLinks.ScriptArgument(run, "ru"));
    }

    [Fact]
    public void FindsDirectRunScripts()
    {
        var script = new CommandBarLink { Name = "s", Kind = CommandBarLinkKind.Script, Destination = "a.cmd", RunsDirectly = true };
        Assert.Same(script, CommandBarLinks.DirectRunScript("link." + script.Id, [script]));
        Assert.Null(CommandBarLinks.DirectRunScript("app.x", [script]));
        script.RunsDirectly = false;
        Assert.Null(CommandBarLinks.DirectRunScript("link." + script.Id, [script]));
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("www.github.io/x", "https://www.github.io/x")]
    [InlineData("localhost.dev:8080", "https://localhost.dev:8080/")]
    public void DetectsTypedUrls(string text, string expected) => Assert.Equal(expected, CommandBarLinks.TypedUrl(text)?.ToString());

    [Theory]
    [InlineData("notes.md")]
    [InlineData("build.sh")]
    [InlineData("hello world.com")]
    [InlineData("me@example.com")]
    [InlineData("3.14")]
    [InlineData("ftp://x.com")]
    [InlineData("example")]
    public void LeavesSearchesAlone(string text) => Assert.Null(CommandBarLinks.TypedUrl(text));

    [Fact]
    public void BuildsUrls()
    {
        var site = new CommandBarLink { Name = "x", Destination = "example.com" };
        Assert.Equal("https://example.com/", CommandBarLinks.Url(site, "example.com")?.ToString());
        var app = new CommandBarLink { Name = "x", Destination = "ms-settings:display" };
        Assert.Equal("ms-settings:display", CommandBarLinks.Url(app, "ms-settings:display")?.ToString());
        var script = new CommandBarLink { Name = "x", Kind = CommandBarLinkKind.Script, Destination = "a.ps1" };
        Assert.Null(CommandBarLinks.Url(script, "a.ps1"));
    }

    [Fact]
    public void RevealsOnlyFinishedPlaces()
    {
        Assert.NotNull(CommandBarLinks.RevealPath(new CommandBarLink { Name = "d", Kind = CommandBarLinkKind.Place, Destination = "C:\\Docs" }));
        Assert.Null(CommandBarLinks.RevealPath(new CommandBarLink { Name = "d", Kind = CommandBarLinkKind.Place, Destination = "C:\\{query}" }));
        Assert.Null(CommandBarLinks.RevealPath(new CommandBarLink { Name = "d", Destination = "example.com" }));
    }

    [Fact]
    public void TrimsScriptOutput()
    {
        Assert.Equal("ok", CommandBarLinks.ResultText("  ok\r\n"));
        Assert.Null(CommandBarLinks.ResultText(" \n"));
    }
}
