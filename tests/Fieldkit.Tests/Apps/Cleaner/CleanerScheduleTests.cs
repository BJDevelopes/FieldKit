// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Apps.Cleaner;
using F = Fieldkit.Features.Apps.Cleaner.CleanerSchedule.Frequency;

namespace Fieldkit.Tests.Apps.Cleaner;

public class CleanerScheduleTests
{
    // Wednesday 14 October 2026.
    private static readonly DateTime Wednesday = new(2026, 10, 14, 8, 30, 0);

    [Fact]
    public void DailyFiresTodayOrTomorrow()
    {
        Assert.Equal(new DateTime(2026, 10, 14, 9, 0, 0), CleanerSchedule.NextFireDate(Wednesday, F.Daily, 9, 0, 2));
        Assert.Equal(new DateTime(2026, 10, 15, 8, 0, 0), CleanerSchedule.NextFireDate(Wednesday, F.Daily, 8, 0, 2));
        // Strictly after: the exact fire moment schedules the next day.
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), CleanerSchedule.NextFireDate(Wednesday, F.Daily, 8, 30, 2));
    }

    [Fact]
    public void WeeklyUsesSundayBasedWeekdays()
    {
        // 2 = Monday → next Monday 19 Oct.
        Assert.Equal(new DateTime(2026, 10, 19, 9, 0, 0), CleanerSchedule.NextFireDate(Wednesday, F.Weekly, 9, 0, 2));
        // 4 = Wednesday, later today.
        Assert.Equal(new DateTime(2026, 10, 14, 21, 15, 0), CleanerSchedule.NextFireDate(Wednesday, F.Weekly, 21, 15, 4));
        // 4 = Wednesday, already past today → next week.
        Assert.Equal(new DateTime(2026, 10, 21, 7, 0, 0), CleanerSchedule.NextFireDate(Wednesday, F.Weekly, 7, 0, 4));
        // 1 = Sunday.
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), CleanerSchedule.NextFireDate(Wednesday, F.Weekly, 9, 0, 1));
    }

    [Fact]
    public void OffNeverFiresAndValuesAreClamped()
    {
        Assert.Null(CleanerSchedule.NextFireDate(Wednesday, F.Off, 9, 0, 2));
        Assert.Equal(new DateTime(2026, 10, 14, 23, 59, 0), CleanerSchedule.NextFireDate(Wednesday, F.Daily, 99, 99, 2));
        Assert.Equal(F.Off, CleanerSchedule.Sanitized("nonsense"));
        Assert.Equal(F.Off, CleanerSchedule.Sanitized(null));
        Assert.Equal(F.Weekly, CleanerSchedule.Sanitized("weekly"));
        Assert.Equal("daily", CleanerSchedule.Raw(F.Daily));
    }

    [Fact]
    public void DetectsRunsMissedWhileThePcWasOff()
    {
        var lastRun = new DateTime(2026, 10, 12, 9, 0, 5);
        Assert.True(CleanerSchedule.MissedRun(Wednesday, lastRun, F.Daily, 9, 0, 2));
        Assert.False(CleanerSchedule.MissedRun(Wednesday, new DateTime(2026, 10, 13, 9, 0, 5), F.Daily, 9, 0, 2));
        Assert.False(CleanerSchedule.MissedRun(Wednesday, lastRun, F.Weekly, 9, 0, 2)); // next Monday not reached yet
        Assert.False(CleanerSchedule.MissedRun(Wednesday, null, F.Daily, 9, 0, 2));     // never ran: no surprise catch-up
        Assert.False(CleanerSchedule.MissedRun(Wednesday, lastRun, F.Off, 9, 0, 2));
    }

    [Theory]
    [InlineData(12, false, 0)]
    [InlineData(12, true, 12)]
    [InlineData(1, false, 1)]
    [InlineData(11, true, 23)]
    public void ConvertsTwelveHourClock(int hour12, bool pm, int hour24)
    {
        Assert.Equal(hour24, CleanerSchedule.Hour24(hour12, pm));
        Assert.Equal((hour12, pm), CleanerSchedule.Hour12Components(hour24));
    }

    [Fact]
    public void ReadsTheClockStyleFromTheCulture()
    {
        Assert.True(CleanerSchedule.Uses12HourClock(new CultureInfo("en-US")));
        Assert.False(CleanerSchedule.Uses12HourClock(new CultureInfo("de-DE")));
    }

    [Fact]
    public void StampsRoundTrip()
    {
        var now = new DateTime(2026, 10, 14, 9, 30, 15);
        Assert.Equal(now, CleanerSchedule.FromStamp(CleanerSchedule.ToStamp(now)));
        Assert.Null(CleanerSchedule.FromStamp(0));
    }
}
