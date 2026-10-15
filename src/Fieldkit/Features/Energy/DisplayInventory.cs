// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Fieldkit.Interop;
using static Fieldkit.Features.Energy.EnergyNative;

namespace Fieldkit.Features.Energy;

/// <summary>One active display: what Windows calls a monitor (HMONITOR) joined with its display-config target.</summary>
public sealed record DisplayDescriptor(
    string Key,
    string Name,
    bool IsInternal,
    bool IsPrimary,
    IntPtr HMonitor,
    Native.RECT Bounds,
    string GdiName,
    string DevicePath,
    uint AdapterLow,
    int AdapterHigh,
    uint TargetId);

/// <summary>Enumerates active displays (read-only queries only).</summary>
public static class DisplayInventory
{
    private sealed record Target(string GdiName, string FriendlyName, string DevicePath, uint OutputTechnology, LUID Adapter, uint TargetId);

    public static List<DisplayDescriptor> Enumerate()
    {
        var targets = QueryTargets();
        var result = new List<DisplayDescriptor>();
        var index = 1;
        foreach (var (handle, bounds, _, primary) in WindowHelper.GetMonitors())
        {
            var info = WindowHelper.GetMonitorInfo(handle);
            var target = targets.FirstOrDefault(t => string.Equals(t.GdiName, info.szDevice, StringComparison.OrdinalIgnoreCase));
            var internalPanel = target is not null && DisplayMath.IsInternalTechnology(target.OutputTechnology);
            var name = target?.FriendlyName is { Length: > 0 } friendly
                ? friendly
                : internalPanel ? "Built-in display" : $"Display {index}";
            if (internalPanel && (target?.FriendlyName is null or { Length: 0 })) name = "Built-in display";
            var key = DisplayMath.ConnectionKey(target?.DevicePath, info.szDevice);
            result.Add(new DisplayDescriptor(key, name, internalPanel, primary, handle, bounds, info.szDevice,
                target?.DevicePath ?? string.Empty, target?.Adapter.LowPart ?? 0, target?.Adapter.HighPart ?? 0, target?.TargetId ?? 0));
            index++;
        }
        return result;
    }

    /// <summary>True when any active display is not the laptop's built-in panel.</summary>
    public static bool ExternalDisplayConnected()
    {
        var targets = QueryTargets();
        return targets.Any(t => !DisplayMath.IsInternalTechnology(t.OutputTechnology));
    }

    private static List<Target> QueryTargets()
    {
        var list = new List<Target>();
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return list;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return list;
            for (var i = 0; i < pathCount; i++)
            {
                var p = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    },
                };
                var gdi = DisplayConfigGetDeviceInfo(ref source) == 0 ? source.viewGdiDeviceName : string.Empty;
                var hasTarget = DisplayConfigGetDeviceInfo(ref target) == 0;
                list.Add(new Target(gdi,
                    hasTarget ? target.monitorFriendlyDeviceName ?? string.Empty : string.Empty,
                    hasTarget ? target.monitorDevicePath ?? string.Empty : string.Empty,
                    p.targetInfo.outputTechnology, p.targetInfo.adapterId, p.targetInfo.id));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"QueryDisplayConfig failed: {ex.Message}");
        }
        return list;
    }

    /// <summary>Reads (advanced colour enabled, SDR white level) for an HDR-capable target.</summary>
    public static (bool HdrOn, uint WhiteLevel) ReadHdr(DisplayDescriptor d)
    {
        var adapter = new LUID { LowPart = d.AdapterLow, HighPart = d.AdapterHigh };
        var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = adapter,
                id = d.TargetId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref color) != 0) return (false, 0);
        var enabled = (color.value & 0x2) != 0 && (color.value & 0x8) == 0;
        var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                adapterId = adapter,
                id = d.TargetId,
            },
        };
        return DisplayConfigGetDeviceInfo(ref white) == 0 ? (enabled, white.SDRWhiteLevel) : (enabled, 0u);
    }

    /// <summary>Sets the SDR content brightness (white level, 1000 = 80 nits) of an HDR display.</summary>
    public static bool WriteSdrWhiteLevel(DisplayDescriptor d, uint level)
    {
        var request = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL>(),
                adapterId = new LUID { LowPart = d.AdapterLow, HighPart = d.AdapterHigh },
                id = d.TargetId,
            },
            SDRWhiteLevel = level,
            finalValue = 1,
        };
        return DisplayConfigSetDeviceInfo(ref request) == 0;
    }
}
