// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Agents;

public enum AgentPeriod { Today, Week, Month }

/// <summary>Sums of a set of records.</summary>
public sealed record AgentTotals(AgentTokens Tokens, double Cost, double Savings, int Requests, int Unpriced)
{
    public static AgentTotals Zero { get; } = new(default, 0, 0, 0, 0);
    public bool FullyPriced => Unpriced == 0;

    public static AgentTotals Of(IEnumerable<AgentUsageRecord> records)
    {
        AgentTokens tokens = default;
        double cost = 0, savings = 0;
        int requests = 0, unpriced = 0;
        foreach (var r in records)
        {
            tokens += r.Tokens;
            if (r.Cost is { } c) cost += c;
            else unpriced++;
            savings += r.Savings;
            requests += r.Requests;
        }
        return new AgentTotals(tokens, cost, savings, requests, unpriced);
    }
}

/// <summary>A row of the Models / Projects cards.</summary>
public sealed record AgentShare(string Id, string Name, AgentProvider Provider, AgentTotals Totals);

/// <summary>One day of the activity heatmap / trend.</summary>
public sealed record AgentBucket(DateTime Start, IReadOnlyDictionary<AgentProvider, AgentTotals> ByProvider)
{
    public double Cost => ByProvider.Values.Sum(t => t.Cost);
    public long Tokens => ByProvider.Values.Sum(t => t.Tokens.Total);
}

/// <summary>Claude Code's local 5-hour block, when the plan limits are not available.</summary>
public sealed record AgentBlock(DateTimeOffset Start, DateTimeOffset End, AgentTotals Totals);

/// <summary>What the Agents page and strip show, computed off the UI thread.</summary>
public sealed record AgentUsageSnapshot
{
    public required DateTimeOffset Now { get; init; }
    public required IReadOnlySet<AgentProvider> Seen { get; init; }
    public required IReadOnlyDictionary<AgentPeriod, AgentTotals> Totals { get; init; }
    public required IReadOnlyDictionary<AgentPeriod, IReadOnlyDictionary<AgentProvider, AgentTotals>> ByProvider { get; init; }
    public required IReadOnlyDictionary<AgentPeriod, IReadOnlyList<AgentShare>> Models { get; init; }
    public required IReadOnlyDictionary<AgentPeriod, IReadOnlyList<AgentShare>> Projects { get; init; }
    public required IReadOnlyList<AgentBucket> Days { get; init; }
    public required IReadOnlyList<AgentBucket> Hours { get; init; }
    public required IReadOnlyDictionary<AgentProvider, AgentLimits> Limits { get; init; }
    public required IReadOnlyDictionary<AgentProvider, AgentPlan> Plans { get; init; }
    public required IReadOnlyList<AgentLiveSession> Live { get; init; }
    public required IReadOnlyDictionary<AgentProvider, DateTimeOffset> LastActivity { get; init; }
    public AgentBlock? ClaudeBlock { get; init; }

    public static AgentUsageSnapshot Empty(DateTimeOffset now) => AgentSummary.Build([], new Dictionary<AgentProvider, AgentLimits>(), [],
        new Dictionary<AgentProvider, AgentPlan>(), now);
}

/// <summary>Periods, buckets, shares and the 5-hour block (port of upstream <c>AgentUsageSummary</c>).</summary>
public static class AgentSummary
{
    public static int Days(AgentPeriod p) => p switch { AgentPeriod.Today => 1, AgentPeriod.Week => 7, _ => 30 };

    public static string Title(AgentPeriod p) => p switch { AgentPeriod.Today => "Today", AgentPeriod.Week => "7 days", _ => "30 days" };

    public static AgentUsageSnapshot Build(IReadOnlyList<AgentUsageRecord> records, IReadOnlyDictionary<AgentProvider, AgentLimits> limits,
        IReadOnlyCollection<AgentLiveSession> live, IReadOnlyDictionary<AgentProvider, AgentPlan> plans, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var totals = new Dictionary<AgentPeriod, AgentTotals>();
        var byProvider = new Dictionary<AgentPeriod, IReadOnlyDictionary<AgentProvider, AgentTotals>>();
        var models = new Dictionary<AgentPeriod, IReadOnlyList<AgentShare>>();
        var projects = new Dictionary<AgentPeriod, IReadOnlyList<AgentShare>>();
        foreach (var period in new[] { AgentPeriod.Today, AgentPeriod.Week, AgentPeriod.Month })
        {
            var start = today.AddDays(-(Days(period) - 1));
            var inPeriod = records.Where(r => r.Date.ToLocalTime().DateTime >= start).ToList();
            var total = AgentTotals.Of(inPeriod);
            totals[period] = total;
            byProvider[period] = inPeriod.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => AgentTotals.Of(g));
            models[period] = Shares(inPeriod.GroupBy(r => (r.Provider, Name: AgentPricing.DisplayName(r.Model) is { Length: > 0 } n ? n : "?")),
                g => $"{g.Key.Provider.Key()}:{g.Key.Name}", g => g.Key.Name, g => g.Key.Provider, total.FullyPriced);
            projects[period] = Shares(inPeriod.Where(r => r.Project.Length > 0).GroupBy(r => (Provider: r.Provider, r.Project)),
                g => $"{g.Key.Provider.Key()}:{g.Key.Project}", g => g.Key.Project, g => g.Key.Provider, total.FullyPriced, mergeAcrossProviders: true);
        }

        var days = new List<AgentBucket>();
        var first = today.AddDays(-90);
        var byDay = records.GroupBy(r => r.Date.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = first; d <= today; d = d.AddDays(1))
            days.Add(new AgentBucket(d, byDay.TryGetValue(d, out var list) ? list.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => AgentTotals.Of(g)) : new Dictionary<AgentProvider, AgentTotals>()));
        var hours = new List<AgentBucket>();
        var todays = byDay.TryGetValue(today, out var t) ? t : [];
        for (var h = 0; h < 24; h++)
        {
            var hourStart = today.AddHours(h);
            var inHour = todays.Where(r => r.Date.ToLocalTime().Hour == h).ToList();
            hours.Add(new AgentBucket(hourStart, inHour.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => AgentTotals.Of(g))));
        }

        var lastActivity = records.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Max(r => r.Date));
        var seen = new HashSet<AgentProvider>(records.Select(r => r.Provider));
        seen.UnionWith(limits.Keys);
        seen.UnionWith(live.Select(l => l.Provider));

        return new AgentUsageSnapshot
        {
            Now = now,
            Seen = seen,
            Totals = totals,
            ByProvider = byProvider,
            Models = models,
            Projects = projects,
            Days = days,
            Hours = hours,
            Limits = limits,
            Plans = plans,
            Live = live.OrderBy(l => l.Started).ToList(),
            LastActivity = lastActivity,
            ClaudeBlock = CurrentBlock(records.Where(r => r.Provider == AgentProvider.Claude && r.Date >= now.AddHours(-24)).ToList(), now),
        };
    }

    private static List<AgentShare> Shares<TKey>(IEnumerable<IGrouping<TKey, AgentUsageRecord>> groups, Func<IGrouping<TKey, AgentUsageRecord>, string> id,
        Func<IGrouping<TKey, AgentUsageRecord>, string> name, Func<IGrouping<TKey, AgentUsageRecord>, AgentProvider> provider, bool fullyPriced,
        bool mergeAcrossProviders = false)
    {
        var shares = groups.Select(g => new AgentShare(id(g), name(g), provider(g), AgentTotals.Of(g))).ToList();
        if (mergeAcrossProviders)
        {
            shares = shares.GroupBy(s => s.Name)
                .Select(g => g.Count() == 1 ? g.First() : new AgentShare(g.Key, g.Key, g.OrderByDescending(s => s.Totals.Tokens.Total).First().Provider,
                    new AgentTotals(g.Aggregate(default(AgentTokens), (a, s) => a + s.Totals.Tokens), g.Sum(s => s.Totals.Cost),
                        g.Sum(s => s.Totals.Savings), g.Sum(s => s.Totals.Requests), g.Sum(s => s.Totals.Unpriced))))
                .ToList();
        }
        return shares
            .OrderByDescending(s => fullyPriced ? s.Totals.Cost : s.Totals.Tokens.Total)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Claude's 5-hour blocks: a block starts at a record, lasts 5 h; the next record after it starts another.</summary>
    public static AgentBlock? CurrentBlock(IReadOnlyList<AgentUsageRecord> claude, DateTimeOffset now)
    {
        AgentBlock? block = null;
        var members = new List<AgentUsageRecord>();
        foreach (var r in claude.OrderBy(r => r.Date))
        {
            if (block is null || r.Date >= block.End)
            {
                members = [r];
                block = new AgentBlock(r.Date, r.Date.AddHours(5), AgentTotals.Of(members));
            }
            else
            {
                members.Add(r);
                block = block with { Totals = AgentTotals.Of(members) };
            }
        }
        return block is not null && block.End > now ? block : null;
    }

    /// <summary>
    /// The window a limit row/strip focuses on: "mostUsed" (default), "session" or "weekly".
    /// </summary>
    public static AgentLimitWindow? Focus(AgentLimits? limits, string focus, DateTimeOffset now)
    {
        if (limits is null || limits.Windows.Count == 0) return null;
        var windows = limits.Windows.Select(w => w.Current(now)).ToList();
        return focus switch
        {
            "session" => windows.FirstOrDefault(w => w.Kind == AgentLimitKind.Session) ?? windows.MaxBy(w => w.UsedPercent),
            "weekly" => windows.FirstOrDefault(w => w.Kind == AgentLimitKind.Weekly && w.Scope is null) ?? windows.MaxBy(w => w.UsedPercent),
            _ => windows.MaxBy(w => w.UsedPercent),
        };
    }

    /// <summary>The (up to two) rows of a Limits card: the session window plus the most-used longer window.</summary>
    public static List<AgentLimitWindow> CardRows(AgentLimits limits, DateTimeOffset now)
    {
        var windows = limits.Windows.Select(w => w.Current(now)).ToList();
        var rows = new List<AgentLimitWindow>();
        if (windows.FirstOrDefault(w => w.Kind == AgentLimitKind.Session) is { } session) rows.Add(session);
        if (windows.Where(w => w.Kind != AgentLimitKind.Session).MaxBy(w => w.UsedPercent) is { } longer) rows.Add(longer);
        return rows;
    }

    /// <summary>Whether a limit crossed the warning threshold between two readings.</summary>
    public static bool CrossedThreshold(AgentLimitWindow? before, AgentLimitWindow after, double threshold)
    {
        if (after.UsedPercent < threshold) return false;
        if (before is null) return false;
        if (before.UsedPercent < threshold) return true;
        return before.ResetsAt is { } b && after.ResetsAt is { } a && Math.Abs((a - b).TotalSeconds) > 60;
    }

    /// <summary>The 7×13 heatmap's opacity for a day's value, by quartile of the active days.</summary>
    public static double HeatOpacity(double value, IReadOnlyList<double> activeValues)
    {
        if (value <= 0 || activeValues.Count == 0) return 0.07;
        var sorted = activeValues.OrderBy(v => v).ToList();
        double Q(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
        if (value <= Q(0.25)) return 0.28;
        if (value <= Q(0.5)) return 0.45;
        if (value <= Q(0.75)) return 0.65;
        return 0.9;
    }

    /// <summary>Consecutive active days ending today (or yesterday when today is still empty).</summary>
    public static int Streak(IReadOnlyList<AgentBucket> days)
    {
        var i = days.Count - 1;
        if (i >= 0 && days[i].Tokens == 0) i--;
        var n = 0;
        for (; i >= 0 && days[i].Tokens > 0; i--) n++;
        return n;
    }
}
