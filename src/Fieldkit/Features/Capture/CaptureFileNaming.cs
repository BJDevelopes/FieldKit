// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Capture;

/// <summary>File and folder naming for captures (upstream ScreenshotSupport file naming).</summary>
public static partial class CaptureFileNaming
{
    /// <summary>"Screenshot 2026-10-14 at 13.05.22.png": stable, sortable and colon-free.</summary>
    public static string DefaultFileName(string prefix, DateTime date, string extension = "png") =>
        $"{prefix} {date.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture)}.{extension}";

    /// <summary>
    /// Expands a subfolder pattern ("%y-%mo" → "24-03", "%year\%month" → "2024\March").
    /// Slashes nest folders; empty, "." and ".." parts are dropped so the result never escapes the base.
    /// </summary>
    public static string ExpandSubfolder(string? pattern, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return string.Empty;
        var parts = ApplyDateTokens(pattern, date)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => SanitizeComponent(p.Trim()))
            .Where(p => p.Length > 0 && p != "." && p != "..");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Expands a file name pattern: the date tokens plus %#, %##, … for a zero-padded number.</summary>
    public static string ExpandFileNamePattern(string pattern, DateTime date, int number)
    {
        var withDate = ApplyDateTokens(pattern, date);
        var withNumber = NumberToken().Replace(withDate, m =>
            number.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(m.Length - 1, 1), '0'));
        return SanitizeComponent(withNumber.Replace('/', '-').Replace('\\', '-').Replace(':', '-'));
    }

    public static bool PatternUsesNumber(string? pattern) => pattern?.Contains("%#", StringComparison.Ordinal) == true;

    /// <summary>The full file name for a capture: the pattern when set, otherwise the default name.</summary>
    public static string FileName(string prefix, string? pattern, DateTime date, int number, string extension)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return DefaultFileName(prefix, date, extension);
        var stem = ExpandFileNamePattern(pattern.Trim(), date, number).Trim().TrimEnd('.');
        return stem.Length == 0 ? DefaultFileName(prefix, date, extension) : $"{stem}.{extension}";
    }

    /// <summary>First free variant of a name: "name.png", "name 2.png", ….</summary>
    public static string UniqueFileName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i <= 9999; i++)
        {
            var candidate = $"{stem} {i}{ext}";
            if (!exists(candidate)) return candidate;
        }
        return name;
    }

    /// <summary>Replaces characters Windows refuses in file names and trims reserved trailing dots/spaces.</summary>
    public static string SanitizeComponent(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.ToString().TrimEnd(' ', '.');
    }

    private static string ApplyDateTokens(string pattern, DateTime date)
    {
        // Longer tokens first so "%year"/"%month" are not clobbered by "%y"/"%mo".
        (string Token, string Value)[] tokens =
        [
            ("%year", date.Year.ToString("0000", CultureInfo.InvariantCulture)),
            ("%month", CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month)),
            ("%y", (date.Year % 100).ToString("00", CultureInfo.InvariantCulture)),
            ("%mo", date.Month.ToString("00", CultureInfo.InvariantCulture)),
            ("%d", date.Day.ToString("00", CultureInfo.InvariantCulture)),
            ("%h", date.Hour.ToString("00", CultureInfo.InvariantCulture)),
            ("%mi", date.Minute.ToString("00", CultureInfo.InvariantCulture)),
            ("%s", date.Second.ToString("00", CultureInfo.InvariantCulture)),
        ];
        var result = pattern;
        foreach (var (token, value) in tokens) result = result.Replace(token, value, StringComparison.Ordinal);
        return result;
    }

    [GeneratedRegex("%#+")]
    private static partial Regex NumberToken();
}
