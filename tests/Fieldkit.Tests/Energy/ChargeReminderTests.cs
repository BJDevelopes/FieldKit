// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Energy;

namespace Fieldkit.Tests.Energy;

public class ChargeReminderTests
{
    [Fact]
    public void RemindsOncePerCharge()
    {
        var (remind, reminded) = ChargeReminderLogic.Evaluate(pluggedIn: true, 80, 80, reminded: false);
        Assert.True(remind);
        (remind, reminded) = ChargeReminderLogic.Evaluate(true, 83, 80, reminded);
        Assert.False(remind);
        Assert.True(reminded);
    }

    [Fact]
    public void UnpluggingReArms()
    {
        var (_, reminded) = ChargeReminderLogic.Evaluate(false, 85, 80, reminded: true);
        Assert.False(reminded);
        Assert.True(ChargeReminderLogic.Evaluate(true, 85, 80, reminded).Remind);
    }

    [Fact]
    public void SmallDipsDoNotReArm()
    {
        Assert.True(ChargeReminderLogic.Evaluate(true, 78, 80, reminded: true).Reminded);
        Assert.False(ChargeReminderLogic.Evaluate(true, 74, 80, reminded: true).Reminded);
    }

    [Fact]
    public void BelowTheLimitStaysQuiet() => Assert.False(ChargeReminderLogic.Evaluate(true, 70, 80, false).Remind);
}
