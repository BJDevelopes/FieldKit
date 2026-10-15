// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Fieldkit.Features.Island.Interop;

/// <summary>Win32 calls only the island needs (kept local to the area).</summary>
internal static class IslandNative
{
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_DISPLAYCHANGE = 0x007E;

    public const int VK_VOLUME_MUTE = 0xAD;
    public const int VK_VOLUME_DOWN = 0xAE;
    public const int VK_VOLUME_UP = 0xAF;
    public const int VK_MEDIA_NEXT_TRACK = 0xB0;
    public const int VK_MEDIA_PREV_TRACK = 0xB1;
    public const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    /// <summary>SHQueryUserNotificationState results.</summary>
    public enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool MessageBeep(uint type);

    public const uint MB_ICONASTERISK = 0x40;

    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
