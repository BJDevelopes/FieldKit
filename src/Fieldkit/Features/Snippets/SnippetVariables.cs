// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Snippets;

public enum DateVariableKind { Date, Time, DateTime }

public enum DateVariableStyle { Short, Medium, Long, Full, Iso8601, Custom }

/// <summary>A date/time tag found in a replacement: where it is, and what it says.</summary>
public sealed record DateToken(int Start, int Length, DateVariableKind Kind, string Pattern, string? TimeZoneId);

/// <summary>The text to insert and how far the caret moves back afterwards (for <c>{{cursor}}</c>).</summary>
public sealed record Expansion(string Text, int CaretBack);

/// <summary>
/// Snippet variables (upstream TextSnippetSupport.expand and the date variable builder):
/// <c>{{date}}</c>, <c>{{time}}</c>, <c>{{datetime}}</c>, <c>{{clipboard}}</c>, formatted dates such as
/// <c>{{date:yyyy-MM-dd}}</c> or <c>{{date-tz(America/New_York):HH:mm}}</c>, and (Windows addition)
/// <c>{{cursor}}</c>, where the caret is left after the text is inserted. Unknown tags stay visible.
/// </summary>
public static partial class SnippetVariables
{
    public const string CursorTag = "{{cursor}}";
    private static readonly string[] Kinds = ["datetime", "date", "time"];

    public static bool NeedsClipboard(string replacement) => replacement.Contains("{{clipboard}}", StringComparison.Ordinal);

    /// <summary>Text with a line break goes in by a (transient) paste; a single line is typed.</summary>
    public static bool RequiresPaste(string text) => text.Contains('\n') || text.Contains('\r');

    /// <summary>Expands every variable at <paramref name="utcNow"/>. The clipboard goes in last so pasted text is never re-expanded.</summary>
    public static Expansion Expand(string replacement, DateTime utcNow, string? clipboard, CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        zone ??= TimeZoneInfo.Local;
        var text = replacement;
        if (text.Contains("{{", StringComparison.Ordinal))
        {
            var dateText = IcuDateFormat.Format(utcNow, ResolvedPattern(DateVariableKind.Date, DateVariableStyle.Medium, string.Empty, culture), culture, zone);
            var timeText = IcuDateFormat.Format(utcNow, ResolvedPattern(DateVariableKind.Time, DateVariableStyle.Short, string.Empty, culture), culture, zone);
            text = text.Replace("{{date}}", dateText, StringComparison.Ordinal)
                .Replace("{{time}}", timeText, StringComparison.Ordinal)
                .Replace("{{datetime}}", $"{dateText} {timeText}", StringComparison.Ordinal);
            text = ExpandFormattedDates(text, utcNow, culture, zone);
        }
        // The cursor marker is found before the clipboard goes in, so a copied "{{cursor}}" stays text.
        var clip = clipboard ?? string.Empty;
        var caretBack = 0;
        var marker = text.IndexOf(CursorTag, StringComparison.Ordinal);
        if (marker >= 0)
        {
            var before = text[..marker].Replace("{{clipboard}}", clip, StringComparison.Ordinal);
            var after = text[(marker + CursorTag.Length)..].Replace(CursorTag, string.Empty, StringComparison.Ordinal)
                .Replace("{{clipboard}}", clip, StringComparison.Ordinal);
            caretBack = CaretSteps(after);
            return new Expansion(before + after, caretBack);
        }
        return new Expansion(text.Replace("{{clipboard}}", clip, StringComparison.Ordinal), 0);
    }

    /// <summary>Left-arrow presses that move the caret back over <paramref name="text"/> (a line break counts once).</summary>
    public static int CaretSteps(string text) =>
        new StringInfo(text.Replace("\r\n", "\n")).LengthInTextElements;

    [GeneratedRegex(@"^\{\{(date|time|datetime)(?:-tz\(([^)]*)\))?:(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The kind, pattern and zone inside one "{{kind:pattern" or "{{kind-tz(id):pattern" chunk; null when not a date tag.</summary>
    private static (DateVariableKind Kind, string Pattern, string? Zone)? Parse(string tagWithoutClose)
    {
        var m = TagPattern().Match(tagWithoutClose);
        if (!m.Success) return null;
        var kind = m.Groups[1].Value switch
        {
            "date" => DateVariableKind.Date,
            "time" => DateVariableKind.Time,
            _ => DateVariableKind.DateTime,
        };
        string? zone = null;
        if (m.Groups[2].Success)
        {
            zone = m.Groups[2].Value;
            // An identifier this PC can't resolve leaves the tag visible rather than formatting the wrong time.
            if (FindZone(zone) is null) return null;
        }
        return (kind, m.Groups[3].Value, zone);
    }

    private static string ExpandFormattedDates(string text, DateTime utcNow, CultureInfo culture, TimeZoneInfo localZone)
    {
        if (!Kinds.Any(k => text.Contains("{{" + k, StringComparison.Ordinal))) return text;
        var result = new StringBuilder();
        var rest = 0;
        while (true)
        {
            var start = text.IndexOf("{{", rest, StringComparison.Ordinal);
            if (start < 0) break;
            result.Append(text, rest, start - rest);
            var close = text.IndexOf("}}", start, StringComparison.Ordinal);
            var parsed = close < 0 ? null : Parse(text[start..close]);
            if (parsed is not { } tag)
            {
                result.Append("{{");
                rest = start + 2;
                continue;
            }
            var zone = tag.Zone is null ? localZone : FindZone(tag.Zone)!;
            result.Append(IcuDateFormat.Format(utcNow, tag.Pattern, culture, zone));
            rest = close + 2;
        }
        result.Append(text, rest, text.Length - rest);
        return result.ToString();
    }

    /// <summary>The date/time tag containing <paramref name="index"/>, if any (the editor edits it in place).</summary>
    public static DateToken? TokenAt(string text, int index)
    {
        var rest = 0;
        while (true)
        {
            var start = text.IndexOf("{{", rest, StringComparison.Ordinal);
            if (start < 0) return null;
            var close = text.IndexOf("}}", start, StringComparison.Ordinal);
            var parsed = close < 0 ? null : Parse(text[start..close]);
            if (parsed is not { } tag)
            {
                rest = start + 2;
                continue;
            }
            var end = close + 2;
            if (index > start && index < end) return new DateToken(start, end - start, tag.Kind, tag.Pattern, tag.Zone);
            rest = end;
        }
    }

    // ------------------------------------------------------- the builder

    /// <summary>The ICU pattern a builder selection resolves to (named styles use this PC's region format).</summary>
    public static string ResolvedPattern(DateVariableKind kind, DateVariableStyle style, string customPattern, CultureInfo culture)
    {
        var dtf = culture.DateTimeFormat;
        switch (style)
        {
            case DateVariableStyle.Custom:
                return customPattern;
            case DateVariableStyle.Iso8601:
                return kind switch
                {
                    DateVariableKind.Date => "yyyy-MM-dd",
                    DateVariableKind.Time => "HH:mm:ssXXX",
                    _ => "yyyy-MM-dd'T'HH:mm:ssXXX",
                };
        }
        string DatePart() => style switch
        {
            DateVariableStyle.Short => IcuDateFormat.FromDotNet(dtf.ShortDatePattern, dtf),
            DateVariableStyle.Medium => IcuDateFormat.FromDotNet(WithoutWeekday(dtf.LongDatePattern).Replace("MMMM", "MMM", StringComparison.Ordinal), dtf),
            DateVariableStyle.Long => IcuDateFormat.FromDotNet(WithoutWeekday(dtf.LongDatePattern), dtf),
            _ => IcuDateFormat.FromDotNet(dtf.LongDatePattern, dtf),
        };
        string TimePart() => style switch
        {
            DateVariableStyle.Short => IcuDateFormat.FromDotNet(dtf.ShortTimePattern, dtf),
            DateVariableStyle.Medium => IcuDateFormat.FromDotNet(dtf.LongTimePattern, dtf),
            DateVariableStyle.Long => IcuDateFormat.FromDotNet(dtf.LongTimePattern, dtf) + " z",
            _ => IcuDateFormat.FromDotNet(dtf.LongTimePattern, dtf) + " zzzz",
        };
        return kind switch
        {
            DateVariableKind.Date => DatePart(),
            DateVariableKind.Time => TimePart(),
            _ => DatePart() + " " + TimePart(),
        };
    }

    /// <summary>A long date pattern without its weekday ("dddd, MMMM d, yyyy" → "MMMM d, yyyy").</summary>
    public static string WithoutWeekday(string pattern)
    {
        var result = WeekdayPattern().Replace(pattern, string.Empty).Trim();
        return result.Trim(',', ' ', '،');
    }

    [GeneratedRegex(@"d{4}[,.]?\s*|\s*[,.]?\s*d{4}")]
    private static partial Regex WeekdayPattern();

    /// <summary>The literal tag a builder selection inserts: "{{kind:pattern}}" or "{{kind-tz(id):pattern}}".</summary>
    public static string TagText(DateVariableKind kind, DateVariableStyle style, string customPattern, string? zoneId, CultureInfo culture)
    {
        var pattern = ResolvedPattern(kind, style, customPattern, culture);
        var name = kind switch { DateVariableKind.Date => "date", DateVariableKind.Time => "time", _ => "datetime" };
        var tz = zoneId is null ? string.Empty : $"-tz({zoneId})";
        return $"{{{{{name}{tz}:{pattern}}}}}";
    }

    /// <summary>What a builder selection formats the given time as (the same code path expansion uses).</summary>
    public static string PreviewText(DateVariableKind kind, DateVariableStyle style, string customPattern, string? zoneId,
        DateTime utcNow, CultureInfo culture)
    {
        var pattern = ResolvedPattern(kind, style, customPattern, culture);
        if (pattern.Length == 0) return string.Empty;
        var zone = zoneId is null ? TimeZoneInfo.Local : FindZone(zoneId) ?? TimeZoneInfo.Local;
        return IcuDateFormat.Format(utcNow, pattern, culture, zone);
    }

    /// <summary>Which named style an existing pattern came from (Custom when none matches today).</summary>
    public static DateVariableStyle MatchingStyle(string pattern, DateVariableKind kind, CultureInfo culture)
    {
        foreach (var candidate in new[] { DateVariableStyle.Iso8601, DateVariableStyle.Short, DateVariableStyle.Medium, DateVariableStyle.Long, DateVariableStyle.Full })
        {
            if (ResolvedPattern(kind, candidate, string.Empty, culture) == pattern) return candidate;
        }
        return DateVariableStyle.Custom;
    }

    // ------------------------------------------------------------ time zones

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "Etc/UTC", ["GMT"] = "Europe/London", ["BST"] = "Europe/London", ["WET"] = "Europe/Lisbon",
        ["CET"] = "Europe/Paris", ["CEST"] = "Europe/Paris", ["EET"] = "Europe/Athens", ["MSK"] = "Europe/Moscow",
        ["IST"] = "Asia/Kolkata", ["JST"] = "Asia/Tokyo", ["KST"] = "Asia/Seoul", ["HKT"] = "Asia/Hong_Kong",
        ["SGT"] = "Asia/Singapore", ["AEST"] = "Australia/Sydney", ["AEDT"] = "Australia/Sydney", ["NZST"] = "Pacific/Auckland",
        ["EST"] = "America/New_York", ["EDT"] = "America/New_York", ["CST"] = "America/Chicago", ["CDT"] = "America/Chicago",
        ["MST"] = "America/Denver", ["MDT"] = "America/Denver", ["PST"] = "America/Los_Angeles", ["PDT"] = "America/Los_Angeles",
        ["AKST"] = "America/Anchorage", ["HST"] = "Pacific/Honolulu", ["BRT"] = "America/Sao_Paulo", ["ART"] = "America/Argentina/Buenos_Aires",
    };

    private static List<string>? _knownZones;

    /// <summary>IANA identifiers this PC knows (one per Windows zone, plus common aliases).</summary>
    public static IReadOnlyList<string> KnownZoneIds => _knownZones ??= BuildKnownZones();

    private static List<string> BuildKnownZones()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) set.Add(iana);
        }
        foreach (var iana in Abbreviations.Values)
        {
            if (FindZone(iana) is not null) set.Add(iana);
        }
        return [.. set];
    }

    public static TimeZoneInfo? FindZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static string NormalizeZone(string value) => value.ToLowerInvariant().Replace('_', ' ');

    private static int Rank(string id, string query)
    {
        var normalized = NormalizeZone(id);
        if (normalized == query) return 0;
        var city = normalized.Split('/')[^1];
        if (city == query) return 1;
        if (city.StartsWith(query, StringComparison.Ordinal)) return 2;
        if (normalized.StartsWith(query, StringComparison.Ordinal)) return 3;
        return 4;
    }

    /// <summary>Identifiers whose name or common abbreviation contains the query, best first.</summary>
    public static List<string> MatchingZones(string query, IReadOnlyList<string>? known = null)
    {
        if (query.Length == 0) return [];
        var q = NormalizeZone(query);
        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in known ?? KnownZoneIds)
        {
            if (NormalizeZone(id).Contains(q, StringComparison.Ordinal) && seen.Add(id)) matches.Add(id);
        }
        foreach (var (abbreviation, id) in Abbreviations)
        {
            if (NormalizeZone(abbreviation).Contains(q, StringComparison.Ordinal) && seen.Add(id)) matches.Add(id);
        }
        return matches.OrderBy(id => Rank(id, q)).ThenBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The one identifier the query unambiguously names: an exact id or abbreviation, or the only match.</summary>
    public static string? ResolvedZone(string query, IReadOnlyList<string>? known = null)
    {
        if (query.Length == 0) return null;
        var q = NormalizeZone(query);
        var ids = known ?? KnownZoneIds;
        var exact = ids.FirstOrDefault(id => NormalizeZone(id) == q);
        if (exact is not null) return exact;
        if (Abbreviations.TryGetValue(query.Trim(), out var abbreviated)) return abbreviated;
        var matches = MatchingZones(query, ids);
        return matches.Count == 1 ? matches[0] : null;
    }
}
