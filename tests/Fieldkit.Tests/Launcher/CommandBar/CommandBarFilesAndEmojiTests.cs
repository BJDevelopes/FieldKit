// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Launcher.CommandBar;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarFileSearchSupportTests
{
    [Fact]
    public void BuildsScopedSqlWithEveryWord()
    {
        var sql = CommandBarFileSearchSupport.Sql("annual report", ["C:\\Users\\me\\Documents", "D:\\Work"]);
        Assert.NotNull(sql);
        Assert.Contains("SCOPE='file:C:/Users/me/Documents' OR SCOPE='file:D:/Work'", sql);
        Assert.Contains("System.FileName LIKE '%annual%'", sql);
        Assert.Contains("System.FileName LIKE '%report%'", sql);
        Assert.StartsWith("SELECT TOP 1000 ", sql);
    }

    [Fact]
    public void EscapesWhatWasTyped()
    {
        var sql = CommandBarFileSearchSupport.Sql("it's 50%_[x]", ["C:\\A"])!;
        Assert.Contains("'%it''s%'", sql);
        Assert.Contains("'%50[%][_][[]x]%'", sql);
    }

    [Fact]
    public void AsksNothingWithoutScopesOrLetters()
    {
        Assert.Null(CommandBarFileSearchSupport.Sql("a", ["C:\\A"]));
        Assert.Null(CommandBarFileSearchSupport.Sql("report", []));
        Assert.Null(CommandBarFileSearchSupport.Sql("  ", ["C:\\A"]));
    }

    [Fact]
    public void ExpandsTheProfileIntoItsFolders()
    {
        var scopes = CommandBarFileSearchSupport.ResolvedScopes(["~", "D:\\Work\\", "D:\\Work", "X:\\Missing"], "C:\\Users\\me",
            ["Documents", "AppData", ".config", "Pictures"], path => !path.StartsWith("X:", StringComparison.Ordinal));
        Assert.Equal(["C:\\Users\\me\\Documents", "C:\\Users\\me\\Pictures", "D:\\Work"], scopes);
        Assert.Empty(CommandBarFileSearchSupport.ResolvedScopes([], "C:\\Users\\me", [], _ => true));
    }

    [Fact]
    public void NeverOffersHiddenOrAppDataPaths()
    {
        Assert.True(CommandBarFileSearchSupport.IsOfferable("C:\\Users\\me\\Documents\\a.txt", _ => false));
        Assert.False(CommandBarFileSearchSupport.IsOfferable("C:\\Users\\me\\.ssh\\id", _ => false));
        Assert.False(CommandBarFileSearchSupport.IsOfferable("C:\\Users\\me\\AppData\\Local\\x.txt", _ => false));
        Assert.False(CommandBarFileSearchSupport.IsOfferable("C:\\Users\\me\\Docs\\.env", _ => false));
        Assert.False(CommandBarFileSearchSupport.IsOfferable("C:\\Users\\me\\Secret\\a.txt", p => p.EndsWith("Secret", StringComparison.Ordinal)));
    }

    [Fact]
    public void IgnoresWholeNamesAndExtensions()
    {
        Assert.True(CommandBarFileSearchSupport.IsIgnored("C:\\p\\build\\a.txt", ["build"]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored("C:\\p\\rebuild-notes.md", ["build"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored("C:\\p\\x.log", ["*.log"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored("C:\\p\\x.LOG", [".log"]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored("C:\\p\\catalog", [".log"]));
    }

    [Fact]
    public void OffersDeduplicatedCappedPaths()
    {
        var paths = new[] { "C:\\a\\x.txt", "C:\\a\\x.txt", "C:\\a\\node_modules\\y.txt", "C:\\a\\z.log" };
        Assert.Equal(["C:\\a\\x.txt"], CommandBarFileSearchSupport.Offerable(paths, ["*.log"], _ => false));
        var many = Enumerable.Range(0, 500).Select(i => $"C:\\a\\f{i}.txt");
        Assert.Equal(CommandBarFileSearchSupport.ResultLimit, CommandBarFileSearchSupport.Offerable(many, [], _ => false).Count);
    }

    [Fact]
    public void StoresListsOnePerLine()
    {
        Assert.Equal(["a", "b"], CommandBarFileSearchSupport.DecodeList("a\n\n b \nA"));
        Assert.Equal("a\nb", CommandBarFileSearchSupport.EncodeList(["a", "b", "a"]));
    }

    [Fact]
    public void AbbreviatesTheProfile()
    {
        Assert.Equal("~\\Documents\\a.txt", CommandBarFileSearchSupport.Abbreviating("C:\\Users\\me\\Documents\\a.txt", "C:\\Users\\me"));
        Assert.Equal("~", CommandBarFileSearchSupport.Abbreviating("C:\\Users\\me", "C:\\Users\\me\\"));
        Assert.Equal("D:\\x", CommandBarFileSearchSupport.Abbreviating("D:\\x", "C:\\Users\\me"));
        Assert.Equal("C:\\Users\\meme", CommandBarFileSearchSupport.Abbreviating("C:\\Users\\meme", "C:\\Users\\me"));
    }

    [Fact]
    public void MatchesNamesWordByWord()
    {
        Assert.True(CommandBarFileSearchSupport.NameMatches("Report Annual.pdf", "annual report"));
        Assert.False(CommandBarFileSearchSupport.NameMatches("Report.pdf", "annual report"));
        Assert.True(CommandBarFileSearchSupport.ShouldPublish("a", "a"));
        Assert.False(CommandBarFileSearchSupport.ShouldPublish("a", "ab"));
    }
}

public class CommandBarEmojiTests
{
    [Fact]
    public void ShipsPopularEmojiFirstWithoutDuplicates()
    {
        var all = CommandBarEmoji.All;
        Assert.True(all.Count > 300);
        Assert.Equal("😀", all[0].Character);
        Assert.Equal(all.Count, all.Select(e => CommandBarEmoji.Canonical(e.Character)).Distinct().Count());
        Assert.All(all, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }

    [Fact]
    public void FindsEmojiByIntentWords()
    {
        var fire = CommandBarEmoji.All.Single(e => e.Character == "🔥");
        Assert.Contains("hot", fire.Keywords);
        var thanks = CommandBarEmoji.All.Where(e => CommandBarSearch.Matches(e.Name, e.Keywords, "thanks")).Select(e => e.Character).ToList();
        Assert.Contains("🙏", thanks);
    }

    [Fact]
    public void AppliesSkinTonesOnlyToBases()
    {
        Assert.Equal("👍\U0001F3FD", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Medium, "👍"));
        Assert.Equal("✌\U0001F3FB", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Light, "✌️"));
        Assert.Equal("🔥", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Dark, "🔥"));
        Assert.Equal("👍", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.None, "👍"));
        Assert.True(CommandBarEmoji.AcceptsSkinTone("✋"));
        Assert.False(CommandBarEmoji.AcceptsSkinTone("😀"));
    }

    [Theory]
    [InlineData(CommandBarEmoji.SkinTone.None)]
    [InlineData(CommandBarEmoji.SkinTone.Light)]
    [InlineData(CommandBarEmoji.SkinTone.MediumDark)]
    public void TonesRoundTrip(CommandBarEmoji.SkinTone tone) => Assert.Equal(tone, CommandBarEmoji.ParseTone(CommandBarEmoji.StorageValue(tone)));

    [Fact]
    public void UnknownToneIsTheDefault() => Assert.Equal(CommandBarEmoji.SkinTone.None, CommandBarEmoji.ParseTone("purple"));
}
