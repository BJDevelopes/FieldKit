// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.Launcher.CommandBar;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarPreferencesTests
{
    [Fact]
    public void StoresDisabledSourcesStably()
    {
        var disabled = CommandBarPreferences.DisabledSources("files, emoji,actions,bogus");
        Assert.Equal(new HashSet<CommandBarSource> { CommandBarSource.Files, CommandBarSource.Emoji }, disabled);
        Assert.Equal("emoji,files", CommandBarPreferences.StorageValue(disabled));
        Assert.True(CommandBarPreferences.IsEnabled(CommandBarSource.Actions, new HashSet<CommandBarSource> { CommandBarSource.Actions }));
        Assert.False(CommandBarPreferences.IsEnabled(CommandBarSource.Files, disabled));
    }

    [Theory]
    [InlineData("app.Chrome", CommandBarSource.Apps)]
    [InlineData("window.123", CommandBarSource.Windows)]
    [InlineData("winsettings.display", CommandBarSource.WindowsSettings)]
    [InlineData("settings.general", CommandBarSource.SettingsPages)]
    [InlineData("emoji.🔥", CommandBarSource.Emoji)]
    [InlineData("action.clipboard.open", CommandBarSource.Actions)]
    [InlineData("math.result", CommandBarSource.Actions)]
    public void FilesRowsUnderTheirSource(string id, CommandBarSource source) => Assert.Equal(source, CommandBarPreferences.SourceOf(id));

    [Fact]
    public void BrowseRowsCountAsActions()
    {
        Assert.True(CommandBarPreferences.IsActionRow(CommandBarPreferences.EmojiBrowserRowId));
        Assert.False(CommandBarPreferences.IsActionRow(CommandBarPreferences.EmojiBrowserRowId, new HashSet<CommandBarSource> { CommandBarSource.Emoji }));
        Assert.False(CommandBarPreferences.IsActionRow("app.x"));
    }

    [Fact]
    public void AliasesAreTrimmedCappedAndExclusive()
    {
        var aliases = CommandBarPreferences.SettingAlias("  codex  ", "app.Chat", []);
        Assert.Equal("codex", aliases["app.Chat"]);
        Assert.Equal(60, CommandBarPreferences.SettingAlias(new string('x', 90), "a", aliases)["a"].Length);
        Assert.False(CommandBarPreferences.SettingAlias("", "app.Chat", aliases).ContainsKey("app.Chat"));
        Assert.Equal("app.Chat", CommandBarPreferences.RowUsingAlias("CODEX tool", aliases, "app.Other"));
        Assert.Null(CommandBarPreferences.RowUsingAlias("codex", aliases, "app.Chat"));
        var roundTrip = CommandBarPreferences.DecodeAliases(CommandBarPreferences.EncodeAliases(aliases));
        Assert.Equal("codex", roundTrip["app.Chat"]);
        Assert.Empty(CommandBarPreferences.DecodeAliases("{bad"));
    }

    [Fact]
    public void AliasHitsOutrankTitles()
    {
        Assert.Equal(CommandBarPreferences.AliasExact, CommandBarPreferences.AliasHit("my codex", "codex"));
        Assert.Equal(CommandBarPreferences.AliasPrefix, CommandBarPreferences.AliasHit("codex", "cod"));
        Assert.Null(CommandBarPreferences.AliasHit("codex", "dex"));
        Assert.False(CommandBarPreferences.AcceptsAlias("window.1"));
        Assert.True(CommandBarPreferences.AcceptsAlias("app.x"));
    }

    [Fact]
    public void PinsToggleKeepOrderAndCap()
    {
        var pins = CommandBarPreferences.TogglingPin("a", []);
        pins = CommandBarPreferences.TogglingPin("b", pins);
        Assert.Equal(["a", "b"], pins);
        Assert.Equal(["b"], CommandBarPreferences.TogglingPin("a", pins));
        var many = Enumerable.Range(0, 30).Select(i => i.ToString()).ToList();
        var capped = CommandBarPreferences.TogglingPin("new", many);
        Assert.Equal(30, capped.Count);
        Assert.Equal("new", capped[^1]);
        Assert.Equal(["a", "b"], CommandBarPreferences.DecodePins("a\n b \n\na"));
        Assert.False(CommandBarPreferences.AcceptsPin("emoji.x"));
        Assert.True(CommandBarPreferences.AcceptsPin("app.x"));
        Assert.Equal(["b"], CommandBarPreferences.LeadingPins(["a", "b"], new HashSet<string> { "b" }));
        Assert.Equal(["app.gone", "action.here"], CommandBarPreferences.ListedPins(["app.gone", "action.here", "action.gone"], new HashSet<string> { "action.here" }));
    }

    [Fact]
    public void HiddenRowsToggle()
    {
        var hidden = CommandBarPreferences.TogglingHidden("x", new HashSet<string>());
        Assert.Contains("x", hidden);
        Assert.Empty(CommandBarPreferences.TogglingHidden("x", hidden));
        Assert.Equal("a\nb", CommandBarPreferences.EncodeHidden(["b", "a"]));
        Assert.Equal(2, CommandBarPreferences.DecodeHidden("a\nb\n").Count);
    }

    [Fact]
    public void PositionOffsetsRoundTripAndClamp()
    {
        Assert.Equal(new Vector(-40, 12), CommandBarPreferences.DecodePositionOffset(CommandBarPreferences.EncodePositionOffset(new Vector(-40.2, 11.7))));
        Assert.Equal(string.Empty, CommandBarPreferences.EncodePositionOffset(new Vector(0.2, -0.3)));
        Assert.Equal(new Vector(0, 0), CommandBarPreferences.DecodePositionOffset("nan,x"));
        var work = new Rect(0, 0, 1920, 1040);
        var origin = CommandBarPreferences.ClampedOrigin(new Size(560, 600), work, new Vector(0, 0));
        Assert.Equal(680, origin.X);
        Assert.Equal(1040 * 0.28, origin.Y, 3);
        var far = CommandBarPreferences.ClampedOrigin(new Size(560, 600), work, new Vector(5000, 5000));
        Assert.Equal(1920 - 560 - 16, far.X);
        Assert.Equal(1040 - 600 - 16, far.Y);
    }
}

public class CommandBarLearningTests
{
    [Fact]
    public void MemoryRemembersPrefixesAsATieBreaker()
    {
        var memory = new CommandBarQueryMemory();
        Assert.Equal(["n", "no", "not"], CommandBarQueryMemory.Prefixes("Not"));
        memory.Record("not", "app.notes", 1);
        Assert.Equal(1, memory.Boost("no", "app.notes"));
        memory.Record("not", "app.notes", 2);
        memory.Record("not", "app.notes", 3);
        memory.Record("not", "app.notes", 4);
        Assert.Equal(CommandBarQueryMemory.MaximumBoost, memory.Boost("n", "app.notes"));
        Assert.Equal(0, memory.Boost("x", "app.notes"));
        memory.Forget("app.notes");
        Assert.True(memory.IsEmpty);
    }

    [Fact]
    public void MemoryKeepsAFewRowsPerPrefix()
    {
        var memory = new CommandBarQueryMemory();
        for (var i = 0; i < 6; i++) memory.Record("a", "row" + i, i);
        var kept = Enumerable.Range(0, 6).Count(i => memory.Boost("a", "row" + i) > 0);
        Assert.Equal(CommandBarQueryMemory.IdsPerQuery, kept);
        Assert.Equal(0, memory.Boost("a", "row0"));
    }

    [Fact]
    public void UsageBoostsDecayWithAge()
    {
        const double now = 1_000_000;
        Assert.Equal(0, CommandBarUsage.Boost(null, now));
        Assert.Equal(24, CommandBarUsage.Boost(new CommandBarUse(2, now - 60), now));
        Assert.Equal(16, CommandBarUsage.Boost(new CommandBarUse(2, now - 10 * 3600), now));
        Assert.Equal(4, CommandBarUsage.Boost(new CommandBarUse(2, now - 30 * 86400), now));
        Assert.Equal(40 * 12, CommandBarUsage.Boost(new CommandBarUse(500, now), now));
    }

    [Fact]
    public void UsageRecordsAndCaps()
    {
        var usage = new Dictionary<string, CommandBarUse>();
        for (var i = 0; i < CommandBarUsage.StoredIdLimit + 5; i++) usage = CommandBarUsage.Recording(usage, "id" + i, i);
        Assert.Equal(CommandBarUsage.StoredIdLimit, usage.Count);
        Assert.False(usage.ContainsKey("id0"));
        usage = CommandBarUsage.Recording(usage, "id10", 1000);
        Assert.Equal(2, usage["id10"].Count);
        var decoded = CommandBarUsage.Decode(CommandBarUsage.Encode(usage));
        Assert.Equal(2, decoded["id10"].Count);
    }

    [Fact]
    public void SuggestionsLeadWithUsageThenCurated()
    {
        var usage = new Dictionary<string, CommandBarUse> { ["b"] = new(5, 10), ["c"] = new(9, 5), ["gone"] = new(50, 5) };
        Assert.Equal(["c", "b", "a"], CommandBarUsage.SuggestionIds(usage, ["a", "b", "c", "d"], ["a", "c", "d"], 3));
        Assert.Equal(["c", "b", "a", "d"], CommandBarUsage.CategoryIds(usage, ["a", "b", "c", "d"]));
    }

    [Fact]
    public void QueryHabitsNeverStoreTheText()
    {
        var key = CommandBarQueryHabits.NewKey();
        var prepared = CommandBarQueryHabits.Prepare("Chrome", key);
        Assert.Equal(6, prepared.Keys.Count);
        var store = CommandBarQueryHabits.Recording(new(), prepared, "app.chrome", 100);
        var raw = CommandBarQueryHabits.Encode(store);
        Assert.DoesNotContain("chrome\"", raw.Replace("app.chrome", string.Empty), StringComparison.OrdinalIgnoreCase);
        var boost = CommandBarQueryHabits.Boost("app.chrome", CommandBarQueryHabits.Prepare("chr", key), store, 100);
        Assert.True(boost > 0);
        Assert.Equal(0, CommandBarQueryHabits.Boost("app.chrome", CommandBarQueryHabits.Prepare("chr", CommandBarQueryHabits.NewKey()), store, 100));
        Assert.Empty(CommandBarQueryHabits.Removing("app.chrome", store));
        Assert.Empty(CommandBarQueryHabits.Prepare("x", null).Keys);
    }

    [Fact]
    public void QueryHabitsKeepFourResultsPerPrefix()
    {
        var key = CommandBarQueryHabits.NewKey();
        var prepared = CommandBarQueryHabits.Prepare("a", key);
        var store = new Dictionary<string, Dictionary<string, CommandBarUse>>();
        for (var i = 0; i < 6; i++) store = CommandBarQueryHabits.Recording(store, prepared, "r" + i, i);
        Assert.Equal(4, store.Single().Value.Count);
        Assert.False(store.Single().Value.ContainsKey("r0"));
    }
}

public class CommandBarRowShortcutsTests
{
    private static readonly Shortcut CtrlAltN = Shortcut.Parse("Ctrl+Alt+N");

    [Fact]
    public void RoundTripsShortcuts()
    {
        var map = CommandBarRowShortcuts.Setting(CtrlAltN, "app.notes", new Dictionary<string, Shortcut>());
        var decoded = CommandBarRowShortcuts.Decode(CommandBarRowShortcuts.Encode(map));
        Assert.Equal(CtrlAltN, decoded["app.notes"]);
        Assert.Empty(CommandBarRowShortcuts.Decode("{bad"));
    }

    [Fact]
    public void AShortcutMovesToTheLastRow()
    {
        var map = CommandBarRowShortcuts.Setting(CtrlAltN, "a", new Dictionary<string, Shortcut>());
        map = CommandBarRowShortcuts.Setting(CtrlAltN, "b", map);
        Assert.False(map.ContainsKey("a"));
        Assert.Equal("b", CommandBarRowShortcuts.KeyFor(CtrlAltN, map));
        Assert.Empty(CommandBarRowShortcuts.Setting(null, "b", map));
    }

    [Fact]
    public void ReportsIssues()
    {
        var map = CommandBarRowShortcuts.Setting(CtrlAltN, "a", new Dictionary<string, Shortcut>());
        Assert.Equal(CommandBarRowShortcuts.Issue.Invalid, CommandBarRowShortcuts.AssignmentIssue(Shortcut.Parse("N"), "b", map)?.Issue);
        Assert.Equal("a", CommandBarRowShortcuts.AssignmentIssue(CtrlAltN, "b", map)?.Owner);
        Assert.Null(CommandBarRowShortcuts.AssignmentIssue(CtrlAltN, "a", map));
        Assert.True(CommandBarRowShortcuts.IsUsable(Shortcut.Parse("F9")));
        var fullMap = new Dictionary<string, Shortcut>();
        for (var i = 0; i < CommandBarRowShortcuts.Limit; i++) fullMap["k" + i] = new Shortcut(Modifiers.Ctrl | Modifiers.Shift, 0x41 + i);
        Assert.False(CommandBarRowShortcuts.HasRoom("new", fullMap));
        Assert.Equal(CommandBarRowShortcuts.Issue.Full, CommandBarRowShortcuts.AssignmentIssue(CtrlAltN, "new", fullMap)?.Issue);
        Assert.True(CommandBarRowShortcuts.HasRoom("k1", fullMap));
    }
}
