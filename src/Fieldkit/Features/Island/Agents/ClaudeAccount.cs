// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;

namespace Fieldkit.Features.Island.Agents;

/// <summary>
/// What Claude keeps on this PC about the account: the plan in <c>~/.claude.json</c> and the plan
/// limits the Claude desktop app saves (<c>plan-usage-history.json</c>). Files only: no sign-in, nothing sent.
/// </summary>
public static class ClaudeAccount
{
    public sealed record PlanInfo(string? OrganizationType, string? RateLimitTier, string? OrganizationUuid);

    public static PlanInfo? ReadPlan(string home)
    {
        try
        {
            var path = Path.Combine(home, ".claude.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 << 20) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("oauthAccount", out var a) || a.ValueKind != JsonValueKind.Object) return null;
            string? S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new PlanInfo(S("organizationType"), S("organizationRateLimitTier") ?? S("userRateLimitTier"), S("organizationUuid"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Candidate locations of the desktop app's limits file on Windows (unverified upstream path, mapped).</summary>
    public static IEnumerable<string> LimitFiles()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(roaming, "Claude", "plan-usage-history.json");
        yield return Path.Combine(local, "Claude", "plan-usage-history.json");
        yield return Path.Combine(local, "AnthropicClaude", "plan-usage-history.json");
    }

    public static bool AppInstalled()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Directory.Exists(Path.Combine(roaming, "Claude")) || Directory.Exists(Path.Combine(local, "AnthropicClaude"));
    }

    public sealed record Sample(DateTimeOffset Time, string? Org, double? Session, double? Weekly, double? Opus, double? Sonnet);

    public static List<Sample> ParseSamples(string json)
    {
        var result = new List<Sample>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return result;
            foreach (var s in samples.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                var raw = t.GetDouble();
                var time = DateTimeOffset.FromUnixTimeMilliseconds((long)(raw > 100_000_000_000 ? raw : raw * 1000));
                var u = s.TryGetProperty("u", out var uu) && uu.ValueKind == JsonValueKind.Object ? uu : s;
                double? N(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), 0, 100) : null;
                var org = s.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                result.Add(new Sample(time, org, N("fh"), N("sd"), N("so"), N("sn")));
            }
        }
        catch (JsonException) { }
        return result.OrderBy(s => s.Time).ToList();
    }

    /// <summary>
    /// Limit windows from the app's samples. The 5-hour session resets 5 h after the run of non-zero readings
    /// began (estimated when the start falls between readings); weekly windows reset a week after their last drop.
    /// </summary>
    public static AgentLimits? Limits(IReadOnlyList<Sample> all, string? org, DateTimeOffset now, DateTimeOffset? blockStart = null)
    {
        var samples = all.Where(s => org is null || s.Org is null || s.Org == org).ToList();
        if (samples.Count == 0) return null;
        var latest = samples[^1];
        if (now - latest.Time > TimeSpan.FromDays(7)) return null;
        var windows = new List<AgentLimitWindow>();

        if (latest.Session is { } fh && now - latest.Time < TimeSpan.FromHours(5))
        {
            DateTimeOffset? resets = null;
            DateTimeOffset? estimated = null;
            if (fh > 0)
            {
                var i = samples.Count - 1;
                while (i > 0 && samples[i - 1].Session is > 0) i--;
                var first = samples[i].Time;
                var previous = i > 0 ? samples[i - 1].Time : (DateTimeOffset?)null;
                if (blockStart is { } b && b <= first && (previous is null || b >= previous)) resets = b.AddHours(5);
                else
                {
                    resets = first.AddHours(5);
                    if (previous is { } p && first - p > TimeSpan.FromMinutes(30))
                        estimated = p + (first - p) / 2 + TimeSpan.FromHours(5);
                }
            }
            var w = new AgentLimitWindow("claude.session", AgentLimitKind.Session, 300, null, fh, resets, estimated);
            if (w.ResetsAt is null || w.ResetsAt > now) windows.Add(w);
        }

        void Weekly(Func<Sample, double?> pick, string id, string? scope)
        {
            if (pick(latest) is not { } used) return;
            DateTimeOffset? resets = null;
            for (var i = samples.Count - 1; i > 0; i--)
            {
                if (pick(samples[i - 1]) is { } before && pick(samples[i]) is { } after && before - after > 1)
                {
                    var earlier = samples[i - 1].Time;
                    var hour = new DateTimeOffset(earlier.UtcDateTime.Date.AddHours(earlier.UtcDateTime.Hour + 1), TimeSpan.Zero);
                    if (hour > samples[i].Time) hour = samples[i].Time;
                    while (hour <= latest.Time) hour = hour.AddDays(7);
                    resets = hour;
                    break;
                }
            }
            if (resets is null && now - latest.Time >= TimeSpan.FromDays(1)) return;
            var w = new AgentLimitWindow(id, AgentLimitKind.Weekly, 10080, scope, used, resets);
            if (w.ResetsAt is null || w.ResetsAt > now) windows.Add(w);
        }
        Weekly(s => s.Weekly, "claude.weekly", null);
        Weekly(s => s.Opus, "claude.weekly.opus", "Opus");
        Weekly(s => s.Sonnet, "claude.weekly.sonnet", "Sonnet");
        return windows.Count == 0 ? null : new AgentLimits(AgentProvider.Claude, windows, latest.Time, AgentLimitSource.ClaudeApp);
    }
}
