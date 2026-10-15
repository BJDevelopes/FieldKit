// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Synthesized pastes and typing into another app: re-activates the window the user came from,
/// waits until no modifier is physically held (otherwise Ctrl+V or typed text would turn into
/// shortcuts there), then sends the keys. Everything is tagged as Fieldkit's own input.
/// </summary>
public static class PasteHelper
{
    private const int VK_CONTROL = 0x11;
    private const int VK_V = 0x56;

    /// <summary>The foreground window right now, unless it belongs to Fieldkit.</summary>
    public static IntPtr CaptureTarget()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        return WindowHelper.GetProcessId(hwnd) == (uint)Environment.ProcessId ? IntPtr.Zero : hwnd;
    }

    /// <summary>Activates <paramref name="target"/> (if any) and presses Ctrl+V there once the keyboard is clean.</summary>
    public static void PasteInto(IntPtr target, Action? afterPaste = null) =>
        RunInto(target, () => InputSender.Chord(VK_CONTROL, VK_V), afterPaste);

    /// <summary>Activates <paramref name="target"/> (if any) and runs <paramref name="send"/> once the keyboard is clean.</summary>
    public static void RunInto(IntPtr target, Action send, Action? after = null)
    {
        if (target != IntPtr.Zero)
        {
            if (!Native.IsWindow(target))
            {
                SystemSounds.Beep.Play();
                return;
            }
            WindowHelper.Activate(target);
        }
        _ = SendWhenReleasedAsync(send, after);
    }

    /// <summary>Waits (up to ~1.5 s) for every modifier to be released, then sends. Gives up audibly.</summary>
    public static async Task SendWhenReleasedAsync(Action send, Action? after = null)
    {
        await Task.Delay(120);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Shortcut.CurrentModifiers() == Modifiers.None)
            {
                await Task.Delay(40);
                Ui.OnUi(() =>
                {
                    send();
                    after?.Invoke();
                });
                return;
            }
            await Task.Delay(15);
        }
        SystemSounds.Beep.Play();
    }
}
