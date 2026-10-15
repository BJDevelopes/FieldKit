// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Island.Calendar;

/// <summary>A moment the closed island counts down to.</summary>
public sealed record CalendarCountdown(CalendarEvent Event, DateTime Target, bool Ongoing);

/// <summary>Calendar decisions (port of upstream <c>NotchCalendarSupport</c>).</summary>
public static class CalendarLogic
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(60);

    /// <summary>Events ordered by start, end, title; duplicates and zero/negative lengths dropped.</summary>
    public static List<CalendarEvent> Ordered(IEnumerable<CalendarEvent> events) => events
        .Where(e => e.End > e.Start)
        .DistinctBy(e => (e.Title, e.Start, e.End))
        .OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.Title, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>"Up next": the first upcoming timed event whose end is after now.</summary>
    public static CalendarEvent? Next(IReadOnlyList<CalendarEvent> events, DateTime now) =>
        events.FirstOrDefault(e => !e.IsAllDay && e.End > now && e.Start > now);

    /// <summary>
    /// The soonest followed moment within the hour: an event start (when every countdown is on) or the end of an
    /// event in progress (time left). Ties go to the ongoing event.
    /// </summary>
    public static CalendarCountdown? Countdown(IReadOnlyList<CalendarEvent> events, DateTime now, bool startCountdown, bool timeLeft)
    {
        CalendarCountdown? best = null;
        foreach (var e in events)
        {
            if (e.IsAllDay) continue;
            if (startCountdown && Within(e.Start, now)) Consider(new CalendarCountdown(e, e.Start, false));
            if (timeLeft && e.Start <= now && Within(e.End, now)) Consider(new CalendarCountdown(e, e.End, true));
        }
        return best;

        void Consider(CalendarCountdown c)
        {
            if (best is null || c.Target < best.Target || (c.Target == best.Target && c.Ongoing && !best.Ongoing)) best = c;
        }
    }

    private static bool Within(DateTime target, DateTime now)
    {
        var d = target - now;
        return d > TimeSpan.Zero && d <= LeadTime;
    }

    /// <summary>"59:12", "0:45" (minutes not wrapped).</summary>
    public static string CountdownText(TimeSpan left)
    {
        var s = (long)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"→ 14:30" for an ongoing event's end, "· 14:30" for a start.</summary>
    public static string TimeText(CalendarCountdown c) =>
        (c.Ongoing ? "→ " : "· ") + c.Target.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Agenda days: today..today+6, events whose end is after now, all-day first within a day.</summary>
    public static List<(DateTime Day, List<CalendarEvent> Events)> Agenda(IReadOnlyList<CalendarEvent> events, DateTime now, DateTime? selectedDay = null)
    {
        var result = new List<(DateTime, List<CalendarEvent>)>();
        var days = selectedDay is { } sd ? new[] { sd.Date } : Enumerable.Range(0, 7).Select(i => now.Date.AddDays(i)).ToArray();
        foreach (var day in days)
        {
            var next = day.AddDays(1);
            var list = events
                .Where(e => e.Start < next && e.End > day && (selectedDay is not null || e.End > now))
                .OrderByDescending(e => e.IsAllDay).ThenBy(e => e.Start)
                .ToList();
            if (list.Count > 0) result.Add((day, list));
        }
        return result;
    }

    /// <summary>"All day", "09:00 · 10:00", or "9 Oct 23:00 → 10 Oct 01:00".</summary>
    public static string TimeLine(CalendarEvent e)
    {
        if (e.IsAllDay) return "All day";
        var c = CultureInfo.CurrentCulture;
        if (e.Start.Date == e.End.Date || e.End == e.Start.Date.AddDays(1))
            return $"{e.Start.ToString("t", c)} · {e.End.ToString("t", c)}";
        return $"{e.Start.ToString("d MMM", c)} {e.Start.ToString("t", c)} → {e.End.ToString("d MMM", c)} {e.End.ToString("t", c)}";
    }
}
