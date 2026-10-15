// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.Snippets;

/// <summary>
/// Date patterns in the Unicode (ICU / CLDR) syntax upstream snippets use — <c>yyyy-MM-dd</c>,
/// <c>EEEE, MMMM d</c>, <c>HH:mm:ssXXX</c> — formatted with .NET culture data, plus a converter from
/// .NET's culture patterns so the named styles (short … full) can be saved in the same syntax.
/// </summary>
public static class IcuDateFormat
{
    /// <summary>Formats <paramref name="utc"/> in <paramref name="zone"/> with an ICU pattern.</summary>
    public static string Format(DateTime utc, string pattern, CultureInfo culture, TimeZoneInfo zone)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var offset = zone.GetUtcOffset(utc);
        var dtf = culture.DateTimeFormat;
        var sb = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }
                var end = i + 1;
                while (end < pattern.Length)
                {
                    if (pattern[end] == '\'')
                    {
                        if (end + 1 < pattern.Length && pattern[end + 1] == '\'')
                        {
                            sb.Append('\'');
                            end += 2;
                            continue;
                        }
                        break;
                    }
                    sb.Append(pattern[end]);
                    end++;
                }
                i = end + 1;
                continue;
            }
            if (!IsAsciiLetter(c))
            {
                sb.Append(c);
                i++;
                continue;
            }
            var count = 1;
            while (i + count < pattern.Length && pattern[i + count] == c) count++;
            sb.Append(Field(c, count, local, offset, dtf, culture, zone, utc));
            i += count;
        }
        return sb.ToString();
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static string Pad(long value, int count) => value.ToString(new string('0', Math.Max(1, count)), CultureInfo.InvariantCulture);

    private static string Field(char c, int count, DateTime local, TimeSpan offset, DateTimeFormatInfo dtf,
        CultureInfo culture, TimeZoneInfo zone, DateTime utc)
    {
        var calendar = dtf.Calendar;
        switch (c)
        {
            case 'G':
                var era = calendar.GetEra(local);
                return count >= 4 ? dtf.GetEraName(era) : dtf.GetAbbreviatedEraName(era);
            case 'y' or 'Y' or 'u':
                var year = calendar.GetYear(local);
                return count == 2 ? Pad(year % 100, 2) : Pad(year, count);
            case 'Q' or 'q':
                var quarter = (local.Month - 1) / 3 + 1;
                return count switch
                {
                    <= 2 => Pad(quarter, count),
                    3 => $"Q{quarter}",
                    _ => quarter switch { 1 => "1st quarter", 2 => "2nd quarter", 3 => "3rd quarter", _ => "4th quarter" },
                };
            case 'M' or 'L':
                var month = calendar.GetMonth(local);
                return count switch
                {
                    1 => month.ToString(CultureInfo.InvariantCulture),
                    2 => Pad(month, 2),
                    3 => dtf.GetAbbreviatedMonthName(month),
                    4 => dtf.GetMonthName(month),
                    _ => dtf.GetMonthName(month)[..1],
                };
            case 'w':
                return Pad(calendar.GetWeekOfYear(local, dtf.CalendarWeekRule, dtf.FirstDayOfWeek), count);
            case 'd':
                return Pad(calendar.GetDayOfMonth(local), count);
            case 'D':
                return Pad(calendar.GetDayOfYear(local), count);
            case 'E':
                return Weekday(local.DayOfWeek, Math.Max(count, 3), dtf);
            case 'e' or 'c':
                if (count <= 2)
                {
                    var local1 = ((int)local.DayOfWeek - (int)dtf.FirstDayOfWeek + 7) % 7 + 1;
                    return Pad(local1, count);
                }
                return Weekday(local.DayOfWeek, count, dtf);
            case 'a':
                var designator = local.Hour < 12 ? dtf.AMDesignator : dtf.PMDesignator;
                return designator.Length > 0 ? designator : local.Hour < 12 ? "AM" : "PM";
            case 'h':
                var h12 = local.Hour % 12;
                return Pad(h12 == 0 ? 12 : h12, count);
            case 'H':
                return Pad(local.Hour, count);
            case 'k':
                return Pad(local.Hour == 0 ? 24 : local.Hour, count);
            case 'K':
                return Pad(local.Hour % 12, count);
            case 'm':
                return Pad(local.Minute, count);
            case 's':
                return Pad(local.Second, count);
            case 'S':
                var fraction = (local.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture);
                return count <= 7 ? fraction[..count] : fraction.PadRight(count, '0');
            case 'A':
                return Pad((long)local.TimeOfDay.TotalMilliseconds, count);
            case 'z':
                return count >= 4 ? (zone.IsDaylightSavingTime(utc) ? zone.DaylightName : zone.StandardName) : Gmt(offset, longForm: false);
            case 'O':
                return Gmt(offset, longForm: count >= 4);
            case 'Z':
                return count switch
                {
                    <= 3 => Offset(offset, colon: false, minutesAlways: true, zForZero: false),
                    4 => Gmt(offset, longForm: true),
                    _ => Offset(offset, colon: true, minutesAlways: true, zForZero: true),
                };
            case 'X' or 'x':
                var z = c == 'X';
                return count switch
                {
                    1 => Offset(offset, colon: false, minutesAlways: false, zForZero: z),
                    2 or 4 => Offset(offset, colon: false, minutesAlways: true, zForZero: z),
                    _ => Offset(offset, colon: true, minutesAlways: true, zForZero: z),
                };
            case 'V' or 'v':
                return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
            default:
                return new string(c, count);
        }
    }

    private static string Weekday(DayOfWeek day, int count, DateTimeFormatInfo dtf) => count switch
    {
        <= 3 => dtf.GetAbbreviatedDayName(day),
        4 => dtf.GetDayName(day),
        5 => dtf.GetDayName(day)[..1],
        _ => dtf.GetShortestDayName(day),
    };

    private static string Offset(TimeSpan offset, bool colon, bool minutesAlways, bool zForZero)
    {
        if (offset == TimeSpan.Zero && zForZero) return "Z";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var hours = Pad(abs.Hours, 2);
        if (!minutesAlways && abs.Minutes == 0) return sign + hours;
        return sign + hours + (colon ? ":" : string.Empty) + Pad(abs.Minutes, 2);
    }

    private static string Gmt(TimeSpan offset, bool longForm)
    {
        if (offset == TimeSpan.Zero) return "GMT";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        if (longForm) return $"GMT{sign}{Pad(abs.Hours, 2)}:{Pad(abs.Minutes, 2)}";
        return abs.Minutes == 0 ? $"GMT{sign}{abs.Hours}" : $"GMT{sign}{abs.Hours}:{Pad(abs.Minutes, 2)}";
    }

    /// <summary>Converts a .NET custom date/time format (a culture's pattern) into ICU syntax.</summary>
    public static string FromDotNet(string pattern, DateTimeFormatInfo dtf)
    {
        var sb = new StringBuilder();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            sb.Append(QuoteLiteral(literal.ToString()));
            literal.Clear();
        }
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\'' or '"':
                {
                    var end = pattern.IndexOf(c, i + 1);
                    if (end < 0) end = pattern.Length;
                    literal.Append(pattern, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
                case '\\':
                    if (i + 1 < pattern.Length) literal.Append(pattern[i + 1]);
                    i += 2;
                    continue;
                case '%':
                    i++;
                    continue;
                case ':':
                    literal.Append(dtf.TimeSeparator);
                    i++;
                    continue;
                case '/':
                    literal.Append(dtf.DateSeparator);
                    i++;
                    continue;
            }
            if (!IsAsciiLetter(c))
            {
                literal.Append(c);
                i++;
                continue;
            }
            var count = 1;
            while (i + count < pattern.Length && pattern[i + count] == c) count++;
            var token = c switch
            {
                'd' => count switch { 1 => "d", 2 => "dd", 3 => "EEE", _ => "EEEE" },
                'f' or 'F' => new string('S', Math.Min(count, 7)),
                'g' => "G",
                'h' => count == 1 ? "h" : "hh",
                'H' => count == 1 ? "H" : "HH",
                'K' => "xxx",
                'm' => count == 1 ? "m" : "mm",
                'M' => new string('M', Math.Min(count, 4)),
                's' => count == 1 ? "s" : "ss",
                't' => "a",
                'y' => count <= 2 ? "yy" : "yyyy",
                'z' => count >= 3 ? "xxx" : "x",
                _ => null,
            };
            if (token is null)
            {
                literal.Append(c, count);
            }
            else
            {
                FlushLiteral();
                sb.Append(token);
            }
            i += count;
        }
        FlushLiteral();
        return sb.ToString();
    }

    /// <summary>ICU literal text: letters (and apostrophes) quoted, everything else as is.</summary>
    public static string QuoteLiteral(string text)
    {
        if (!text.Any(ch => IsAsciiLetter(ch) || ch == '\'')) return text;
        if (text == "'") return "''";
        return "'" + text.Replace("'", "''") + "'";
    }
}
