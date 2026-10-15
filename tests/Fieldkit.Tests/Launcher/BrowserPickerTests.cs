// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Launcher.BrowserPicker;

namespace Fieldkit.Tests.Launcher;

public class BrowserPickerTests
{
    [Theory]
    [InlineData("https://github.com/BJDevelopes/FieldKit", true)]
    [InlineData("http://example.com", true)]
    [InlineData("\"https://example.com/a b\"", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("--disable-web-security", false)]
    [InlineData("", false)]
    public void OnlyWebLinksAreHandedToABrowser(string raw, bool accepted) =>
        Assert.Equal(accepted, BrowserPickerLogic.SafeUrl(raw) is not null);

    [Fact]
    public void TheBrowserGetsOneQuotedArgumentThatCannotCarrySwitches()
    {
        var url = BrowserPickerLogic.SafeUrl("https://example.com/a b\" --incognito")!;
        var arg = BrowserPickerLogic.Argument(url);
        Assert.StartsWith("\"https://", arg);
        Assert.Equal(2, arg.Count(c => c == '"')); // the link's own quote and space are escaped
        Assert.DoesNotContain(" ", arg);
    }

    [Fact]
    public void TheMostSpecificSiteRuleWins()
    {
        var rules = new List<BrowserRule> { new("github.com", "Chrome"), new("docs.github.com", "Edge"), new("example.org", "Firefox") };
        Assert.Equal("Edge", BrowserPickerLogic.Match(rules, "docs.github.com")?.BrowserId);
        Assert.Equal("Chrome", BrowserPickerLogic.Match(rules, "www.github.com")?.BrowserId);
        Assert.Equal("Chrome", BrowserPickerLogic.Match(rules, "gist.github.com")?.BrowserId);
        Assert.Null(BrowserPickerLogic.Match(rules, "notgithub.com"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("\"C:\\Edge\\msedge.exe\" --single-argument %1", @"C:\Edge\msedge.exe")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe -osint -url %1", @"C:\Program Files\Mozilla Firefox\firefox.exe")]
    public void TheProgramIsReadFromTheRegistryCommand(string command, string exe) =>
        Assert.Equal(exe, BrowserPickerLogic.ExeFromCommand(command));

    [Fact]
    public void RulesRoundTrip()
    {
        var rules = BrowserPickerLogic.Load("WWW.GitHub.com|Chrome\njunk\n|Edge\nexample.org|Firefox");
        Assert.Equal([new BrowserRule("github.com", "Chrome"), new BrowserRule("example.org", "Firefox")], rules);
        Assert.Equal("github.com|Chrome\nexample.org|Firefox", BrowserPickerLogic.Store(rules));
    }
}
