// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>Win32 declarations only the window features use (the shared ones live in <see cref="Native"/>).</summary>
internal static class WinApi
{
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint SWP_NOREDRAW = 0x0008;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTCAPTION = 2;
    public const int HTMAXBUTTON = 9;
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_RESTORE = 0xF120;
    public const int SC_CLOSE = 0xF060;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const int DWM_CLOAKED_SHELL = 0x2;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    public const uint MAPVK_VK_TO_CHAR = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public Native.POINT ptMinPosition;
        public Native.POINT ptMaxPosition;
        public Native.RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    public static extern int ToUnicode(uint virtKey, uint scanCode, byte[] keyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int bufferSize, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(IntPtr hProcess, ref int length, StringBuilder? id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public long padding;
        [FieldOffset(16)] public long padding2; // 24 bytes on x64
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    /// <summary>PKEY_AppUserModel_ID.</summary>
    public static PROPERTYKEY AppUserModelIdKey => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    /// <summary>The documented virtual desktop query interface (Windows 10+).</summary>
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    public static readonly Guid VirtualDesktopManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    public static long ExStyle(IntPtr hwnd) => Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();

    public static long Style(IntPtr hwnd) => Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();

    /// <summary>Hides a WPF tool window from Alt+Tab and, optionally, from activation.</summary>
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate)
    {
        var ex = ExStyle(hwnd) | Native.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Asks DWM for rounded corners (Windows 11; ignored on Windows 10).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        var pref = DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }
}
