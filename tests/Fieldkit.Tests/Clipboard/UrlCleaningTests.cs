// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.ClipboardTools;

namespace Fieldkit.Tests.ClipboardTools;

public class UrlCleaningTests
{
    private static UrlCleaning.Rules Rules(string global = "", string sites = "", string disabled = "") =>
        UrlCleaning.RulesFrom(global, sites, disabled);

    [Fact]
    public void RemovesUtmAndClickIdsKeepingTheRest()
    {
        var result = UrlCleaning.Clean("https://example.com/a?id=7&utm_source=x&utm_medium=y&fbclid=abc&page=2")!;
        Assert.Equal("https://example.com/a?id=7&page=2", result.Url);
        Assert.Equal(["utm_source", "utm_medium", "fbclid"], result.Removed);
    }

    [Fact]
    public void AnyUtmPrefixIsATracker()
    {
        var result = UrlCleaning.Clean("https://example.com/?utm_whatever=1")!;
        Assert.Equal("https://example.com/", result.Url);
        Assert.Equal(["utm_whatever"], result.Removed);
    }

    [Fact]
    public void DropsTheQuestionMarkWhenNothingIsLeftAndKeepsTheFragment()
    {
        var result = UrlCleaning.Clean("https://example.com/page?gclid=1#section")!;
        Assert.Equal("https://example.com/page#section", result.Url);
    }

    [Fact]
    public void AQuestionMarkInTheFragmentIsNotAQuery()
    {
        var result = UrlCleaning.Clean("https://example.com/page#a?utm_source=x")!;
        Assert.Equal("https://example.com/page#a?utm_source=x", result.Url);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void ACleanLinkIsReturnedUntouched()
    {
        var result = UrlCleaning.Clean("  https://example.com/a?q=1  ")!;
        Assert.Equal("https://example.com/a?q=1", result.Url);
        Assert.Empty(result.Removed);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("https://example.com/a b")]
    [InlineData("ftp://example.com/?utm_source=x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("example.com/?utm_source=x")]
    [InlineData("")]
    [InlineData("two https://a.com links https://b.com")]
    public void NonLinksAreNeverAltered(string text) => Assert.Null(UrlCleaning.Clean(text));

    [Fact]
    public void SiteRulesOnlyApplyOnTheirSite()
    {
        Assert.Equal("https://www.youtube.com/watch?v=abc",
            UrlCleaning.Clean("https://www.youtube.com/watch?v=abc&si=XYZ&feature=share")!.Url);
        // si is a real parameter elsewhere.
        Assert.Equal("https://example.com/?si=1", UrlCleaning.Clean("https://example.com/?si=1")!.Url);
    }

    [Fact]
    public void TheSameNameMeansDifferentThingsOnDifferentSites()
    {
        // t is a tracker on X but the playback position on YouTube.
        Assert.Equal("https://x.com/user/status/1", UrlCleaning.Clean("https://x.com/user/status/1?t=abc&s=20")!.Url);
        Assert.Equal("https://youtu.be/abc?t=42", UrlCleaning.Clean("https://youtu.be/abc?t=42&si=zzz")!.Url);
    }

    [Fact]
    public void SubdomainsPickUpTheirSitesRules()
    {
        Assert.Equal("https://open.spotify.com/track/1", UrlCleaning.Clean("https://open.spotify.com/track/1?si=abc")!.Url);
    }

    [Fact]
    public void PercentEncodedNamesAreMatchedDecoded()
    {
        Assert.Equal("https://example.com/?a=1", UrlCleaning.Clean("https://example.com/?a=1&%75tm_source=x")!.Url);
        var reddit = UrlCleaning.Clean("https://www.reddit.com/r/x?%24deep_link=true&share_id=9")!;
        Assert.Equal("https://www.reddit.com/r/x", reddit.Url);
        Assert.Contains("$deep_link", reddit.Removed);
    }

    [Fact]
    public void ABareFlagIsMatchedByItsName()
    {
        Assert.Equal("https://example.com/?keep", UrlCleaning.Clean("https://example.com/?keep&utm_source")!.Url);
    }

    [Fact]
    public void ExistingEscapesAreNotReencoded()
    {
        Assert.Equal("https://example.com/caf%C3%A9?q=a%20b", UrlCleaning.Clean("https://example.com/caf%C3%A9?q=a%20b&fbclid=1")!.Url);
    }

    [Fact]
    public void UserAdditionsAndSwitchedOffBuiltInsApply()
    {
        var rules = Rules(global: "ref, Campaign", sites: "example.com|lang", disabled: "|fbclid,youtube.com|si");
        Assert.Equal("https://example.com/?fbclid=1", UrlCleaning.Clean("https://example.com/?ref=a&campaign=b&lang=en&fbclid=1", rules)!.Url);
        Assert.Equal("https://youtube.com/watch?v=1&si=2", UrlCleaning.Clean("https://youtube.com/watch?v=1&si=2&pp=3", rules)!.Url);
        // The site addition does not leak to other sites.
        Assert.Equal("https://other.com/?lang=en", UrlCleaning.Clean("https://other.com/?lang=en", rules)!.Url);
    }

    [Fact]
    public void TheUtmWildcardCanBeSwitchedOff()
    {
        var rules = Rules(disabled: "|utm_*");
        Assert.Equal("https://example.com/?utm_source=x", UrlCleaning.Clean("https://example.com/?utm_source=x&gclid=1", rules)!.Url);
    }

    [Fact]
    public void TokensRoundTrip()
    {
        var map = UrlCleaning.Tokens("youtube.com|si, Example.com|REF ,|global,broken,|");
        Assert.Equal(["si"], map["youtube.com"]);
        Assert.Equal(["ref"], map["example.com"]);
        Assert.Equal(["global"], map[UrlCleaning.AllSites]);
        Assert.Equal(3, map.Count);
        Assert.Equal("|global,example.com|ref,youtube.com|si", UrlCleaning.StorageValue(map));
    }

    [Theory]
    [InlineData("https://www.Example.com/path?q=1", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("www.youtube.com", "youtube.com")]
    [InlineData("localhost", null)]
    [InlineData(".example.com", null)]
    [InlineData("exa mple.com", null)]
    [InlineData("a|b.com", null)]
    public void SiteKeyAcceptsHostsAndLinks(string input, string? expected) => Assert.Equal(expected, UrlCleaning.SiteKey(input));

    [Theory]
    [InlineData(" UTM_Foo ", "utm_foo")]
    [InlineData("a=b", null)]
    [InlineData("a&b", null)]
    [InlineData("a b", null)]
    [InlineData("", null)]
    public void ParameterNameIsOneQueryName(string input, string? expected) => Assert.Equal(expected, UrlCleaning.ParameterName(input));

    [Fact]
    public void RuleGroupsStartWithAllSitesAndTheWildcard()
    {
        var groups = UrlCleaning.RuleGroupsFor(Rules(sites: "zzz.com|foo", disabled: "|gclid"));
        Assert.Equal(UrlCleaning.AllSites, groups[0].Site);
        Assert.Equal(UrlCleaning.UtmWildcard, groups[0].Entries[0].Name);
        Assert.False(groups[0].Entries.Single(e => e.Name == "gclid").IsEnabled);
        Assert.Contains(groups, g => g.Site == "youtube.com");
        var custom = groups.Single(g => g.Site == "zzz.com");
        Assert.False(custom.Entries[0].IsBuiltIn);
        Assert.Equal(groups.Select(g => g.Site).Skip(1).Order(StringComparer.Ordinal), groups.Select(g => g.Site).Skip(1));
    }

    [Fact]
    public void OutcomeMessagesMatchUpstream()
    {
        Assert.Equal("Paste a valid URL.", UrlCleaning.Message(UrlCleaning.OutcomeFor(null, "x")));
        Assert.Equal("Nothing to clean.", UrlCleaning.Message(UrlCleaning.OutcomeFor(UrlCleaning.Clean("https://a.com/"), "https://a.com/")));
        Assert.Equal("Removed utm_source, gclid",
            UrlCleaning.Message(UrlCleaning.OutcomeFor(UrlCleaning.Clean("https://a.com/?utm_source=1&gclid=2"), "https://a.com/?utm_source=1&gclid=2")));
    }

    [Fact]
    public void OnlyPlainLinkCopiesMayBeRewritten()
    {
        Assert.True(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "CF_TEXT", "CF_LOCALE", "CF_OEMTEXT"]));
        Assert.True(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "HTML Format", "Chromium internal source URL"]));
        Assert.False(UrlCleaning.CanRewriteClipboard([]));
        Assert.False(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "CF_HDROP"]));
        Assert.False(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "PNG"]));
        Assert.False(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "CF_DIB"]));
        Assert.False(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", "image/webp"]));
        Assert.False(UrlCleaning.CanRewriteClipboard(["CF_UNICODETEXT", ClipboardPrivacy.ExcludeFromMonitoring]));
    }

    [Fact]
    public void HtmlOfJustTheLinkMayBeDropped()
    {
        const string link = "https://example.com/a?utm_source=x";
        Assert.True(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\">Example page</a>", link));
        Assert.True(UrlCleaning.HtmlAllowsRewrite("<html><head><title>Other</title></head><body><a href='https://EXAMPLE.com/a?utm_source=x'>x</a></body></html>", link));
        Assert.True(UrlCleaning.HtmlAllowsRewrite($"Version:0.9\r\nStartHTML:0001\r\n<html><body><!--StartFragment-->{link}<!--EndFragment--></body></html>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite("<a href=\"https://other.com/\">Other</a>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\"><img src=\"pic.png\"></a>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite("<p>Some paragraph that shows more than the link</p>", link));
    }
}
