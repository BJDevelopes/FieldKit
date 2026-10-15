// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.ClipboardTools;

namespace Fieldkit.Tests.ClipboardTools;

public class ClipboardSearchTests
{
    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("  Line\nBreak\tTab ", "line break tab")]
    [InlineData("İstanbul", "istanbul")]
    public void NormalizeFoldsCaseAccentsAndWidth(string input, string expected) =>
        Assert.Equal(expected, ClipboardSearch.Normalize(input));

    [Fact]
    public void EveryTokenMustMatch()
    {
        Assert.True(ClipboardSearch.Matches("The quick brown fox", "QUICK fox"));
        Assert.False(ClipboardSearch.Matches("The quick brown fox", "quick cat"));
        Assert.True(ClipboardSearch.Matches("anything", "   "));
        Assert.True(ClipboardSearch.Matches("résumé.pdf", "resume"));
    }

    [Fact]
    public void ExactThenPrefixThenWordThenSubstring()
    {
        var candidates = new List<SearchCandidate>
        {
            new(0, "a report about apples", false),
            new(1, "reportage", false),
            new(2, "report", false),
            new(3, "report card", false),
            new(4, "unrelated", false),
        };
        Assert.Equal([2, 3, 1, 0], ClipboardSearch.Ranked(candidates, "report"));
    }

    [Fact]
    public void PinsWinTiesAndOrderIsStableOtherwise()
    {
        var candidates = new List<SearchCandidate>
        {
            new(0, "note one", false),
            new(1, "note two", true),
            new(2, "note three", false),
        };
        Assert.Equal([1, 0, 2], ClipboardSearch.Ranked(candidates, "note"));
        Assert.Equal([0, 1, 2], ClipboardSearch.Ranked(candidates, ""));
    }

    [Fact]
    public void HighlightsAreAccentInsensitiveAndMerged()
    {
        var ranges = ClipboardSearch.HighlightRanges("Café and CAFE", ["cafe"]);
        Assert.Equal([(0, 4), (9, 4)], ranges);
        Assert.Equal([(0, 5)], ClipboardSearch.HighlightRanges("abcde", ["abc", "cde"]));
        Assert.Empty(ClipboardSearch.HighlightRanges("", ["x"]));
    }
}
