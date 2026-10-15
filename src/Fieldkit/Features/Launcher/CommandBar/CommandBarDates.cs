// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Questions about time typed into the bar (port of upstream CommandBarDates): "in 3 weeks",
/// "3 days ago", "today + 10 days", "days until 25/12", "time in tokyo". Answered by the local
/// calendar and time zone database, never the network. Deliberately narrow: every answer needs a
/// full pattern, so "3 days" alone stays a search.
/// </summary>
public static class CommandBarDates
{
    /// <param name="Formatted">The answer itself.</param>
    /// <param name="Detail">One line under it (weekday, place, full date).</param>
    public sealed record Result(string Formatted, string Detail);

    private static readonly HashSet<string> TodayWords =
    [
        "today", "hoje", "heute", "hoy", "oggi", "aujourdhui", "aujourd'hui", "segodnya", "сегодня", "bugun", "今日", "오늘", "今天",
    ];
    private static readonly HashSet<string> FutureWords =
    [
        "in", "em", "daqui", "dentro", "nach", "tra", "fra", "dans", "через", "sonra", "後", "후", "后", "from",
    ];
    private static readonly HashSet<string> PastWords =
    [
        "ago", "atras", "ha", "hace", "vor", "fa", "назад", "once", "前", "전",
    ];
    private static readonly HashSet<string> UntilWords =
    [
        "until", "till", "ate", "hasta", "bis", "fino", "jusqu", "до", "kadar", "까지", "까지는",
    ];
    private static readonly HashSet<string> TimeWords =
    [
        "time", "hora", "horas", "hour", "uhrzeit", "uhr", "ora", "heure", "время", "saat", "時刻", "時間", "시간", "时间", "现在",
    ];

    private enum Component { Day, Week, Month, Year }

    private static readonly Dictionary<string, Component> Units = BuildUnits();

    private static Dictionary<string, Component> BuildUnits()
    {
        var map = new Dictionary<string, Component>(StringComparer.Ordinal);
        void Add(Component c, params string[] names)
        {
            foreach (var n in names) map[n] = c;
        }
        Add(Component.Day, "day", "days", "dia", "dias", "tag", "tage", "giorno", "giorni", "jour", "jours", "den", "dnya", "dney",
            "дня", "дней", "день", "gun", "gunler", "日", "일", "天");
        Add(Component.Week, "week", "weeks", "semana", "semanas", "woche", "wochen", "settimana", "settimane", "semaine", "semaines",
            "nedelya", "недели", "недель", "неделя", "hafta", "週", "주", "周", "星期");
        Add(Component.Month, "month", "months", "mes", "meses", "monat", "monate", "mese", "mesi", "mois", "месяц", "месяца", "месяцев",
            "ay", "月", "월", "个月");
        Add(Component.Year, "year", "years", "ano", "anos", "jahr", "jahre", "anno", "anni", "an", "ans", "annee", "annees", "god",
            "года", "лет", "год", "yil", "年", "년");
        return map;
    }

    /// <summary>Places whose everyday name differs from the time zone database's city.</summary>
    private static readonly Dictionary<string, string> PlaceAliases = new(StringComparer.Ordinal)
    {
        ["londres"] = "London", ["lisboa"] = "Lisbon", ["roma"] = "Rome", ["moscou"] = "Moscow", ["moscovo"] = "Moscow",
        ["москва"] = "Moscow", ["nova york"] = "New_York", ["nueva york"] = "New_York", ["nova iorque"] = "New_York",
        ["cidade do mexico"] = "Mexico_City", ["ciudad de mexico"] = "Mexico_City", ["pequim"] = "Shanghai", ["beijing"] = "Shanghai",
        ["pequin"] = "Shanghai", ["toquio"] = "Tokyo", ["tokio"] = "Tokyo", ["genebra"] = "Zurich", ["geneva"] = "Zurich",
        ["viena"] = "Vienna", ["copenhague"] = "Copenhagen", ["praga"] = "Prague", ["atenas"] = "Athens", ["varsovia"] = "Warsaw",
        ["bruxelas"] = "Brussels", ["zurique"] = "Zurich", ["munique"] = "Berlin", ["munich"] = "Berlin", ["colonia"] = "Berlin",
        ["cologne"] = "Berlin", ["estocolmo"] = "Stockholm", ["hamburgo"] = "Berlin", ["hamburg"] = "Berlin",
        ["san francisco"] = "Los_Angeles", ["seattle"] = "Los_Angeles", ["boston"] = "New_York", ["washington"] = "New_York",
        ["miami"] = "New_York", ["dallas"] = "Chicago", ["houston"] = "Chicago", ["sydney"] = "Sydney", ["delhi"] = "Kolkata",
        ["new delhi"] = "Kolkata", ["mumbai"] = "Kolkata", ["bangalore"] = "Kolkata", ["rio"] = "Sao_Paulo", ["rio de janeiro"] = "Sao_Paulo",
        ["barcelona"] = "Madrid", ["milan"] = "Rome", ["milao"] = "Rome", ["porto"] = "Lisbon", ["kyiv"] = "Kiev", ["kiev"] = "Kiev",
    };

    /// <summary>IANA identifiers the bar knows by city (the Windows build has no zone enumeration by city).</summary>
    private static readonly string[] Zones =
    [
        "Pacific/Honolulu", "America/Anchorage", "America/Los_Angeles", "America/Vancouver", "America/Tijuana", "America/Denver",
        "America/Phoenix", "America/Chicago", "America/Mexico_City", "America/New_York", "America/Toronto", "America/Montreal",
        "America/Halifax", "America/St_Johns", "America/Bogota", "America/Lima", "America/Caracas", "America/Santiago",
        "America/Buenos_Aires", "America/Sao_Paulo", "America/Montevideo", "America/Havana", "America/Panama", "America/Guatemala",
        "Atlantic/Reykjavik", "Atlantic/Azores", "Europe/London", "Europe/Dublin", "Europe/Lisbon", "Europe/Madrid", "Europe/Paris",
        "Europe/Brussels", "Europe/Amsterdam", "Europe/Berlin", "Europe/Zurich", "Europe/Rome", "Europe/Vienna", "Europe/Prague",
        "Europe/Warsaw", "Europe/Budapest", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Helsinki",
        "Europe/Athens", "Europe/Bucharest", "Europe/Sofia", "Europe/Istanbul", "Europe/Kiev", "Europe/Minsk", "Europe/Moscow",
        "Africa/Casablanca", "Africa/Lagos", "Africa/Cairo", "Africa/Johannesburg", "Africa/Nairobi", "Asia/Jerusalem",
        "Asia/Beirut", "Asia/Riyadh", "Asia/Baghdad", "Asia/Tehran", "Asia/Dubai", "Asia/Baku", "Asia/Kabul", "Asia/Karachi",
        "Asia/Tashkent", "Asia/Kolkata", "Asia/Kathmandu", "Asia/Dhaka", "Asia/Yangon", "Asia/Bangkok", "Asia/Jakarta",
        "Asia/Ho_Chi_Minh", "Asia/Singapore", "Asia/Kuala_Lumpur", "Asia/Manila", "Asia/Hong_Kong", "Asia/Shanghai", "Asia/Taipei",
        "Asia/Seoul", "Asia/Tokyo", "Australia/Perth", "Australia/Adelaide", "Australia/Darwin", "Australia/Brisbane",
        "Australia/Sydney", "Australia/Melbourne", "Pacific/Auckland", "Pacific/Fiji",
    ];

    private static readonly Lazy<Dictionary<string, string>> ZonesByCity = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in Zones)
        {
            var city = CommandBarSearch.Normalize(id[(id.LastIndexOf('/') + 1)..].Replace('_', ' '));
            map.TryAdd(city, id);
        }
        foreach (var (alias, city) in PlaceAliases)
        {
            var name = CommandBarSearch.Normalize(city.Replace('_', ' '));
            if (map.TryGetValue(name, out var id)) map[alias] = id;
        }
        return map;
    });

    public static Result? Evaluate(string input, DateTime? now = null, CultureInfo? culture = null)
    {
        var at = now ?? DateTime.Now;
        culture ??= CultureInfo.CurrentCulture;
        var trimmed = input.Trim();
        if (trimmed.Length > 80) return null;
        var tokens = CommandBarSearch.Normalize(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count < 2) return null;
        return RelativeDate(tokens, at, culture) ?? DaysUntil(tokens, at, culture) ?? TimeSomewhereElse(tokens, at, culture);
    }

    /// <summary>"in 3 weeks", "3 days ago", "today + 10 days".</summary>
    private static Result? RelativeDate(List<string> tokens, DateTime now, CultureInfo culture)
    {
        int? amount = null;
        Component? component = null;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > 10_000) continue;
            if (!Units.TryGetValue(tokens[i + 1], out var unit)) continue;
            amount = value;
            component = unit;
            break;
        }
        if (amount is null || component is null) return null;
        var hasToday = tokens.Any(TodayWords.Contains);
        var hasPast = tokens.Any(PastWords.Contains);
        var hasFuture = tokens.Any(FutureWords.Contains);
        var hasMinus = tokens.Contains("-");
        if (!hasToday && !hasPast && !hasFuture) return null;
        var delta = hasPast || hasMinus ? -amount.Value : amount.Value;
        DateTime date;
        try
        {
            date = component switch
            {
                Component.Day => now.AddDays(delta),
                Component.Week => now.AddDays(7 * delta),
                Component.Month => now.AddMonths(delta),
                _ => now.AddYears(delta),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        return new Result(LongDate(date, culture), date.ToString("dddd", culture));
    }

    /// <summary>"days until 25/12": how far away a written date is.</summary>
    private static Result? DaysUntil(List<string> tokens, DateTime now, CultureInfo culture)
    {
        var untilIndex = tokens.FindIndex(UntilWords.Contains);
        if (untilIndex < 0 || untilIndex + 1 >= tokens.Count) return null;
        var written = string.Join(' ', tokens.Skip(untilIndex + 1));
        var target = ParseDate(written, now, culture);
        if (target is null) return null;
        var days = (int)Math.Round((target.Value.Date - now.Date).TotalDays);
        var counted = Math.Abs(days) == 1 ? "1 day" : $"{Math.Abs(days).ToString("#,0", culture)} days";
        return new Result(counted, LongDate(target.Value, culture));
    }

    /// <summary>"time in tokyo", "tokyo time".</summary>
    private static Result? TimeSomewhereElse(List<string> tokens, DateTime now, CultureInfo culture)
    {
        if (!tokens.Any(TimeWords.Contains)) return null;
        var rest = tokens.Where(t => !TimeWords.Contains(t) && !FutureWords.Contains(t) && t is not ("a" or "at" or "de")).ToList();
        if (rest.Count == 0) return null;
        var name = string.Join(' ', rest);
        if (!ZonesByCity.Value.TryGetValue(name, out var id) && !ZonesByCity.Value.TryGetValue(rest[^1], out id)) return null;
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)) return null;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
            catch (Exception) { return null; }
        }
        var local = TimeZoneInfo.ConvertTime(now.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(now, DateTimeKind.Local) : now, zone);
        var place = id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return new Result(local.ToString(culture.DateTimeFormat.ShortTimePattern, culture),
            place + " · " + local.ToString(culture.DateTimeFormat.LongDatePattern, culture));
    }

    /// <summary>The culture's long date without the weekday ("October 14, 2026").</summary>
    public static string LongDate(DateTime date, CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.LongDatePattern;
        foreach (var weekday in new[] { "dddd, ", "dddd,", "dddd ", ", dddd", " dddd", "dddd" })
            pattern = pattern.Replace(weekday, string.Empty, StringComparison.Ordinal);
        return date.ToString(pattern.Trim(), culture);
    }

    /// <summary>A date the person wrote, read the way this PC writes dates. A missing year means the
    /// next time that day comes around (29 February waits for the next leap year).</summary>
    private static DateTime? ParseDate(string written, DateTime now, CultureInfo culture)
    {
        if (DateTime.TryParseExact(written, culture.DateTimeFormat.ShortDatePattern, culture, DateTimeStyles.None, out var exact)) return exact;
        var parts = System.Text.RegularExpressions.Regex.Split(written, @"\D+").Where(p => p.Length > 0)
            .Select(p => int.TryParse(p, out var v) ? v : -1).ToList();
        if (parts.Count == 3 && parts.All(p => p >= 0))
        {
            var (d, m, y) = DayComesFirst(culture) ? (parts[0], parts[1], parts[2]) : (parts[1], parts[0], parts[2]);
            if (y < 100) y += 2000;
            if (m is >= 1 and <= 12 && y is >= 1 and <= 9999 && d >= 1 && d <= DateTime.DaysInMonth(y, m)) return new DateTime(y, m, d);
            return null;
        }
        if (parts.Count != 2 || parts.Any(p => p < 0)) return null;
        var dayFirst = DayComesFirst(culture);
        var day = dayFirst ? parts[0] : parts[1];
        var month = dayFirst ? parts[1] : parts[0];
        if (day is < 1 or > 31 || month is < 1 or > 12) return null;
        for (var year = now.Year; year <= now.Year + 8; year++)
        {
            if (day > DateTime.DaysInMonth(year, month)) continue;
            var candidate = new DateTime(year, month, day);
            if (candidate >= now.Date) return candidate;
        }
        return null;
    }

    /// <summary>Whether this PC writes the day before the month.</summary>
    public static bool DayComesFirst(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var d = pattern.IndexOf('d');
        var m = pattern.IndexOf('M');
        return d >= 0 && m >= 0 && d < m;
    }
}
