// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Fieldkit.Core;

namespace Fieldkit.Features.QuickTools;

public enum MdBlockKind { Heading, Bullet, Ordered, Quote, Code, Rule, Paragraph }

/// <summary>A Markdown block: kind, nesting level (heading level, list indent, quote depth), number and text.</summary>
public sealed record MdBlock(MdBlockKind Kind, int Level, int Number, string Text);

[Flags]
public enum MdStyle { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }

public sealed record MdSpan(string Text, MdStyle Style, string? Url = null);

/// <summary>
/// The small Markdown subset the Scratchpad preview renders (upstream: headings, lists,
/// quotes, code blocks, rules, paragraphs; bold, italic, strikethrough, code, links).
/// Parsing is pure; <see cref="ToFlowDocument"/> builds the WPF view.
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^(?<indent>\s*)[-*+]\s+(?<text>.*)$")]
    private static partial Regex BulletRe();

    [GeneratedRegex(@"^(?<indent>\s*)(?<n>\d+)[.)]\s+(?<text>.*)$")]
    private static partial Regex OrderedRe();

    [GeneratedRegex(@"^\s*(?<marks>(>\s?)+)(?<text>.*)$")]
    private static partial Regex QuoteRe();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRe();

    public static List<MdBlock> Parse(string text)
    {
        var blocks = new List<MdBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, 0, 0, string.Join("\n", paragraph)));
            paragraph.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                blocks.Add(new MdBlock(MdBlockKind.Code, 0, 0, string.Join("\n", code).TrimEnd('\n')));
                continue;
            }
            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (paragraph.Count == 0 && (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')))
            {
                var code = new List<string>();
                while (i < lines.Length && (lines[i].StartsWith("    ", StringComparison.Ordinal) || lines[i].StartsWith('\t') || lines[i].Trim().Length == 0))
                {
                    code.Add(lines[i].StartsWith('\t') ? lines[i][1..] : lines[i].Length >= 4 ? lines[i][4..] : string.Empty);
                    i++;
                }
                i--;
                blocks.Add(new MdBlock(MdBlockKind.Code, 0, 0, string.Join("\n", code).TrimEnd('\n')));
                continue;
            }
            if (RuleRe().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Rule, 0, 0, string.Empty));
                continue;
            }
            if (HeadingRe().Match(line) is { Success: true } h)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, h.Groups["hashes"].Length, 0, h.Groups["text"].Value.TrimEnd('#', ' ')));
                continue;
            }
            if (BulletRe().Match(line) is { Success: true } b)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, IndentLevel(b.Groups["indent"].Value), 0, b.Groups["text"].Value));
                continue;
            }
            if (OrderedRe().Match(line) is { Success: true } o)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Ordered, IndentLevel(o.Groups["indent"].Value), int.Parse(o.Groups["n"].Value[..Math.Min(9, o.Groups["n"].Value.Length)]), o.Groups["text"].Value));
                continue;
            }
            if (QuoteRe().Match(line) is { Success: true } q)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, q.Groups["marks"].Value.Count(c => c == '>'), 0, q.Groups["text"].Value));
                continue;
            }
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        return blocks;
    }

    private static int IndentLevel(string indent) => indent.Replace("\t", "  ").Length / 2;

    [GeneratedRegex(@"`(?<code>[^`\n]+)`|\[(?<label>[^\]\n]+)\]\((?<url>[^)\s]+)\)|(?<m>\*\*|__|~~|\*|_)")]
    private static partial Regex InlineRe();

    /// <summary>Splits inline Markdown into styled spans.</summary>
    public static List<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        var style = MdStyle.None;
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new MdSpan(buffer.ToString(), style));
            buffer.Clear();
        }
        // Only toggle a marker that has a closing partner later in the text.
        var position = 0;
        foreach (Match m in InlineRe().Matches(text))
        {
            if (m.Index < position) continue;
            buffer.Append(text, position, m.Index - position);
            position = m.Index + m.Length;
            if (m.Groups["code"].Success)
            {
                Flush();
                spans.Add(new MdSpan(m.Groups["code"].Value, style | MdStyle.Code));
                continue;
            }
            if (m.Groups["label"].Success)
            {
                Flush();
                spans.Add(new MdSpan(m.Groups["label"].Value, style, m.Groups["url"].Value));
                continue;
            }
            var marker = m.Groups["m"].Value;
            var flag = marker switch { "**" or "__" => MdStyle.Bold, "~~" => MdStyle.Strike, _ => MdStyle.Italic };
            var opening = !style.HasFlag(flag);
            if (opening && text.IndexOf(marker, position, StringComparison.Ordinal) < 0)
            {
                buffer.Append(marker); // unmatched: literal
                continue;
            }
            Flush();
            style ^= flag;
        }
        buffer.Append(text, position, text.Length - position);
        Flush();
        return spans;
    }

    /// <summary>Builds the read-only preview document.</summary>
    public static FlowDocument ToFlowDocument(string text, double baseSize)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = baseSize,
            PagePadding = new Thickness(8, 4, 8, 8),
            LineHeight = baseSize * 1.35 + 2,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimary");
        MdBlock? previous = null;
        foreach (var block in Parse(text))
        {
            var p = new Paragraph();
            var tight = previous is not null && previous.Kind == block.Kind && block.Kind is MdBlockKind.Bullet or MdBlockKind.Ordered or MdBlockKind.Quote;
            p.Margin = new Thickness(0, previous is null ? 0 : tight ? 2 : baseSize * 0.8, 0, 0);
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    p.FontSize = baseSize + (block.Level == 1 ? 5 : block.Level == 2 ? 3 : 1);
                    p.FontWeight = FontWeights.SemiBold;
                    AddInline(p, block.Text, baseSize);
                    break;
                case MdBlockKind.Bullet:
                    p.Inlines.Add(new Run(new string(' ', block.Level * 2) + "• "));
                    AddInline(p, block.Text, baseSize);
                    break;
                case MdBlockKind.Ordered:
                    p.Inlines.Add(new Run(new string(' ', block.Level * 2) + $"{block.Number}. "));
                    AddInline(p, block.Text, baseSize);
                    break;
                case MdBlockKind.Quote:
                    p.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondary");
                    p.Inlines.Add(new Run(string.Concat(Enumerable.Repeat("▏ ", Math.Max(1, block.Level)))));
                    AddInline(p, block.Text, baseSize);
                    break;
                case MdBlockKind.Code:
                    p.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    p.FontSize = Math.Max(9, baseSize - 1);
                    p.Padding = new Thickness(6, 4, 6, 4);
                    p.Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128));
                    p.Inlines.Add(new Run(block.Text));
                    break;
                case MdBlockKind.Rule:
                    p.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondary");
                    p.Inlines.Add(new Run(new string('─', 24)));
                    break;
                default:
                    var lines = block.Text.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) p.Inlines.Add(new LineBreak());
                        AddInline(p, lines[i], baseSize);
                    }
                    break;
            }
            doc.Blocks.Add(p);
            previous = block;
        }
        return doc;
    }

    private static void AddInline(Paragraph p, string text, double baseSize)
    {
        foreach (var span in ParseInline(text))
        {
            Inline inline;
            if (span.Url is { } url)
            {
                var link = new Hyperlink(new Run(span.Text));
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                link.Click += (_, _) =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Warn($"Opening link failed: {ex.Message}"); }
                };
                inline = link;
            }
            else
            {
                inline = new Run(span.Text);
            }
            if (span.Style.HasFlag(MdStyle.Bold)) inline.FontWeight = FontWeights.SemiBold;
            if (span.Style.HasFlag(MdStyle.Italic)) inline.FontStyle = FontStyles.Italic;
            if (span.Style.HasFlag(MdStyle.Strike)) inline.TextDecorations = TextDecorations.Strikethrough;
            if (span.Style.HasFlag(MdStyle.Code))
            {
                inline.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                inline.FontSize = Math.Max(9, baseSize - 1);
                inline.Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
            }
            p.Inlines.Add(inline);
        }
    }
}
