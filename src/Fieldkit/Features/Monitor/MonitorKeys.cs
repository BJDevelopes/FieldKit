// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Features.Monitor;

/// <summary>
/// Every setting the system monitor reads. Keys are prefixed with the feature that owns them;
/// readings shared by the whole monitor (interval, units, alert repeat) live under monitorCPU.
/// </summary>
public static class MonitorKeys
{
    // Shared readings (upstream "Readings and alerts").
    public const string Interval = "monitorCPU.interval";                 // 1 | 2 | 5 seconds
    public const int DefaultInterval = 2;
    public const string TemperatureUnit = "monitorCPU.temperatureUnit";   // celsius | fahrenheit
    public const string NetworkSpeedUnit = "monitorNetwork.speedUnit";    // bytes | bits
    public const string MemoryMetric = "monitorMemory.metric";            // used | committed
    public const string GraphScale = "monitorCPU.graphScale";             // ceiling label on graphs

    /// <summary>Opt-in: let LibreHardwareMonitor load its PawnIO kernel modules (CPU, board, fans).</summary>
    public const string HardwareSensors = "monitorCPU.hardwareSensors";

    // System section blocks.
    public const string SysTemps = "monitorCPU.panel.temps";
    public const string SysCpu = "monitorCPU.panel.cpu";
    public const string SysCpuCores = "monitorCPU.panel.cores";
    public const string SysGpu = "monitorGPU.panel.gpu";
    public const string SysBattery = "monitorPower.panel.charge";
    public const string SysEnergyApps = "monitorPower.panel.energyApps";
    public const string SysMemory = "monitorMemory.panel.memory";
    public const string SysUptime = "monitorCPU.panel.uptime";
    public const string SysConnectedDevices = "connectedDevices.panel.row";
    public const string GraphCpu = "monitorCPU.graph";
    public const string GraphGpu = "monitorGPU.graph";
    public const string GraphMemory = "monitorMemory.graph";
    public const string GraphBattery = "monitorPower.graphBattery";

    // Network section blocks.
    public const string NetSpeed = "monitorNetwork.panel.speed";
    public const string NetTotals = "monitorNetwork.panel.totals";
    public const string NetAddresses = "monitorNetwork.panel.addresses";
    public const string NetTest = "monitorNetwork.panel.test";
    public const string GraphNetwork = "monitorNetwork.graph";

    // Disk section blocks.
    public const string DiskUsage = "monitorDisk.panel.usage";
    public const string DiskActivity = "monitorDisk.panel.activity";
    public const string DiskTools = "monitorDisk.panel.tools";
    public const string GraphDisk = "monitorDisk.graph";

    // Power section blocks.
    public const string PwrSystem = "monitorPower.panel.system";
    public const string PwrAdapter = "monitorPower.panel.adapter";
    public const string PwrBattery = "monitorPower.panel.battery";
    public const string PwrTimeRemaining = "monitorPower.panel.remaining";
    public const string PwrHealth = "monitorPower.panel.health";
    public const string PwrTemperature = "monitorPower.panel.temperature";
    public const string PwrDevices = "connectedDevices.panel.list";
    public const string GraphPower = "monitorPower.graph";

    // Notification-area readouts (upstream "In the menu bar").
    public const string TraySeparate = "monitorCPU.tray.separate";
    public const string TrayCombineTemperatures = "monitorCPU.tray.combineTemperatures";
    public const string TrayMemoryDot = "monitorMemory.tray.pressureColor";
    public const string TrayUploadFirst = "monitorNetwork.tray.uploadFirst";
    public const string TrayDiskStyle = "monitorDisk.tray.style";         // percent | free | used

    // Alerts.
    public const string AlertCpu = "monitorCPU.alert.cpu";
    public const string AlertCpuThreshold = "monitorCPU.alert.cpuThreshold";
    public const string AlertCpuTemperature = "monitorCPU.alert.cpuTemperature";
    public const string AlertCpuTemperatureThreshold = "monitorCPU.alert.cpuTemperatureThreshold";
    public const string AlertMemory = "monitorMemory.alert.pressure";
    public const string AlertDisk = "monitorDisk.alert.lowSpace";
    public const string AlertDiskFreePercent = "monitorDisk.alert.freePercent";
    public const string AlertBattery = "monitorPower.alert.lowBattery";
    public const string AlertBatteryPercent = "monitorPower.alert.batteryPercent";
    public const string AlertBatteryTemperature = "monitorPower.alert.batteryTemperature";
    public const string AlertBatteryTemperatureThreshold = "monitorPower.alert.batteryTemperatureThreshold";
    public const string AlertCooldown = "monitorCPU.alert.cooldownMinutes";

    // Fan control.
    public const string FanMode = "fanControl.mode";                       // system | manual | curve
    public const string FanManualLevel = "fanControl.manualLevel";
    public const string FanCurves = "fanControl.curves";
    public const string FanApplied = "fanControl.applied";                 // a manual/curve config is active
    public const string FanAppliedMode = "fanControl.appliedMode";         // what Apply saved (the editor may differ)
    public const string FanAppliedLevel = "fanControl.appliedLevel";
    public const string FanAppliedCurves = "fanControl.appliedCurves";
    public const string FanResume = "fanControl.resume";

    public static int SanitizedInterval() =>
        Settings.GetInt(Interval, DefaultInterval) switch { 1 => 1, 5 => 5, _ => 2 };

    public static bool Fahrenheit => Settings.GetString(TemperatureUnit, "celsius") == "fahrenheit";

    public static bool SpeedInBits => Settings.GetString(NetworkSpeedUnit, "bytes") == "bits";

    /// <summary>A panel block visibility flag (all default on, like upstream).</summary>
    public static bool Shown(string key) => Settings.GetBool(key, true);
}
