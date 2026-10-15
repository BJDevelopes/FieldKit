// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fieldkit.Features.QuickTools;

/// <summary>P/Invokes used only by the Quick tools area.</summary>
internal static class QuickToolsNative
{
    public const int WM_COMMAND = 0x0111;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int SC_MONITORPOWER = 0xF170;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint SHERB_NOCONFIRMATION = 0x1;
    public const uint SHERB_NOPROGRESSUI = 0x2;
    public const uint SHERB_NOSOUND = 0x4;
    public const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    // ---- Storage bus type (eject) ------------------------------------------------
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref STORAGE_PROPERTY_QUERY input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>STORAGE_BUS_TYPE of a volume ("C"), or -1 when unknown. 7 = USB, 4 = 1394, 12 = SD, 13 = MMC.</summary>
    public static int BusType(char driveLetter)
    {
        try
        {
            using var handle = CreateFile($@"\\.\{driveLetter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return -1;
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
            var buffer = new byte[1024];
            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buffer, buffer.Length, out var n, IntPtr.Zero) || n < 32)
                return -1;
            // STORAGE_DEVICE_DESCRIPTOR: ... BusType (enum, 4 bytes) at offset 28.
            return BitConverter.ToInt32(buffer, 28);
        }
        catch
        {
            return -1;
        }
    }

    // ---- Processes -----------------------------------------------------------------
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---- TCP / UDP tables ----------------------------------------------------------
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    public const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
