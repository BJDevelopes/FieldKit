// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Interop;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// Battery state from GetSystemPowerStatus plus the battery class driver (IOCTL_BATTERY_*):
/// design vs full-charge capacity, cycle count, charge/discharge rate and, where the firmware
/// reports it, temperature. All read-only and available to standard users.
/// </summary>
public static class BatteryReader
{
    private sealed record BatteryDetails(uint Design, uint Full, uint Cycles, int? RateMw, double? Celsius, string? Name);

    /// <summary>True when the PC has a system battery (cached after the first check).</summary>
    public static bool HasBattery
    {
        get
        {
            if (_hasBattery is { } known) return known;
            var has = Native.GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
            _hasBattery = has;
            return has;
        }
    }

    private static bool? _hasBattery;

    public static (PowerReading Reading, double? Temperature) Read()
    {
        Native.GetSystemPowerStatus(out var status);
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
        _hasBattery = hasBattery;
        var pluggedIn = status.ACLineStatus == 1;
        int? charge = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        var details = hasBattery ? ReadDetails() : [];

        uint design = 0, full = 0, cycles = 0;
        int? rate = null;
        double? celsius = null;
        string? name = null;
        foreach (var d in details)
        {
            design += d.Design;
            full += d.Full;
            cycles = Math.Max(cycles, d.Cycles);
            if (d.RateMw is { } r) rate = (rate ?? 0) + r;
            if (d.Celsius is { } c) celsius = Math.Max(celsius ?? c, c);
            name ??= d.Name;
        }

        var charging = hasBattery && ((status.BatteryFlag & 8) != 0 || rate > 0);
        double? batteryWatts = rate is { } mw ? mw / 1000.0 : null;
        double? systemWatts = !pluggedIn && batteryWatts < 0 ? -batteryWatts : null;
        double? remaining = !pluggedIn && status.BatteryLifeTime > 0 ? status.BatteryLifeTime : null;
        double? health = design > 0 && full > 0 ? Math.Min(100, full * 100.0 / design) : null;

        var reading = new PowerReading(hasBattery, pluggedIn, charging, charge, batteryWatts, systemWatts, remaining, health,
            cycles > 0 ? cycles : null, design > 0 ? design : null, full > 0 ? full : null, name,
            status.SystemStatusFlag == 1);
        return (reading, celsius);
    }

    private static List<string> _paths = [];
    private static DateTime _pathsAt = DateTime.MinValue;

    private static List<BatteryDetails> ReadDetails()
    {
        // Enumerating the battery interfaces is the slow part; batteries rarely come and go.
        if (DateTime.UtcNow - _pathsAt > TimeSpan.FromMinutes(5))
        {
            _paths = BatteryPaths();
            _pathsAt = DateTime.UtcNow;
        }
        var result = new List<BatteryDetails>();
        foreach (var path in _paths)
        {
            if (ReadBattery(path) is { } details) result.Add(details);
        }
        if (result.Count == 0 && _paths.Count > 0) _pathsAt = DateTime.MinValue; // re-enumerate next time
        return result;
    }

    private static List<string> BatteryPaths()
    {
        var result = new List<string>();
        var guid = GUID_DEVCLASS_BATTERY;
        var devs = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return result;
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, i, ref data)) break;
                if (InterfacePath(devs, ref data) is { } path) result.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devs);
        }
        return result;
    }

    private static string? InterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(devs, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return null;
        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devs, ref data, detail, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static BatteryDetails? ReadBattery(string path)
    {
        using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        uint wait = 0;
        if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, ref wait, sizeof(uint), out var tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
            return null;

        var info = QueryStruct<BATTERY_INFORMATION>(handle, tag, BatteryInformation);
        if (info is null) return null;
        // Relative-capacity batteries report percentages, not mWh: no health or rate from those.
        const uint relativeCapacity = 0x40000000;
        var relative = (info.Value.Capabilities & relativeCapacity) != 0;

        int? rate = null;
        var waitStatus = new BATTERY_WAIT_STATUS { BatteryTag = tag };
        if (!relative && DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, ref waitStatus, Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                out var status, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero)
            && status.Rate != unchecked((int)BATTERY_UNKNOWN_RATE))
        {
            rate = status.Rate;
        }

        double? celsius = null;
        if (QueryStruct<uint>(handle, tag, BatteryTemperature) is { } tenthsKelvin && tenthsKelvin > 0)
        {
            var c = tenthsKelvin / 10.0 - 273.15;
            if (c is > 0 and < 100) celsius = c;
        }

        return new BatteryDetails(
            relative ? 0 : info.Value.DesignedCapacity,
            relative ? 0 : info.Value.FullChargedCapacity,
            info.Value.CycleCount,
            rate,
            celsius,
            QueryString(handle, tag, BatteryDeviceName));
    }

    private static T? QueryStruct<T>(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint tag, int level) where T : struct
    {
        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref query, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                    buffer, size, out var returned, IntPtr.Zero) || returned < size)
                return null;
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint tag, int level)
    {
        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        const int size = 512;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref query, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                    buffer, size, out var returned, IntPtr.Zero) || returned < 2)
                return null;
            var text = Marshal.PtrToStringUni(buffer, returned / 2).TrimEnd('\0').Trim();
            return text.Length > 0 ? text : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
