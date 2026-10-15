// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>
/// When the automatic cleanup runs. Pure calendar math (upstream CleanerSchedule), kept away from
/// the timer so the tests can pin every boundary. Times are local wall-clock times.
/// </summary>
public static class CleanerSchedule
{
    public enum Frequency { Off, Daily, Weekly }

    public static Frequency Sanitized(string? raw) => raw?.ToLowerInvariant() switch
    {
        "daily" => Frequency.Daily,
        "weekly" => Frequency.Weekly,
        _ => Frequency.Off,
    };

    public static string Raw(Frequency f) => f.ToString().ToLowerInvariant();

    /// <summary>The next moment the cleanup should fire strictly after <paramref name="after"/>.
    /// Weekly runs use <paramref name="weekday"/> (1 = Sunday … 7 = Saturday); daily runs ignore it.</summary>
    public static DateTime? NextFireDate(DateTime after, Frequency frequency, int hour, int minute, int weekday)
    {
        if (frequency == Frequency.Off) return null;
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        var candidate = after.Date.AddHours(hour).AddMinutes(minute);
        if (frequency == Frequency.Daily)
        {
            return candidate > after ? candidate : candidate.AddDays(1);
        }
        var target = (DayOfWeek)(Math.Clamp(weekday, 1, 7) - 1);
        var delta = ((int)target - (int)candidate.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(delta);
        return candidate > after ? candidate : candidate.AddDays(7);
    }

    /// <summary>Whether a scheduled run was missed while the PC was off or asleep: the fire that
    /// followed the last run is already in the past. Never ran before → no surprise catch-up.</summary>
    public static bool MissedRun(DateTime now, DateTime? lastRun, Frequency frequency, int hour, int minute, int weekday)
    {
        if (frequency == Frequency.Off || lastRun is null) return false;
        var due = NextFireDate(lastRun.Value, frequency, hour, minute, weekday);
        return due is not null && due <= now;
    }

    public static int Hour24(int hour12, bool isPm)
    {
        var clamped = Math.Clamp(hour12, 1, 12);
        return clamped % 12 + (isPm ? 12 : 0);
    }

    public static (int Hour12, bool IsPm) Hour12Components(int hour24)
    {
        var clamped = Math.Clamp(hour24, 0, 23);
        var hour12 = clamped % 12 == 0 ? 12 : clamped % 12;
        return (hour12, clamped >= 12);
    }

    /// <summary>Whether the user's clock runs on twelve hours (6 PM) or twenty-four (18:00).</summary>
    public static bool Uses12HourClock(CultureInfo? culture = null)
    {
        var pattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortTimePattern;
        return pattern.Contains('h') || pattern.Contains('t');
    }

    /// <summary>Seconds since the Unix epoch (how last-run stamps are stored), and back.</summary>
    public static double ToStamp(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds() / 1000.0;

    public static DateTime? FromStamp(double stamp) =>
        stamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(stamp * 1000)).LocalDateTime : null;
}
