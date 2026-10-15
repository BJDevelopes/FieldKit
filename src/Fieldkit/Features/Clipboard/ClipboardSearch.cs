// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>A searchable row: its index in the list, its text and whether it is pinned.</summary>
public sealed record SearchCandidate(int Index, string Text, bool IsPinned)
{
    private string? _normalized;
    private HashSet<string>? _words;

    public string Normalized => _normalized ??= ClipboardSearch.Normalize(Text);

    public HashSet<string> Words => _words ??= ClipboardSearch.Words(Normalized);
}

/// <summary>
/// Clipboard history search (upstream ClipboardHistorySearch): every whitespace-separated token must
/// appear (case, accent and width insensitive); results rank exact, prefix and whole-word matches
/// first, pinned entries get a small boost, ties keep the history order.
/// </summary>
public static class ClipboardSearch
{
    /// <summary>Case, diacritic and width folded, invariant culture, newlines and tabs as spaces, trimmed.</summary>
    public static string Normalize(string value)
    {
        if (value.Length == 0) return value;
        // FormKD splits accents off and folds full-width forms to their plain letters.
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c is '\n' or '\t' or '\r' ? ' ' : char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    public static HashSet<string> Words(string normalized) =>
        new(normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>Non-empty raw tokens of a query.</summary>
    public static List<string> Tokens(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    public static bool Matches(string text, string query)
    {
        var tokens = Tokens(Normalize(query));
        if (tokens.Count == 0) return true;
        var normalized = Normalize(text);
        return tokens.All(t => normalized.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Indexes of the matching candidates, best first. An empty query returns every index in order.</summary>
    public static List<int> Ranked(IReadOnlyList<SearchCandidate> candidates, string query)
    {
        var normalizedQuery = Normalize(query);
        var tokens = Tokens(normalizedQuery);
        if (tokens.Count == 0) return candidates.Select(c => c.Index).ToList();
        return candidates
            .Where(c => tokens.All(t => c.Normalized.Contains(t, StringComparison.Ordinal)))
            .Select(c => (c.Index, Score: Score(c, normalizedQuery, tokens)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Index)
            .ToList();
    }

    private static int Score(SearchCandidate candidate, string query, List<string> tokens)
    {
        var text = candidate.Normalized;
        var score = candidate.IsPinned ? 30 : 0;
        if (text == query) score += 1_200;
        if (text.StartsWith(query, StringComparison.Ordinal)) score += 900;
        if (text.Contains(query, StringComparison.Ordinal)) score += 700;
        foreach (var token in tokens)
        {
            if (candidate.Words.Contains(token)) score += 140;
            else if (candidate.Words.Any(w => w.StartsWith(token, StringComparison.Ordinal))) score += 80;
            else score += 40;
        }
        return score;
    }

    /// <summary>Ranges of <paramref name="text"/> that match any token (case and accent insensitive).</summary>
    public static List<(int Start, int Length)> HighlightRanges(string text, IReadOnlyList<string> tokens)
    {
        var ranges = new List<(int, int)>();
        if (text.Length == 0) return ranges;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;
            var start = 0;
            while (start < text.Length)
            {
                var found = compare.IndexOf(text.AsSpan(start), token.AsSpan(), options, out var length);
                if (found < 0 || length == 0) break;
                found += start;
                ranges.Add((found, length));
                start = found + length;
            }
        }
        return Merge(ranges);
    }

    private static List<(int Start, int Length)> Merge(List<(int Start, int Length)> ranges)
    {
        var merged = new List<(int Start, int Length)>();
        foreach (var (s, l) in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && s <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                var end = Math.Max(last.Start + last.Length, s + l);
                merged[^1] = (last.Start, end - last.Start);
            }
            else
            {
                merged.Add((s, l));
            }
        }
        return merged;
    }
}
