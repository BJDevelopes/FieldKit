// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Snippets;

namespace Fieldkit.Tests.Snippets;

public class SnippetEngineTests
{
    private static TextSnippet Snip(string trigger, string text = "x", SnippetExpansion mode = SnippetExpansion.AfterDelimiter,
        bool ignoreCase = false, bool enabled = true, string folder = "", bool library = true, string name = "") =>
        new() { Trigger = trigger, Replacement = text, Expansion = mode, IgnoresCase = ignoreCase, Enabled = enabled, Folder = folder, ShowsInLibrary = library, Name = name };

    [Fact]
    public void TheLongestTriggerWins()
    {
        var snippets = new[] { Snip(";email"), Snip(";email2"), Snip("l2") };
        Assert.Equal(";email2", SnippetEngine.Match("hi ;email2", SnippetExpansion.AfterDelimiter, snippets)!.Trigger);
        Assert.Equal(";email", SnippetEngine.Match(";email", SnippetExpansion.AfterDelimiter, snippets)!.Trigger);
    }

    [Fact]
    public void ModeDisabledAndCaseAreRespected()
    {
        var snippets = new[] { Snip(";now", mode: SnippetExpansion.Immediate), Snip(";off", enabled: false), Snip(";Sig", ignoreCase: true) };
        Assert.Null(SnippetEngine.Match(";now", SnippetExpansion.AfterDelimiter, snippets));
        Assert.NotNull(SnippetEngine.Match("x;now", SnippetExpansion.Immediate, snippets));
        Assert.Null(SnippetEngine.Match(";off", SnippetExpansion.AfterDelimiter, snippets));
        Assert.NotNull(SnippetEngine.Match(";SIG", SnippetExpansion.AfterDelimiter, snippets));
        Assert.Null(SnippetEngine.Match(";SIG", SnippetExpansion.AfterDelimiter, [Snip(";Sig")]));
    }

    [Fact]
    public void BufferKeepsOnlyTheNewestCharacters()
    {
        var buffer = SnippetEngine.Append(new string('a', 63), "bc");
        Assert.Equal(SnippetEngine.BufferLimit, buffer.Length);
        Assert.EndsWith("abc", buffer);
        Assert.Equal("ab", SnippetEngine.Backspace("abc"));
        Assert.Equal("a", SnippetEngine.Backspace("a😀"));
        Assert.Equal(string.Empty, SnippetEngine.Backspace(string.Empty));
    }

    [Theory]
    [InlineData(" ", true)]
    [InlineData("\t", true)]
    [InlineData("\r", true)]
    [InlineData("a", false)]
    [InlineData("", false)]
    public void Delimiters(string typed, bool expected) => Assert.Equal(expected, SnippetEngine.IsDelimiter(typed));

    [Fact]
    public void TriggersAreSanitizedAndValidated()
    {
        Assert.Equal(";ab", SnippetEngine.SanitizedTrigger(" ;a b\t"));
        Assert.Equal(SnippetEngine.MaxTriggerLength, SnippetEngine.SanitizedTrigger(new string('x', 99)).Length);
        var others = new[] { Snip(";mail") };
        Assert.Equal("The trigger needs at least 2 characters.", SnippetEngine.Validate(";", "text", false, others));
        Assert.Equal("Another snippet already uses this trigger.", SnippetEngine.Validate(";mail", "text", false, others));
        Assert.Equal("Another snippet already uses this trigger.", SnippetEngine.Validate(";MAIL", "text", true, others));
        Assert.Null(SnippetEngine.Validate(";MAIL", "text", false, others));
        Assert.Equal(string.Empty, SnippetEngine.Validate(";new", "", false, others));
        Assert.Equal(string.Empty, SnippetEngine.Validate("", "text", false, others));
    }

    [Fact]
    public void VisibleLengthCountsWhatTheUserSees() => Assert.Equal(3, SnippetEngine.VisibleLength("a😀é"));

    [Fact]
    public void LibraryGroupsByFolderWithLooseSnippetsLast()
    {
        var snippets = new[]
        {
            Snip(";loose", name: "Loose note"),
            Snip(";w", folder: "Work", name: "Work mail"),
            Snip(";a", folder: "Home", name: "Address", text: "Elm Street 1"),
            Snip(";h", folder: "Hidden", library: false),
            Snip(";d", folder: "Disabled", enabled: false),
            Snip(";s", folder: "Work", name: "Signature"),
        };
        var sections = SnippetEngine.LibrarySections(snippets, "");
        Assert.Equal(["Home", "Work", ""], sections.Select(s => s.Folder));
        Assert.Equal([";a", ";w", ";s", ";loose"], SnippetEngine.LibraryRows(sections).Select(s => s.Trigger));
        Assert.Equal([";a"], SnippetEngine.LibraryRows(SnippetEngine.LibrarySections(snippets, "elm street")).Select(s => s.Trigger));
        Assert.Equal([";w", ";s"], SnippetEngine.LibraryRows(SnippetEngine.LibrarySections(snippets, "WORK")).Select(s => s.Trigger));
        Assert.Equal(["Disabled", "Hidden", "Home", "Work"], SnippetEngine.FolderSuggestions(snippets));
    }

    [Fact]
    public void SnippetFilesRoundTripInUpstreamsFormat()
    {
        var snippet = Snip(";addr", "Elm Street 1", SnippetExpansion.Immediate, folder: "Home", name: "Address");
        var json = SnippetStore.Serialize([snippet]);
        Assert.Contains("\"expansion\": \"immediate\"", json);
        Assert.Contains("\"showsInLibrary\": true", json);
        Assert.Equal(snippet, Assert.Single(SnippetStore.Parse(json)!));
    }

    [Fact]
    public void ParsesUpstreamFilesAndSkipsBadEntries()
    {
        const string json = """
            [
              {"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"Mail","trigger":";m ail","replacement":"me@x.example","expansion":"afterDelimiter","enabled":true},
              {"id":"not-a-guid","trigger":";x","replacement":"y"},
              {"trigger":";","replacement":"too short"},
              {"trigger":";empty","replacement":""}
            ]
            """;
        var parsed = SnippetStore.Parse(json)!;
        var only = Assert.Single(parsed);
        Assert.Equal(";mail", only.Trigger);
        Assert.False(only.IgnoresCase);
        Assert.True(only.ShowsInLibrary);
        Assert.Null(SnippetStore.Parse("{\"not\":\"an array\"}"));
        Assert.Null(SnippetStore.Parse("nonsense"));
    }

    [Fact]
    public void ImportReplacesByIdAndSkipsTakenTriggers()
    {
        var a = Snip(";a", "old");
        var b = Snip(";b");
        var (result, imported) = SnippetStore.Merge([a, b], [a with { Replacement = "new" }, Snip(";b", "dupe"), Snip(";c")]);
        Assert.Equal(2, imported);
        Assert.Equal(["new", "x", "x"], result.Select(s => s.Replacement));
        Assert.Equal([";a", ";b", ";c"], result.Select(s => s.Trigger));
    }
}

public class SnippetVariablesTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTime Noon = new(2026, 3, 7, 15, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void DateTimeAndClipboardVariables()
    {
        var e = SnippetVariables.Expand("{{date}} | {{time}} | {{datetime}} | {{clipboard}}", Noon, "copied", Us, TimeZoneInfo.Utc);
        Assert.Equal("Mar 7, 2026 | 3:04 PM | Mar 7, 2026 3:04 PM | copied", e.Text);
        Assert.Equal(0, e.CaretBack);
    }

    [Fact]
    public void FormattedDatesUseIcuPatterns()
    {
        var e = SnippetVariables.Expand("{{date:yyyy-MM-dd}} {{time:HH:mm:ss}} {{datetime:EEEE, MMMM d 'at' h a}}", Noon, null, Us, TimeZoneInfo.Utc);
        Assert.Equal("2026-03-07 15:04:05 Saturday, March 7 at 3 PM", e.Text);
    }

    [Fact]
    public void TimeZoneOverrides()
    {
        var e = SnippetVariables.Expand("{{time-tz(Asia/Tokyo):HH:mm XXX}}", Noon, null, Us, TimeZoneInfo.Utc);
        Assert.Equal("00:04 +09:00", e.Text);
    }

    [Fact]
    public void UnknownOrBrokenTagsStayVisible()
    {
        var e = SnippetVariables.Expand("{{nope}} {{date-tz(Nowhere/City):yyyy}} {{date:}} {{date:yyyy", Noon, null, Us, TimeZoneInfo.Utc);
        Assert.Equal("{{nope}} {{date-tz(Nowhere/City):yyyy}} {{date:}} {{date:yyyy", e.Text);
    }

    [Fact]
    public void ClipboardTextIsNeverReexpanded()
    {
        var e = SnippetVariables.Expand("[{{clipboard}}]", Noon, "{{date}} {{cursor}}", Us, TimeZoneInfo.Utc);
        Assert.Equal("[{{date}} {{cursor}}]", e.Text);
        Assert.Equal(0, e.CaretBack);
    }

    [Fact]
    public void CursorMarksWhereTheCaretEnds()
    {
        var e = SnippetVariables.Expand("Dear {{cursor}},\nBest {{cursor}}", Noon, null, Us, TimeZoneInfo.Utc);
        Assert.Equal("Dear ,\nBest ", e.Text);
        Assert.Equal(7, e.CaretBack);
        Assert.True(SnippetVariables.RequiresPaste(e.Text));
        Assert.False(SnippetVariables.RequiresPaste("single line"));
    }

    [Fact]
    public void NeedsClipboardOnlyWithTheVariable()
    {
        Assert.True(SnippetVariables.NeedsClipboard("x {{clipboard}}"));
        Assert.False(SnippetVariables.NeedsClipboard("x {{date}}"));
    }

    [Fact]
    public void FindsTheTagUnderTheCaret()
    {
        const string text = "a {{bad}} {{date:yyyy}} b";
        var token = SnippetVariables.TokenAt(text, 12)!;
        Assert.Equal(10, token.Start);
        Assert.Equal(13, token.Length);
        Assert.Equal(DateVariableKind.Date, token.Kind);
        Assert.Equal("yyyy", token.Pattern);
        Assert.Null(SnippetVariables.TokenAt(text, 4));
        Assert.Null(SnippetVariables.TokenAt(text, 10)); // before the opening braces
    }

    [Fact]
    public void BuilderStylesRoundTrip()
    {
        Assert.Equal("{{date:yyyy-MM-dd}}", SnippetVariables.TagText(DateVariableKind.Date, DateVariableStyle.Iso8601, "", null, Us));
        Assert.Equal("{{datetime-tz(Europe/Paris):yyyy-MM-dd'T'HH:mm:ssXXX}}",
            SnippetVariables.TagText(DateVariableKind.DateTime, DateVariableStyle.Iso8601, "", "Europe/Paris", Us));
        Assert.Equal("{{time:H.mm}}", SnippetVariables.TagText(DateVariableKind.Time, DateVariableStyle.Custom, "H.mm", null, Us));
        foreach (var style in new[] { DateVariableStyle.Short, DateVariableStyle.Medium, DateVariableStyle.Long, DateVariableStyle.Full })
        {
            var pattern = SnippetVariables.ResolvedPattern(DateVariableKind.Date, style, "", Us);
            Assert.Equal(style, SnippetVariables.MatchingStyle(pattern, DateVariableKind.Date, Us));
        }
        Assert.Equal(DateVariableStyle.Custom, SnippetVariables.MatchingStyle("yy/d", DateVariableKind.Date, Us));
        Assert.Equal("M/d/yyyy", SnippetVariables.ResolvedPattern(DateVariableKind.Date, DateVariableStyle.Short, "", Us));
        Assert.Equal("MMM d, yyyy", SnippetVariables.ResolvedPattern(DateVariableKind.Date, DateVariableStyle.Medium, "", Us));
        Assert.Equal("Saturday, March 7, 2026",
            SnippetVariables.PreviewText(DateVariableKind.Date, DateVariableStyle.Full, "", "Etc/UTC", Noon, Us));
    }

    [Theory]
    [InlineData("dddd, MMMM d, yyyy", "MMMM d, yyyy")]
    [InlineData("d MMMM yyyy", "d MMMM yyyy")]
    [InlineData("dddd d MMMM yyyy", "d MMMM yyyy")]
    [InlineData("yyyy'年'M'月'd'日' dddd", "yyyy'年'M'月'd'日'")]
    public void WeekdaysCanBeDropped(string pattern, string expected) => Assert.Equal(expected, SnippetVariables.WithoutWeekday(pattern));

    [Fact]
    public void TimeZonesAreSearchable()
    {
        var known = new[] { "America/New_York", "America/Los_Angeles", "Europe/London", "Asia/Tokyo" };
        Assert.Equal("America/New_York", SnippetVariables.MatchingZones("new york", known)[0]);
        Assert.Equal("America/New_York", SnippetVariables.ResolvedZone("new york", known));
        Assert.Equal("America/Los_Angeles", SnippetVariables.ResolvedZone("PST", known));
        Assert.Null(SnippetVariables.ResolvedZone("america", known));
        Assert.Empty(SnippetVariables.MatchingZones("", known));
    }
}

public class IcuDateFormatTests
{
    private static readonly DateTime When = new(2026, 1, 9, 7, 5, 3, 120, DateTimeKind.Utc);

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.SSS", "2026-01-09 07:05:03.120")]
    [InlineData("yy y M MM MMM MMMM d dd", "26 2026 1 01 Jan January 9 09")]
    [InlineData("E EEEE EEEEE", "Fri Friday F")]
    [InlineData("h:mm a k K", "7:05 AM 7 7")]
    [InlineData("'Week' w, 'day' D, Q QQQ", "Week 2, day 9, 1 Q1")]
    [InlineData("'It''s' h 'o''clock'", "It's 7 o'clock")]
    [InlineData("X XX XXX Z ZZZZ ZZZZZ", "Z Z Z +0000 GMT Z")]
    public void FormatsLikeIcu(string pattern, string expected) =>
        Assert.Equal(expected, IcuDateFormat.Format(When, pattern, CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.Utc));

    [Fact]
    public void OffsetsInOtherZones()
    {
        var india = SnippetVariables.FindZone("Asia/Kolkata")!;
        Assert.Equal("12:35 +0530 +05:30 GMT+5:30", IcuDateFormat.Format(When, "HH:mm xx xxx O", CultureInfo.InvariantCulture, india));
    }

    [Fact]
    public void ConvertsDotNetPatterns()
    {
        var dtf = CultureInfo.GetCultureInfo("en-US").DateTimeFormat;
        Assert.Equal("EEEE, MMMM d, yyyy", IcuDateFormat.FromDotNet("dddd, MMMM d, yyyy", dtf));
        Assert.Equal("h:mm:ss a", IcuDateFormat.FromDotNet("h:mm:ss tt", dtf));
        Assert.Equal("d. MMMM yyyy", IcuDateFormat.FromDotNet("d. MMMM yyyy", CultureInfo.GetCultureInfo("de-DE").DateTimeFormat));
        Assert.Equal("yyyy年M月d日", IcuDateFormat.FromDotNet("yyyy'年'M'月'd'日'", dtf));
        Assert.Equal("d' de 'MMMM", IcuDateFormat.FromDotNet("d 'de' MMMM", dtf));
        Assert.Equal("dd.MM.yyyy", IcuDateFormat.FromDotNet("dd/MM/yyyy", CultureInfo.GetCultureInfo("de-DE").DateTimeFormat));
    }
}
