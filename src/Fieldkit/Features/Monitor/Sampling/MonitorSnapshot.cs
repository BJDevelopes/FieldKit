// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Monitor.Logic;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>What a consumer needs sampled. The sampler reads only the union of active leases.</summary>
[Flags]
public enum MonitorNeeds
{
    None = 0,
    Cpu = 1,
    CpuCores = 2,
    Gpu = 4,
    Memory = 8,
    Network = 16,
    Disk = 32,
    Power = 64,
    Temperatures = 128,
    Fans = 256,
    Devices = 512,
}

public sealed record DriveReading(
    string Name,            // "C:"
    string Label,           // volume label or "Local Disk"
    string Root,            // "C:\"
    string? FileSystem,
    bool IsInternal,
    ulong TotalBytes,
    ulong FreeBytes,
    double? ReadBytesPerSec,
    double? WriteBytesPerSec)
{
    public ulong UsedBytes => TotalBytes > FreeBytes ? TotalBytes - FreeBytes : 0;
    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public sealed record PowerReading(
    bool HasBattery,
    bool PluggedIn,
    bool Charging,
    int? ChargePercent,
    double? BatteryWatts,          // + charging, − discharging
    double? SystemWatts,           // measured (sensor) or battery discharge rate
    double? TimeRemainingSeconds,  // on battery only
    double? HealthPercent,         // full charge / design capacity
    uint? CycleCount,
    uint? DesignCapacityMWh,
    uint? FullChargeCapacityMWh,
    string? BatteryName,
    bool SaverOn);

public sealed record FanReading(string Id, string Name, double? Rpm, double? DutyPercent, bool Controllable);

public enum DeviceKind { Usb, Bluetooth }

public sealed record DeviceReading(string Id, string Name, DeviceKind Kind, int? BatteryPercent, string? Category);

/// <summary>An immutable reading of everything sampled on one tick.</summary>
public sealed class MonitorSnapshot
{
    public static readonly MonitorSnapshot Empty = new();

    public double ReadAt { get; init; }
    public long UptimeSeconds { get; init; }

    public double? CpuUsage { get; init; }
    public double? CpuReadAt { get; init; }
    public IReadOnlyList<double?> CoreUsage { get; init; } = [];
    public IReadOnlyList<CoreGroup> CoreGroups { get; init; } = [];
    public IReadOnlyList<double> CpuHistory { get; init; } = [];

    public double? GpuUsage { get; init; }
    public IReadOnlyList<double> GpuHistory { get; init; } = [];

    public double? CpuTemperature { get; init; }
    public double? CpuTemperatureReadAt { get; init; }
    public double? GpuTemperature { get; init; }
    public double? BatteryTemperature { get; init; }
    public double? BatteryTemperatureReadAt { get; init; }
    public double? MotherboardTemperature { get; init; }
    public double? AverageCpuTemperature { get; init; }
    public SensorStatus Sensors { get; init; } = SensorStatus.NotRequested;

    public ulong? MemoryTotal { get; init; }
    public ulong? MemoryUsed { get; init; }
    public ulong? MemoryCommitted { get; init; }
    public ulong? MemoryCommitLimit { get; init; }
    public ulong? MemoryCached { get; init; }
    public ulong? MemorySwapUsed { get; init; }
    public MemoryPressure MemoryPressure { get; init; }
    public IReadOnlyList<double> MemoryHistory { get; init; } = [];

    public double? NetDown { get; init; }
    public double? NetUp { get; init; }
    public ulong? NetTotalDown { get; init; }
    public ulong? NetTotalUp { get; init; }
    /// <summary>The connected adapters' own counters: since each came up, usually since Windows started.</summary>
    public ulong? NetSinceBootDown { get; init; }
    public ulong? NetSinceBootUp { get; init; }
    public IReadOnlyList<double> NetDownHistory { get; init; } = [];
    public IReadOnlyList<double> NetUpHistory { get; init; } = [];

    public IReadOnlyList<DriveReading> Drives { get; init; } = [];
    public double? DiskRead { get; init; }
    public double? DiskWrite { get; init; }
    public ulong DiskTotalRead { get; init; }
    public ulong DiskTotalWritten { get; init; }
    public IReadOnlyList<double> DiskReadHistory { get; init; } = [];
    public IReadOnlyList<double> DiskWriteHistory { get; init; } = [];

    public PowerReading? Power { get; init; }
    public IReadOnlyList<double> BatteryHistory { get; init; } = [];
    public IReadOnlyList<double> SystemPowerHistory { get; init; } = [];

    public IReadOnlyList<FanReading> Fans { get; init; } = [];
    public IReadOnlyList<DeviceReading> Devices { get; init; } = [];
    public bool DevicesRead { get; init; }

    public double? MemoryFraction(bool committed) =>
        committed
            ? MemoryCommitted is { } c && MemoryCommitLimit is { } l && l > 0 ? (double)c / l : null
            : MemoryUsed is { } u && MemoryTotal is { } t && t > 0 ? (double)u / t : null;
}

/// <summary>Whether temperature/fan sensors could be read, and why not.</summary>
public enum SensorStatus
{
    NotRequested,
    /// <summary>User-mode sources only (GPU, battery); CPU/board sensors are opt-in.</summary>
    DriverOff,
    Starting,
    /// <summary>Opted in, but the app is not elevated.</summary>
    NeedsAdmin,
    /// <summary>Opted in and elevated, but PawnIO is not installed.</summary>
    NeedsPawnIo,
    Ready,
    Failed,
}
