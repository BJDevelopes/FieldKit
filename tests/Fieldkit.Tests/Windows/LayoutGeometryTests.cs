// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.WindowTools;

using Fieldkit.Interop;

namespace Fieldkit.Tests.Windows;

public class LayoutGeometryTests
{
    // A work area that does not start at the origin (taskbar on the left, second display, ...).
    private static readonly Rect Work = new(100, 40, 1600, 1000);
    private static readonly Rect Current = new(400, 300, 600, 400);

    private static Rect R(LayoutAction a, double windowGap = 0, double screenGap = 0, double margin = 5) =>
        LayoutGeometry.RectFor(a, Current, Work, windowGap, screenGap, margin);

    [Theory]
    [InlineData(LayoutAction.LeftHalf, 100, 40, 800, 1000)]
    [InlineData(LayoutAction.RightHalf, 900, 40, 800, 1000)]
    [InlineData(LayoutAction.TopHalf, 100, 40, 1600, 500)]
    [InlineData(LayoutAction.BottomHalf, 100, 540, 1600, 500)]
    [InlineData(LayoutAction.CenterHalf, 500, 40, 800, 1000)]
    [InlineData(LayoutAction.TopLeft, 100, 40, 800, 500)]
    [InlineData(LayoutAction.BottomRight, 900, 540, 800, 500)]
    [InlineData(LayoutAction.Maximize, 100, 40, 1600, 1000)]
    [InlineData(LayoutAction.TopQuarter, 100, 40, 1600, 250)]
    [InlineData(LayoutAction.UpperMiddleQuarter, 100, 290, 1600, 250)]
    [InlineData(LayoutAction.LowerMiddleQuarter, 100, 540, 1600, 250)]
    [InlineData(LayoutAction.BottomQuarter, 100, 790, 1600, 250)]
    [InlineData(LayoutAction.LeftQuarter, 100, 40, 400, 1000)]
    [InlineData(LayoutAction.RightMiddleQuarter, 900, 40, 400, 1000)]
    [InlineData(LayoutAction.RightQuarter, 1300, 40, 400, 1000)]
    public void PlacesExactFractions(LayoutAction action, double x, double y, double w, double h)
    {
        Assert.Equal(new Rect(x, y, w, h), R(action));
    }

    [Fact]
    public void ThirdsShareEdgesWithoutGapsOrOverlap()
    {
        var left = R(LayoutAction.LeftThird);
        var center = R(LayoutAction.CenterThird);
        var right = R(LayoutAction.RightThird);
        Assert.Equal(Work.Left, left.Left);
        Assert.Equal(left.Right, center.Left);
        Assert.Equal(center.Right, right.Left);
        Assert.Equal(Work.Right, right.Right);
        Assert.Equal(R(LayoutAction.LeftTwoThirds).Right, center.Right);
        Assert.Equal(R(LayoutAction.RightTwoThirds).Left, left.Right);
        var c23 = R(LayoutAction.CenterTwoThirds);
        Assert.Equal(Work.Left + Work.Width / 2, c23.Left + c23.Width / 2, 0);
    }

    [Fact]
    public void VerticalThirdsFromTheTop()
    {
        Assert.Equal(Work.Top, R(LayoutAction.TopThird).Top);
        Assert.Equal(R(LayoutAction.TopThird).Bottom, R(LayoutAction.MiddleThird).Top);
        Assert.Equal(Work.Bottom, R(LayoutAction.BottomThird).Bottom);
        Assert.Equal(Work.Bottom, R(LayoutAction.BottomTwoThirds).Bottom);
        Assert.Equal(Work.Top, R(LayoutAction.TopTwoThirds).Top);
    }

    [Fact]
    public void SixthsTileTheScreen()
    {
        LayoutAction[] all =
        [
            LayoutAction.TopLeftSixth, LayoutAction.TopCenterSixth, LayoutAction.TopRightSixth,
            LayoutAction.BottomLeftSixth, LayoutAction.BottomCenterSixth, LayoutAction.BottomRightSixth,
        ];
        var area = all.Select(a => R(a)).Sum(r => r.Width * r.Height);
        Assert.Equal(Work.Width * Work.Height, area, 0);
        Assert.Equal(Work.Top, R(LayoutAction.TopCenterSixth).Top);
        Assert.Equal(Work.Bottom, R(LayoutAction.BottomRightSixth).Bottom);
        Assert.Equal(Work.Right, R(LayoutAction.TopRightSixth).Right);
    }

    [Fact]
    public void CenterKeepsSizeAndClampsToTheScreen()
    {
        var r = R(LayoutAction.Center);
        Assert.Equal(Current.Size, r.Size);
        Assert.Equal(new Point(900, 540), new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
        var big = LayoutGeometry.RectFor(LayoutAction.Center, new Rect(0, 0, 3000, 3000), Work);
        Assert.Equal(Work, big);
    }

    [Fact]
    public void MarginMaximizeInsetsEveryEdge()
    {
        Assert.Equal(new Rect(180, 90, 1440, 900), R(LayoutAction.MarginMaximize));
        Assert.Equal(Work, R(LayoutAction.MarginMaximize, margin: 0));
        Assert.Equal(25, LayoutGeometry.SanitizedMargin(90));
    }

    [Fact]
    public void ScreenGapInsetsAndWindowGapSplitsBetweenNeighbours()
    {
        var left = R(LayoutAction.LeftHalf, windowGap: 16, screenGap: 10);
        var right = R(LayoutAction.RightHalf, windowGap: 16, screenGap: 10);
        Assert.Equal(Work.Left + 10, left.Left);
        Assert.Equal(Work.Top + 10, left.Top);
        Assert.Equal(16, right.Left - left.Right);
        Assert.Equal(Work.Right - 10, right.Right);
        // Maximize ignores the window gap but keeps the screen gap.
        Assert.Equal(new Rect(110, 50, 1580, 980), R(LayoutAction.Maximize, windowGap: 16, screenGap: 10));
    }

    [Fact]
    public void RepeatedTopMaximizesAndSideCycleRotates()
    {
        Assert.Equal(LayoutAction.Maximize, LayoutGeometry.EffectiveAction(LayoutAction.TopHalf, LayoutAction.TopHalf, false));
        Assert.Equal(LayoutAction.LeftHalf, LayoutGeometry.EffectiveAction(LayoutAction.LeftHalf, LayoutAction.LeftHalf, false));
        Assert.Equal(LayoutAction.LeftTwoThirds, LayoutGeometry.EffectiveAction(LayoutAction.LeftHalf, LayoutAction.LeftHalf, true));
        Assert.Equal(LayoutAction.LeftThird, LayoutGeometry.EffectiveAction(LayoutAction.LeftHalf, LayoutAction.LeftTwoThirds, true));
        Assert.Equal(LayoutAction.LeftHalf, LayoutGeometry.EffectiveAction(LayoutAction.LeftHalf, LayoutAction.LeftThird, true));
        // A left after a right third starts over.
        Assert.Equal(LayoutAction.LeftHalf, LayoutGeometry.EffectiveAction(LayoutAction.LeftHalf, LayoutAction.RightThird, true));
    }

    [Fact]
    public void RepeatedHalfCrossesToTheNeighbouringDisplay()
    {
        Assert.Equal((LayoutAction.RightHalf, DisplayDirection.Left),
            LayoutGeometry.DisplayCrossing(LayoutAction.LeftHalf, LayoutAction.LeftHalf, false));
        Assert.Equal((LayoutAction.TopHalf, DisplayDirection.Down),
            LayoutGeometry.DisplayCrossing(LayoutAction.BottomHalf, LayoutAction.BottomHalf, false));
        Assert.Null(LayoutGeometry.DisplayCrossing(LayoutAction.LeftHalf, LayoutAction.LeftHalf, true));
        Assert.Null(LayoutGeometry.DisplayCrossing(LayoutAction.LeftHalf, LayoutAction.RightHalf, false));
    }

    [Fact]
    public void NeighbourDisplaysByDirection()
    {
        // [0] main 1920x1080, [1] right of it, [2] above it, [3] left of it, slightly lower.
        Rect[] frames =
        [
            new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440), new(0, -1080, 1920, 1080), new(-1280, 200, 1280, 1024),
        ];
        Assert.Equal(1, LayoutGeometry.NeighbourIndex(0, frames, DisplayDirection.Right));
        Assert.Equal(3, LayoutGeometry.NeighbourIndex(0, frames, DisplayDirection.Left));
        Assert.Equal(2, LayoutGeometry.NeighbourIndex(0, frames, DisplayDirection.Up));
        Assert.Null(LayoutGeometry.NeighbourIndex(0, frames, DisplayDirection.Down));
        Assert.Equal(0, LayoutGeometry.NeighbourIndex(2, frames, DisplayDirection.Down));
    }

    [Fact]
    public void NextAndPreviousDisplayWrapLeftToRight()
    {
        Rect[] frames = [new(0, 0, 1920, 1080), new(-1280, 0, 1280, 1024), new(1920, 0, 1920, 1080)];
        Assert.Equal(2, LayoutGeometry.AdjacentDisplayIndex(0, frames, forward: true));
        Assert.Equal(1, LayoutGeometry.AdjacentDisplayIndex(2, frames, forward: true));
        Assert.Equal(2, LayoutGeometry.AdjacentDisplayIndex(1, frames, forward: false));
        Assert.Null(LayoutGeometry.AdjacentDisplayIndex(0, [frames[0]], forward: true));
    }

    [Fact]
    public void MovingToAnotherDisplayKeepsSizeAndInsets()
    {
        var source = new Rect(0, 0, 1920, 1040);
        var destination = new Rect(1920, 0, 2560, 1400);
        var moved = LayoutGeometry.RectForDisplay(new Rect(100, 50, 800, 600), source, destination);
        Assert.Equal(new Rect(2020, 50, 800, 600), moved);
        // Flush right stays flush right.
        var right = LayoutGeometry.RectForDisplay(new Rect(1120, 0, 800, 1040), source, destination);
        Assert.Equal(destination.Right, right.Right);
        // Filling the source fills the destination.
        Assert.Equal(destination, LayoutGeometry.RectForDisplay(source, source, destination));
        // Too big shrinks to fit.
        var small = new Rect(0, 0, 1280, 720);
        var shrunk = LayoutGeometry.RectForDisplay(new Rect(10, 10, 1500, 700), source, small);
        Assert.True(shrunk.Width <= small.Width && shrunk.Height <= small.Height);
    }

    [Fact]
    public void DisplayIndexForPicksTheLargestOverlap()
    {
        Rect[] frames = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
        Assert.Equal(1, LayoutGeometry.DisplayIndexFor(new Rect(1800, 100, 800, 600), frames));
        Assert.Equal(0, LayoutGeometry.DisplayIndexFor(new Rect(-5000, 100, 10, 10), frames));
        Assert.Equal(1, LayoutGeometry.DisplayIndexAt(new Point(2000, 10), frames));
    }

    [Fact]
    public void HistoryPopsTheLastDifferentFrame()
    {
        var history = new LayoutHistory();
        var w = new IntPtr(42);
        history.Record(w, new Rect(0, 0, 100, 100));
        history.Record(w, new Rect(10, 10, 100, 100));
        Assert.Equal(new Rect(0, 0, 100, 100), history.PopPrevious(w, new Rect(10, 10, 100, 100)));
        Assert.Null(history.PopPrevious(w, new Rect(0, 0, 100, 100)));
        for (var i = 0; i < 30; i++) history.Record(w, new Rect(i, 0, 10, 10));
        Assert.Equal(LayoutHistory.PerWindowLimit, history.Count(w));
    }

    [Fact]
    public void ActionIdsRoundTripAndDefaultsParse()
    {
        foreach (var action in LayoutActions.All)
        {
            Assert.Equal(action, LayoutActions.FromId(action.Id()));
            var text = action.DefaultShortcut();
            if (text.Length > 0) Assert.Equal(text, Fieldkit.Interop.Shortcut.Parse(text).ToString());
        }
        Assert.Equal("leftHalf", LayoutAction.LeftHalf.Id());
        Assert.Equal(43, LayoutActions.All.Count);
        Assert.Equal(LayoutActions.All.Count, LayoutActions.Groups.Sum(g => g.Actions.Length));
        var hidden = LayoutActions.ParseHidden("topHalf, leftHalf,bogus");
        Assert.Equal("leftHalf,topHalf", LayoutActions.HiddenStorage(hidden));
    }

    [Fact]
    public void DefaultShortcutsAreUnique()
    {
        var defaults = LayoutActions.All.Select(a => a.DefaultShortcut()).Where(s => s.Length > 0).ToList();
        Assert.Equal(defaults.Count, defaults.Distinct().Count());
        foreach (var text in defaults)
        {
            var shortcut = Shortcut.Parse(text);
            // Win+X without Ctrl+Alt belongs to Windows (Snap, Explorer, ...).
            if (shortcut.Modifiers.HasFlag(Modifiers.Win))
                Assert.True(shortcut.Modifiers.HasFlag(Modifiers.Ctrl) && shortcut.Modifiers.HasFlag(Modifiers.Alt), text);
            // Ctrl+Alt+letter/digit is AltGr+letter on many layouts (€, @, ...): it must also carry Win or Shift.
            var printable = shortcut.Key is >= 0x30 and <= 0x5A;
            if (printable && shortcut.Modifiers == (Modifiers.Ctrl | Modifiers.Alt)) Assert.Fail($"{text} would block AltGr typing");
        }
    }
}

public class LayoutStepTests
{
    private static readonly System.Windows.Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void GrowsAroundTheCentre()
    {
        var current = new System.Windows.Rect(560, 320, 800, 400);
        var r = Fieldkit.Features.WindowTools.LayoutGeometry.RectFor(Fieldkit.Features.WindowTools.LayoutAction.Grow, current, Work);
        Assert.Equal(800 + 2 * 96, r.Width);
        Assert.Equal(400 + 2 * 52, r.Height);
        Assert.Equal(current.Left + current.Width / 2, r.Left + r.Width / 2, 1);
    }

    [Fact]
    public void GrowingAtAnEdgeStaysOnScreen()
    {
        var current = new System.Windows.Rect(1700, 900, 220, 140);
        var r = Fieldkit.Features.WindowTools.LayoutGeometry.RectFor(Fieldkit.Features.WindowTools.LayoutAction.Grow, current, Work);
        Assert.True(r.Right <= Work.Right && r.Bottom <= Work.Bottom);
        Assert.True(r.Width > current.Width);
    }

    [Fact]
    public void ShrinkingStopsAtTheMinimum()
    {
        var current = new System.Windows.Rect(800, 400, 210, 210);
        var r = Fieldkit.Features.WindowTools.LayoutGeometry.RectFor(Fieldkit.Features.WindowTools.LayoutAction.Shrink, current, Work);
        Assert.Equal(200, r.Width);
        Assert.Equal(200, r.Height);
    }

    [Fact]
    public void GrowingNeverExceedsTheScreen()
    {
        var r = Fieldkit.Features.WindowTools.LayoutGeometry.RectFor(Fieldkit.Features.WindowTools.LayoutAction.Grow, Work, Work);
        Assert.Equal(Work, r);
    }
}
