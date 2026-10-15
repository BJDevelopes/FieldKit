// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.UI;

namespace Fieldkit.Tests.UI;

public class WindowFitTests
{
    [Fact]
    public void AWindowThatFitsKeepsItsSize() =>
        Assert.Equal((740d, 480d), WindowFitLogic.Fit(740, 480, 1000));

    [Fact]
    public void ATallWindowShrinksToTheUsableArea()
    {
        // 1920×1080 at 150 % with a 48 px taskbar: 688 DIPs of usable height.
        var (size, min) = WindowFitLogic.Fit(820, 600, 688);
        Assert.Equal(Math.Floor(688 * WindowFitLogic.MaxShare), size);
        Assert.True(size <= 688);
        Assert.True(min <= size);
    }

    [Fact]
    public void TheMinimumNeverExceedsTheDisplay() =>
        Assert.True(WindowFitLogic.Fit(1180, 820, 700).Min <= 700);

    [Fact]
    public void AnUnknownDisplayChangesNothing() =>
        Assert.Equal((1080d, 760d), WindowFitLogic.Fit(1080, 760, 0));
}
