// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Fieldkit.Features.Island.Agents;

/// <summary>US dollars per million tokens, from each provider's published API list.</summary>
public sealed record AgentPrice(double Input, double Output, double CacheRead, double CacheWrite, double CacheWriteLong,
    double FastMultiplier = 1, AgentLongContext? LongContext = null);

/// <summary>Past <see cref="Above"/> prompt tokens, input and cache rates are multiplied by
/// <see cref="Input"/> and output by <see cref="Output"/>, on top of any fast tier.</summary>
public sealed record AgentLongContext(int Above, double Input, double Output);

/// <summary>What one response bills, beyond its model.</summary>
public sealed record AgentBillable
{
    public AgentTokens Tokens { get; init; }
    /// <summary>Session totals hide individual prompt sizes: priced at base rates.</summary>
    public bool IsAggregate { get; init; }
    /// <summary>The part of cacheWrite kept for an hour.</summary>
    public long LongCacheWrite { get; init; }
    public bool Fast { get; init; }
    /// <summary>Inference pinned to the United States.</summary>
    public bool Domestic { get; init; }
    public long WebSearches { get; init; }

    /// <summary>Per-field maximum (streamed chunks of the same response repeat growing counts).</summary>
    public AgentBillable Merge(AgentBillable other) => this with
    {
        Tokens = Tokens.Max(other.Tokens),
        IsAggregate = IsAggregate || other.IsAggregate,
        LongCacheWrite = Math.Max(LongCacheWrite, other.LongCacheWrite),
        Fast = Fast || other.Fast,
        Domestic = Domestic || other.Domestic,
        WebSearches = Math.Max(WebSearches, other.WebSearches),
    };
}

/// <summary>A subscription recognised from what the agents keep on disk.</summary>
public sealed record AgentPlan(string Name, double? MonthlyPrice);

/// <summary>
/// Every price the island knows (a port of upstream <c>AgentPriceList</c>). The list shipped with
/// upstream is bundled; a list that breaks the rules is refused as a whole.
/// </summary>
public sealed class AgentPriceList
{
    public sealed record Model(string Id, AgentPrice Price);
    public sealed record Plan(string Match, string Name, double? Monthly);

    public const int Schema = 1;

    public DateTime Updated { get; init; }
    public IReadOnlyList<Model> Claude { get; init; } = [];
    public IReadOnlyList<Model> Codex { get; init; } = [];
    public IReadOnlyList<Plan> ClaudePlans { get; init; } = [];
    public IReadOnlyList<Plan> CodexPlans { get; init; } = [];
    public double WebSearch { get; init; }
    public double UsOnlyMultiplier { get; init; } = 1;

    public static AgentPriceList Empty { get; } = new();

    private static AgentPriceList? _bundled;

    /// <summary>The list shipped with the app.</summary>
    public static AgentPriceList Bundled => _bundled ??= Decode(AgentPricesBundled.Json) ?? Empty;

    public static AgentPriceList? Decode(string json)
    {
        try
        {
            if (json.Length > 256 * 1024) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Number(root, "schema", 1, 1000) != Schema) return null;
            if (!root.TryGetProperty("updated", out var u) || u.ValueKind != JsonValueKind.String || Day(u.GetString()!) is not { } updated) return null;
            if (!root.TryGetProperty("claude", out var claude) || claude.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("codex", out var codex) || codex.ValueKind != JsonValueKind.Object) return null;
            var claudeModels = Models(claude, "claude-");
            var codexModels = Models(codex, null);
            var claudePlans = Plans(claude);
            var codexPlans = Plans(codex);
            var web = Number(claude, "webSearch", 0, 1);
            var us = Number(claude, "usOnlyMultiplier", 1, 3);
            if (claudeModels is null || codexModels is null || claudePlans is null || codexPlans is null || web is null || us is null) return null;
            return new AgentPriceList
            {
                Updated = updated, Claude = claudeModels, Codex = codexModels, ClaudePlans = claudePlans, CodexPlans = codexPlans,
                WebSearch = web.Value, UsOnlyMultiplier = us.Value,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<Model>? Models(JsonElement section, string? prefix)
    {
        if (!section.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var count = arr.GetArrayLength();
        if (count is < 1 or > 500) return null;
        var result = new List<Model>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
            var id = idEl.GetString()!;
            if (!Identifier(id, 64) || !seen.Add(id)) return null;
            if (prefix is not null ? !id.StartsWith(prefix, StringComparison.Ordinal) : id.StartsWith("claude-", StringComparison.Ordinal)) return null;
            var input = Number(e, "input", 0, 1000);
            var output = Number(e, "output", 0, 1000);
            var cacheRead = Number(e, "cacheRead", 0, 1000);
            if (input is null || output is null || cacheRead is null) return null;
            var cacheWrite = e.TryGetProperty("cacheWrite", out _) ? Number(e, "cacheWrite", 0, 1000) : input;
            var cacheWriteLong = e.TryGetProperty("cacheWriteLong", out _) ? Number(e, "cacheWriteLong", 0, 1000) : cacheWrite;
            var fast = e.TryGetProperty("fastMultiplier", out _) ? Number(e, "fastMultiplier", 1, 10) : 1;
            if (cacheWrite is null || cacheWriteLong is null || fast is null) return null;
            AgentLongContext? lc = null;
            if (e.TryGetProperty("longContext", out var l))
            {
                if (l.ValueKind != JsonValueKind.Object) return null;
                var above = Number(l, "above", 1000, 10_000_000);
                var li = Number(l, "inputMultiplier", 1, 10);
                var lo = Number(l, "outputMultiplier", 1, 10);
                if (above is null || li is null || lo is null) return null;
                lc = new AgentLongContext((int)above.Value, li.Value, lo.Value);
            }
            result.Add(new Model(id, new AgentPrice(input.Value, output.Value, cacheRead.Value, cacheWrite.Value, cacheWriteLong.Value, fast.Value, lc)));
        }
        return result;
    }

    private static List<Plan>? Plans(JsonElement section)
    {
        if (!section.TryGetProperty("plans", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 50) return null;
        var result = new List<Plan>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty("match", out var m) || m.ValueKind != JsonValueKind.String || !Identifier(m.GetString()!, 40)) return null;
            if (!e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
            var name = n.GetString()!.Trim();
            if (name.Length is < 1 or > 24) return null;
            double? monthly = null;
            if (e.TryGetProperty("monthly", out _))
            {
                monthly = Number(e, "monthly", 0, 100_000);
                if (monthly is null) return null;
            }
            result.Add(new Plan(m.GetString()!, name, monthly));
        }
        return result;
    }

    private static bool Identifier(string text, int length) =>
        text.Length >= 1 && text.Length <= length && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static double? Number(JsonElement obj, string name, double min, double max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        var d = v.GetDouble();
        return double.IsFinite(d) && d >= min && d <= max ? d : null;
    }

    private static DateTime? Day(string text)
    {
        var parts = text.Split('-');
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var (y, m, d) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        if (y is < 2024 or > 2100 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        return new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    }
}

/// <summary>Costs are what the same traffic would bill through the API (shown as "API value").</summary>
public static class AgentPricing
{
    private static AgentPriceList _list = AgentPriceList.Bundled;

    /// <summary>The list prices come from.</summary>
    public static AgentPriceList List
    {
        get => Volatile.Read(ref _list);
        set => Volatile.Write(ref _list, value);
    }

    /// <summary>Provider prefixes, snapshot dates and context tags vary by where the model runs; the family does not.</summary>
    public static string Normalized(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        var at = id.IndexOf("claude-", StringComparison.Ordinal);
        if (at >= 0) id = id[at..];
        // Copilot names Claude point releases with a dot where the price list uses a dash.
        if (id.StartsWith("claude-", StringComparison.Ordinal)) id = id.Replace('.', '-');
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        foreach (var marker in new[] { '@', '[' })
        {
            var i = id.IndexOf(marker);
            if (i >= 0) id = id[..i];
        }
        return id;
    }

    /// <summary>Words that make a variant a model of its own, with its own price.</summary>
    public static readonly string[] Siblings =
        ["mini", "nano", "pro", "lite", "research", "search", "audio", "realtime", "transcribe", "tts", "cyber", "image"];

    /// <summary>The longest matching family wins; an unnamed sibling gets no price.</summary>
    public static AgentPrice? PriceFor(string model, AgentPriceList? list = null)
    {
        list ??= List;
        var id = Normalized(model);
        var table = id.StartsWith("claude-", StringComparison.Ordinal) ? list.Claude : list.Codex;
        var match = table.Where(m => Matches(id, m.Id)).MaxBy(m => m.Id.Length);
        if (match is null) return null;
        var rest = id[match.Id.Length..];
        if (Siblings.Any(rest.Contains)) return null;
        return match.Price;
    }

    /// <summary>A family matches at a word boundary; a snapshot date (4+ digits) after it is the same model.</summary>
    private static bool Matches(string id, string family)
    {
        if (!id.StartsWith(family, StringComparison.Ordinal)) return false;
        var rest = id[family.Length..];
        if (rest.Length == 0) return true;
        if (rest[0] is not ('-' or '_' or ':')) return false;
        var digits = rest.Skip(1).TakeWhile(char.IsDigit).Count();
        return digits == 0 || digits >= 4;
    }

    /// <summary>API list cost (null when the model has no known price) and what the cache saved.</summary>
    public static (double? Cost, double Savings) Cost(AgentBillable billable, string model, AgentPriceList? list = null)
    {
        list ??= List;
        var tokens = billable.Tokens;
        if (billable.IsAggregate && tokens.Total == 0) return (0, 0);
        var price = PriceFor(model, list);
        if (price is null) return (null, 0);
        var longWrite = Math.Min(Math.Max(0, billable.LongCacheWrite), tokens.CacheWrite);
        var multiplier = billable.Fast ? price.FastMultiplier : 1;
        if (billable.Domestic) multiplier *= list.UsOnlyMultiplier;
        var inRate = multiplier;
        var outRate = multiplier;
        if (!billable.IsAggregate && price.LongContext is { } lc && tokens.Prompt > lc.Above)
        {
            inRate *= lc.Input;
            outRate *= lc.Output;
        }
        var cost = (tokens.Input * price.Input * inRate
                    + (tokens.CacheWrite - longWrite) * price.CacheWrite * inRate
                    + longWrite * price.CacheWriteLong * inRate
                    + tokens.CacheRead * price.CacheRead * inRate
                    + tokens.Output * price.Output * outRate) / 1_000_000
                   + Math.Max(0, billable.WebSearches) * list.WebSearch;
        var savings = tokens.CacheRead * Math.Max(0, price.Input - price.CacheRead) * inRate / 1_000_000;
        return (cost, savings);
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5", "gpt-6-astra" → "GPT-6 Astra".</summary>
    public static string DisplayName(string model)
    {
        var id = Normalized(model);
        if (id.Length == 0) return string.Empty;
        var ti = CultureInfo.InvariantCulture.TextInfo;
        static bool AllDigits(string s) => s.Length > 0 && s.All(char.IsDigit);
        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            var family = id.Split(':', 2)[0];
            var parts = family[7..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            parts.RemoveAll(p => p.Length >= 6 && AllDigits(p));
            parts.RemoveAll(p => p.Length > 1 && p[0] == 'v' && char.IsDigit(p[1]));
            parts.RemoveAll(p => p == "latest");
            static bool Version(string s) => s.All(c => char.IsDigit(c) || c == '.');
            var words = parts.Where(p => !Version(p)).ToList();
            var version = string.Join(".", parts.Where(Version));
            var name = new List<string> { words.Count > 0 ? Capitalize(words[0]) : string.Empty, version };
            name.AddRange(words.Skip(1).Select(Capitalize));
            return string.Join(" ", name.Where(n => n.Length > 0));
        }
        if (!id.StartsWith("gpt-", StringComparison.Ordinal)) return id;
        var gp = id[4..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        gp.RemoveAll(p => p == "latest");
        gp.RemoveAll(p => p.Length >= 6 && AllDigits(p));
        if (gp.Count >= 4 && int.TryParse(gp[^3], out var year) && year is >= 2000 and <= 2100 &&
            gp.TakeLast(2).All(p => p.Length == 2 && AllDigits(p)))
            gp.RemoveRange(gp.Count - 3, 3);
        if (gp.Count == 0) return "GPT";
        var head = char.IsDigit(gp[0][0]) ? "GPT-" + gp[0] : "GPT " + Capitalize(gp[0]);
        return string.Join(" ", new[] { head }.Concat(gp.Skip(1).Select(Capitalize)));

        string Capitalize(string s) => ti.ToTitleCase(s);
    }

    public static AgentPlan? ClaudePlan(string? organizationType, string? rateLimitTier, AgentPriceList? list = null)
    {
        list ??= List;
        var type = (organizationType ?? string.Empty).ToLowerInvariant();
        var recorded = (rateLimitTier ?? string.Empty).ToLowerInvariant() + " " + type;
        if (list.ClaudePlans.FirstOrDefault(p => recorded.Contains(p.Match, StringComparison.Ordinal)) is { } plan)
            return new AgentPlan(plan.Name, plan.Monthly);
        var name = type.Replace("claude_", "").Replace('_', ' ').Trim();
        return name.Length == 0 ? null : new AgentPlan(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name), null);
    }

    public static AgentPlan? CodexPlan(string? planType, AgentPriceList? list = null)
    {
        list ??= List;
        var raw = planType?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return null;
        if (list.CodexPlans.FirstOrDefault(p => p.Match == raw) is { } plan) return new AgentPlan(plan.Name, plan.Monthly);
        return new AgentPlan(char.ToUpperInvariant(raw[0]) + raw[1..], null);
    }
}
