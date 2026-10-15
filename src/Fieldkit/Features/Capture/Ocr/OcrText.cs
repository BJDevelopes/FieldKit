// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Fieldkit.Features.Capture;

/// <summary>Turns recognised lines into the text that is copied.</summary>
public static class OcrText
{
    /// <summary>The setting, flipped while Shift is held as the selection ends.</summary>
    public static bool RemoveLineBreaks(bool setting, bool shiftHeld) => setting != shiftHeld;

    /// <summary>
    /// Joins lines with line breaks, or (with <paramref name="removeLineBreaks"/>) into one paragraph:
    /// a word broken with a hyphen at a line end is rejoined, and CJK lines join without a space.
    /// </summary>
    public static string Join(IEnumerable<string> lines, bool removeLineBreaks)
    {
        var clean = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!removeLineBreaks) return string.Join(Environment.NewLine, clean);
        var sb = new StringBuilder();
        foreach (var line in clean)
        {
            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }
            var last = sb[^1];
            if (last == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
            {
                sb.Length--;
                sb.Append(line);
            }
            else if (IsCjk(last) || IsCjk(line[0]))
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
        }
        return sb.ToString();
    }

    /// <summary>Windows OCR separates CJK characters with spaces; drop those between two CJK characters.</summary>
    public static string TidyCjkSpacing(string line)
    {
        if (line.Length < 3) return line;
        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ' && i > 0 && i + 1 < line.Length && IsCjk(line[i - 1]) && IsCjk(line[i + 1])) continue;
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    public static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ' // kana
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= '가' and <= '힯' // hangul
            or >= '豈' and <= '﫿'
            or >= '　' and <= '〿';

    /// <summary>Whether a decoded QR payload is a web link worth offering to open.</summary>
    public static bool IsOpenableLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    /// <summary>
    /// Scale factor so small captures are enlarged for recognition (Windows OCR is more reliable
    /// with letters at least ~20 px tall) and huge ones fit the engine's maximum.
    /// </summary>
    public static double RecognitionScale(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0) return 1;
        var longest = Math.Max(width, height);
        if (longest > maxDimension) return (double)maxDimension / longest;
        if (height < 120 && longest * 3 <= maxDimension) return 3;
        if (height < 400 && longest * 2 <= maxDimension) return 2;
        return 1;
    }
}
