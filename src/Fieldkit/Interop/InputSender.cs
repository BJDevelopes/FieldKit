// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Fieldkit.Interop;

/// <summary>
/// SendInput helpers. Every event is tagged with <see cref="Tag"/> in dwExtraInfo so
/// our own hooks can recognise (and ignore) what we inject.
/// </summary>
public static class InputSender
{
    /// <summary>Marker placed in dwExtraInfo of injected events ("FKIT").</summary>
    public static readonly UIntPtr Tag = new(0x464B4954);

    /// <summary>
    /// Marker for input Fieldkit sends on the user's behalf as a remap (a mouse button that types a
    /// shortcut). It is not <see cref="KeyEvent.IsOwn"/>, so Fieldkit's own hook-based features (the
    /// Alt+Tab switcher, layouts, snippets) react to it as if typed (upstream #401) ("FKIU").
    /// </summary>
    public static readonly UIntPtr RemapTag = new(0x464B4955);

    /// <summary>Re-tags events as a user remap (see <see cref="RemapTag"/>).</summary>
    public static Native.INPUT[] AsRemap(Native.INPUT[] inputs)
    {
        for (var i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].type == Native.INPUT_KEYBOARD) inputs[i].U.ki.dwExtraInfo = RemapTag;
            else inputs[i].U.mi.dwExtraInfo = RemapTag;
        }
        return inputs;
    }

    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();

    private static readonly HashSet<int> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
        0x2D, 0x2E, // Insert Delete
        0x5B, 0x5C, 0x5D, // LWin RWin Apps
        0xA3, 0xA5, // RControl RMenu
        0x6F, 0x90, // Divide NumLock
    ];

    public static Native.INPUT Key(int vk, bool down)
    {
        var flags = down ? 0u : Native.KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)Native.MapVirtualKey((uint)vk, 0),
                    dwFlags = flags,
                    dwExtraInfo = Tag,
                },
            },
        };
    }

    public static void Send(params Native.INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        Native.SendInput((uint)inputs.Length, inputs, InputSize);
    }

    /// <summary>Taps a key (down + up).</summary>
    public static void Tap(int vk) => Send(Key(vk, true), Key(vk, false));

    /// <summary>
    /// Presses a combination such as Ctrl+V: modifiers down, key tap, modifiers up.
    /// Modifiers the user is physically holding are left alone.
    /// </summary>
    public static void Chord(params int[] keys)
    {
        var list = new List<Native.INPUT>();
        foreach (var k in keys) list.Add(Key(k, true));
        for (var i = keys.Length - 1; i >= 0; i--) list.Add(Key(keys[i], false));
        Send(list.ToArray());
    }

    /// <summary>Releases modifiers that are currently down (so a synthesized shortcut is not polluted).</summary>
    public static void ReleaseModifiers()
    {
        var list = new List<Native.INPUT>();
        foreach (var vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            if (InputHooks.IsKeyDown(vk)) list.Add(Key(vk, false));
        }
        Send(list.ToArray());
    }

    /// <summary>Types text using Unicode key events (works regardless of keyboard layout).</summary>
    public static void TypeText(string text)
    {
        var list = new List<Native.INPUT>(text.Length * 2);
        foreach (var ch in text.ReplaceLineEndings("\r"))
        {
            if (ch == '\r')
            {
                list.Add(Key(0x0D, true));
                list.Add(Key(0x0D, false));
                continue;
            }
            list.Add(Unicode(ch, true));
            list.Add(Unicode(ch, false));
        }
        Send(list.ToArray());
    }

    /// <summary>Sends Backspace <paramref name="count"/> times.</summary>
    public static void Backspace(int count)
    {
        var list = new List<Native.INPUT>(count * 2);
        for (var i = 0; i < count; i++)
        {
            list.Add(Key(0x08, true));
            list.Add(Key(0x08, false));
        }
        Send(list.ToArray());
    }

    public static void Wheel(int delta, bool horizontal = false) =>
        Send(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    mouseData = delta,
                    dwFlags = horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = Tag,
                },
            },
        });

    public static Native.INPUT MouseButton(uint flags, int data = 0) => new()
    {
        type = Native.INPUT_MOUSE,
        U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = Tag } },
    };

    public static void Click(uint downFlag, uint upFlag, int data = 0) =>
        Send(MouseButton(downFlag, data), MouseButton(upFlag, data));

    private static Native.INPUT Unicode(char ch, bool down) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wScan = ch,
                dwFlags = Native.KEYEVENTF_UNICODE | (down ? 0 : Native.KEYEVENTF_KEYUP),
                dwExtraInfo = Tag,
            },
        },
    };
}
