// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Fieldkit.Features.Island.Agents;

/// <summary>
/// Reduces one log line to the few facts the island keeps (port of upstream <c>AgentLogParser</c>).
/// Only usage counters, model names, times and folder names leave a line; prompts, replies and tool
/// output are never kept.
/// </summary>
public static class AgentLogParser
{
    /// <summary>OpenCode starts the next step at once; a settled step with nothing after it for this long stopped.</summary>
    public const double OpenCodeSettle = 30;
    /// <summary>A turn quiet this long is no longer shown as working.</summary>
    public const double IdleTurn = 600;

    private static readonly HashSet<string> CodexEvents =
        ["token_count", "task_started", "task_complete", "turn_aborted", "thread_settings_applied"];

    private static readonly HashSet<string> CopilotEvents =
    [
        "user.message", "assistant.turn_start", "assistant.message", "assistant.turn_end",
        "session.start", "session.context_changed", "session.model_change", "session.shutdown", "abort",
    ];

    public static List<AgentLogEntry> Parse(AgentProvider provider, string line, AgentLogState state, DateTimeOffset now) => provider switch
    {
        AgentProvider.Claude => ParseClaude(line, state, now),
        AgentProvider.Codex => ParseCodex(line, state, now),
        AgentProvider.OpenCode => ParseOpenCode(line, state, now),
        AgentProvider.Copilot => ParseCopilot(line, state, now),
        _ => [],
    };

    private static JsonDocument? Doc(string line)
    {
        try
        {
            var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    // ------------------------------------------------------------------ Claude Code

    public static List<AgentLogEntry> ParseClaude(string line, AgentLogState state, DateTimeOffset now)
    {
        if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) return ClaudeAssistant(line, state, now);
        if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) return [];

        if (line.Contains("[Request interrupted by user", StringComparison.Ordinal) || line.Contains("<local-command-std", StringComparison.Ordinal))
        {
            using var doc = Doc(line);
            if (doc is not null && Str(doc.RootElement, "type") == "user" && EndsTurn(doc.RootElement))
            {
                var open = state.TurnOpen;
                state.TurnOpen = false;
                return open ? [new AgentLogEntry.TurnEnded(null, false, null)] : [];
            }
        }
        if (line.Contains("\"toolEndsTurn\":true", StringComparison.Ordinal))
        {
            using var doc = Doc(line);
            if (doc is not null && Str(doc.RootElement, "type") == "user" && Bool(doc.RootElement, "toolEndsTurn") == true &&
                Bool(doc.RootElement, "isSidechain") != true)
            {
                var open = state.TurnOpen;
                state.TurnOpen = false;
                return open ? [new AgentLogEntry.TurnEnded(Timestamp(doc.RootElement, "timestamp") ?? now, true, null)] : [];
            }
        }
        if (state.TurnOpen)
        {
            if (state.RunningCommands.Count > 0)
            {
                using var doc = Doc(line);
                if (doc is not null && Str(doc.RootElement, "type") == "user" && Bool(doc.RootElement, "isMeta") != true &&
                    Bool(doc.RootElement, "isSidechain") != true)
                {
                    var results = new List<JsonElement>();
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                        msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        results.AddRange(content.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "tool_result"));
                    if (results.Count > 0)
                    {
                        foreach (var r in results)
                            if (Str(r, "tool_use_id") is { } id) state.RunningCommands.Remove(id);
                    }
                    else
                    {
                        state.RunningCommands.Clear();
                    }
                }
            }
            return [new AgentLogEntry.TurnActive(null)];
        }
        if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) return [];
        using (var doc = Doc(line))
        {
            if (doc is null) return [];
            var json = doc.RootElement;
            if (Str(json, "type") != "user" || Bool(json, "isMeta") == true || Bool(json, "isSidechain") == true) return [];
            Adopt(json, state);
            state.TurnOpen = true;
            state.RunningCommands.Clear();
            return [new AgentLogEntry.TurnBegan(Timestamp(json, "timestamp") ?? now)];
        }
    }

    private static List<AgentLogEntry> ClaudeAssistant(string line, AgentLogState state, DateTimeOffset now)
    {
        using var doc = Doc(line);
        if (doc is null) return [];
        var json = doc.RootElement;
        if (Str(json, "type") != "assistant" || !json.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return [];
        Adopt(json, state);
        var date = Timestamp(json, "timestamp") ?? now;
        var entries = new List<AgentLogEntry>();
        var model = Str(message, "model") ?? string.Empty;
        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && model.Length > 0 && !model.StartsWith('<'))
        {
            state.Model = model;
            var billable = new AgentBillable
            {
                Tokens = new AgentTokens(
                    Int(usage, "input_tokens"),
                    Int(usage, "cache_creation_input_tokens"),
                    Int(usage, "cache_read_input_tokens"),
                    Int(usage, "output_tokens"),
                    Int(Obj(usage, "output_tokens_details"), "thinking_tokens")),
                LongCacheWrite = Int(Obj(usage, "cache_creation"), "ephemeral_1h_input_tokens"),
                Fast = Str(usage, "speed") == "fast",
                Domestic = Str(usage, "inference_geo") == "us",
                WebSearches = Int(Obj(usage, "server_tool_use"), "web_search_requests"),
            };
            var id = Str(message, "id") ?? string.Empty;
            var request = Str(json, "requestId") ?? string.Empty;
            var key = id.Length == 0 && request.Length == 0
                ? $"claude:{state.Session}:{Unix(date)}"
                : $"claude:{id}:{request}";
            var (cost, savings) = AgentPricing.Cost(billable, model);
            entries.Add(new AgentLogEntry.Usage(key,
                new AgentUsageRecord(AgentProvider.Claude, date, model, state.Project, state.Session, billable.Tokens, cost, savings),
                billable));
        }
        if (Bool(json, "isSidechain") == true) return entries;
        switch (Str(message, "stop_reason"))
        {
            case "end_turn" or "stop_sequence" or "max_tokens" or "refusal":
                var failed = Bool(json, "isApiErrorMessage") == true || model.StartsWith('<');
                if (state.TurnOpen) entries.Add(new AgentLogEntry.TurnEnded(date, !failed, null));
                state.TurnOpen = false;
                break;
            default:
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_use" && Str(block, "name") == "Bash" &&
                            Str(block, "id") is { } bid)
                            state.RunningCommands.Add(bid);
                    }
                }
                entries.Insert(0, state.TurnOpen ? new AgentLogEntry.TurnActive(date) : new AgentLogEntry.TurnBegan(date));
                state.TurnOpen = true;
                break;
        }
        return entries;
    }

    private static bool EndsTurn(JsonElement json)
    {
        if (!json.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("content", out var content))
            return false;
        IEnumerable<string> texts = content.ValueKind switch
        {
            JsonValueKind.String => [content.GetString()!],
            JsonValueKind.Array => content.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text" && Str(b, "text") is not null)
                .Select(b => Str(b, "text")!),
            _ => [],
        };
        return texts.Any(t => t.StartsWith("[Request interrupted by user", StringComparison.Ordinal) ||
                              t.StartsWith("<local-command-std", StringComparison.Ordinal));
    }

    private static void Adopt(JsonElement json, AgentLogState state)
    {
        if (Str(json, "sessionId") is { Length: > 0 } session) state.Session = session;
        if (Str(json, "cwd") is { Length: > 0 } cwd) state.Project = ProjectName(cwd);
    }

    // ------------------------------------------------------------------ Codex

    public static List<AgentLogEntry> ParseCodex(string line, AgentLogState state, DateTimeOffset now)
    {
        // Cheap filter: only a handful of line types matter, and they say so near the start.
        var head = line.Length > 400 ? line[..400] : line;
        if (!(head.Contains("\"session_meta\"", StringComparison.Ordinal) || head.Contains("\"turn_context\"", StringComparison.Ordinal) ||
              head.Contains("\"token_usage_record\"", StringComparison.Ordinal) ||
              (head.Contains("\"event_msg\"", StringComparison.Ordinal) && CodexEvents.Any(e => head.Contains($"\"{e}\"", StringComparison.Ordinal)))))
            return [];
        using var doc = Doc(line);
        if (doc is null) return [];
        var json = doc.RootElement;
        if (!json.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return [];
        var date = Timestamp(json, "timestamp") ?? now;
        switch (Str(json, "type"))
        {
            case "session_meta":
                if (Str(payload, "id") is { Length: > 0 } id) state.Session = id;
                if (Str(payload, "cwd") is { Length: > 0 } cwd) state.Project = ProjectName(cwd);
                return [];
            case "turn_context":
                if (Str(payload, "model") is { Length: > 0 } model) state.Model = model;
                if (Str(payload, "cwd") is { Length: > 0 } cwd2) state.Project = ProjectName(cwd2);
                if (Str(payload, "service_tier") is { } tier) state.Fast = FastTier(tier);
                return [];
            case "token_usage_record":
                if (!payload.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return [];
                state.SawUsageRecords = true;
                if (Str(payload, "session_id") is { Length: > 0 } s && state.Session.Length == 0) state.Session = s;
                var response = Str(payload, "response_id") ?? string.Empty;
                var key = response.Length == 0 ? $"codex:{state.Session}:{Unix(date)}" : $"codex:{response}";
                return [CodexUsage(CodexTokens(u), key, date, state)];
            case "event_msg":
                return CodexEvent(payload, date, state);
            default:
                return [];
        }
    }

    private static List<AgentLogEntry> CodexEvent(JsonElement payload, DateTimeOffset date, AgentLogState state)
    {
        switch (Str(payload, "type"))
        {
            case "token_count":
            {
                var entries = new List<AgentLogEntry>();
                if (payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                {
                    if (IsMainBucket(limits) && CodexWindows(limits, date) is { Count: > 0 } windows)
                        entries.Add(new AgentLogEntry.Limits(new AgentLimits(AgentProvider.Codex, windows, date, AgentLimitSource.SessionLog)));
                    if (Str(limits, "plan_type") is { Length: > 0 } plan) entries.Add(new AgentLogEntry.Plan(plan, date));
                }
                if (!state.SawUsageRecords && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("total_token_usage", out var totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
                {
                    var total = CodexTokens(totalUsage);
                    if (total != state.LastTotal && total.Total > (state.LastTotal?.Total ?? 0))
                    {
                        var delta = info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object
                            ? CodexTokens(last)
                            : total.Minus(state.LastTotal ?? default);
                        entries.Add(CodexUsage(delta, $"codex:{state.Session}:total:{total.Total}", date, state));
                    }
                    state.LastTotal = total;
                }
                return entries;
            }
            case "thread_settings_applied":
                if (payload.TryGetProperty("thread_settings", out var ts) && ts.ValueKind == JsonValueKind.Object && Str(ts, "service_tier") is { } tier)
                    state.Fast = FastTier(tier);
                return [];
            case "task_started":
                state.TurnOpen = true;
                return [new AgentLogEntry.TurnBegan(Seconds(payload, "started_at") ?? date)];
            case "task_complete" or "turn_aborted":
            {
                var open = state.TurnOpen;
                state.TurnOpen = false;
                if (!open) return [];
                TimeSpan? duration = payload.TryGetProperty("duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromMilliseconds(ms.GetDouble())
                    : null;
                return [new AgentLogEntry.TurnEnded(Seconds(payload, "completed_at") ?? date, Str(payload, "type") == "task_complete", duration)];
            }
            default:
                return [];
        }
    }

    public static bool FastTier(string tier) => tier.ToLowerInvariant() is "fast" or "priority";

    private static AgentLogEntry CodexUsage(AgentTokens tokens, string key, DateTimeOffset date, AgentLogState state)
    {
        var billable = new AgentBillable { Tokens = tokens, Fast = state.Fast };
        var (cost, savings) = AgentPricing.Cost(billable, state.Model);
        return new AgentLogEntry.Usage(key,
            new AgentUsageRecord(AgentProvider.Codex, date, state.Model, state.Project, state.Session, tokens, cost, savings), billable);
    }

    public static AgentTokens CodexTokens(JsonElement usage)
    {
        var input = Int(usage, "input_tokens");
        var cached = Math.Min(input, Int(usage, "cached_input_tokens"));
        var written = Math.Min(input - cached, Int(usage, "cache_write_input_tokens"));
        return new AgentTokens(input - cached - written, written, cached, Int(usage, "output_tokens"), Int(usage, "reasoning_output_tokens"));
    }

    public static bool IsMainBucket(JsonElement limits)
    {
        var id = (Str(limits, "limit_id") ?? string.Empty).ToLowerInvariant();
        return id.Length == 0 || id == "codex";
    }

    public static List<AgentLimitWindow> CodexWindows(JsonElement limits, DateTimeOffset observed)
    {
        var windows = new List<AgentLimitWindow>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("used_percent", out var usedEl) || usedEl.ValueKind != JsonValueKind.Number) continue;
            var used = usedEl.GetDouble();
            if (!double.IsFinite(used)) continue;
            int? minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? (int)m.GetDouble() : null;
            var resets = Seconds(w, "resets_at");
            if (resets is null && w.TryGetProperty("resets_in_seconds", out var rin) && rin.ValueKind == JsonValueKind.Number && double.IsFinite(rin.GetDouble()))
                resets = observed.AddSeconds(Math.Max(0, rin.GetDouble()));
            windows.Add(new AgentLimitWindow($"codex.{(minutes?.ToString(CultureInfo.InvariantCulture) ?? slot)}", AgentLimitWindow.KindFor(minutes),
                minutes, null, Math.Clamp(used, 0, 100), resets));
        }
        if (windows.Count == 0 && limits.TryGetProperty("individual_limit", out var ind) && ind.ValueKind == JsonValueKind.Object &&
            ind.TryGetProperty("remaining_percent", out var rem) && rem.ValueKind == JsonValueKind.Number && double.IsFinite(rem.GetDouble()))
            windows.Add(new AgentLimitWindow("codex.individual", AgentLimitKind.Other, null, null, Math.Clamp(100 - rem.GetDouble(), 0, 100), Seconds(ind, "resets_at")));
        return windows.OrderBy(w => w.Minutes ?? int.MaxValue).ToList();
    }

    // ------------------------------------------------------------------ OpenCode

    /// <summary>One OpenCode database row, as the SQLite reader hands it over (JSON with role/id/session_id/...).</summary>
    public static List<AgentLogEntry> ParseOpenCode(string line, AgentLogState state, DateTimeOffset now)
    {
        if (!line.Contains("\"role\":\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"reset\"", StringComparison.Ordinal)) return [];
        using var doc = Doc(line);
        if (doc is null) return [];
        var json = doc.RootElement;
        if (Str(json, "type") == "reset")
        {
            state.OpenCodeSessions.Clear();
            state.TurnOpen = false;
            return [new AgentLogEntry.Reset(now)];
        }
        if (Str(json, "role") is not { } role) return [];
        var id = Str(json, "id") ?? string.Empty;
        var session = Str(json, "session_id") ?? string.Empty;
        var sessionId = session.Length == 0 ? state.Session : session;
        state.Session = sessionId;
        state.ParentSession = Str(json, "parent_session_id") ?? string.Empty;
        var isNew = !state.OpenCodeSessions.TryGetValue(sessionId, out var s);
        s ??= new OpenCodeSessionState();
        var cwd = Str(Obj(json, "path"), "cwd") ?? Str(json, "directory") ?? string.Empty;
        if (cwd.Length > 0) s.Project = ProjectName(cwd);
        var date = Seconds(Obj(json, "time"), "created") ?? Seconds(json, "time_created") ?? now;
        if (isNew)
        {
            foreach (var key in state.OpenCodeSessions.Where(p => p.Value.WritingReplyId.Length == 0 &&
                                                                     (date - (p.Value.LastActivity ?? date)).TotalSeconds >= IdleTurn).Select(p => p.Key).ToList())
                state.OpenCodeSessions.Remove(key);
        }
        var lastActivity = s.LastActivity;
        s.LastActivity = Max(lastActivity ?? date, date);
        var entries = new List<AgentLogEntry>();
        switch (role)
        {
            case "user":
            {
                if (id.Length > 0)
                {
                    if (s.SeenUserMessageIds.Contains(id)) return [];
                    s.SeenUserMessageIds.Remember(id);
                    s.ActiveUserMessageId = id;
                }
                bool quiet;
                if (s.SettledAt is { } settled) quiet = (date - settled).TotalSeconds >= OpenCodeSettle;
                else if (s.WritingReplyId.Length == 0 && lastActivity is { } la) quiet = (date - la).TotalSeconds >= IdleTurn;
                else quiet = false;
                s.SettledAt = null;
                s.ActivePromptDate = date;
                if (s.TurnOpen && !quiet)
                {
                    entries.Add(new AgentLogEntry.TurnActive(date));
                }
                else
                {
                    if (s.TurnOpen) entries.Add(new AgentLogEntry.TurnEnded(lastActivity, false, null));
                    s.TurnOpen = true;
                    s.TurnStarted = date;
                    entries.Add(new AgentLogEntry.TurnBegan(date));
                }
                if (OpenCodeModel(json) is { Length: > 0 } um) s.Model = um;
                state.OpenCodeSessions[sessionId] = s;
                state.Project = s.Project;
                state.Model = s.Model;
                state.TurnOpen = true;
                return entries;
            }
            case "assistant" or "compaction":
            {
                if (OpenCodeModel(json) is { Length: > 0 } am) s.Model = am;
                var parentId = Str(json, "parentID") ?? string.Empty;
                var alreadyCompleted = (id.Length > 0 && s.CompletedAssistantMessageIds.Contains(id)) ||
                                       (parentId.Length > 0 && s.CompletedUserMessageIds.Contains(parentId));
                var matchesPrompt = role == "compaction" || parentId.Length == 0 || parentId == s.ActiveUserMessageId;
                var completed = Seconds(Obj(json, "time"), "completed");
                var hasError = json.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined);
                var finish = Str(json, "finish");
                var status = Str(json, "status");
                if (role == "compaction")
                {
                    // OpenCode 2 compacts in a row of its own; the turn goes on meanwhile.
                    if (OpenCodeUsage(json, id, sessionId, date, s.Model, s.Project) is { } cu) entries.Add(cu);
                    if (s.TurnOpen) entries.Add(new AgentLogEntry.TurnActive(completed ?? date));
                    state.OpenCodeSessions[sessionId] = s;
                    state.TurnOpen = s.TurnOpen;
                    _ = status;
                    return entries;
                }
                if (completed is { } c) s.LastActivity = Max(s.LastActivity ?? c, c);
                if (s.SettledAt is { } st && date >= st) s.SettledAt = null;
                if (id.Length > 0 && s.WritingReplyId.Length > 0 && s.WritingReplyId != id && s.TurnOpen && parentId.Length > 0 &&
                    parentId == s.ActiveUserMessageId && s.ActivePromptDate is { } prompt && s.TurnStarted is { } started && prompt > started)
                {
                    entries.Add(new AgentLogEntry.TurnEnded(null, false, null));
                    entries.Add(new AgentLogEntry.TurnBegan(prompt));
                    s.TurnStarted = prompt;
                }
                if (completed is null && finish is null && !hasError && id.Length > 0) s.WritingReplyId = id;
                else if (s.WritingReplyId == id) s.WritingReplyId = string.Empty;
                if (!alreadyCompleted && matchesPrompt && !s.TurnOpen)
                {
                    s.TurnOpen = true;
                    s.TurnStarted = date;
                    entries.Add(new AgentLogEntry.TurnBegan(date));
                }
                if (OpenCodeUsage(json, id, sessionId, date, s.Model, s.Project) is { } usage) entries.Add(usage);
                bool ends;
                if (hasError) ends = true;
                else if (finish is not null)
                    ends = finish is not ("tool-calls" or "unknown") && Bool(json, "tool_calls") != true && Bool(json, "auto_compaction") != true;
                else ends = completed is not null;
                if (ends)
                {
                    if (!alreadyCompleted && matchesPrompt && s.TurnOpen)
                    {
                        var endDate = completed ?? Seconds(json, "time_updated") ?? date;
                        TimeSpan? duration = null;
                        if (s.TurnStarted is { } ts) { if (endDate >= ts) duration = endDate - ts; }
                        else if (endDate >= date) duration = endDate - date;
                        s.TurnOpen = false;
                        s.TurnStarted = null;
                        s.ActiveUserMessageId = string.Empty;
                        s.SettledAt = null;
                        entries.Add(new AgentLogEntry.TurnEnded(endDate, !hasError, duration));
                    }
                    if (id.Length > 0) s.CompletedAssistantMessageIds.Remember(id);
                    if (parentId.Length > 0) s.CompletedUserMessageIds.Remember(parentId);
                }
                else if (!alreadyCompleted && matchesPrompt)
                {
                    if (completed is { } done && s.TurnOpen)
                    {
                        if (json.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() == 2)
                            entries.Add(new AgentLogEntry.TurnActive(done));
                        else
                        {
                            s.SettledAt = done;
                            entries.Add(new AgentLogEntry.TurnSettled(done));
                        }
                    }
                    else
                    {
                        entries.Add(new AgentLogEntry.TurnActive(date));
                    }
                }
                state.OpenCodeSessions[sessionId] = s;
                state.Project = s.Project;
                state.Model = s.Model;
                state.TurnOpen = s.TurnOpen;
                return entries;
            }
            case "idle":
            {
                if (s.TurnOpen)
                {
                    TimeSpan? duration = s.TurnStarted is { } ts && date >= ts ? date - ts : null;
                    s.TurnOpen = false;
                    s.TurnStarted = null;
                    s.ActiveUserMessageId = string.Empty;
                    entries.Add(new AgentLogEntry.TurnEnded(date, Str(json, "outcome") == "succeeded", duration));
                }
                state.OpenCodeSessions[sessionId] = s;
                state.TurnOpen = false;
                return entries;
            }
            default:
                return [];
        }
    }

    private static string? OpenCodeModel(JsonElement json) =>
        Str(json, "model_id") ?? Str(json, "modelID") ?? Str(Obj(json, "model"), "modelID") ?? Str(Obj(json, "model"), "id") ?? Str(json, "model");

    private static AgentLogEntry? OpenCodeUsage(JsonElement json, string id, string session, DateTimeOffset date, string model, string project)
    {
        var t = Obj(json, "tokens");
        var reasoning = Int(t, "reasoning");
        var cache = Obj(t, "cache");
        var tokens = new AgentTokens(Int(t, "input"), Int(cache, "write"), Int(cache, "read"), Int(t, "output") + reasoning, reasoning);
        double? recorded = json.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
        if (tokens.Total <= 0 && (recorded ?? 0) <= 0) return null;
        var billable = new AgentBillable { Tokens = tokens };
        var priced = AgentPricing.Cost(billable, model);
        double? cost;
        bool reported;
        if (priced.Cost is { } calc) { cost = calc; reported = false; }
        else if (recorded is { } r) { cost = r; reported = r > 0; }
        else { cost = null; reported = false; }
        var key = $"opencode:{session}:{(id.Length == 0 ? Unix(date) : id)}";
        return new AgentLogEntry.Usage(key,
            new AgentUsageRecord(AgentProvider.OpenCode, date, model, project, session, tokens, cost, priced.Savings, ReportedCost: reported), billable);
    }

    // ------------------------------------------------------------------ GitHub Copilot CLI

    public static bool IsCopilotEvent(string type) => CopilotEvents.Contains(type);

    public static List<AgentLogEntry> ParseCopilot(string line, AgentLogState state, DateTimeOffset now)
    {
        using var doc = Doc(line);
        if (doc is null) return [];
        var env = doc.RootElement;
        if (Str(env, "type") is not { } type || !CopilotEvents.Contains(type)) return [];
        if (!env.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [];
        var agent = Str(env, "agentId") is { Length: > 0 } a ? a : null;
        var root = agent is null;
        if (!root && type != "assistant.message") return [];
        var date = Timestamp(env, "timestamp") ?? now;
        switch (type)
        {
            case "user.message" or "assistant.turn_start":
            {
                state.CopilotFinalResponse = false;
                state.CopilotTurnId = type == "assistant.turn_start" ? Str(data, "turnId") : null;
                if (Str(data, "model") is { Length: > 0 } m) state.Model = m;
                AgentLogEntry entry = state.TurnOpen ? new AgentLogEntry.TurnActive(date) : new AgentLogEntry.TurnBegan(date);
                state.TurnOpen = true;
                return [entry, new AgentLogEntry.TurnContext(state.Model, state.Project)];
            }
            case "assistant.message":
            {
                var model = Str(data, "model") is { Length: > 0 } dm ? dm : root ? state.Model : string.Empty;
                var entries = CopilotActivity(data, env, agent, model, date, state);
                if (!root) return entries;
                state.Model = model;
                var hasTools = data.TryGetProperty("toolRequests", out var tr) && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0;
                state.CopilotFinalResponse = !hasTools && Str(data, "phase") is not ("thinking" or "commentary");
                if (state.TurnOpen)
                {
                    entries.Add(new AgentLogEntry.TurnContext(state.Model, state.Project));
                    entries.Add(new AgentLogEntry.TurnActive(date));
                }
                return entries;
            }
            case "assistant.turn_end":
                if (!state.TurnOpen || (state.CopilotTurnId is not null && state.CopilotTurnId != Str(data, "turnId"))) return [];
                if (!state.CopilotFinalResponse) return [new AgentLogEntry.TurnActive(date)];
                state.TurnOpen = false;
                state.CopilotFinalResponse = false;
                return [new AgentLogEntry.TurnEnded(date, true, null)];
            case "session.start":
                if (Str(data, "sessionId") is { Length: > 0 } sid) state.Session = sid;
                if (Str(data, "selectedModel") is { Length: > 0 } sm) state.Model = sm;
                if (Obj(data, "context") is { } ctx && FirstPath(ctx) is { } p) state.Project = ProjectName(p);
                return [];
            case "session.context_changed":
                if (FirstPath(data) is { } p2) state.Project = ProjectName(p2);
                return state.TurnOpen ? [new AgentLogEntry.TurnContext(state.Model, state.Project)] : [];
            case "session.model_change":
                if (Str(data, "newModel") is { Length: > 0 } nm) state.Model = nm;
                return state.TurnOpen ? [new AgentLogEntry.TurnContext(state.Model, state.Project)] : [];
            case "abort":
                if (!state.TurnOpen) return [];
                state.TurnOpen = false;
                state.CopilotFinalResponse = false;
                return [new AgentLogEntry.TurnEnded(date, false, null)];
            case "session.shutdown":
            {
                var entries = CopilotUsage(Obj(data, "modelMetrics"), Str(env, "id"), date, state);
                if (state.TurnOpen)
                {
                    state.TurnOpen = false;
                    entries.Add(new AgentLogEntry.TurnEnded(date, false, null));
                }
                return entries;
            }
            default:
                return [];
        }
    }

    private static string? FirstPath(JsonElement o) =>
        new[] { "gitRoot", "cwd", "repository" }.Select(k => Str(o, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static List<AgentLogEntry> CopilotActivity(JsonElement data, JsonElement env, string? agent, string model, DateTimeOffset date, AgentLogState state)
    {
        var call = Str(data, "apiCallId") is { Length: > 0 } ac ? ac : null;
        var message = Str(data, "messageId") is { Length: > 0 } mi ? mi : Str(env, "id") ?? Unix(date);
        var request = (agent is null ? "root" : "agent:" + agent) + ":" + (call is null ? "message:" + message : "api:" + call);
        var key = $"copilot:{state.Session}:{request}:activity";
        if (state.CopilotRequestModels.TryGetValue(request, out var previous))
        {
            if (previous.Length > 0 || model.Length == 0) return [];
            state.CopilotRequestModels[request] = model;
            state.CopilotRequests[previous] = state.CopilotRequests.GetValueOrDefault(previous) - 1;
            state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + 1;
            return [new AgentLogEntry.UsageModel(key, model)];
        }
        state.CopilotRequestModels[request] = model;
        state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + 1;
        return
        [
            new AgentLogEntry.Usage(key,
                new AgentUsageRecord(AgentProvider.Copilot, date, model, state.Project, state.Session, default, 0, 0),
                new AgentBillable { IsAggregate = true }),
        ];
    }

    private static List<AgentLogEntry> CopilotUsage(JsonElement? metrics, string? eventId, DateTimeOffset date, AgentLogState state)
    {
        if (metrics is not { } m) return [];
        var growth = new Dictionary<string, AgentTokens>(StringComparer.Ordinal);
        foreach (var prop in m.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var total = CopilotTokens(prop.Value);
            growth[prop.Name] = state.CopilotTotals.TryGetValue(prop.Name, out var prev) && total.Total >= prev.Total ? total.Minus(prev) : total;
            state.CopilotTotals[prop.Name] = total;
            var reported = (int)Int(Obj(prop.Value, "requests"), "count");
            state.CopilotReportedRequests[prop.Name] = Math.Max(state.CopilotReportedRequests.GetValueOrDefault(prop.Name),
                Math.Max(reported, total.Total > 0 ? 1 : 0));
        }
        var deficits = state.CopilotReportedRequests
            .Select(p => (Model: p.Key, Count: Math.Max(0, p.Value - state.CopilotRequests.GetValueOrDefault(p.Key))))
            .ToList();
        var unresolved = state.CopilotRequests.GetValueOrDefault(string.Empty);
        var missing = Math.Max(0, deficits.Sum(d => d.Count) - unresolved);
        var requests = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in deficits)
        {
            var certain = Math.Max(0, d.Count - unresolved);
            requests[d.Model] = certain;
            missing -= certain;
        }
        if (missing > 0) requests[string.Empty] = requests.GetValueOrDefault(string.Empty) + missing;

        var entries = new List<AgentLogEntry>();
        foreach (var model in growth.Keys.Union(requests.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var tokens = growth.GetValueOrDefault(model);
            var count = requests.GetValueOrDefault(model);
            state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + count;
            if (tokens.Total <= 0 && count <= 0) continue;
            var billable = new AgentBillable { Tokens = tokens, IsAggregate = true };
            var priced = AgentPricing.Cost(billable, model);
            var checkpoint = string.IsNullOrEmpty(eventId) ? Unix(date) : eventId;
            entries.Add(new AgentLogEntry.Usage($"copilot:{state.Session}:{checkpoint}:{model}",
                new AgentUsageRecord(AgentProvider.Copilot, date, model, state.Project, state.Session, tokens, priced.Cost, priced.Savings, count),
                billable));
        }
        return entries;
    }

    private static AgentTokens CopilotTokens(JsonElement metric)
    {
        var details = Obj(metric, "tokenDetails");
        var usage = Obj(metric, "usage");
        long Count(string detail, string fallback) =>
            details is { } d && d.TryGetProperty(detail, out var x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty("tokenCount", out _)
                ? Int(x, "tokenCount")
                : Int(usage, fallback);
        return new AgentTokens(Count("input", "inputTokens"), Count("cache_write", "cacheWriteTokens"), Count("cache_read", "cacheReadTokens"),
            Count("output", "outputTokens"), Int(usage, "reasoningTokens"));
    }

    // ------------------------------------------------------------------ shared values

    /// <summary>The folder name of a working directory, ignoring Claude worktrees (both separators).</summary>
    public static string ProjectName(string path)
    {
        var p = path;
        foreach (var marker in new[] { "/.claude/worktrees/", "\\.claude\\worktrees\\" })
        {
            var i = p.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) p = p[..i];
        }
        p = p.TrimEnd('/', '\\');
        if (p.Length == 0) return path.Length > 0 ? path[..1] : string.Empty;
        var slash = p.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    private static string Unix(DateTimeOffset d) => (d.ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static JsonElement? Obj(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    /// <summary>JSON numbers only, positive and finite, capped at 1e12.</summary>
    public static long Int(JsonElement? o, string name)
    {
        if (o is not { ValueKind: JsonValueKind.Object } e || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        var d = v.GetDouble();
        return double.IsFinite(d) && d > 0 ? (long)Math.Min(d, 1e12) : 0;
    }

    /// <summary>Unix seconds, or milliseconds when above 1e11.</summary>
    public static DateTimeOffset? Seconds(JsonElement? o, string name)
    {
        if (o is not { ValueKind: JsonValueKind.Object } e || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        var raw = v.GetDouble();
        if (!double.IsFinite(raw) || raw <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(raw > 100_000_000_000 ? raw : raw * 1000));
    }

    public static DateTimeOffset? Timestamp(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return Seconds(o, name);
        if (v.ValueKind != JsonValueKind.String) return null;
        return ParseIso(v.GetString()!);
    }

    public static DateTimeOffset? ParseIso(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) &&
        text.Length >= 20 && text[10] == 'T'
            ? d
            : null;
}
