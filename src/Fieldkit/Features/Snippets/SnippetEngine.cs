// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Snippets;

/// <summary>One folder's worth of library rows; an empty folder name is the loose group (no header).</summary>
public sealed record LibrarySection(string Folder, IReadOnlyList<TextSnippet> Snippets);

/// <summary>
/// The pure half of the snippets engine (upstream TextSnippetSupport): typing buffer bookkeeping,
/// trigger matching, validation and the library's grouping/search.
/// </summary>
public static class SnippetEngine
{
    /// <summary>Keystrokes the buffer remembers; longer triggers cannot match.</summary>
    public const int BufferLimit = 64;
    public const int MaxTriggerLength = 40;
    public const int MinTriggerLength = 2;

    /// <summary>Characters that fire an after-delimiter snippet.</summary>
    public static bool IsDelimiter(string typed) => typed.Length > 0 && typed[0] is ' ' or '\t' or '\r' or '\n';

    /// <summary>Triggers cannot contain whitespace (the buffer resets on it) and stay a sane length.</summary>
    public static string SanitizedTrigger(string raw)
    {
        var filtered = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return filtered.Length > MaxTriggerLength ? filtered[..MaxTriggerLength] : filtered;
    }

    /// <summary>Whitespace-only folder names mean no folder.</summary>
    public static string SanitizedFolder(string raw) => raw.Trim();

    /// <summary>The buffer after typing <paramref name="typed"/>; only the newest <see cref="BufferLimit"/> characters stay.</summary>
    public static string Append(string buffer, string typed)
    {
        var next = buffer + typed;
        return next.Length > BufferLimit ? next[^BufferLimit..] : next;
    }

    /// <summary>The buffer after Backspace.</summary>
    public static string Backspace(string buffer)
    {
        if (buffer.Length == 0) return buffer;
        // Remove one text element so a surrogate pair goes as one.
        var info = StringInfo.ParseCombiningCharacters(buffer);
        return buffer[..info[^1]];
    }

    /// <summary>
    /// The snippet whose trigger the buffer just completed for the given mode. The longest trigger wins,
    /// so ";email2" beats ";email".
    /// </summary>
    public static TextSnippet? Match(string buffer, SnippetExpansion expansion, IEnumerable<TextSnippet> snippets)
    {
        TextSnippet? best = null;
        foreach (var snippet in snippets)
        {
            if (!snippet.Enabled || snippet.Expansion != expansion || snippet.Trigger.Length == 0) continue;
            if (!Completes(buffer, snippet.Trigger, snippet.IgnoresCase)) continue;
            if (best is not null && best.Trigger.Length >= snippet.Trigger.Length) continue;
            best = snippet;
        }
        return best;
    }

    /// <summary>Whether the buffer just finished typing the trigger (comparing exactly the trigger's length).</summary>
    public static bool Completes(string buffer, string trigger, bool ignoresCase) =>
        buffer.Length >= trigger.Length &&
        string.Compare(buffer, buffer.Length - trigger.Length, trigger, 0, trigger.Length,
            ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;

    /// <summary>How many characters (as the user sees them) the trigger occupies, i.e. Backspaces to erase it.</summary>
    public static int VisibleLength(string text) => new StringInfo(text).LengthInTextElements;

    public static bool IsDuplicateTrigger(string trigger, bool ignoresCase, IEnumerable<TextSnippet> others)
    {
        var sanitized = SanitizedTrigger(trigger);
        return others.Any(o => ignoresCase || o.IgnoresCase
            ? string.Equals(o.Trigger, sanitized, StringComparison.OrdinalIgnoreCase)
            : string.Equals(o.Trigger, sanitized, StringComparison.Ordinal));
    }

    /// <summary>The validation message for the editor, or null when the snippet can be saved.</summary>
    public static string? Validate(string trigger, string replacement, bool ignoresCase, IEnumerable<TextSnippet> others)
    {
        var sanitized = SanitizedTrigger(trigger);
        if (sanitized.Length < MinTriggerLength) return trigger.Length == 0 ? string.Empty : "The trigger needs at least 2 characters.";
        if (IsDuplicateTrigger(sanitized, ignoresCase, others)) return "Another snippet already uses this trigger.";
        if (replacement.Length == 0) return string.Empty;
        return null;
    }

    /// <summary>
    /// The library for a search: enabled snippets shown in the library, matched on name, trigger, text
    /// and folder (case and accent insensitive), grouped by folder (alphabetical), loose snippets last.
    /// </summary>
    public static List<LibrarySection> LibrarySections(IEnumerable<TextSnippet> snippets, string query)
    {
        var trimmed = query.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;
        bool Contains(string text) => compare.IndexOf(text, trimmed, options) >= 0;
        var visible = snippets.Where(s => s.Enabled && s.ShowsInLibrary &&
            (trimmed.Length == 0 || Contains(s.Name) || Contains(s.Trigger) || Contains(s.Replacement) || Contains(s.Folder))).ToList();
        var sections = visible.Where(s => s.Folder.Length > 0)
            .GroupBy(s => s.Folder)
            .OrderBy(g => g.Key, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase))
            .Select(g => new LibrarySection(g.Key, g.ToList()))
            .ToList();
        var loose = visible.Where(s => s.Folder.Length == 0).ToList();
        if (loose.Count > 0) sections.Add(new LibrarySection(string.Empty, loose));
        return sections;
    }

    /// <summary>The same content as one flat list, in reading order (the keyboard selection walks it).</summary>
    public static List<TextSnippet> LibraryRows(IEnumerable<LibrarySection> sections) => sections.SelectMany(s => s.Snippets).ToList();

    /// <summary>Existing folder names for the editor's suggestions, distinct and alphabetical.</summary>
    public static List<string> FolderSuggestions(IEnumerable<TextSnippet> snippets) =>
        snippets.Select(s => s.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A one-line preview of the replacement (line breaks folded).</summary>
    public static string Preview(string replacement) =>
        replacement.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
