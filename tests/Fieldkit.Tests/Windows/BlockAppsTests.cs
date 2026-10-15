// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.WindowTools.BlockApps;

namespace Fieldkit.Tests.Windows;

public class BlockAppsTests
{
    private static readonly IReadOnlySet<DayOfWeek> Weekdays = BlockAppsLogic.ParseDays("1,2,3,4,5");

    // 2026-10-16 is a Friday.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    [Fact]
    public void AlwaysIsAlwaysOn() => Assert.True(BlockAppsLogic.IsActive(At(18, 3), "always", 0, 0, new HashSet<DayOfWeek>()));

    [Theory]
    [InlineData(16, 9, 0, true)]    // Friday 09:00, start included
    [InlineData(16, 16, 59, true)]
    [InlineData(16, 17, 0, false)]  // end excluded
    [InlineData(16, 8, 59, false)]
    [InlineData(17, 12, 0, false)]  // Saturday
    public void ADaytimeScheduleCoversItsHoursOnItsDays(int day, int hour, int minute, bool active) =>
        Assert.Equal(active, BlockAppsLogic.IsActive(At(day, hour, minute), "schedule", 9 * 60, 17 * 60, Weekdays));

    [Theory]
    [InlineData(16, 23, 0, true)]   // Friday night
    [InlineData(17, 6, 59, true)]   // Saturday morning belongs to Friday's night
    [InlineData(17, 23, 0, false)]  // Saturday night: Saturday is not a chosen day
    [InlineData(19, 3, 0, false)]   // Monday 03:00 belongs to Sunday's night
    [InlineData(19, 23, 30, true)]  // Monday night
    public void AnOvernightScheduleBelongsToTheDayItStarts(int day, int hour, int minute, bool active) =>
        Assert.Equal(active, BlockAppsLogic.IsActive(At(day, hour, minute), "schedule", 22 * 60, 7 * 60, Weekdays));

    [Fact]
    public void EqualTimesMeanTheWholeDay()
    {
        Assert.True(BlockAppsLogic.IsActive(At(16, 3), "schedule", 600, 600, Weekdays));
        Assert.False(BlockAppsLogic.IsActive(At(17, 3), "schedule", 600, 600, Weekdays));
    }

    [Theory]
    [InlineData("discord", @"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe", true)]
    [InlineData("explorer", @"C:\Windows\explorer.exe", false)]
    [InlineData("taskmgr", @"C:\Windows\System32\Taskmgr.exe", false)]
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", false)] // anything in the Windows folder
    [InlineData("fieldkit", @"D:\Tools\Fieldkit.exe", false)]
    [InlineData("", null, false)]
    public void WindowsOwnProgramsCanNeverBeBlocked(string name, string? path, bool allowed) =>
        Assert.Equal(allowed, BlockAppsLogic.CanBlock(name, path, @"C:\Windows"));

    [Fact]
    public void DaysRoundTrip()
    {
        var days = BlockAppsLogic.ParseDays("5, 1,junk,9,1");
        Assert.Equal("1,5", BlockAppsLogic.FormatDays(days));
    }
}
