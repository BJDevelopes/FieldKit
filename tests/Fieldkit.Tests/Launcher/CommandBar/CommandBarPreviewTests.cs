// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Launcher.CommandBar;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarPreviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersTheHomeBar(bool dark)
    {
        var path = StaRunner.PreviewPath($"commandbar-home-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => CommandBarView.RenderPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersASearch(bool dark)
    {
        var path = StaRunner.PreviewPath($"commandbar-search-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => CommandBarView.RenderSearchPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Fact]
    public void RendersTheCalculatorActionsAndConfirmation()
    {
        var calc = StaRunner.PreviewPath("commandbar-calculator.png");
        var actions = StaRunner.PreviewPath("commandbar-actions.png");
        var confirm = StaRunner.PreviewPath("commandbar-confirm.png");
        var emoji = StaRunner.PreviewPath("commandbar-emoji.png");
        StaRunner.Run(() =>
        {
            CommandBarPreview.RenderCalculator(calc, true);
            CommandBarPreview.RenderActions(actions, true);
            CommandBarPreview.RenderConfirm(confirm, true);
            CommandBarPreview.RenderSearch(emoji, true, ":heart");
        });
        Assert.True(new FileInfo(calc).Length > 3000);
        Assert.True(new FileInfo(actions).Length > 3000);
        Assert.True(new FileInfo(confirm).Length > 3000);
        Assert.True(new FileInfo(emoji).Length > 3000);
    }
}
