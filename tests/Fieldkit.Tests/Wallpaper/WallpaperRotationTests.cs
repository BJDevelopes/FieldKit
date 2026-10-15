// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Features.Wallpaper;

namespace Fieldkit.Tests.Wallpaper;

public class WallpaperTargetsTests
{
    private static readonly WallpaperMonitor Primary = new("primary", new MonitorBounds(0, 0, 1920, 1080), @"C:\a.jpg");
    private static readonly WallpaperMonitor Left = new("left", new MonitorBounds(-2560, 0, 0, 1440), @"C:\b.jpg");
    private static readonly WallpaperMonitor Detached = new("detached", new MonitorBounds(0, 0, 0, 0), "");

    [Fact]
    public void All_displays_targets_every_attached_monitor()
    {
        Assert.Equal(["left", "primary"], WallpaperTargets.TargetMonitors(true, [Left, Primary, Detached], (5, 5)));
    }

    [Fact]
    public void One_display_follows_the_pointer()
    {
        Assert.Equal(["left"], WallpaperTargets.TargetMonitors(false, [Primary, Left], (-100, 200)));
        Assert.Equal(["primary"], WallpaperTargets.TargetMonitors(false, [Left, Primary], (1919, 1079)));
    }

    [Fact]
    public void Without_pointer_falls_back_to_primary_then_first()
    {
        Assert.Equal(["primary"], WallpaperTargets.TargetMonitors(false, [Left, Primary], null));
        Assert.Equal(["primary"], WallpaperTargets.TargetMonitors(false, [Left, Primary], (99999, 99999)));
        var other = new WallpaperMonitor("other", new MonitorBounds(100, 100, 200, 200), "");
        Assert.Equal(["left"], WallpaperTargets.TargetMonitors(false, [Left, other], null));
        Assert.Empty(WallpaperTargets.TargetMonitors(true, [Detached], null));
        Assert.Empty(WallpaperTargets.TargetMonitors(false, [], null));
    }

    [Fact]
    public void Assign_same_or_different_pictures()
    {
        Assert.Equal(
            [new WallpaperAssignment("a", "x"), new WallpaperAssignment("b", "x")],
            WallpaperTargets.Assign(["a", "b"], ["x", "y"], differentPerDisplay: false));
        Assert.Equal(
            [new WallpaperAssignment("a", "x"), new WallpaperAssignment("b", "y"), new WallpaperAssignment("c", "x")],
            WallpaperTargets.Assign(["a", "b", "c"], ["x", "y"], differentPerDisplay: true));
        Assert.Empty(WallpaperTargets.Assign([], ["x"], true));
        Assert.Empty(WallpaperTargets.Assign(["a"], [], true));
    }

    [Fact]
    public void Snapshot_keeps_attached_monitors_with_a_picture()
    {
        var blank = new WallpaperMonitor("blank", new MonitorBounds(0, 0, 10, 10), "");
        Assert.Equal(
            [new WallpaperAssignment("primary", @"C:\a.jpg")],
            WallpaperTargets.Snapshot([Primary, Detached, blank]));
    }

    [Fact]
    public void MonitorBounds_contains_is_half_open()
    {
        var b = new MonitorBounds(0, 0, 10, 10);
        Assert.True(b.Contains(0, 0));
        Assert.True(b.Contains(9, 9));
        Assert.False(b.Contains(10, 5));
        Assert.True(new MonitorBounds(5, 5, 5, 9).IsEmpty);
    }
}

public class RotationPickerTests
{
    private static readonly string[] Pool = ["a", "b", "c", "d", "e"];

    [Fact]
    public void Sequential_walks_after_current_and_wraps()
    {
        Assert.Equal(["c"], RotationPicker.PickSequential(Pool, "B", 1));
        Assert.Equal(["a"], RotationPicker.PickSequential(Pool, "e", 1));
        Assert.Equal(["a"], RotationPicker.PickSequential(Pool, "zzz", 1));
        Assert.Equal(["a"], RotationPicker.PickSequential(Pool, null, 1));
        Assert.Equal(["e", "a", "b"], RotationPicker.PickSequential(Pool, "d", 3));
        Assert.Empty(RotationPicker.PickSequential([], "a", 1));
    }

    [Fact]
    public void Shuffle_shows_every_picture_once_per_cycle()
    {
        var picker = new RotationPicker(new Random(1));
        string? current = null;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var seen = new List<string>();
            for (var i = 0; i < Pool.Length; i++)
            {
                var pick = Assert.Single(picker.Pick(Pool, current, 1, shuffle: true));
                Assert.NotEqual(current, pick); // never the same picture twice in a row
                seen.Add(pick);
                current = pick;
            }
            Assert.Equal(Pool.Order(), seen.Order());
        }
    }

    [Fact]
    public void Shuffle_with_one_picture_returns_it()
    {
        var picker = new RotationPicker(new Random(2));
        Assert.Equal(["only"], picker.Pick(["only"], "only", 1, shuffle: true));
        Assert.Equal(["only"], picker.Pick(["only"], "only", 1, shuffle: true));
    }

    [Fact]
    public void Shuffle_for_several_displays_picks_distinct_pictures()
    {
        var picker = new RotationPicker(new Random(3));
        for (var i = 0; i < 50; i++)
        {
            var picks = picker.Pick(Pool, "a", 3, shuffle: true);
            Assert.Equal(3, picks.Count);
            Assert.Equal(3, picks.Distinct().Count());
        }
        Assert.Equal(2, picker.Pick(["x", "y"], null, 4, shuffle: true).Take(2).Distinct().Count());
    }

    [Fact]
    public void Shuffle_forgets_pictures_that_left_the_pool()
    {
        var picker = new RotationPicker(new Random(4));
        picker.Pick(Pool, null, 3, shuffle: true);
        var smaller = new[] { "a", "z" };
        var picks = picker.Pick(smaller, null, 2, shuffle: true);
        Assert.Equal(smaller.Order(), picks.Order());
        Assert.All(picker.ShownThisCycle, p => Assert.Contains(p, smaller));
    }

    [Fact]
    public void Empty_pool_or_zero_count_picks_nothing()
    {
        var picker = new RotationPicker();
        Assert.Empty(picker.Pick([], "a", 1, true));
        Assert.Empty(picker.Pick(Pool, "a", 0, false));
    }
}

public class WallpaperHistoryTests
{
    [Fact]
    public void Back_and_forward_walk_the_history()
    {
        var h = new WallpaperHistory<string>();
        Assert.False(h.CanGoBack);
        h.Push("1");
        h.Push("2");
        h.Push("3");
        Assert.True(h.TryBack(out var b1));
        Assert.Equal("2", b1);
        Assert.True(h.TryBack(out var b2));
        Assert.Equal("1", b2);
        Assert.False(h.TryBack(out _));
        Assert.True(h.TryForward(out var f1));
        Assert.Equal("2", f1);
        Assert.Equal("2", h.Current);
    }

    [Fact]
    public void Pushing_after_going_back_drops_forward_entries()
    {
        var h = new WallpaperHistory<string>();
        h.Push("1");
        h.Push("2");
        h.Push("3");
        h.TryBack(out _);
        h.Push("4");
        Assert.False(h.CanGoForward);
        Assert.Equal(3, h.Count);
        h.TryBack(out var back);
        Assert.Equal("2", back);
    }

    [Fact]
    public void Oldest_entries_fall_off_at_capacity()
    {
        var h = new WallpaperHistory<int>(capacity: 3);
        for (var i = 1; i <= 5; i++) h.Push(i);
        Assert.Equal(3, h.Count);
        h.TryBack(out _);
        h.TryBack(out var oldest);
        Assert.Equal(3, oldest);
        Assert.False(h.CanGoBack);
        h.Clear();
        Assert.True(h.IsEmpty);
        Assert.False(h.CanGoForward);
    }
}

public class RotationScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Due_counts_from_the_last_change()
    {
        var interval = TimeSpan.FromMinutes(30);
        Assert.Equal(TimeSpan.FromMinutes(20), RotationSchedule.DueIn(Now.AddMinutes(-10), interval, Now));
        Assert.Equal(TimeSpan.Zero, RotationSchedule.DueIn(Now.AddHours(-3), interval, Now));
        Assert.Equal(interval, RotationSchedule.DueIn(null, interval, Now));
        Assert.Equal(interval, RotationSchedule.DueIn(Now.AddHours(1), interval, Now)); // clock went back
        Assert.Equal(TimeSpan.FromMinutes(RotationSchedule.DefaultMinutes), RotationSchedule.DueIn(null, TimeSpan.Zero, Now));
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("1440", 1440)]
    [InlineData("0", RotationSchedule.DefaultMinutes)]
    [InlineData("-5", RotationSchedule.DefaultMinutes)]
    [InlineData("soon", RotationSchedule.DefaultMinutes)]
    [InlineData(null, RotationSchedule.DefaultMinutes)]
    [InlineData("999999", 7 * 1440)]
    public void ParseMinutes_falls_back_and_caps(string? value, int expected) =>
        Assert.Equal(expected, RotationSchedule.ParseMinutes(value));

    [Fact]
    public void Interval_choices_parse_back()
    {
        foreach (var (minutes, _) in RotationSchedule.Intervals)
            Assert.Equal(minutes, RotationSchedule.ParseMinutes(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains(RotationSchedule.Intervals, i => i.Minutes == RotationSchedule.DefaultMinutes);
    }

    [Fact]
    public void Timestamps_round_trip()
    {
        Assert.Equal(Now, RotationSchedule.ParseTimestamp(RotationSchedule.FormatTimestamp(Now)));
        Assert.Null(RotationSchedule.ParseTimestamp(""));
        Assert.Null(RotationSchedule.ParseTimestamp("not a date"));
    }

    [Theory]
    [InlineData("fill", WallpaperFit.Fill)]
    [InlineData("fit", WallpaperFit.Fit)]
    [InlineData("stretch", WallpaperFit.Stretch)]
    [InlineData("tile", WallpaperFit.Tile)]
    [InlineData("center", WallpaperFit.Center)]
    [InlineData("span", WallpaperFit.Span)]
    [InlineData("weird", WallpaperFit.Fill)]
    [InlineData(null, WallpaperFit.Fill)]
    public void ParseFit_defaults_to_fill_like_upstream(string? value, WallpaperFit expected) =>
        Assert.Equal(expected, RotationSchedule.ParseFit(value));

    [Fact]
    public void Fit_values_match_desktop_wallpaper_position()
    {
        // DESKTOP_WALLPAPER_POSITION: CENTER 0, TILE 1, STRETCH 2, FIT 3, FILL 4, SPAN 5
        Assert.Equal(0, (int)WallpaperFit.Center);
        Assert.Equal(4, (int)WallpaperFit.Fill);
        Assert.Equal(5, (int)WallpaperFit.Span);
    }
}

public class DesktopWallpaperApiTests
{
    /// <summary>Read-only: lists displays and their current pictures; never sets anything.</summary>
    [Fact]
    public void Monitors_reads_attached_displays()
    {
        List<WallpaperMonitor> monitors;
        try
        {
            monitors = DesktopWallpaperApi.Monitors();
        }
        catch (COMException)
        {
            return; // no interactive desktop (CI service session)
        }
        Assert.All(monitors, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Id));
            Assert.False(m.Bounds.IsEmpty);
        });
        Assert.Equal(monitors.Count, monitors.Select(m => m.Id).Distinct().Count());
    }
}
