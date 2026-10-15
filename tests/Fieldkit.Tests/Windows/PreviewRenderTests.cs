// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Features.WindowTools;

using Fieldkit.Tests.Support;

namespace Fieldkit.Tests.Windows;

/// <summary>
/// Renders the switcher looks (and the window pages at full height) to PNG files for visual
/// review, in an STA thread with the app's theme. No hooks, no other apps' windows.
/// </summary>
public class PreviewRenderTests
{
    private static string OutputDirectory => Path.Combine(Path.GetTempPath(), "fieldkit-windows-previews");

    [Fact]
    public void RendersSwitcherLooksAndPages()
    {
        WpfTestHost.Run(() =>
        {
            Directory.CreateDirectory(OutputDirectory);

            foreach (var mode in Enum.GetValues<SwitcherLayoutMode>())
            {
                var path = Path.Combine(OutputDirectory, $"switcher-{mode}.png");
                SwitcherService.RenderPreview(path, mode);
                Assert.True(new FileInfo(path).Length > 1000);
            }

            RenderPage("page-switcher.png", SwitcherPage.Build());
            RenderPage("page-windowLayout.png", WindowLayoutPage.Build());
            RenderPage("page-taskbar.png", TaskbarPage.Build());
            RenderPage("page-quitOnClose.png", QuitOnClosePage.Build());
        });
    }

    private static void RenderPage(string name, FrameworkElement page)
    {
        var content = page is ScrollViewer sv ? (FrameworkElement)sv.Content : page;
        if (page is ScrollViewer s) s.Content = null;
        var host = new Border { Child = content, Width = 900 };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        host.Measure(new Size(900, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(OutputDirectory, name);
        using (var stream = File.Create(path)) encoder.Save(stream);
        Assert.True(new FileInfo(path).Length > 1000);
    }
}
