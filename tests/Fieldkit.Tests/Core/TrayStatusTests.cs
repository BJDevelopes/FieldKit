// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Tests.Core;

public class TrayStatusTests
{
    [Fact]
    public void StatusDotIsDrawnInTheBottomRightCorner()
    {
        using var icon = TrayIcon.RenderBrandIcon(lightTaskbar: false, 32, System.Drawing.Color.FromArgb(255, 255, 159, 10));
        using var bmp = icon.ToBitmap();
        var centre = bmp.GetPixel(32 - 8, 32 - 8);
        Assert.Equal(255, centre.R);
        Assert.Equal(159, centre.G);
        Assert.Equal(10, centre.B);
    }

    [Fact]
    public void BadgesAreOrderedByPriorityAndClear()
    {
        TrayStatus.Set("test.low", new TrayBadge("low", 0x00FF00, 1));
        TrayStatus.Set("test.high", new TrayBadge("high", 0xFF0000, 5));
        var current = TrayStatus.Current.Where(b => b.Text is "low" or "high").ToList();
        Assert.Equal(["high", "low"], current.Select(b => b.Text));
        TrayStatus.Set("test.low", null);
        TrayStatus.Set("test.high", null);
        Assert.DoesNotContain(TrayStatus.Current, b => b.Text is "low" or "high");
    }
}
