// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Tests.Core;

public class CommandChannelTests
{
    [Fact]
    public void UnknownActionsAreRefusedWithAHint()
    {
        FeatureRuntime.Initialize();
        var reply = CommandChannel.RunAction("no.such.action");
        Assert.StartsWith("error:", reply);
        Assert.Contains("--list-actions", reply);
    }

    [Fact]
    public void EveryActionIsListed()
    {
        FeatureRuntime.Initialize();
        var lines = CommandChannel.DescribeActions().ToList();
        Assert.Equal(FeatureRuntime.Registry.Actions.Count, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("keepAwake.toggle", StringComparison.Ordinal));
    }
}
