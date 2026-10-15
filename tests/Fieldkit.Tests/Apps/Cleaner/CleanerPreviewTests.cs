// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Cleaner;
using Fieldkit.Features.Apps.Cleaner.Messaging;

namespace Fieldkit.Tests.Apps.Cleaner;

/// <summary>Renders the tool windows offscreen with sample data (PNGs kept in %TEMP%\fieldkit-previews for review).</summary>
public class CleanerPreviewTests
{
    [Theory]
    [InlineData("results", true)]
    [InlineData("results", false)]
    [InlineData("idle", true)]
    [InlineData("idle", false)]
    [InlineData("scanning", true)]
    [InlineData("done", true)]
    [InlineData("empty", false)]
    public void RendersCleaner(string state, bool dark)
    {
        var path = StaRunner.PreviewPath($"cleaner-{state}-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => CleanerWindow.RenderPreview(path, dark, state));
        Assert.True(new FileInfo(path).Length > 1000);
    }

    [Fact]
    public void RendersSettingsPage()
    {
        var path = StaRunner.PreviewPath("cleaner-settings-dark.png");
        StaRunner.Run(() => CleanerSettingsPage.RenderPreview(path, dark: true));
        Assert.True(new FileInfo(path).Length > 1000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersMessagingReview(bool dark)
    {
        var path = StaRunner.PreviewPath($"cleaner-messaging-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => MessagingWindow.RenderPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 1000);
    }
}
