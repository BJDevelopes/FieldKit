// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Energy;

namespace Fieldkit.Tests.Energy;

public class PowerTimerTests
{
    [Theory]
    [InlineData(PowerTimerAction.Shutdown, 65, "Shutting down in 1 h 05 min")]
    [InlineData(PowerTimerAction.Sleep, 30, "Sleeping in 30 min")]
    [InlineData(PowerTimerAction.Restart, 0.5, "Restarting in 30 s")]
    [InlineData(PowerTimerAction.Hibernate, -1, "Hibernating in 1 s")]
    public void StatusReadsNaturally(PowerTimerAction action, double minutes, string expected) =>
        Assert.Equal(expected, PowerTimerLogic.Status(action, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void WarnsOnceInTheLastMinute()
    {
        Assert.False(PowerTimerLogic.ShouldWarn(TimeSpan.FromMinutes(2), warned: false));
        Assert.True(PowerTimerLogic.ShouldWarn(TimeSpan.FromSeconds(59), warned: false));
        Assert.False(PowerTimerLogic.ShouldWarn(TimeSpan.FromSeconds(30), warned: true));
    }

    [Theory]
    [InlineData("Shutdown", PowerTimerAction.Shutdown)]
    [InlineData("restart", PowerTimerAction.Restart)]
    [InlineData(null, PowerTimerAction.Sleep)]
    [InlineData("nonsense", PowerTimerAction.Sleep)]
    public void ParsesStoredChoice(string? value, PowerTimerAction expected) => Assert.Equal(expected, PowerTimerLogic.Parse(value));
}
