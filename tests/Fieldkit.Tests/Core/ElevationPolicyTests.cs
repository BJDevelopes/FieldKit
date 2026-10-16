// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Tests.Core;

public class ElevationPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]   // switch on, normal user, plain start: ask
    [InlineData(false, false, false, false)] // switch off: never ask
    [InlineData(true, true, false, false)]   // already administrator: nothing to do
    [InlineData(true, false, true, false)]   // this is the restart that was asked for: no loop if it was declined
    [InlineData(false, true, true, false)]
    public void ItAsksOnlyWhenTheSwitchIsOnAndFieldkitIsNotElevatedYet(bool always, bool elevated, bool isRestart, bool expected)
    {
        string[] args = isRestart ? ["--elevated-restart", "--background"] : ["--background"];

        Assert.Equal(expected, ElevationPolicy.ShouldElevateAtStartup(always, elevated, args));
    }

    [Fact]
    public void TheRestartSwitchIsRecognisedWhateverItsCase() =>
        Assert.False(ElevationPolicy.ShouldElevateAtStartup(true, false, ["--ELEVATED-RESTART"]));

    [Fact]
    public void TheElevatedCopyGetsTheSameArgumentsPlusTheRestartSwitch()
    {
        Assert.Equal("--elevated-restart", ElevationPolicy.RestartArguments([]));
        Assert.Equal("--elevated-restart --background", ElevationPolicy.RestartArguments(["--background"]));
        Assert.Equal("--elevated-restart --action keepAwake.toggle", ElevationPolicy.RestartArguments(["--action", "keepAwake.toggle"]));
    }

    [Fact]
    public void ArgumentsWithSpacesOrQuotesStayOneArgument()
    {
        Assert.Equal("--elevated-restart --open-url \"https://example.com/a b\"", ElevationPolicy.RestartArguments(["--open-url", "https://example.com/a b"]));
        Assert.Equal("--elevated-restart \"say \\\"hi\\\"\"", ElevationPolicy.RestartArguments(["say \"hi\""]));
        Assert.Equal("--elevated-restart \"\"", ElevationPolicy.RestartArguments([""]));
    }

    [Fact]
    public void AnExistingRestartSwitchIsNotDoubled() =>
        Assert.Equal("--elevated-restart --background", ElevationPolicy.RestartArguments(["--elevated-restart", "--background"]));

    [Fact]
    public void TheSwitchIsOffUntilTheUserTurnsItOn() => Assert.Equal("general.alwaysAdmin", ElevationPolicy.AlwaysKey);
}
