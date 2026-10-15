// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Tests.UI;

public class TaskbarDockTests
{
    private static readonly Native.RECT Screen = new(0, 0, 1920, 1080);
    private static readonly Native.RECT Bottom = new(0, 1020, 1920, 1080);
    private static readonly Native.RECT Notify = new(1565, 1020, 1920, 1080);

    [Fact]
    public void DocksAgainstTheNotificationArea()
    {
        var r = TaskbarDock.Place(Bottom, Screen, Notify, new Native.RECT(69, 1020, 619, 1080), 1.25)!.Value;
        Assert.Equal(1565 - 5, r.Right);
        Assert.Equal(50, r.Width);
        Assert.Equal(1020, r.Top);
        Assert.Equal(1080, r.Bottom);
    }

    [Fact]
    public void StepsAsideWhenAppButtonsReachTheTray()
    {
        Assert.Null(TaskbarDock.Place(Bottom, Screen, Notify, new Native.RECT(300, 1020, 1530, 1080), 1.25));
    }

    [Theory]
    [InlineData(0, 0, 1920, 60)]      // top
    [InlineData(0, 0, 60, 1080)]      // left
    [InlineData(1860, 0, 1920, 1080)] // right
    public void OnlyDocksOnABottomTaskbar(int l, int t, int r, int b)
    {
        Assert.Null(TaskbarDock.Place(new Native.RECT(l, t, r, b), Screen, null, null, 1));
    }

    [Fact]
    public void FallsBackWhenTheTrayCannotBeFound()
    {
        var r = TaskbarDock.Place(Bottom, Screen, null, null, 1)!.Value;
        Assert.Equal(1920 - 320, r.Right);
        Assert.Equal(40, r.Width);
    }
}
