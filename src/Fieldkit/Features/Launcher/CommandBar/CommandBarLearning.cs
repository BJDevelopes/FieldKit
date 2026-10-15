// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// What the bar noticed this session: which row was chosen after which few letters, so typing them
/// again lands on it (port of upstream CommandBarQueryMemory). In memory only; a tie-breaker that
/// can never turn a weaker match into a better answer.
/// </summary>
public sealed class CommandBarQueryMemory
{
    public const int QueryLimit = 60;
    public const int IdsPerQuery = 4;
    public const int LongestPrefix = 12;
    public const int MaximumBoost = 3;

    private sealed class Pick
    {
        public int Count;
        public int Step;
    }

    private readonly Dictionary<string, Dictionary<string, Pick>> _picks = new(StringComparer.Ordinal);

    public bool IsEmpty => _picks.Count == 0;

    /// <summary>Every leading piece of what was typed, folded like the ranking folds.</summary>
    public static List<string> Prefixes(string query)
    {
        var normalized = CommandBarSearch.Normalize(query);
        var result = new List<string>();
        if (normalized.Length == 0) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var length = 1; length <= Math.Min(normalized.Length, LongestPrefix); length++)
        {
            var prefix = normalized[..length].Trim();
            if (prefix.Length > 0 && seen.Add(prefix)) result.Add(prefix);
        }
        return result;
    }

    /// <summary>Notes that this row answered what was typed. <paramref name="step"/> only has to grow.</summary>
    public void Record(string query, string id, int step)
    {
        foreach (var prefix in Prefixes(query))
        {
            if (!_picks.TryGetValue(prefix, out var forPrefix)) _picks[prefix] = forPrefix = new Dictionary<string, Pick>(StringComparer.Ordinal);
            if (!forPrefix.TryGetValue(id, out var pick)) forPrefix[id] = pick = new Pick { Step = step };
            pick.Count = Math.Min(pick.Count + 1, 99);
            pick.Step = step;
            if (forPrefix.Count > IdsPerQuery)
            {
                foreach (var key in forPrefix.OrderBy(p => p.Value.Count).ThenBy(p => p.Value.Step)
                             .Take(forPrefix.Count - IdsPerQuery).Select(p => p.Key).ToList())
                    forPrefix.Remove(key);
            }
        }
        if (_picks.Count <= QueryLimit) return;
        foreach (var key in _picks.OrderBy(p => p.Value.Values.Select(v => v.Step).DefaultIfEmpty(0).Max())
                     .Take(_picks.Count - QueryLimit).Select(p => p.Key).ToList())
            _picks.Remove(key);
    }

    public int Boost(string query, string id) => BoostNormalized(CommandBarSearch.Normalize(query), id);

    public int BoostNormalized(string normalizedQuery, string id)
    {
        if (normalizedQuery.Length == 0 || !_picks.TryGetValue(normalizedQuery, out var forPrefix) || !forPrefix.TryGetValue(id, out var pick)) return 0;
        return Math.Min(pick.Count, 3) * (MaximumBoost / 3);
    }

    public void Forget(string id)
    {
        foreach (var prefix in _picks.Keys.ToList())
        {
            var forPrefix = _picks[prefix];
            if (!forPrefix.Remove(id)) continue;
            if (forPrefix.Count == 0) _picks.Remove(prefix);
        }
    }

    public void Clear() => _picks.Clear();
}

/// <summary>How often and how recently one row ran (unix seconds).</summary>
public sealed record CommandBarUse(int Count, double LastUsed);

/// <summary>Usage bookkeeping behind "the order privileges what the person uses most" (port).</summary>
public static class CommandBarUsage
{
    public const int StoredIdLimit = 200;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Dictionary<string, CommandBarUse> Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, CommandBarUse>>(raw, Json);
            return map is null ? new(StringComparer.Ordinal) : new(map, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public static string Encode(Dictionary<string, CommandBarUse> usage) => JsonSerializer.Serialize(usage, Json);

    /// <summary>The map after one more run of <paramref name="id"/>; oldest ids fall off past the cap.</summary>
    public static Dictionary<string, CommandBarUse> Recording(Dictionary<string, CommandBarUse> usage, string id, double now)
    {
        var next = new Dictionary<string, CommandBarUse>(usage, StringComparer.Ordinal);
        var use = next.TryGetValue(id, out var existing) ? existing : new CommandBarUse(0, now);
        next[id] = new CommandBarUse(Math.Min(use.Count + 1, 999), now);
        if (next.Count > StoredIdLimit)
        {
            foreach (var key in next.OrderBy(p => p.Value.LastUsed).Take(next.Count - StoredIdLimit).Select(p => p.Key).ToList())
                next.Remove(key);
        }
        return next;
    }

    /// <summary>The boost habit earns, capped well below a literal text hit.</summary>
    public static int Boost(CommandBarUse? use, double now)
    {
        if (use is null || use.Count <= 0) return 0;
        var age = Math.Max(0, now - use.LastUsed);
        var weight = age switch
        {
            < 2 * 3600 => 12,
            < 48 * 3600 => 8,
            < 14 * 86400 => 5,
            _ => 2,
        };
        return Math.Min(use.Count, 40) * weight;
    }

    /// <summary>What an empty bar suggests: most used first, then curated ones, never more than <paramref name="limit"/>.</summary>
    public static List<string> SuggestionIds(Dictionary<string, CommandBarUse> usage, IEnumerable<string> available,
        IEnumerable<string> curated, int limit)
    {
        var availableSet = new HashSet<string>(available, StringComparer.Ordinal);
        var picked = usage.Where(p => availableSet.Contains(p.Key) && p.Value.Count > 0)
            .OrderByDescending(p => p.Value.Count).ThenByDescending(p => p.Value.LastUsed)
            .Select(p => p.Key).Take(limit).ToList();
        foreach (var id in curated)
        {
            if (picked.Count >= limit) break;
            if (availableSet.Contains(id) && !picked.Contains(id)) picked.Add(id);
        }
        return picked;
    }

    /// <summary>A category keeps catalog order until used; used rows then lead by frequency and recency.</summary>
    public static List<string> CategoryIds(Dictionary<string, CommandBarUse> usage, IReadOnlyList<string> available)
    {
        return available.Select((id, offset) => (id, offset, use: usage.TryGetValue(id, out var u) && u.Count > 0 ? u : null))
            .OrderBy(x => x.use is null ? 1 : 0)
            .ThenByDescending(x => x.use?.Count ?? 0)
            .ThenByDescending(x => x.use?.LastUsed ?? 0)
            .ThenBy(x => x.offset)
            .Select(x => x.id)
            .ToList();
    }
}

/// <summary>
/// Which result won after a typed query, saved across launches as keyed HMAC digests of the
/// prefixes (never the typed text) — port of upstream CommandBarQueryHabits.
/// </summary>
public static class CommandBarQueryHabits
{
    public const int StoredQueryLimit = 320;
    private const int ResultLimit = 4;

    /// <summary>The keyed prefixes of one query, with their length (specificity).</summary>
    public sealed record PreparedQuery(IReadOnlyList<(string Digest, int Specificity)> Keys)
    {
        public static readonly PreparedQuery Empty = new([]);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CommandBarUse>>>(raw, Json) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public static string Encode(Dictionary<string, Dictionary<string, CommandBarUse>> store) => JsonSerializer.Serialize(store, Json);

    public static PreparedQuery Prepare(string query, byte[]? key)
    {
        var normalized = CommandBarSearch.Normalize(query);
        if (normalized.Length > 24) normalized = normalized[..24];
        if (key is not { Length: 32 } || normalized.Length == 0) return PreparedQuery.Empty;
        using var hmac = new HMACSHA256(key);
        var keys = new List<(string, int)>();
        for (var length = 1; length <= normalized.Length; length++)
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized[..length]));
            keys.Add((Convert.ToHexString(hash, 0, 12).ToLowerInvariant(), length));
        }
        return new PreparedQuery(keys);
    }

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Recording(
        Dictionary<string, Dictionary<string, CommandBarUse>> store, PreparedQuery prepared, string resultId, double now)
    {
        var next = new Dictionary<string, Dictionary<string, CommandBarUse>>(store, StringComparer.Ordinal);
        foreach (var (digest, _) in prepared.Keys)
        {
            var choices = next.TryGetValue(digest, out var existing)
                ? new Dictionary<string, CommandBarUse>(existing, StringComparer.Ordinal)
                : new Dictionary<string, CommandBarUse>(StringComparer.Ordinal);
            var use = choices.TryGetValue(resultId, out var u) ? u : new CommandBarUse(0, now);
            choices[resultId] = new CommandBarUse(Math.Min(use.Count + 1, 999), now);
            if (choices.Count > ResultLimit)
            {
                foreach (var stale in choices.OrderBy(c => c.Value.LastUsed).Take(choices.Count - ResultLimit).Select(c => c.Key).ToList())
                    choices.Remove(stale);
            }
            next[digest] = choices;
        }
        if (next.Count > StoredQueryLimit)
        {
            foreach (var stale in next.OrderBy(e => e.Value.Values.Select(v => v.LastUsed).DefaultIfEmpty(0).Max())
                         .Take(next.Count - StoredQueryLimit).Select(e => e.Key).ToList())
                next.Remove(stale);
        }
        return next;
    }

    /// <summary>Learned priority for a row under this query: habit (x5) plus specificity, capped.</summary>
    public static int Boost(string resultId, PreparedQuery prepared, Dictionary<string, Dictionary<string, CommandBarUse>> store, double now)
    {
        var best = 0;
        foreach (var (digest, specificity) in prepared.Keys)
        {
            if (!store.TryGetValue(digest, out var choices) || !choices.TryGetValue(resultId, out var use)) continue;
            var learned = CommandBarUsage.Boost(use, now) * 5 + specificity * 6;
            best = Math.Max(best, Math.Min(learned, 720));
        }
        return best;
    }

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Removing(string resultId,
        Dictionary<string, Dictionary<string, CommandBarUse>> store)
    {
        var result = new Dictionary<string, Dictionary<string, CommandBarUse>>(StringComparer.Ordinal);
        foreach (var (digest, choices) in store)
        {
            var copy = new Dictionary<string, CommandBarUse>(choices, StringComparer.Ordinal);
            copy.Remove(resultId);
            if (copy.Count > 0) result[digest] = copy;
        }
        return result;
    }

    /// <summary>A fresh random installation key (32 bytes).</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
}
