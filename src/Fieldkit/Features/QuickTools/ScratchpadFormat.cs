// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.QuickTools;

public enum FormatMark { Bold, Italic, Strikethrough, Heading, Bullet, Numbered, Quote, Code, Link }

/// <summary>Result of a format mark: the new text and selection.</summary>
public readonly record struct FormatEdit(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// Scratchpad format marks: writes the Markdown a user would type, and applying a mark
/// again removes it (upstream ScratchpadSupport). Pure, unit-tested.
/// </summary>
public static partial class ScratchpadFormat
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<mark>#{1,6} |[-*+] |> |\d+\. )?")]
    private static partial Regex LineMark();

    public static FormatEdit Apply(FormatMark mark, string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        return mark switch
        {
            FormatMark.Bold => Wrap(text, start, length, "**"),
            FormatMark.Italic => Wrap(text, start, length, "*"),
            FormatMark.Strikethrough => Wrap(text, start, length, "~~"),
            FormatMark.Code => Wrap(text, start, length, "`"),
            FormatMark.Link => Link(text, start, length),
            _ => Lines(mark, text, start, length),
        };
    }

    // ---- Inline -----------------------------------------------------------------------

    private static int RunBefore(string text, int index, char c)
    {
        var n = 0;
        while (index - n - 1 >= 0 && text[index - n - 1] == c) n++;
        return n;
    }

    private static int RunAfter(string text, int index, char c)
    {
        var n = 0;
        while (index + n < text.Length && text[index + n] == c) n++;
        return n;
    }

    private static bool Escaped(string text, int index) => index > 0 && text[index - 1] == '\\';

    private static bool MarkerRun(string marker, int run) => marker switch
    {
        "*" => run % 2 == 1,           // 1 or 3 stars: italic (3 = bold + italic)
        "**" => run >= 2,
        _ => run >= marker.Length,
    };

    private static FormatEdit Wrap(string text, int start, int length, string marker)
    {
        // Trim surrounding whitespace out of the selection.
        while (length > 0 && char.IsWhiteSpace(text[start])) { start++; length--; }
        while (length > 0 && char.IsWhiteSpace(text[start + length - 1])) length--;
        var end = start + length;
        var m = marker.Length;
        var c = marker[0];

        // Markers right outside the selection: remove them.
        var before = RunBefore(text, start, c);
        var after = RunAfter(text, end, c);
        if (before >= m && after >= m && MarkerRun(marker, before) && MarkerRun(marker, after) && !Escaped(text, start - before))
        {
            var removed = text.Remove(end, m).Remove(start - m, m);
            return new FormatEdit(removed, start - m, length);
        }

        // Markers inside the selection: strip them.
        if (length >= 2 * m && text.AsSpan(start, m).SequenceEqual(marker) && text.AsSpan(end - m, m).SequenceEqual(marker))
        {
            var innerRunStart = RunAfter(text, start, c);
            var innerRunEnd = RunBefore(text, end, c);
            if (MarkerRun(marker, innerRunStart) && MarkerRun(marker, innerRunEnd) && length > 2 * m - 1)
            {
                var inner = text.Substring(start + m, length - 2 * m);
                return new FormatEdit(text[..start] + inner + text[end..], start, inner.Length);
            }
        }

        if (length == 0)
        {
            var inserted = text.Insert(start, marker + marker);
            return new FormatEdit(inserted, start + m, 0);
        }
        var wrapped = text.Insert(end, marker).Insert(start, marker);
        return new FormatEdit(wrapped, start + m, length);
    }

    [GeneratedRegex(@"(?<!!)\[(?<label>[^\]\n]*)\]\((?<url>[^)\n]*)\)")]
    private static partial Regex LinkPattern();

    private static FormatEdit Link(string text, int start, int length)
    {
        foreach (Match match in LinkPattern().Matches(text))
        {
            if (start >= match.Index && start + length <= match.Index + match.Length)
            {
                var label = match.Groups["label"].Value;
                var replaced = text[..match.Index] + label + text[(match.Index + match.Length)..];
                return new FormatEdit(replaced, match.Index, label.Length);
            }
        }
        var selected = text.Substring(start, length);
        var insert = $"[{selected}](url)";
        var result = text[..start] + insert + text[(start + length)..];
        return new FormatEdit(result, start + selected.Length + 3, 3);
    }

    // ---- Line marks -------------------------------------------------------------------

    private static FormatEdit Lines(FormatMark mark, string text, int start, int length)
    {
        var blockStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start == 0) blockStart = 0;
        var end = start + length;
        if (length > 0 && end > 0 && text[end - 1] == '\n') end--; // a selection ending at a line start excludes that line
        var blockEnd = text.IndexOf('\n', Math.Max(end, blockStart));
        if (blockEnd < 0) blockEnd = text.Length;
        var lines = text[blockStart..blockEnd].Split('\n');
        var allBlank = lines.All(l => l.Trim().Length == 0);

        var parsed = lines.Select(l =>
        {
            var match = LineMark().Match(l);
            return (Indent: match.Groups["indent"].Value, Mark: match.Groups["mark"].Value, Body: l[match.Length..], Line: l);
        }).ToList();
        var targets = parsed.Where(p => allBlank || p.Line.Trim().Length > 0).ToList();

        string Kind(string m) => m.Length == 0 ? "" : m[0] == '#' ? "heading" : m[0] is '-' or '*' or '+' ? "bullet" : m[0] == '>' ? "quote" : "numbered";
        var wanted = mark switch { FormatMark.Heading => "heading", FormatMark.Bullet => "bullet", FormatMark.Quote => "quote", _ => "numbered" };
        var allHave = targets.Count > 0 && targets.All(t => Kind(t.Mark) == wanted);

        string? headingMark = null;
        if (mark == FormatMark.Heading)
        {
            var first = targets.FirstOrDefault().Mark ?? string.Empty;
            headingMark = Kind(first) != "heading" ? "# " : (first.Length - 1) switch
            {
                1 => "## ",
                2 => "### ",
                _ => string.Empty,
            };
        }

        var sb = new StringBuilder();
        var number = 1;
        for (var i = 0; i < parsed.Count; i++)
        {
            var p = parsed[i];
            if (i > 0) sb.Append('\n');
            if (!(allBlank || p.Line.Trim().Length > 0))
            {
                sb.Append(p.Line);
                continue;
            }
            string newMark = mark switch
            {
                FormatMark.Heading => headingMark!,
                _ when allHave => string.Empty,
                FormatMark.Bullet => "- ",
                FormatMark.Quote => "> ",
                _ => $"{number++}. ",
            };
            sb.Append(p.Indent).Append(newMark).Append(p.Body);
        }
        var replaced = sb.ToString();
        var result = text[..blockStart] + replaced + text[blockEnd..];
        if (length == 0 && parsed.Count == 1)
        {
            // Keep the caret at the end of the line, where the user was typing.
            return new FormatEdit(result, blockStart + replaced.Length, 0);
        }
        return new FormatEdit(result, blockStart, replaced.Length);
    }
}
