// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.CompilerServices;
using Fieldkit.Features.Island.Agents;

namespace Fieldkit.Tests.Island;

internal static class Fixtures
{
    public static string Path(string name, [CallerFilePath] string here = "") =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public static string[] Lines(string name) => File.ReadAllLines(Path(name));
}

public class AgentUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static (AgentUsageStore Store, List<AgentUsageEvent> Events) Run(AgentProvider provider, string fixture, DateTimeOffset now, bool reports = true)
    {
        var store = new AgentUsageStore { ReportsTransitions = reports };
        var state = new AgentLogState();
        var events = new List<AgentUsageEvent>();
        foreach (var line in Fixtures.Lines(fixture))
            events.AddRange(store.Apply(AgentLogParser.Parse(provider, line, state, now), fixture, provider, true, null, now, now));
        return (store, events);
    }

    [Fact]
    public void ClaudeMergesStreamedChunksAndPricesAtListRates()
    {
        var (store, events) = Run(AgentProvider.Claude, "claude-session.jsonl", T0.AddSeconds(90));
        Assert.Equal(2, store.Records.Count); // msg_1 merged, msg_2, <synthetic> skipped
        var first = store.Records[0];
        Assert.Equal(new AgentTokens(10, 1000, 5000, 200), first.Tokens);
        Assert.Equal("fieldkit", first.Project);
        Assert.Equal("s-claude", first.Session);
        // 10×3 + 1000×3.75 + 5000×0.3 + 200×15 per million
        Assert.Equal(0.00828, first.Cost!.Value, 6);
        // 20×3 + 6000×0.3 + 300×15 per million + one web search at $0.01
        Assert.Equal(0.01636, store.Records[1].Cost!.Value, 6);

        var finished = Assert.Single(events.OfType<AgentUsageEvent.Finished>());
        Assert.Equal(AgentProvider.Claude, finished.Provider);
        Assert.Equal(TimeSpan.FromSeconds(60), finished.Duration);
        Assert.Equal(12530, finished.Tokens);
        Assert.Equal(0.02464, finished.Cost, 6);
        Assert.Equal("fieldkit", finished.Project);
        Assert.Empty(store.Live);
    }

    [Fact]
    public void HistoryNeverReplaysAsNews()
    {
        var (_, quiet) = Run(AgentProvider.Claude, "claude-session.jsonl", T0.AddSeconds(90), reports: false);
        Assert.Empty(quiet.OfType<AgentUsageEvent.Finished>());
        var (_, late) = Run(AgentProvider.Claude, "claude-session.jsonl", T0.AddMinutes(30));
        Assert.Empty(late.OfType<AgentUsageEvent.Finished>());
    }

    [Fact]
    public void ClaudeInterruptionEndsTurnWithoutNotice()
    {
        var (store, events) = Run(AgentProvider.Claude, "claude-interrupted.jsonl", T0.AddHours(1).AddSeconds(10));
        Assert.Empty(events);
        Assert.Empty(store.Live);
    }

    [Fact]
    public void ClaudeTurnStaysLiveUntilItEnds()
    {
        var store = new AgentUsageStore { ReportsTransitions = true };
        var state = new AgentLogState();
        var lines = Fixtures.Lines("claude-session.jsonl");
        foreach (var line in lines.Take(4))
            store.Apply(AgentLogParser.ParseClaude(line, state, T0), "f", AgentProvider.Claude, true, null, T0, T0.AddSeconds(20));
        var live = Assert.Single(store.Live);
        Assert.Equal(T0, live.Started);
        Assert.Equal(6210, live.Tokens.Total);
        Assert.Equal("claude-sonnet-4-5-20250929", live.Model);
        Assert.Empty(state.RunningCommands); // the tool result completed toolu_1
    }

    [Fact]
    public void CodexTotalsBecomeDeltasAndLimitsComeFromTheMainBucket()
    {
        var (store, events) = Run(AgentProvider.Codex, "codex-session.jsonl", T0.AddSeconds(100));
        Assert.Equal(2, store.Records.Count);
        Assert.Equal(new AgentTokens(600, 0, 400, 200, 50), store.Records[0].Tokens);
        Assert.Equal(new AgentTokens(300, 0, 200, 100), store.Records[1].Tokens);
        Assert.Equal(0.0042, store.Records.Sum(r => r.Cost!.Value), 6);
        Assert.Equal("proj", store.Records[0].Project);

        var limits = store.Limits[AgentProvider.Codex];
        Assert.Equal(2, limits.Windows.Count);
        Assert.Equal(AgentLimitKind.Session, limits.Windows[0].Kind);
        Assert.Equal(12.5, limits.Windows[0].UsedPercent);
        Assert.Equal(T0.AddHours(1), limits.Windows[0].ResetsAt);
        Assert.Equal(AgentLimitKind.Weekly, limits.Windows[1].Kind);
        Assert.Equal(T0.AddSeconds(30).AddDays(1), limits.Windows[1].ResetsAt);
        Assert.Equal("plus", store.CodexPlan);

        var finished = Assert.Single(events.OfType<AgentUsageEvent.Finished>());
        Assert.Equal(TimeSpan.FromSeconds(90), finished.Duration);
    }

    [Fact]
    public void CodexUsageRecordsWinOverRunningTotals()
    {
        var (store, _) = Run(AgentProvider.Codex, "codex-records.jsonl", T0.AddMinutes(1));
        var record = Assert.Single(store.Records);
        Assert.Equal(new AgentTokens(1000, 0, 1000, 100), record.Tokens);
        // gpt-5.5 at the fast tier (priority, ×2.5): (1000×5 + 1000×0.5 + 100×30) × 2.5 per million
        Assert.Equal(0.02125, record.Cost!.Value, 6);
    }

    [Fact]
    public void CopilotCountsActivityAndShutdownTotals()
    {
        var (store, events) = Run(AgentProvider.Copilot, "copilot-events.jsonl", T0.AddMinutes(1));
        var finished = Assert.Single(events.OfType<AgentUsageEvent.Finished>());
        Assert.Equal(TimeSpan.FromSeconds(30), finished.Duration);
        Assert.Equal(0, finished.Tokens); // Copilot usage never counts toward the live turn

        var activity = store.Records.Where(r => r.Tokens.Total == 0).ToList();
        Assert.Equal(3, activity.Count);
        Assert.Contains(activity, r => r.Model == "gpt-5");

        var sonnet = Assert.Single(store.Records, r => r.Model == "claude-sonnet-4.5" && r.Tokens.Total > 0);
        Assert.Equal(4200, sonnet.Tokens.Total);
        Assert.Equal(0, sonnet.Requests);
        Assert.Equal(0.0069, sonnet.Cost!.Value, 6);
        Assert.Equal("copilot-proj", sonnet.Project);
    }

    [Fact]
    public void OpenCodeTurnsEndOnAFinalFinishAndUnknownModelsKeepTheirCost()
    {
        var (store, events) = Run(AgentProvider.OpenCode, "opencode-rows.jsonl", T0.AddMinutes(2));
        var finished = Assert.Single(events.OfType<AgentUsageEvent.Finished>());
        Assert.Equal(TimeSpan.FromSeconds(65), finished.Duration);
        Assert.Equal("oc", finished.Project);
        var local = Assert.Single(store.Records, r => r.Model == "some-local-model");
        Assert.Equal(0.5, local.Cost);
        Assert.True(local.ReportedCost);
        var priced = store.Records.First(r => r.Model == "claude-sonnet-4-5");
        Assert.False(priced.ReportedCost);
        Assert.Equal(new AgentTokens(100, 0, 2000, 60, 10), priced.Tokens);
    }

    [Theory]
    [InlineData("claude-opus-4-5-20251101", "claude-opus-4-5")]
    [InlineData("us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4-5")]
    [InlineData("claude-sonnet-4-5[1m]", "claude-sonnet-4-5")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("openai/gpt-5.1", "gpt-5.1")]
    public void PricesMatchTheLongestFamily(string model, string family)
    {
        var expected = AgentPriceList.Bundled.Claude.Concat(AgentPriceList.Bundled.Codex).First(m => m.Id == family).Price;
        Assert.Equal(expected, AgentPricing.PriceFor(model));
    }

    [Theory]
    [InlineData("claude-opus-4")]      // matches claude-opus-4 family exactly
    [InlineData("gpt-5-2025-08-07")]  // dated snapshot of gpt-5
    public void SnapshotsAndExactIdsArePriced(string model) => Assert.NotNull(AgentPricing.PriceFor(model));

    [Theory]
    [InlineData("gpt-5-turbo-max")]    // unknown variant word stays priced as gpt-5? no: "-turbo" is not a sibling
    public void UnknownSuffixWordsKeepTheFamily(string model) => Assert.NotNull(AgentPricing.PriceFor(model));

    [Theory]
    [InlineData("gpt-5.4-mini-2026")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("claude-opus-9-9")]
    public void UnknownSiblingsAndOtherVendorsHaveNoPrice(string model) => Assert.Null(AgentPricing.PriceFor(model + "-xmini"));

    [Fact]
    public void LongContextPremiumAppliesAbovePromptThreshold()
    {
        var billable = new AgentBillable { Tokens = new AgentTokens(250_000, 0, 0, 1000) };
        var (cost, _) = AgentPricing.Cost(billable, "claude-sonnet-4-5");
        Assert.Equal((250_000 * 3 * 2 + 1000 * 15 * 1.5) / 1e6, cost!.Value, 9);
        var aggregate = billable with { IsAggregate = true };
        Assert.Equal((250_000 * 3 + 1000 * 15) / 1e6, AgentPricing.Cost(aggregate, "claude-sonnet-4-5").Cost!.Value, 9);
    }

    [Theory]
    [InlineData("claude-opus-4-5-20251101", "Opus 4.5")]
    [InlineData("claude-sonnet-4-5", "Sonnet 4.5")]
    [InlineData("claude-3-5-haiku-latest", "Haiku 3.5")]
    [InlineData("gpt-5.5", "GPT-5.5")]
    [InlineData("gpt-6-astra", "GPT-6 Astra")]
    [InlineData("gpt-5-2025-08-07", "GPT-5")]
    [InlineData("o3", "o3")]
    public void DisplayNames(string model, string name) => Assert.Equal(name, AgentPricing.DisplayName(model));

    [Fact]
    public void PlansAreRecognised()
    {
        Assert.Equal("Max 20×", AgentPricing.ClaudePlan("claude_max", "default_claude_max_20x")!.Name);
        Assert.Equal("Pro", AgentPricing.ClaudePlan("claude_pro", null)!.Name);
        Assert.Equal("Plus", AgentPricing.CodexPlan("plus")!.Name);
        Assert.Equal("Weird", AgentPricing.CodexPlan("weird")!.Name);
    }

    [Fact]
    public void BundledPriceListIsValid()
    {
        var list = AgentPriceList.Bundled;
        Assert.NotEmpty(list.Claude);
        Assert.NotEmpty(list.Codex);
        Assert.Equal(0.01, list.WebSearch);
        Assert.Null(AgentPriceList.Decode("{\"schema\":2}"));
    }

    [Theory]
    [InlineData(3.4, "$3.40")]
    [InlineData(0, "$0.00")]
    [InlineData(1234, "$1,234")]
    [InlineData(12500, "$12K")]
    public void CostFormat(double v, string text) => Assert.Equal(text, AgentFormat.Cost(v));

    [Theory]
    [InlineData(812, "812")]
    [InlineData(48_500, "48K")]
    [InlineData(4_250, "4.2K")]
    [InlineData(1_250_000, "1.2M")]
    [InlineData(3_400_000_000, "3.4B")]
    public void TokenFormat(long n, string text) => Assert.Equal(text, AgentFormat.Tokens(n));

    [Theory]
    [InlineData(252, "4m 12s")]
    [InlineData(3900, "1h 5m")]
    [InlineData(183600, "2d 3h")]
    public void DurationFormat(double s, string text) => Assert.Equal(text, AgentFormat.Duration(s));

    [Theory]
    [InlineData("/home/u/proj/", "proj")]
    [InlineData("C:\\dev\\fieldkit", "fieldkit")]
    [InlineData("C:\\dev\\fieldkit\\.claude\\worktrees\\agent-1", "fieldkit")]
    [InlineData("/x/app/.claude/worktrees/w", "app")]
    public void ProjectNames(string path, string name) => Assert.Equal(name, AgentLogParser.ProjectName(path));

    [Fact]
    public void ClaudeBlockFollowsFiveHourWindows()
    {
        var records = new[] { 0, 60, 301, 400 }.Select(m => new AgentUsageRecord(AgentProvider.Claude, T0.AddMinutes(m), "m", "", "", new AgentTokens(1), 1, 0)).ToList();
        var block = AgentSummary.CurrentBlock(records, T0.AddMinutes(410));
        Assert.NotNull(block);
        Assert.Equal(T0.AddMinutes(301), block!.Start);
        Assert.Equal(2, block.Totals.Requests);
        Assert.Null(AgentSummary.CurrentBlock(records, T0.AddMinutes(301 + 301)));
    }

    [Fact]
    public void SummarySortsSharesByCostWhenFullyPriced()
    {
        var now = DateTimeOffset.Now;
        var records = new List<AgentUsageRecord>
        {
            new(AgentProvider.Claude, now, "claude-opus-4-5", "a", "s", new AgentTokens(10), 1.0, 0),
            new(AgentProvider.Claude, now, "claude-sonnet-4-5", "b", "s", new AgentTokens(1000), 0.5, 0),
            new(AgentProvider.Codex, now.AddDays(-3), "gpt-5", "b", "s", new AgentTokens(5), 2.0, 0),
        };
        var snap = AgentSummary.Build(records, new Dictionary<AgentProvider, AgentLimits>(), [], new Dictionary<AgentProvider, AgentPlan>(), now);
        Assert.Equal(1.5, snap.Totals[AgentPeriod.Today].Cost, 6);
        Assert.Equal(3.5, snap.Totals[AgentPeriod.Week].Cost, 6);
        Assert.Equal("Opus 4.5", snap.Models[AgentPeriod.Today][0].Name);
        Assert.Equal("b", snap.Projects[AgentPeriod.Week][0].Name);
        Assert.Equal(91, snap.Days.Count);
        Assert.Contains(AgentProvider.Codex, snap.Seen);
    }

    [Fact]
    public void LimitWarningsFireWhenCrossingTheThreshold()
    {
        var before = new AgentLimitWindow("w", AgentLimitKind.Session, 300, null, 70, T0);
        var after = before with { UsedPercent = 85 };
        Assert.True(AgentSummary.CrossedThreshold(before, after, 80));
        Assert.False(AgentSummary.CrossedThreshold(after, after with { UsedPercent = 90 }, 80));
        Assert.True(AgentSummary.CrossedThreshold(after, after with { ResetsAt = T0.AddHours(5) }, 80));
    }

    [Fact]
    public void ClaudeAppSamplesBecomeLimitWindows()
    {
        var samples = ClaudeAccount.ParseSamples(Fixtures.Text("claude-plan-usage.json"));
        Assert.Equal(4, samples.Count);
        var limits = ClaudeAccount.Limits(samples, "o1", T0.AddMinutes(-30));
        Assert.NotNull(limits);
        var session = Assert.Single(limits!.Windows, w => w.Kind == AgentLimitKind.Session);
        Assert.Equal(30, session.UsedPercent);
        Assert.Equal(T0.AddHours(-2).AddHours(5), session.ResetsAt);
        var weekly = Assert.Single(limits.Windows, w => w.Id == "claude.weekly");
        Assert.Equal(10, weekly.UsedPercent);
        Assert.NotNull(weekly.ResetsAt); // a drop from 60 to 5 marks the renewal
        Assert.Contains(limits.Windows, w => w.Scope == "Opus");
    }

    [Fact]
    public void ReaderReadsOnlyCompleteLinesAndStartsOverWhenRewritten()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fieldkit-agents-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"a\":1}\n{\"b\":");
            var cursor = new AgentLogCursor(path, AgentProvider.Claude);
            Assert.Equal(["{\"a\":1}"], AgentLogReader.ReadAppended(cursor));
            File.AppendAllText(path, "2}\n");
            Assert.Equal(["{\"b\":2}"], AgentLogReader.ReadAppended(cursor));
            Assert.Empty(AgentLogReader.ReadAppended(cursor));
            File.WriteAllText(path, "{\"c\":3}\n{\"d\":4}\n{\"e\":5}\n");
            Assert.Equal(3, AgentLogReader.ReadAppended(cursor).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClaudeSubagentLogsPointAtTheirParent()
    {
        Assert.Equal("C:\\p\\abc.jsonl", AgentLogReader.ClaudeParent("C:\\p\\abc\\subagents\\agent-1.jsonl"));
        Assert.Null(AgentLogReader.ClaudeParent("C:\\p\\abc.jsonl"));
        Assert.False(new AgentLogCursor("C:\\p\\abc\\subagents\\a.jsonl", AgentProvider.Claude).TracksTurns);
        Assert.False(new AgentLogCursor("C:\\c\\rollout_side.jsonl", AgentProvider.Codex).TracksTurns);
    }
}
