// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// External USB peripherals (SetupAPI, built-in devices and hubs left out) and connected
/// Bluetooth devices with the battery level Windows shows in Settings › Bluetooth &amp; devices.
/// Read-only enumeration; nothing is opened.
/// </summary>
public static class DeviceReader
{
    /// <summary>The PKEY Windows stores a Bluetooth device's battery percentage in.</summary>
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string InternalContainer = "{00000000-0000-0000-ffff-ffffffffffff}";
    private static DateTime _batteriesReadAt = DateTime.MinValue;
    private static Dictionary<string, int> _batteries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object CacheGate = new();
    private static List<DeviceReading>? _cached;
    private static DateTime _cachedAt;
    private static Task? _refreshing;

    /// <summary>
    /// The last enumeration, refreshed in the background when older than <paramref name="maxAge"/>
    /// (enumeration can take seconds, so it never blocks the sampler). Null until the first one ends.
    /// </summary>
    public static List<DeviceReading>? Cached(bool withBatteries, TimeSpan maxAge)
    {
        lock (CacheGate)
        {
            var stale = _cached is null || DateTime.UtcNow - _cachedAt > maxAge;
            if (stale && _refreshing is null)
            {
                _refreshing = Task.Run(() =>
                {
                    var devices = Read(withBatteries);
                    lock (CacheGate)
                    {
                        _cached = devices;
                        _cachedAt = DateTime.UtcNow;
                        _refreshing = null;
                    }
                    MonitorSampler.Poke();
                });
            }
            return _cached;
        }
    }

    public static List<DeviceReading> Read(bool withBatteries)
    {
        var result = new List<DeviceReading>();
        try { result.AddRange(Usb()); }
        catch (Exception ex) { Log.Warn($"USB enumeration failed: {ex.Message}"); }
        try { result.AddRange(Bluetooth(withBatteries)); }
        catch (Exception ex) { Log.Warn($"Bluetooth enumeration failed: {ex.Message}"); }
        return result;
    }

    // ------------------------------------------------------------------ USB

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr devs, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    private static DEVPROPKEY BusReportedDeviceDesc = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    private static IEnumerable<DeviceReading> Usb()
    {
        var list = new List<DeviceReading>();
        var devs = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return list;
        try
        {
            for (uint i = 0; i < 512; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(devs, i, ref data)) break;
                var id = InstanceId(devs, ref data);
                if (id is null || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue; // interface of a composite device
                if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
                var container = RegistryString(devs, ref data, SPDRP_BASE_CONTAINERID);
                if (string.Equals(container, InternalContainer, StringComparison.OrdinalIgnoreCase)) continue; // built in
                var bus = BusDescription(devs, ref data);
                var description = RegistryString(devs, ref data, SPDRP_FRIENDLYNAME) ?? RegistryString(devs, ref data, SPDRP_DEVICEDESC);
                var name = Logic.DeviceNaming.Choose(bus, description);
                if (Logic.DeviceNaming.IsHub(bus, description)) continue;
                list.Add(new DeviceReading(id, name ?? "USB Device", DeviceKind.Usb, null, RegistryString(devs, ref data, SPDRP_CLASS)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devs);
        }
        return list;
    }

    private static string? InstanceId(IntPtr devs, ref SP_DEVINFO_DATA data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceId(devs, ref data, buffer, buffer.Length, out var required)
            ? new string(buffer, 0, Math.Max(0, required - 1))
            : null;
    }

    private static string? RegistryString(IntPtr devs, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(devs, ref data, property, out _, buffer, (uint)buffer.Length, out var required)) return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    private static string? BusDescription(IntPtr devs, ref SP_DEVINFO_DATA data)
    {
        var buffer = new byte[512];
        var key = BusReportedDeviceDesc;
        if (!SetupDiGetDeviceProperty(devs, ref data, ref key, out _, buffer, (uint)buffer.Length, out var required, 0)) return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    // ------------------------------------------------------------ Bluetooth

    private static IEnumerable<DeviceReading> Bluetooth(bool withBatteries)
    {
        var list = new List<DeviceReading>();
        string[] props = ["System.Devices.Aep.ContainerId"];
        var timeout = TimeSpan.FromSeconds(3);
        var classic = Find(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), props, timeout);
        var le = Find(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), props, timeout);
        if (classic.Count + le.Count == 0) return list;

        if (withBatteries && DateTime.UtcNow - _batteriesReadAt > TimeSpan.FromSeconds(30)) RefreshBatteries();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in classic.Concat(le))
        {
            var container = d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g
                ? g.ToString("B") : d.Id;
            if (!seen.Add(container) || string.IsNullOrWhiteSpace(d.Name)) continue;
            int? battery = _batteries.TryGetValue(container, out var b) ? b : null;
            list.Add(new DeviceReading(d.Id, d.Name, DeviceKind.Bluetooth, battery, null));
        }
        return list;
    }

    private static void RefreshBatteries()
    {
        _batteriesReadAt = DateTime.UtcNow;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodes = Find($"\"{BatteryKey}\":<>[]", ["System.Devices.ContainerId", BatteryKey], TimeSpan.FromSeconds(4),
            DeviceInformationKind.Device);
        foreach (var n in nodes)
        {
            if (!n.Properties.TryGetValue(BatteryKey, out var value) || value is not byte level || level > 100) continue;
            if (n.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g)
                map[g.ToString("B")] = level;
        }
        _batteries = map;
    }

    private static IReadOnlyList<DeviceInformation> Find(string aqs, string[] props, TimeSpan timeout,
        DeviceInformationKind? kind = null)
    {
        try
        {
            var op = kind is { } k ? DeviceInformation.FindAllAsync(aqs, props, k) : DeviceInformation.FindAllAsync(aqs, props);
            var task = op.AsTask();
            return task.Wait(timeout) ? task.Result.ToList() : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Device query failed: {ex.Message}");
            return [];
        }
    }
}
