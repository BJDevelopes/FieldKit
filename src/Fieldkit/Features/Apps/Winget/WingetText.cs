// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>
/// Text helpers for winget console output: VT escape sequences, carriage-return redraws
/// (spinners and progress bars overwrite the same line) and the display width winget uses
/// to pad its tables (East Asian wide characters take two cells).
/// </summary>
public static partial class WingetText
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[()][0-9A-Za-z]")]
    private static partial Regex AnsiRegex();

    /// <summary>Removes VT/ANSI control sequences.</summary>
    public static string StripAnsi(string text) =>
        text.IndexOf('\x1B') < 0 ? text : AnsiRegex().Replace(text, string.Empty);

    /// <summary>
    /// The lines a terminal would end up showing. Each line keeps only its last non-blank
    /// carriage-return segment (what is left after spinner/progress redraws), trailing spaces
    /// are trimmed and spinner-only lines become empty.
    /// </summary>
    public static List<string> CleanLines(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(output)) return result;
        foreach (var raw in StripAnsi(output).Split('\n'))
        {
            var line = LastSegment(raw).TrimEnd();
            if (IsSpinner(line)) line = string.Empty;
            result.Add(line);
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static string LastSegment(string raw)
    {
        if (raw.IndexOf('\r') < 0) return raw;
        var segments = raw.Split('\r');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Trim().Length > 0) return segments[i];
        }
        return string.Empty;
    }

    /// <summary>The "- \ | /" busy indicator winget draws while it works.</summary>
    public static bool IsSpinner(string line)
    {
        var t = line.Trim();
        return t is "-" or "\\" or "|" or "/";
    }

    /// <summary>A progress bar line ("  ██████▒▒▒  12.0 MB / 40.0 MB", "  ███▒▒  35%").</summary>
    public static bool IsProgressBar(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        if (t.Contains('█') || t.Contains('▒')) return true;
        var symbols = 0;
        var total = 0;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            if ("#=->.:%/0123456789,".Contains(c)) symbols++;
        }
        return total > 0 && symbols * 100 / total > 85;
    }

    /// <summary>Blank lines, spinners and progress bars: output that carries no message.</summary>
    public static bool IsNoise(string line) => line.Trim().Length == 0 || IsSpinner(line) || IsProgressBar(line);

    /// <summary>Total display width in terminal cells.</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes()) width += CellWidth(rune);
        return width;
    }

    /// <summary>
    /// The text that occupies cells [<paramref name="start"/>, <paramref name="end"/>) of a line,
    /// trimmed. A wide character belongs to the column its first cell falls in.
    /// </summary>
    public static string Slice(string line, int start, int end)
    {
        var sb = new StringBuilder();
        var cell = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var w = CellWidth(rune);
            if (cell >= end) break;
            if (cell >= start) sb.Append(rune.ToString());
            cell += w;
        }
        return sb.ToString().Trim();
    }

    /// <summary>The character drawn at a cell, or null past the end of the line.</summary>
    public static Rune? RuneAt(string line, int targetCell)
    {
        var cell = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var w = CellWidth(rune);
            if (targetCell >= cell && targetCell < cell + Math.Max(w, 1)) return rune;
            cell += w;
        }
        return null;
    }

    /// <summary>Terminal cells used by one character: 0 for controls and combining marks,
    /// 2 for East Asian wide/fullwidth and emoji, 1 otherwise.</summary>
    public static int CellWidth(Rune rune)
    {
        var v = rune.Value;
        if (v < 0x20 || v is >= 0x7F and < 0xA0) return 0;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return IsWide(v) ? 2 : 1;
    }

    private static bool IsWide(int v) =>
        v is (>= 0x1100 and <= 0x115F)
            or (>= 0x231A and <= 0x231B)
            or (>= 0x2329 and <= 0x232A)
            or (>= 0x23E9 and <= 0x23EC)
            or 0x23F0 or 0x23F3
            or (>= 0x25FD and <= 0x25FE)
            or (>= 0x2614 and <= 0x2615)
            or (>= 0x2648 and <= 0x2653)
            or 0x267F or 0x2693 or 0x26A1
            or (>= 0x26AA and <= 0x26AB)
            or (>= 0x26BD and <= 0x26BE)
            or (>= 0x26C4 and <= 0x26C5)
            or 0x26CE or 0x26D4 or 0x26EA
            or (>= 0x26F2 and <= 0x26F3)
            or 0x26F5 or 0x26FA or 0x26FD or 0x2705
            or (>= 0x270A and <= 0x270B)
            or 0x2728 or 0x274C or 0x274E
            or (>= 0x2753 and <= 0x2755)
            or 0x2757
            or (>= 0x2795 and <= 0x2797)
            or 0x27B0 or 0x27BF
            or (>= 0x2B1B and <= 0x2B1C)
            or 0x2B50 or 0x2B55
            or (>= 0x2E80 and <= 0x303E)
            or (>= 0x3041 and <= 0x33FF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xA000 and <= 0xA4CF)
            or (>= 0xA960 and <= 0xA97F)
            or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE10 and <= 0xFE19)
            or (>= 0xFE30 and <= 0xFE6F)
            or (>= 0xFF00 and <= 0xFF60)
            or (>= 0xFFE0 and <= 0xFFE6)
            or (>= 0x16FE0 and <= 0x16FE4)
            or (>= 0x17000 and <= 0x18CFF)
            or (>= 0x1B000 and <= 0x1B2FF)
            or 0x1F004 or 0x1F0CF or 0x1F18E
            or (>= 0x1F191 and <= 0x1F19A)
            or (>= 0x1F200 and <= 0x1F2FF)
            or (>= 0x1F300 and <= 0x1F64F)
            or (>= 0x1F680 and <= 0x1F6FF)
            or (>= 0x1F7E0 and <= 0x1F7EB)
            or (>= 0x1F90C and <= 0x1F9FF)
            or (>= 0x1FA70 and <= 0x1FAFF)
            or (>= 0x20000 and <= 0x2FFFD)
            or (>= 0x30000 and <= 0x3FFFD);
}
