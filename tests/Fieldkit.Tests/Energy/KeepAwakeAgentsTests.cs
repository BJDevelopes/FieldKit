// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Energy;

namespace Fieldkit.Tests.Energy;

public class KeepAwakeAgentsTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoWorkSeenMeansNotWorking() => Assert.False(KeepAwakeLogic.AgentsWorking(null, Now));

    [Theory]
    [InlineData(0, true)]
    [InlineData(119, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void ShortPausesBetweenTurnsKeepItAwake(int secondsAgo, bool working) =>
        Assert.Equal(working, KeepAwakeLogic.AgentsWorking(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void AgentsCountAsAnAutomationCondition()
    {
        Assert.True(KeepAwakeLogic.Satisfied(AwakeCondition.AgentsWorking, AwakeCondition.AgentsWorking | AwakeCondition.Power, requireAll: false));
        Assert.False(KeepAwakeLogic.Satisfied(AwakeCondition.AgentsWorking, AwakeCondition.AgentsWorking | AwakeCondition.Power, requireAll: true));
        Assert.Equal("Active while an AI agent is working", KeepAwakeLogic.AutomationStatus(AwakeCondition.AgentsWorking));
    }
}
