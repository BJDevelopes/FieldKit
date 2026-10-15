// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Agents;

public enum AgentProvider { Claude, Codex, OpenCode, Copilot }

public static class AgentProviders
{
    public static readonly AgentProvider[] All = [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode, AgentProvider.Copilot];

    public static string DisplayName(this AgentProvider p) => p switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.OpenCode => "OpenCode",
        AgentProvider.Copilot => "GitHub Copilot",
        _ => p.ToString(),
    };

    /// <summary>Settings key suffix (notchAgents.claude, ...).</summary>
    public static string Key(this AgentProvider p) => p switch
    {
        AgentProvider.Claude => "claude",
        AgentProvider.Codex => "codex",
        AgentProvider.OpenCode => "opencode",
        AgentProvider.Copilot => "copilot",
        _ => p.ToString().ToLowerInvariant(),
    };

    public static bool ReportsLimits(this AgentProvider p) => p != AgentProvider.Copilot;

    /// <summary>Upstream tint (sRGB 0..1).</summary>
    public static (double R, double G, double B) Tint(this AgentProvider p) => p switch
    {
        AgentProvider.Claude => (0.85, 0.47, 0.34),
        AgentProvider.Codex => (0.49, 0.60, 1.0),
        AgentProvider.OpenCode => (0.06, 0.73, 0.51),
        _ => (0.30, 0.78, 0.68),
    };
}

/// <summary>Token counts. <see cref="Input"/> excludes cache; <see cref="Output"/> already includes reasoning.</summary>
public readonly record struct AgentTokens(long Input = 0, long CacheWrite = 0, long CacheRead = 0, long Output = 0, long Reasoning = 0)
{
    public long Total => Input + CacheWrite + CacheRead + Output;
    public long Prompt => Input + CacheWrite + CacheRead;
    public double? CacheHitRate => Prompt > 0 ? (double)CacheRead / Prompt : null;

    public static AgentTokens operator +(AgentTokens a, AgentTokens b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output, a.Reasoning + b.Reasoning);

    public AgentTokens Max(AgentTokens o) =>
        new(Math.Max(Input, o.Input), Math.Max(CacheWrite, o.CacheWrite), Math.Max(CacheRead, o.CacheRead),
            Math.Max(Output, o.Output), Math.Max(Reasoning, o.Reasoning));

    /// <summary>Per-field growth since <paramref name="previous"/>, never negative.</summary>
    public AgentTokens Minus(AgentTokens previous) =>
        new(Math.Max(0, Input - previous.Input), Math.Max(0, CacheWrite - previous.CacheWrite), Math.Max(0, CacheRead - previous.CacheRead),
            Math.Max(0, Output - previous.Output), Math.Max(0, Reasoning - previous.Reasoning));
}

/// <summary>One response (or aggregate) read from a log.</summary>
public sealed record AgentUsageRecord(
    AgentProvider Provider,
    DateTimeOffset Date,
    string Model,
    string Project,
    string Session,
    AgentTokens Tokens,
    double? Cost,
    double Savings,
    int Requests = 1,
    bool ReportedCost = false);

public enum AgentLimitKind { Session, Weekly, Other }

/// <summary>A plan limit window ("Session" 5 h, "Week", ...).</summary>
public sealed record AgentLimitWindow(
    string Id,
    AgentLimitKind Kind,
    int? Minutes,
    string? Scope,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    DateTimeOffset? EstimatedResetsAt = null)
{
    public double UsedFraction => Math.Clamp(UsedPercent / 100, 0, 1);
    public double RemainingFraction => 1 - UsedFraction;

    public static AgentLimitKind KindFor(int? minutes)
    {
        if (minutes is not > 0) return AgentLimitKind.Other;
        if (minutes <= 12 * 60) return AgentLimitKind.Session;
        if (minutes >= 6 * 1440 && minutes <= 8 * 1440) return AgentLimitKind.Weekly;
        return AgentLimitKind.Other;
    }

    /// <summary>A window past its reset shows 0% used and an unknown reset.</summary>
    public AgentLimitWindow Current(DateTimeOffset now) =>
        ResetsAt is { } r && r <= now ? this with { UsedPercent = 0, ResetsAt = null, EstimatedResetsAt = null } : this;

    /// <summary>"Session", "Week", "Week · Opus", or a duration for other windows.</summary>
    public string Label => Kind switch
    {
        AgentLimitKind.Session => "Session",
        AgentLimitKind.Weekly => Scope is { Length: > 0 } s ? $"Week · {s}" : "Week",
        _ => Minutes is { } m ? AgentFormat.Duration(m * 60) : "Limit",
    };
}

public enum AgentLimitSource { ClaudeApp, SessionLog, Account }

public sealed record AgentLimits(AgentProvider Provider, IReadOnlyList<AgentLimitWindow> Windows, DateTimeOffset ObservedAt, AgentLimitSource Source);

/// <summary>A turn an agent is working on.</summary>
public sealed record AgentLiveSession(
    string Id,
    AgentProvider Provider,
    DateTimeOffset Started,
    DateTimeOffset LastActivity,
    string Model,
    string Project,
    AgentTokens Tokens,
    double Cost);

public abstract record AgentUsageEvent
{
    public sealed record Finished(AgentProvider Provider, TimeSpan Duration, double Cost, long Tokens, string Project) : AgentUsageEvent;
    public sealed record LimitWarning(AgentProvider Provider, AgentLimitWindow Window) : AgentUsageEvent;
    public sealed record LimitReset(AgentProvider Provider, AgentLimitWindow Window) : AgentUsageEvent;
    public sealed record BudgetReached(double Spent, double Budget) : AgentUsageEvent;
}

/// <summary>What one log line says, reduced to the few facts the island keeps.</summary>
public abstract record AgentLogEntry
{
    public sealed record Usage(string Key, AgentUsageRecord Record, AgentBillable Billable) : AgentLogEntry;
    public sealed record UsageModel(string Key, string Model) : AgentLogEntry;
    public sealed record Limits(AgentLimits Reading) : AgentLogEntry;
    public sealed record Plan(string Name, DateTimeOffset ObservedAt) : AgentLogEntry;
    public sealed record TurnBegan(DateTimeOffset Date) : AgentLogEntry;
    public sealed record TurnContext(string Model, string Project) : AgentLogEntry;
    public sealed record TurnActive(DateTimeOffset? Date) : AgentLogEntry;
    public sealed record TurnSettled(DateTimeOffset Date) : AgentLogEntry;
    public sealed record TurnEnded(DateTimeOffset? Date, bool Completed, TimeSpan? Duration) : AgentLogEntry;
    public sealed record Reset(DateTimeOffset Date) : AgentLogEntry;
}

/// <summary>Per-session turn tracking for OpenCode databases.</summary>
public sealed class OpenCodeSessionState
{
    public string Project = string.Empty;
    public string Model = string.Empty;
    public bool TurnOpen;
    public DateTimeOffset? TurnStarted;
    public string ActiveUserMessageId = string.Empty;
    public DateTimeOffset? LastActivity;
    public DateTimeOffset? SettledAt;
    public string WritingReplyId = string.Empty;
    public DateTimeOffset? ActivePromptDate;
    public readonly LinkedHashSet SeenUserMessageIds = new();
    public readonly LinkedHashSet CompletedAssistantMessageIds = new();
    public readonly LinkedHashSet CompletedUserMessageIds = new();
}

/// <summary>A small insertion-ordered set that forgets its oldest entries past 100.</summary>
public sealed class LinkedHashSet
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool Contains(string id) => _set.Contains(id);

    public void Remember(string id)
    {
        if (!_set.Add(id)) return;
        _order.Enqueue(id);
        while (_order.Count > 100) _set.Remove(_order.Dequeue());
    }
}

/// <summary>Per-file context carried from line to line.</summary>
public sealed class AgentLogState
{
    public string Session = string.Empty;
    public string Project = string.Empty;
    public string Model = string.Empty;
    public bool TurnOpen;
    public bool SawUsageRecords;
    public AgentTokens? LastTotal;
    public bool Fast;
    public readonly Dictionary<string, AgentTokens> CopilotTotals = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> CopilotRequests = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CopilotRequestModels = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> CopilotReportedRequests = new(StringComparer.Ordinal);
    public string? CopilotTurnId;
    public bool CopilotFinalResponse;
    public string ParentSession = string.Empty;
    public readonly Dictionary<string, OpenCodeSessionState> OpenCodeSessions = new(StringComparer.Ordinal);
    public readonly HashSet<string> RunningCommands = new(StringComparer.Ordinal);
}
