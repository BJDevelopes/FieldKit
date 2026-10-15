// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.Launcher.RadialMenu;

namespace Fieldkit.Tests.Launcher.RadialMenu;

public class RadialGeometryTests
{
    [Theory]
    [InlineData(0, 1, 0)]                  // straight up = 12 o'clock
    [InlineData(1, 0, Math.PI / 2)]        // right = 3 o'clock
    [InlineData(0, -1, Math.PI)]           // down
    [InlineData(-1, 0, 3 * Math.PI / 2)]   // left
    public void AnglesRunClockwiseFromTop(double dx, double dyUp, double expected)
    {
        Assert.Equal(expected, RadialGeometry.Angle(dx, dyUp), 9);
    }

    [Fact]
    public void AngleIsNeverNegative()
    {
        for (var a = 0.0; a < 2 * Math.PI; a += 0.1)
        {
            var angle = RadialGeometry.Angle(Math.Sin(a), Math.Cos(a));
            Assert.InRange(angle, 0, 2 * Math.PI);
        }
    }

    [Fact]
    public void SlicesAreCentredOnTheirIndex()
    {
        // Four slices: 0 spans -45°..45°, 1 spans 45°..135°, ...
        Assert.Equal(0, RadialGeometry.IndexForAngle(0, 4));
        Assert.Equal(0, RadialGeometry.IndexForAngle(Math.PI / 4 - 0.01, 4));
        Assert.Equal(1, RadialGeometry.IndexForAngle(Math.PI / 4 + 0.01, 4));
        Assert.Equal(0, RadialGeometry.IndexForAngle(2 * Math.PI - 0.1, 4));
        Assert.Equal(3, RadialGeometry.IndexForAngle(3 * Math.PI / 2, 4));
        Assert.Null(RadialGeometry.IndexForAngle(1, 0));
        Assert.Equal(0, RadialGeometry.IndexForAngle(3, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    public void EverySliceCentreMapsBackToItsIndex(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (dx, dyUp) = RadialGeometry.UnitPosition(i, count);
            Assert.Equal(i, RadialGeometry.HighlightedIndex(dx * 100, dyUp * 100, 40, count));
        }
    }

    [Fact]
    public void DeadZoneHighlightsNothing()
    {
        Assert.Null(RadialGeometry.HighlightedIndex(10, 10, 40, 6));
        Assert.Null(RadialGeometry.HighlightedIndex(0, 39.9, 40, 6));
        Assert.Equal(0, RadialGeometry.HighlightedIndex(0, 40, 40, 6));
        Assert.Null(RadialGeometry.HighlightedIndex(0, 100, 40, 0));
    }

    [Fact]
    public void ChipCentresUseScreenCoordinates()
    {
        var c = new Point(200, 200);
        var top = RadialGeometry.ChipCenter(0, 4, 112, c);
        Assert.Equal(200, top.X, 9);
        Assert.Equal(88, top.Y, 9);
        var right = RadialGeometry.ChipCenter(1, 4, 112, c);
        Assert.Equal(312, right.X, 9);
        Assert.Equal(200, right.Y, 9);
    }

    [Fact]
    public void GuidesSitBetweenSlices()
    {
        Assert.Empty(RadialGeometry.GuideAngles(1));
        var guides = RadialGeometry.GuideAngles(4);
        Assert.Equal(4, guides.Count);
        Assert.Equal(Math.PI / 4, guides[0], 9);
        var (center, width) = RadialGeometry.SliceAngles(2, 4);
        Assert.Equal(Math.PI, center, 9);
        Assert.Equal(Math.PI / 2, width, 9);
    }

    [Fact]
    public void WedgeTakesTheShortWayRound()
    {
        // From the last of six slices (300°) to the first (0°): +60°, not -300°.
        var from = 5 * Math.PI / 3;
        var to = RadialGeometry.ShortestTarget(from, 0);
        Assert.Equal(from + Math.PI / 3, to, 9);
        // Unwrapped values keep stepping forward.
        Assert.Equal(to + Math.PI / 3, RadialGeometry.ShortestTarget(to, Math.PI / 3), 9);
        Assert.Equal(1.0, RadialGeometry.ShortestTarget(1.0, 1.0), 9);
    }

    [Fact]
    public void OpeningSweepFitsTheSameDuration()
    {
        Assert.Equal(0, RadialGeometry.OpenDelay(0, 1));
        Assert.Equal(0, RadialGeometry.OpenDelay(0, 12));
        Assert.Equal(RadialLayout.SweepDuration, RadialGeometry.OpenDelay(11, 12), 9);
        Assert.Equal(RadialLayout.SweepDuration, RadialGeometry.OpenDelay(5, 6), 9);
    }

    [Theory]
    [InlineData(0, 0, RadialPointerZone.Hub)]
    [InlineData(39, 0, RadialPointerZone.Hub)]
    [InlineData(41, 0, RadialPointerZone.Ring)]
    [InlineData(150, 0, RadialPointerZone.Ring)]
    [InlineData(151, 0, RadialPointerZone.Outside)]
    [InlineData(120, 120, RadialPointerZone.Outside)]
    public void ClicksResolveByDistance(double dx, double dyUp, RadialPointerZone expected)
    {
        Assert.Equal(expected, RadialGeometry.Zone(dx, dyUp));
    }

    [Fact]
    public void PointerMustTravelBeforeArming()
    {
        Assert.False(RadialGeometry.PointerArmed(3, 4));       // 5 px
        Assert.True(RadialGeometry.PointerArmed(6, 8));        // 10 px
        Assert.True(RadialGeometry.PointerArmed(16, 0, 16));
    }

    [Fact]
    public void WheelStaysOnScreenNearEdges()
    {
        var work = new Rect(0, 0, 1920, 1040);
        Assert.Equal(new Point(960, 500), RadialGeometry.ClampCenter(new Point(960, 500), work, 400));
        Assert.Equal(new Point(200, 200), RadialGeometry.ClampCenter(new Point(10, 5), work, 400));
        Assert.Equal(new Point(1720, 840), RadialGeometry.ClampCenter(new Point(1919, 1039), work, 400));
        // Second monitor to the left with negative coordinates.
        var left = new Rect(-1280, 0, 1280, 984);
        Assert.Equal(new Point(-200, 784), RadialGeometry.ClampCenter(new Point(-5, 980), left, 400));
        // Larger panel (scaled 150 %).
        Assert.Equal(new Point(300, 300), RadialGeometry.ClampCenter(new Point(0, 0), work, 600));
    }

    [Fact]
    public void TinyAreasLeaveTheCentreAlone()
    {
        Assert.Equal(new Point(5, 5), RadialGeometry.ClampCenter(new Point(5, 5), new Rect(0, 0, 300, 300), 400));
    }

    [Fact]
    public void CardStaysInsideTheWorkArea()
    {
        var area = new Rect(0, 0, 1000, 800);
        Assert.Equal(new Point(340, 360), RadialGeometry.PlaceCard(new Point(500, 400), new Size(320, 80), area));
        Assert.Equal(new Point(12, 12), RadialGeometry.PlaceCard(new Point(0, 0), new Size(320, 80), area));
        Assert.Equal(new Point(668, 708), RadialGeometry.PlaceCard(new Point(1000, 800), new Size(320, 80), area));
    }

    [Fact]
    public void DragDropPicksTheSliceUnderThePointer()
    {
        var c = new Point(150, 150);
        Assert.Equal(0, RadialGeometry.DropSlot(new Point(150, 20), c, 6));
        Assert.Equal(3, RadialGeometry.DropSlot(new Point(150, 280), c, 6));
        Assert.Equal(1, RadialGeometry.DropSlot(new Point(260, 90), c, 6));
        Assert.Null(RadialGeometry.DropSlot(new Point(0, 0), c, 0));
    }
}
