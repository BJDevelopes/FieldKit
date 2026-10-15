// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Uninstaller;

namespace Fieldkit.Tests.Apps.Uninstaller;

/// <summary>Renders the Uninstaller's tool-window views offscreen (PNG files kept in %TEMP%\fieldkit-previews).</summary>
public class UninstallerPreviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersAppList(bool dark)
    {
        var path = StaRunner.PreviewPath($"uninstaller-list-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => AppListView.RenderPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersLeftoverReview(bool dark)
    {
        var path = StaRunner.PreviewPath($"uninstaller-review-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => LeftoverReviewView.RenderPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Fact]
    public void RendersStatusViews()
    {
        var busy = StaRunner.PreviewPath("uninstaller-busy.png");
        var done = StaRunner.PreviewPath("uninstaller-done.png");
        var confirm = StaRunner.PreviewPath("uninstaller-confirm.png");
        StaRunner.Run(() =>
        {
            PreviewRenderer.Render(StatusViews.Busy("Running VLC media player’s uninstaller…", "VLC media player", null,
                "Follow the app’s uninstaller, then come back here to review what it left behind.", secondaryText: "Stop waiting", onSecondary: () => { }),
                busy, 720, 420);
            var failed = new Leftover { Path = @"C:\ProgramData\VideoLAN", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support };
            PreviewRenderer.Render(StatusViews.Done(214_000_000, [(failed, "Needs administrator rights")], @"C:\Users\alex\AppData\Roaming\Fieldkit\Registry backups", () => { }),
                done, 720, 520);
            var review = LeftoverReviewView.Build(LeftoverReviewView.SampleModel());
            var grid = new System.Windows.Controls.Grid();
            grid.Children.Add(review);
            grid.Children.Add(StatusViews.Confirm("Uninstall VLC media player?",
                "VLC media player’s own uninstaller runs first. Windows will ask for administrator permission. Afterwards you review what it left behind: nothing else is removed without your confirmation.",
                UninstallerText.Uninstall, () => { }, () => { }));
            PreviewRenderer.Render(grid, confirm, 720, 640);
        });
        Assert.True(new FileInfo(busy).Length > 2000);
        Assert.True(new FileInfo(done).Length > 2000);
        Assert.True(new FileInfo(confirm).Length > 2000);
    }

    [Fact]
    public void ReviewPrimaryActionFollowsTheUninstallerRow()
    {
        var model = LeftoverReviewView.SampleModel();
        Assert.Equal(UninstallerText.Uninstall, model.PrimaryText);
        model.Items[0].Include = false;
        Assert.Equal(UninstallerText.Remove, model.PrimaryText);
    }
}
