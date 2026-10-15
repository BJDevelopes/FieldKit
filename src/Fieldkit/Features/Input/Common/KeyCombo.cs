// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Common;

/// <summary>Builds the input sequence that presses a key combination.</summary>
internal static class KeyCombo
{
    public static int[] ModifierKeys(Modifiers m)
    {
        var list = new List<int>(4);
        if (m.HasFlag(Modifiers.Ctrl)) list.Add(InputNative.VK_LCONTROL);
        if (m.HasFlag(Modifiers.Alt)) list.Add(InputNative.VK_LMENU);
        if (m.HasFlag(Modifiers.Shift)) list.Add(InputNative.VK_LSHIFT);
        if (m.HasFlag(Modifiers.Win)) list.Add(InputNative.VK_LWIN);
        return list.ToArray();
    }

    /// <summary>Modifiers down, key tap, modifiers up (in reverse).</summary>
    public static Native.INPUT[] Press(Shortcut shortcut) => Press(shortcut.Modifiers, shortcut.Key);

    public static Native.INPUT[] Press(Modifiers modifiers, int key)
    {
        var mods = ModifierKeys(modifiers);
        var list = new List<Native.INPUT>(mods.Length * 2 + 2);
        foreach (var m in mods) list.Add(InputSender.Key(m, true));
        list.Add(InputSender.Key(key, true));
        list.Add(InputSender.Key(key, false));
        for (var i = mods.Length - 1; i >= 0; i--) list.Add(InputSender.Key(mods[i], false));
        return list.ToArray();
    }
}
