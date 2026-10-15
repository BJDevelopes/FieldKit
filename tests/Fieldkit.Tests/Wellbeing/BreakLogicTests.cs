// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Wellbeing;

namespace Fieldkit.Tests.Wellbeing;

public class BreakLogicTests
{
    private static readonly TimeSpan Eyes = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Move = TimeSpan.FromMinutes(60);

    private static BreakDecision Run(BreakState state, int minutes, TimeSpan idle, bool canInterrupt = true)
    {
        BreakDecision d = new(state, false, false);
        for (var i = 0; i < minutes; i++)
        {
            d = BreakLogic.Tick(d.State, TimeSpan.FromMinutes(1), idle, Eyes, Move, canInterrupt);
            if (d.RemindEyes || d.RemindMove) return d;
        }
        return d;
    }

    [Fact]
    public void RemindsEyesAfterTwentyActiveMinutes()
    {
        var d = Run(default, 25, TimeSpan.FromSeconds(3));
        Assert.True(d.RemindEyes);
        Assert.Equal(TimeSpan.Zero, d.State.EyesActive);
    }

    [Fact]
    public void IdleTimeDoesNotCountAndAShortBreakResetsTheEyes()
    {
        var d = Run(default, 30, TimeSpan.FromMinutes(2));
        Assert.False(d.RemindEyes);
        Assert.Equal(TimeSpan.Zero, d.State.EyesActive);
    }

    [Fact]
    public void BeingAwayFiveMinutesResetsMoving()
    {
        var state = new BreakState(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(50));
        var d = BreakLogic.Tick(state, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(6), Eyes, Move, true);
        Assert.Equal(TimeSpan.Zero, d.State.MoveActive);
        Assert.Equal(TimeSpan.Zero, d.State.EyesActive);
    }

    [Fact]
    public void FullScreenDefersUntilItEnds()
    {
        var state = new BreakState(TimeSpan.FromMinutes(25), TimeSpan.Zero);
        var busy = BreakLogic.Tick(state, TimeSpan.FromSeconds(15), TimeSpan.Zero, Eyes, Move, canInterrupt: false);
        Assert.False(busy.RemindEyes);
        var free = BreakLogic.Tick(busy.State, TimeSpan.FromSeconds(15), TimeSpan.Zero, Eyes, Move, canInterrupt: true);
        Assert.True(free.RemindEyes);
    }

    [Fact]
    public void MovingCoversTheEyesToo()
    {
        var state = new BreakState(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60));
        var d = BreakLogic.Tick(state, TimeSpan.FromSeconds(15), TimeSpan.Zero, Eyes, Move, true);
        Assert.True(d.RemindMove);
        Assert.False(d.RemindEyes);
        Assert.Equal(default, d.State);
    }

    [Fact]
    public void DisabledRemindersNeverFire()
    {
        var state = new BreakState(TimeSpan.FromHours(5), TimeSpan.FromHours(5));
        var d = BreakLogic.Tick(state, TimeSpan.FromSeconds(15), TimeSpan.Zero, null, null, true);
        Assert.False(d.RemindEyes || d.RemindMove);
    }
}
