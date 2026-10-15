// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// One searchable row offered to the ranking pass. <see cref="Boost"/> carries whatever the
/// caller wants to privilege (usage, pins) so the ranking itself stays a pure function of text.
/// </summary>
public readonly record struct CommandBarCandidate(
    int Index,
    string NormalizedTitle,
    string NormalizedKeywords,
    int Priority = 0,
    int Boost = 0)
{
    public static CommandBarCandidate Create(int index, string title, string keywords = "", int priority = 0, int boost = 0) =>
        new(index, CommandBarSearch.Normalize(title), CommandBarSearch.Normalize(keywords), priority, boost);
}

/// <summary>"brightness 40" split into the verb and its value.</summary>
public readonly record struct CommandBarNumberSplit(string Text, int? Number);

/// <summary>
/// Pure text matching and ranking for the Command Bar (port of upstream CommandBarSearch).
/// Typing mistakes must not break it: a token also matches as an in-order subsequence of a
/// word ("brlho" finds "brilho") and, as a last resort, within one edit of a word.
/// </summary>
public static class CommandBarSearch
{
    /// <summary>A leading colon scopes the search to emoji (":fire").</summary>
    public static string? EmojiQuery(string query)
    {
        var trimmed = query.Trim();
        return trimmed.StartsWith(':') ? trimmed[1..].Trim() : null;
    }

    /// <summary>Case, accent and width differences never matter; whitespace runs collapse to one space.
    /// Folded without a culture so a Turkish locale cannot change what "insta" matches.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // An unpaired surrogate cannot be normalized; fold what can be folded.
            decomposed = value;
        }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (IsInvisible(ch)) continue;
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            sb.Append(FoldWidth(ch));
        }
        string folded;
        try
        {
            folded = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            folded = sb.ToString().ToLowerInvariant();
        }
        return string.Join(' ', folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Characters that take no space and nobody can type (direction marks, zero-width joiners, soft hyphens).</summary>
    private static bool IsInvisible(char ch) =>
        ch >= '­' && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format;

    /// <summary>Full-width ASCII and the ideographic space fold to their half-width forms.</summary>
    private static char FoldWidth(char ch) => ch switch
    {
        >= '！' and <= '～' => (char)(ch - 0xFEE0),
        '　' => ' ',
        _ => ch,
    };

    /// <summary>One character folded on its own (keeps positions aligned with the original title).</summary>
    private static char FoldChar(char ch)
    {
        if (IsInvisible(ch) || char.IsSurrogate(ch)) return ch;
        var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
        var first = decomposed.Length > 0 ? decomposed[0] : ch;
        return char.ToLowerInvariant(FoldWidth(first));
    }

    /// <summary>Whether the query names the verb of a format like "Quit {0}" (keeps rare commands out until asked for).</summary>
    public static bool MatchesVerb(string query, string format)
    {
        var verb = Normalize(format.Replace("{0}", " ")).Trim();
        if (verb.Length == 0) return false;
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0) return false;
        foreach (var token in normalizedQuery.Split(' '))
        {
            if (token.Length < 2) continue;
            foreach (var word in verb.Split(' '))
            {
                if (word.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(word, StringComparison.Ordinal)) return true;
            }
            if (verb.Contains(token, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Where a colour typed on its own goes: first, unless a row already spells what was typed.</summary>
    public static int ColorPreviewIndex(IEnumerable<string> rowTitles, string query)
    {
        var typed = Normalize(query);
        if (typed.Length == 0) return 0;
        return rowTitles.Any(t => Normalize(t).Contains(typed, StringComparison.Ordinal)) ? 1 : 0;
    }

    public static bool Matches(string title, string keywords, string query) => Score(title, keywords, query) is not null;

    /// <summary>Null when the query does not match; otherwise a comparable score.</summary>
    public static int? Score(string title, string keywords, string query) =>
        ScoreNormalized(Normalize(title), Normalize(keywords), Normalize(query));

    /// <summary>The scoring itself, over already folded text.</summary>
    public static int? ScoreNormalized(string normalizedTitle, string normalizedKeywords, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return null;
        var haystack = normalizedKeywords.Length == 0 ? normalizedTitle : normalizedTitle + " " + normalizedKeywords;
        var words = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokenScore = BestTokenScore(token, words, haystack);
            if (tokenScore is null) return null;
            score += tokenScore.Value;
        }
        if (normalizedTitle == normalizedQuery) score += 1200;
        else if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal)) score += 900;
        else if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal)) score += 700;
        else if (normalizedKeywords.Length > 0 && normalizedKeywords.Contains(normalizedQuery, StringComparison.Ordinal)) score += 350;
        return score;
    }

    /// <summary>Indexes of the matching candidates, best first. Deliberate preferences (priority) lead,
    /// then match tier, then score; ties keep the caller's order.</summary>
    public static List<int> RankedIndexes(IReadOnlyList<CommandBarCandidate> candidates, string query)
    {
        var normalizedQuery = Normalize(query);
        var scored = new List<(int Index, int Priority, int Tier, int Score, int Position)>();
        for (var position = 0; position < candidates.Count; position++)
        {
            var c = candidates[position];
            var b = ScoreNormalized(c.NormalizedTitle, c.NormalizedKeywords, normalizedQuery);
            if (b is null) continue;
            scored.Add((c.Index, c.Priority, MatchTier(c.NormalizedTitle, c.NormalizedKeywords, normalizedQuery), b.Value + c.Boost, position));
        }
        scored.Sort((x, y) =>
        {
            if (x.Priority != y.Priority) return y.Priority.CompareTo(x.Priority);
            if (x.Tier != y.Tier) return y.Tier.CompareTo(x.Tier);
            if (x.Score != y.Score) return y.Score.CompareTo(x.Score);
            return x.Position.CompareTo(y.Position);
        });
        return scored.Select(s => s.Index).ToList();
    }

    private static int MatchTier(string title, string keywords, string query)
    {
        if (title == query) return 5;
        if (title.StartsWith(query, StringComparison.Ordinal)) return 4;
        if (title.Contains(query, StringComparison.Ordinal)) return 3;
        if (keywords.Length > 0 && keywords.Contains(query, StringComparison.Ordinal)) return 2;
        return 1;
    }

    private static int? BestTokenScore(string token, string[] words, string haystack)
    {
        int? best = null;
        foreach (var word in words)
        {
            if (word == token) return 140;
            if (word.StartsWith(token, StringComparison.Ordinal)) best = Math.Max(best ?? 0, 80);
        }
        if (best is null && haystack.Contains(token, StringComparison.Ordinal)) best = 44;
        // Fuzzy passes only rescue typing mistakes; digits are values, never typos.
        var fuzzy = !token.All(char.IsDigit);
        if (best is null && token.Length >= 3 && fuzzy && words.Any(w => IsSubsequence(token, w))) best = 24;
        if (best is null && token.Length >= 3 && fuzzy && words.Any(w => IsAdjacentTransposition(token, w))) best = 16;
        if (best is null && token.Length >= 4 && fuzzy && words.Any(w => WithinOneEdit(token, w))) best = 16;
        return best;
    }

    /// <summary>Positions to keep when ids repeat: the first of each, in order.</summary>
    public static List<int> FirstOccurrences(IReadOnlyList<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<int>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (seen.Add(ids[i])) result.Add(i);
        }
        return result;
    }

    /// <summary>Character positions of the title the query literally matched (word starts first).
    /// Fuzzy rescues are never highlighted.</summary>
    public static HashSet<int> HighlightOffsets(string title, string query)
    {
        var offsets = new HashSet<int>();
        if (string.IsNullOrEmpty(title)) return offsets;
        var folded = title.Select(FoldChar).ToArray();
        foreach (var token in Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = FirstIndex(token, folded);
            if (start is null) continue;
            for (var p = start.Value; p < start.Value + token.Length; p++) offsets.Add(p);
        }
        return offsets;
    }

    private static int? FirstIndex(string token, char[] haystack)
    {
        if (token.Length > haystack.Length) return null;
        bool At(int start)
        {
            for (var i = 0; i < token.Length; i++)
            {
                if (haystack[start + i] != token[i]) return false;
            }
            return true;
        }
        for (var start = 0; start <= haystack.Length - token.Length; start++)
        {
            var wordStart = start == 0 || haystack[start - 1] == ' ';
            if (wordStart && At(start)) return start;
        }
        for (var start = 0; start <= haystack.Length - token.Length; start++)
        {
            if (At(start)) return start;
        }
        return null;
    }

    public static bool IsSubsequence(string needle, string word)
    {
        if (needle.Length > word.Length) return false;
        var position = 0;
        foreach (var ch in needle)
        {
            var found = word.IndexOf(ch, position);
            if (found < 0) return false;
            position = found + 1;
        }
        return true;
    }

    /// <summary>True only when one neighbouring pair was typed in reverse order.</summary>
    public static bool IsAdjacentTransposition(string first, string second)
    {
        if (first.Length != second.Length) return false;
        int? firstMismatch = null;
        var swapped = false;
        for (var i = 0; i < first.Length; i++)
        {
            if (first[i] == second[i]) continue;
            if (swapped) return false;
            if (firstMismatch is null)
            {
                firstMismatch = i;
                continue;
            }
            var previous = firstMismatch.Value;
            if (i != previous + 1 || first[previous] != second[i] || first[i] != second[previous]) return false;
            swapped = true;
        }
        return swapped;
    }

    /// <summary>At most one substitution, insertion, deletion or adjacent swap apart.</summary>
    public static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        if (a == b) return true;
        int i = 0, j = 0;
        var edited = false;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }
            if (edited) return false;
            edited = true;
            if (a.Length == b.Length)
            {
                if (i + 1 < a.Length && j + 1 < b.Length && a[i] == b[j + 1] && a[i + 1] == b[j])
                {
                    i += 2;
                    j += 2;
                }
                else
                {
                    i++;
                    j++;
                }
            }
            else if (a.Length > b.Length)
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        return true;
    }

    /// <summary>Splits a trailing bare number ("brightness 40%") off the text. A number alone is a search.</summary>
    public static CommandBarNumberSplit SplitTrailingNumber(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return new CommandBarNumberSplit(query.Trim(), null);
        var digits = tokens[^1];
        if (digits.EndsWith('%')) digits = digits[..^1];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits, out var value))
            return new CommandBarNumberSplit(query.Trim(), null);
        return new CommandBarNumberSplit(string.Join(' ', tokens[..^1]), value);
    }

    /// <summary>The value typed while the bar asks for a number: optional %, clamped into range.</summary>
    public static int? ArgumentValue(string text, int min, int max)
    {
        var digits = text.Trim();
        if (digits.EndsWith('%')) digits = digits[..^1];
        if (digits.Length == 0 || digits.Length > 4 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits, out var value)) return null;
        return Math.Clamp(value, min, max);
    }
}
