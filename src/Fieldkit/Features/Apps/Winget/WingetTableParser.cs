// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Winget;

/// <summary>One fixed-width table printed by winget (list, search, upgrade, source list, pin list).</summary>
/// <param name="Headers">Column titles as printed (localized).</param>
/// <param name="ColumnStarts">Start cell of each column, from the header line.</param>
/// <param name="Rows">Cell values per row, trimmed; one entry per column.</param>
/// <param name="Preamble">Message lines printed right before the header (e.g. "The following packages
/// have an upgrade available, but require explicit targeting for upgrade:").</param>
public sealed record WingetTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<int> ColumnStarts,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> Preamble);

/// <summary>Every table in an output plus the message lines around them (footers, notices, errors).</summary>
public sealed record WingetTables(IReadOnlyList<WingetTable> Tables, IReadOnlyList<string> Messages)
{
    public static readonly WingetTables Empty = new([], []);
}

/// <summary>
/// Parses winget's fixed-width tables without depending on the display language: a table is a
/// header line followed by a rule of dashes; column offsets come from where each header word
/// starts (in terminal cells, so wide characters line up); rows continue until a line that does
/// not fit the columns (the "31 upgrades available." footer, a notice, the next table).
/// </summary>
public static class WingetTableParser
{
    /// <param name="maxColumns">Header words beyond this count are merged with their neighbour
    /// across single spaces (multi-word titles such as "Pin type").</param>
    /// <param name="requiredCells">How many leading cells a row must fill to count as a row.</param>
    public static WingetTables Parse(string output, int maxColumns = 5, int requiredCells = 2)
    {
        var lines = WingetText.CleanLines(output);
        var tables = new List<WingetTable>();
        var messages = new List<string>();
        var pending = new List<string>(); // non-table lines since the last table
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (i + 1 < lines.Count && IsRule(lines[i + 1]) && line.Trim().Length > 0 && !IsRule(line))
            {
                var sample = lines.Skip(i + 2).Take(50).TakeWhile(l => l.Trim().Length > 0 && !IsRule(l)).ToList();
                var (headers, starts) = HeaderColumns(line, maxColumns, sample);
                var preamble = pending.Count > 0 ? new List<string> { pending[^1] } : [];
                messages.AddRange(pending);
                pending.Clear();
                var rows = new List<IReadOnlyList<string>>();
                var j = i + 2;
                while (j < lines.Count)
                {
                    var row = lines[j];
                    if (row.Trim().Length == 0 || WingetText.IsProgressBar(row) && !LooksAligned(row, starts))
                    {
                        j++;
                        continue;
                    }
                    if (j + 1 < lines.Count && IsRule(lines[j + 1])) break; // header of the next table
                    var cells = Cells(row, starts);
                    if (!IsRow(row, cells, starts, requiredCells)) break;
                    rows.Add(cells);
                    j++;
                }
                tables.Add(new WingetTable(headers, starts, rows, preamble));
                i = j;
                continue;
            }
            if (!IsRule(line) && !WingetText.IsNoise(line)) pending.Add(line.Trim());
            i++;
        }
        messages.AddRange(pending);
        return new WingetTables(tables, messages);
    }

    /// <summary>The dashed line under a table header.</summary>
    public static bool IsRule(string line)
    {
        var t = line.Trim();
        return t.Length >= 3 && t.All(c => c == '-');
    }

    /// <summary>Column titles and their start cells. Titles are separated by runs of spaces; if there
    /// are more than <paramref name="maxColumns"/>, words are merged across the boundaries that the
    /// rows below write through (a multi-word title such as "Pin type"), closest gaps first.</summary>
    public static (List<string> Headers, List<int> Starts) HeaderColumns(string header, int maxColumns,
        IReadOnlyList<string>? sampleRows = null)
    {
        var words = new List<(int Start, int End, string Text)>();
        var cell = 0;
        int? wordStart = null;
        var text = new System.Text.StringBuilder();
        foreach (var rune in header.EnumerateRunes())
        {
            var w = WingetText.CellWidth(rune);
            var space = System.Text.Rune.IsWhiteSpace(rune);
            if (!space && wordStart is null)
            {
                wordStart = cell;
                text.Clear();
            }
            if (space && wordStart is not null)
            {
                words.Add((wordStart.Value, cell, text.ToString()));
                wordStart = null;
            }
            if (!space) text.Append(rune.ToString());
            cell += w;
        }
        if (wordStart is not null) words.Add((wordStart.Value, cell, text.ToString()));

        while (words.Count > Math.Max(1, maxColumns))
        {
            var best = 1;
            var bestCrossings = -1;
            var bestGap = int.MaxValue;
            for (var k = 1; k < words.Count; k++)
            {
                var gap = words[k].Start - words[k - 1].End;
                var crossings = sampleRows?.Count(r => WingetText.RuneAt(r, words[k].Start - 1) is { } c && !System.Text.Rune.IsWhiteSpace(c)) ?? 0;
                if (crossings > bestCrossings || crossings == bestCrossings && gap < bestGap)
                {
                    bestCrossings = crossings;
                    bestGap = gap;
                    best = k;
                }
            }
            var a = words[best - 1];
            var b = words[best];
            words[best - 1] = (a.Start, b.End, a.Text + " " + b.Text);
            words.RemoveAt(best);
        }
        return (words.Select(w => w.Text).ToList(), words.Select(w => w.Start).ToList());
    }

    /// <summary>Cell values of a line for the given column starts.</summary>
    public static List<string> Cells(string line, IReadOnlyList<int> starts)
    {
        var cells = new List<string>(starts.Count);
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : int.MaxValue;
            cells.Add(WingetText.Slice(line, starts[k], end));
        }
        return cells;
    }

    private static bool LooksAligned(string line, IReadOnlyList<int> starts) =>
        starts.Count > 1 && Gaps(line, starts);

    /// <summary>A row fills its leading cells and leaves a space in front of every column it reaches.
    /// Sentences that happen to be long enough to reach a column almost never pass both tests.</summary>
    private static bool IsRow(string line, IReadOnlyList<string> cells, IReadOnlyList<int> starts, int requiredCells)
    {
        for (var k = 0; k < Math.Min(requiredCells, cells.Count); k++)
        {
            if (cells[k].Length == 0) return false;
        }
        var trimmed = line.TrimEnd();
        if (trimmed.EndsWith(':')) return false;
        return Gaps(line, starts);
    }

    private static bool Gaps(string line, IReadOnlyList<int> starts)
    {
        var width = WingetText.DisplayWidth(line);
        for (var k = 1; k < starts.Count; k++)
        {
            if (starts[k] >= width) break;
            var before = WingetText.RuneAt(line, starts[k] - 1);
            if (before is { } r && !System.Text.Rune.IsWhiteSpace(r)) return false;
        }
        return true;
    }
}
