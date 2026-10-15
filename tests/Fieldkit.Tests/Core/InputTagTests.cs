// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Tests.Core;

public class InputTagTests
{
    [Fact]
    public void RemappedKeysAreNotMarkedAsFieldkitsOwn()
    {
        var keys = InputSender.AsRemap([InputSender.Key(0x12, true), InputSender.Key(0x09, true), InputSender.Key(0x09, false), InputSender.Key(0x12, false)]);
        Assert.All(keys, k => Assert.Equal(InputSender.RemapTag, k.U.ki.dwExtraInfo));
        Assert.NotEqual(InputSender.Tag, InputSender.RemapTag);
    }

    [Fact]
    public void RemappedMouseEventsAreRetaggedToo()
    {
        var clicks = InputSender.AsRemap([InputSender.MouseButton(Native.MOUSEEVENTF_MIDDLEDOWN)]);
        Assert.Equal(InputSender.RemapTag, clicks[0].U.mi.dwExtraInfo);
    }
}
