// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Sound;

namespace Fieldkit.Tests.Sound;

public class TypingMuteTests
{
    [Theory]
    [InlineData(0x41, true)]   // A
    [InlineData(0x35, true)]   // 5
    [InlineData(0x20, true)]   // Space
    [InlineData(0xBE, true)]   // .
    [InlineData(0x08, true)]   // Backspace
    [InlineData(0x11, false)]  // Ctrl (push-to-talk keys must not mute)
    [InlineData(0xA0, false)]  // left Shift
    [InlineData(0x70, false)]  // F1
    [InlineData(0x25, false)]  // Left arrow
    [InlineData(0xB3, false)]  // media play/pause
    public void TypingKeys(int vk, bool typing) => Assert.Equal(typing, TypingMuteLogic.IsTypingKey(vk));

    [Fact]
    public void ReleasesAfterTheHold()
    {
        Assert.False(TypingMuteLogic.ShouldRelease(1_500, 1_000, 600));
        Assert.True(TypingMuteLogic.ShouldRelease(1_600, 1_000, 600));
    }
}
