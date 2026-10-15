// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.Island.Calendar;

/// <summary>One occurrence of a calendar event, in local time.</summary>
public sealed record CalendarEvent(
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    string? Location = null,
    string? CalendarName = null,
    string? Url = null)
{
    public bool IsOngoing(DateTime now) => Start <= now && now < End;
}

/// <summary>
/// A small iCalendar (RFC 5545) reader: VEVENTs with DTSTART/DTEND/DURATION (UTC, floating, TZID and
/// all-day), SUMMARY/LOCATION/URL, cancelled events skipped, RRULE (DAILY/WEEKLY/MONTHLY/YEARLY with
/// INTERVAL, COUNT, UNTIL, BYDAY for weekly) and EXDATE. Windows' local calendar is not reachable from
/// an unpackaged app, so ICS feeds (an Outlook/Google "secret address" or a local .ics file) are the source.
/// </summary>
public static class IcsParser
{
    private sealed class RawEvent
    {
        public string Summary = string.Empty;
        public string? Location;
        public string? Url;
        public string? Uid;
        public string? RecurrenceId;
        public DateTime? Start;
        public DateTime? End;
        public TimeSpan? Duration;
        public bool AllDay;
        public bool Cancelled;
        public string? RRule;
        public readonly List<DateTime> ExDates = [];
    }

    /// <summary>Occurrences that overlap [from, to), sorted by start.</summary>
    public static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to, string? calendarName = null)
    {
        var raws = ReadEvents(ics);
        // Overridden single instances (RECURRENCE-ID) replace the generated occurrence.
        var overrides = raws.Where(r => r.Uid is not null && r.RecurrenceId is not null)
            .Select(r => (r.Uid!, r.RecurrenceId!)).ToHashSet();
        var result = new List<CalendarEvent>();
        foreach (var e in raws)
        {
            if (e.Cancelled || e.Start is null) continue;
            var start = e.Start.Value;
            var length = e.End is { } end ? end - start : e.Duration ?? (e.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            var title = e.Summary.Length > 0 ? e.Summary : "Untitled event";

            IEnumerable<DateTime> starts = e.RRule is { Length: > 0 } && e.RecurrenceId is null
                ? Expand(start, e.RRule, from - length, to)
                : [start];
            foreach (var s in starts)
            {
                if (e.ExDates.Any(x => x == s)) continue;
                if (e.RecurrenceId is null && e.Uid is not null && e.RRule is not null && s != start &&
                    overrides.Contains((e.Uid, Stamp(s)))) continue;
                var occEnd = s + length;
                var overlaps = occEnd > from && s < to || length == TimeSpan.Zero && s >= from && s < to;
                if (!overlaps) continue;
                result.Add(new CalendarEvent(title, s, occEnd, e.AllDay, e.Location, calendarName, e.Url));
            }
        }
        return result.OrderBy(r => r.Start).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList();
    }

    private static string Stamp(DateTime local) => local.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);

    private static List<RawEvent> ReadEvents(string ics)
    {
        var events = new List<RawEvent>();
        RawEvent? current = null;
        var depth = 0;
        foreach (var line in Unfold(ics))
        {
            var (name, parameters, value) = Split(line);
            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new RawEvent(); depth = 0; }
                else if (current is not null) depth++; // VALARM and friends
                continue;
            }
            if (name == "END")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && current is not null)
                {
                    events.Add(current);
                    current = null;
                }
                else if (current is not null) depth--;
                continue;
            }
            if (current is null || depth > 0) continue;
            switch (name)
            {
                case "SUMMARY": current.Summary = Unescape(value); break;
                case "LOCATION": current.Location = Unescape(value); break;
                // Outside data: keep only absolute http(s)/mailto links.
                case "URL": current.Url = IslandLinks.SafeWebUri(value)?.AbsoluteUri; break;
                case "UID": current.Uid = value; break;
                case "STATUS": current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "RRULE": current.RRule = value; break;
                case "DTSTART":
                    current.Start = ParseDate(value, parameters, out current.AllDay);
                    break;
                case "DTEND":
                    current.End = ParseDate(value, parameters, out _);
                    break;
                case "DURATION": current.Duration = ParseDuration(value); break;
                case "RECURRENCE-ID":
                    if (ParseDate(value, parameters, out _) is { } rid) current.RecurrenceId = Stamp(rid);
                    break;
                case "EXDATE":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (ParseDate(part, parameters, out _) is { } ex) current.ExDates.Add(ex);
                    break;
            }
        }
        return events;
    }

    /// <summary>Joins folded lines (CRLF followed by a space or tab).</summary>
    public static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear();
            sb.Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static (string Name, Dictionary<string, string> Params, string Value) Split(string line)
    {
        var colon = IndexOfUnquoted(line, ':');
        if (colon < 0) return (string.Empty, [], string.Empty);
        var head = line[..colon];
        var value = line[(colon + 1)..];
        var parts = head.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq > 0) parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return (parts[0].ToUpperInvariant(), parameters, value);
    }

    private static int IndexOfUnquoted(string s, char c)
    {
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') quoted = !quoted;
            else if (s[i] == c && !quoted) return i;
        }
        return -1;
    }

    private static string Unescape(string v) =>
        v.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    /// <summary>Parses DATE / DATE-TIME values to local time.</summary>
    public static DateTime? ParseDate(string value, IReadOnlyDictionary<string, string> parameters, out bool allDay)
    {
        allDay = false;
        value = value.Trim();
        if (value.Length == 8 || parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase))
        {
            allDay = true;
            return DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Local)
                : null;
        }
        var utc = value.EndsWith('Z');
        var core = utc ? value[..^1] : value;
        if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return null;
        if (utc) return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
        if (parameters.TryGetValue("TZID", out var tzid) && FindZone(tzid) is { } zone)
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); }
            catch { }
        }
        return DateTime.SpecifyKind(t, DateTimeKind.Local);
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { }
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
        }
        catch { }
        return null;
    }

    /// <summary>ISO 8601 durations as used by iCalendar ("PT1H30M", "P1D", "-PT15M", "P1W").</summary>
    public static TimeSpan? ParseDuration(string value)
    {
        value = value.Trim();
        if (value.Length < 2) return null;
        var sign = 1;
        if (value[0] is '-' or '+')
        {
            if (value[0] == '-') sign = -1;
            value = value[1..];
        }
        if (value[0] != 'P') return null;
        var total = TimeSpan.Zero;
        var number = 0;
        var inTime = false;
        foreach (var ch in value[1..])
        {
            if (char.IsDigit(ch)) { number = number * 10 + (ch - '0'); continue; }
            switch (ch)
            {
                case 'T': inTime = true; break;
                case 'W': total += TimeSpan.FromDays(7 * number); break;
                case 'D': total += TimeSpan.FromDays(number); break;
                case 'H' when inTime: total += TimeSpan.FromHours(number); break;
                case 'M' when inTime: total += TimeSpan.FromMinutes(number); break;
                case 'S' when inTime: total += TimeSpan.FromSeconds(number); break;
                default: return null;
            }
            number = 0;
        }
        return sign < 0 ? total.Negate() : total;
    }

    private static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MO"] = DayOfWeek.Monday, ["TU"] = DayOfWeek.Tuesday, ["WE"] = DayOfWeek.Wednesday, ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday, ["SA"] = DayOfWeek.Saturday, ["SU"] = DayOfWeek.Sunday,
    };

    /// <summary>
    /// Occurrence starts of a recurring event between <paramref name="from"/> and <paramref name="until"/>
    /// (fixed-period rules jump straight to <paramref name="from"/>, so old daily series stay cheap).
    /// </summary>
    public static IEnumerable<DateTime> Expand(DateTime start, string rrule, DateTime from, DateTime until)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : null;
        var limit = until;
        if (parts.TryGetValue("UNTIL", out var u) && ParseDate(u, new Dictionary<string, string>(), out _) is { } untilDate)
        {
            // An all-day UNTIL includes that whole day.
            if (u.Trim().Length == 8) untilDate = untilDate.AddDays(1).AddTicks(-1);
            if (untilDate < limit) limit = untilDate;
        }
        var byDay = parts.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(d => d.Length >= 2 ? d[^2..] : d).Where(Days.ContainsKey).Select(d => Days[d]).ToHashSet()
            : null;

        var produced = 0;
        const int cap = 5000;
        if (freq == "WEEKLY" && byDay is { Count: > 0 })
        {
            // Walk week by week from the week containing the start (weeks start on Monday per RFC default).
            var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
            var firstWeek = 0;
            if (from > start)
            {
                firstWeek = (int)((from - weekStart).TotalDays / 7 / interval) * interval - interval;
                if (firstWeek < 0) firstWeek = 0;
                produced = firstWeek / interval * byDay.Count;
                if (count is not null && produced >= count) yield break;
            }
            for (var week = firstWeek; week - firstWeek < cap * interval; week += interval)
            {
                var ws = weekStart.AddDays(7 * week);
                if (ws > limit) yield break;
                for (var d = 0; d < 7; d++)
                {
                    var day = ws.AddDays(d);
                    if (!byDay.Contains(day.DayOfWeek)) continue;
                    var occ = day + start.TimeOfDay;
                    if (occ < start) continue;
                    if (occ > limit) yield break;
                    yield return occ;
                    produced++;
                    if (count is not null && produced >= count) yield break;
                }
            }
            yield break;
        }

        var first = 0;
        var period = freq switch { "DAILY" => interval, "WEEKLY" => 7 * interval, _ => 0 };
        if (period > 0 && from > start)
        {
            first = Math.Max(0, (int)((from - start).TotalDays / period) - 1);
            produced = first;
            if (count is not null && produced >= count) yield break;
        }
        for (var i = first; i - first < cap; i++)
        {
            var occ = freq switch
            {
                "DAILY" => start.AddDays((double)i * interval),
                "WEEKLY" => start.AddDays(7.0 * i * interval),
                "MONTHLY" => start.AddMonths(i * interval),
                "YEARLY" => start.AddYears(i * interval),
                _ => i == 0 ? start : DateTime.MaxValue,
            };
            if (occ == DateTime.MaxValue || occ > limit) yield break;
            // AddMonths clamps (Jan 31 → Feb 28); RFC skips such months.
            if (freq is "MONTHLY" or "YEARLY" && occ.Day != start.Day) continue;
            yield return occ;
            produced++;
            if (count is not null && produced >= count) yield break;
        }
    }
}
