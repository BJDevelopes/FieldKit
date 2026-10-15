// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Input.Scrolling;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Input;

public class ScrollMathTests
{
    [Fact]
    public void FrameDeltaComposesOverSplitIntervals()
    {
        var once = 100 - SmoothScrollMath.FrameDelta(100, 0.02, 65);
        var first = 100 - SmoothScrollMath.FrameDelta(100, 0.01, 65);
        var twice = first - SmoothScrollMath.FrameDelta(first, 0.01, 65);
        Assert.Equal(once, twice, 6);
    }

    [Fact]
    public void FrameDeltaClampsLongStalls()
    {
        Assert.Equal(SmoothScrollMath.FrameDelta(1000, SmoothScrollMath.MaximumFrameIntervalSeconds, 65),
            SmoothScrollMath.FrameDelta(1000, 5, 65), 9);
    }

    [Fact]
    public void FrameDeltaFlushesTinyRemainders()
    {
        Assert.Equal(0.5, SmoothScrollMath.FrameDelta(0.5, 0.008, 65));
        Assert.Equal(-0.5, SmoothScrollMath.FrameDelta(-0.5, 0.008, 65));
        Assert.Equal(0, SmoothScrollMath.FrameDelta(0, 0.008, 65));
    }

    [Fact]
    public void HigherResponseMovesFaster()
    {
        Assert.True(SmoothScrollMath.FrameDelta(100, 0.008, 100) > SmoothScrollMath.FrameDelta(100, 0.008, 0));
    }

    [Fact]
    public void CoastSlowsOnlyTheLanding()
    {
        Assert.Equal(1, SmoothScrollMath.CoastStretch(100, 40, 40));        // start of a notch: unchanged
        Assert.True(SmoothScrollMath.CoastStretch(100, 4, 40) > 2.5);       // near the end: stretched
        Assert.Equal(1, SmoothScrollMath.CoastStretch(0, 4, 40));           // coast off
    }

    [Fact]
    public void EngineGlidesTheWholeDistanceAndFinishes()
    {
        var engine = new SmoothScrollMath.Engine();
        engine.Add(40, 0);
        double total = 0;
        var frames = 0;
        SmoothScrollMath.Frame frame;
        do
        {
            frame = engine.Advance(1 / 120.0, 65);
            total += frame.Vertical;
            frames++;
        } while (!frame.Finished && frames < 1000);
        Assert.True(frame.Finished);
        Assert.Equal(40, total, 9);
        Assert.InRange(frames, 5, 400);
    }

    [Fact]
    public void EngineReversalReplacesTheTail()
    {
        var engine = new SmoothScrollMath.Engine();
        engine.Add(120, 0);
        engine.Advance(0.01, 65);
        engine.Add(-40, 0);
        Assert.Equal(-40, engine.RemainingVertical);
    }

    [Fact]
    public void WholeUnitsCarryTheFraction()
    {
        var (whole, carry) = SmoothScrollMath.WholeUnits(2.7, 0.5);
        Assert.Equal(3, whole);
        Assert.Equal(0.2, carry, 9);
        Assert.Equal(-3, SmoothScrollMath.WholeUnits(-3.4, 0).Whole);
        Assert.Equal(4, SmoothScrollMath.FinalUnits(3.4, 0.2));
        Assert.Equal(0, SmoothScrollMath.Carry(0.4, -2));
        Assert.Equal(0.4, SmoothScrollMath.Carry(0.4, 2));
    }

    [Fact]
    public void DefaultStepScrollsOneSystemNotch() =>
        Assert.Equal(120, SmoothScrollMath.DefaultStep * SmoothScrollMath.UnitsPerPixel);

    [Theory]
    [InlineData(120, 3, 3, 120)]   // ordinary notch at defaults: unchanged
    [InlineData(360, 3, 3, 120)]   // accelerated burst: one notch
    [InlineData(-480, 3, 3, -120)]
    [InlineData(60, 3, 3, 60)]     // high-resolution fraction stays a fraction
    [InlineData(120, 1, 3, 40)]    // one line per step on a 3-line system
    [InlineData(120, 6, 3, 240)]
    [InlineData(120, 3, 0, 120)]   // page scrolling / unknown system value counts as 3
    public void LinearNormalizes(int delta, int lines, int system, int expected) =>
        Assert.Equal(expected, LinearScrollMath.Normalize(delta, lines, system, 0).Delta);

    [Fact]
    public void LinearCarriesFractionsAndDropsThemOnReversal()
    {
        var (d1, c1) = LinearScrollMath.Normalize(120, 1, 7, 0); // 120/7 = 17.14
        Assert.Equal(17, d1);
        Assert.True(c1 > 0);
        var (_, c2) = LinearScrollMath.Normalize(-120, 1, 7, c1);
        Assert.True(c2 < 0);
    }

    [Fact]
    public void SidewaysRedirectNeedsExactlyTheChosenModifier()
    {
        Assert.True(ScrollDirectionMath.ShouldRedirect(false, Modifiers.Shift, HorizontalScrollModifier.Shift));
        Assert.False(ScrollDirectionMath.ShouldRedirect(false, Modifiers.Shift | Modifiers.Ctrl, HorizontalScrollModifier.Shift));
        Assert.False(ScrollDirectionMath.ShouldRedirect(true, Modifiers.Shift, HorizontalScrollModifier.Shift));
        Assert.False(ScrollDirectionMath.ShouldRedirect(false, Modifiers.Shift, null));
        Assert.True(ScrollDirectionMath.ShouldRedirect(false, Modifiers.Win, HorizontalScrollModifier.Win));
    }

    [Fact]
    public void DirectionFollowsTheAxisTheUserSees()
    {
        Assert.Equal((false, 120), ScrollDirectionMath.Apply(false, -120, false, true, false));
        Assert.Equal((true, -120), ScrollDirectionMath.Apply(true, 120, false, false, true));
        // Wheel down (negative) turned sideways scrolls right (positive)...
        Assert.Equal((true, 120), ScrollDirectionMath.Apply(false, -120, true, false, false));
        // ...and the horizontal inversion, not the vertical one, applies to it.
        Assert.Equal((true, -120), ScrollDirectionMath.Apply(false, -120, true, false, true));
        Assert.Equal((true, 120), ScrollDirectionMath.Apply(false, -120, true, true, false));
    }

    [Theory]
    [InlineData("shift", HorizontalScrollModifier.Shift)]
    [InlineData("option", HorizontalScrollModifier.Alt)]
    [InlineData("ctrl", HorizontalScrollModifier.Ctrl)]
    [InlineData("command", HorizontalScrollModifier.Win)]
    [InlineData(null, HorizontalScrollModifier.Shift)]
    [InlineData("garbage", HorizontalScrollModifier.Shift)]
    public void ModifierParses(string? raw, HorizontalScrollModifier expected) =>
        Assert.Equal(expected, ScrollDirectionMath.ParseModifier(raw));
}
