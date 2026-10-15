// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Monitor.Logic;

/// <summary>Traffic-light memory state shown as the Pressure badge.</summary>
public enum MemoryPressure { Unknown, Normal, Warning, Critical }

/// <summary>
/// Windows has no "memory pressure" signal like macOS, so the level is derived from what makes
/// Windows slow down and page: little available (free + standby) memory, or a commit charge
/// close to the commit limit (RAM + page file).
/// </summary>
public static class MemoryPressureClassifier
{
    public const double WarningAvailable = 0.15;
    public const double CriticalAvailable = 0.05;
    public const double WarningCommit = 0.85;
    public const double CriticalCommit = 0.95;

    public static MemoryPressure Classify(ulong totalPhys, ulong availPhys, ulong commitUsed, ulong commitLimit)
    {
        if (totalPhys == 0) return MemoryPressure.Unknown;
        var available = (double)Math.Min(availPhys, totalPhys) / totalPhys;
        var commit = commitLimit == 0 ? 0 : (double)commitUsed / commitLimit;
        if (available < CriticalAvailable || commit >= CriticalCommit) return MemoryPressure.Critical;
        if (available < WarningAvailable || commit >= WarningCommit) return MemoryPressure.Warning;
        return MemoryPressure.Normal;
    }
}

/// <summary>
/// Decides when a reading over its limit deserves a notification (port of upstream
/// SustainedAlertGate): the reading must hold across separate samples for 12 seconds,
/// so a short burst never alerts on its own.
/// </summary>
public sealed class SustainedAlertGate
{
    public const double SustainedSeconds = 12;

    private double? _heldSince;
    private double? _lastReadingAt;

    /// <param name="readAt">When the reading was actually taken (monotonic seconds).</param>
    public bool ShouldAlert(double? reading, double threshold, double? readAt)
    {
        if (reading is not { } value || readAt is not { } at || value < threshold)
        {
            Reset();
            return false;
        }
        if (_lastReadingAt == at) return false;
        _lastReadingAt = at;
        if (_heldSince is not { } since)
        {
            _heldSince = at;
            return false;
        }
        return at - since >= SustainedSeconds;
    }

    public void Reset()
    {
        _heldSince = null;
        _lastReadingAt = null;
    }
}

/// <summary>Alert limits and their sanitising (upstream MonitorAlertService / MonitorAlertsControls).</summary>
public static class AlertPolicy
{
    public static readonly int[] CooldownChoices = [2, 5, 15, 30, 60];

    public static int Sanitize(int value, int fallback, int min, int max) =>
        value < min || value > max ? fallback : value;

    public static int CpuThreshold(int v) => Sanitize(v, 90, 50, 100);
    public static int CpuTemperatureThreshold(int v) => Sanitize(v, 90, 70, 105);
    public static int DiskFreePercent(int v) => Sanitize(v, 10, 5, 30);
    public static int BatteryPercent(int v) => Sanitize(v, 15, 5, 50);
    public static int BatteryTemperatureThreshold(int v) => Sanitize(v, 40, 30, 50);
    public static int Cooldown(int v) => CooldownChoices.Contains(v) ? v : 15;

    /// <summary>The first drive (≥ 10 GB) whose free space is under the limit.</summary>
    public static (string Name, int Threshold)? LowDisk(IEnumerable<(string Name, ulong Total, ulong Free)> drives, int threshold)
    {
        foreach (var d in drives)
        {
            if (d.Total < 10_000_000_000) continue;
            if ((double)d.Free / d.Total * 100 < threshold) return (d.Name, threshold);
        }
        return null;
    }

    /// <summary>The charge to warn about, when on battery at or under the limit.</summary>
    public static int? LowBattery(bool hasBattery, bool charging, bool pluggedIn, int? charge, int threshold) =>
        hasBattery && !charging && !pluggedIn && charge is { } c && c <= threshold ? c : null;

    /// <summary>Whether enough time passed since the same alert last fired.</summary>
    public static bool CooledDown(DateTime? last, DateTime now, int minutes) =>
        last is not { } previous || (now - previous).TotalMinutes >= minutes;
}
