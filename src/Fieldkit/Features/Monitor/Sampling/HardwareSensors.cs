// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// CPU, motherboard and fan sensors through LibreHardwareMonitor. LHM 0.9.6 reads those through
/// PawnIO, a signed kernel driver installed separately (it no longer ships WinRing0, which
/// Microsoft Defender flags as vulnerable). Because that means kernel-level code, it is strictly
/// opt-in (<see cref="MonitorKeys.HardwareSensors"/>), needs administrator rights, and is
/// opened on a background thread. Everything else in the monitor uses user-mode APIs.
/// </summary>
public static class HardwareSensors
{
    private static readonly object Gate = new();
    private static Computer? _computer;
    private static Thread? _opening;
    private static SensorStatus _status = SensorStatus.NotRequested;

    public sealed record Reading(
        double? CpuHottest, double? CpuAverage, double? Gpu, double? Motherboard, double? SystemWatts,
        IReadOnlyList<FanReading> Fans);

    public static SensorStatus Status
    {
        get { lock (Gate) return _status; }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Whether the PawnIO driver is installed (a read-only check).</summary>
    public static bool PawnIoInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>The status the opt-in would lead to, without opening anything.</summary>
    public static SensorStatus Prerequisite() =>
        !Settings.GetBool(MonitorKeys.HardwareSensors, false) ? SensorStatus.DriverOff
        : !IsElevated ? SensorStatus.NeedsAdmin
        : !PawnIoInstalled ? SensorStatus.NeedsPawnIo
        : SensorStatus.Ready;

    /// <summary>Opens LHM in the background when the user opted in and it can work. Idempotent.</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_computer is not null || _opening is not null) return;
            var pre = Prerequisite();
            if (pre != SensorStatus.Ready)
            {
                _status = pre;
                return;
            }
            _status = SensorStatus.Starting;
            _opening = new Thread(Open) { IsBackground = true, Name = "Fieldkit hardware sensors" };
            _opening.Start();
        }
    }

    private static void Open()
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
            };
            computer.Open();
            lock (Gate)
            {
                if (_opening == Thread.CurrentThread)
                {
                    _computer = computer;
                    computer = null;
                    _status = SensorStatus.Ready;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Opening hardware sensors failed", ex);
            lock (Gate) _status = SensorStatus.Failed;
        }
        finally
        {
            try { computer?.Close(); } catch { }
            lock (Gate)
            {
                if (_opening == Thread.CurrentThread) _opening = null;
            }
        }
    }

    /// <summary>Closes LHM (releases the driver modules). Fan controllers must be restored first.</summary>
    public static void Stop()
    {
        Computer? computer;
        lock (Gate)
        {
            computer = _computer;
            _computer = null;
            _opening = null;
            _status = SensorStatus.NotRequested;
        }
        try { computer?.Close(); }
        catch (Exception ex) { Log.Error("Closing hardware sensors failed", ex); }
    }

    public static Reading? Read()
    {
        lock (Gate)
        {
            if (_computer is null) return null;
            try
            {
                double? cpuHot = null, cpuAvg = null, gpu = null, board = null, cpuPower = null;
                var fans = new List<FanReading>();
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();
                    var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).ToList();
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            var temps = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125).ToList();
                            var package = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                                                  || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase));
                            var hottest = package?.Value ?? temps.Select(s => s.Value).Max();
                            if (hottest is { } h) cpuHot = Math.Max(cpuHot ?? h, h);
                            var cores = temps.Where(s => s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)).ToList();
                            if (cores.Count > 0) cpuAvg = cores.Average(s => s.Value!.Value);
                            cpuPower ??= sensors.FirstOrDefault(s => s.SensorType == SensorType.Power
                                                                    && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Value;
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            var g = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125)
                                .Select(s => (double?)s.Value).Max();
                            if (g is { } gv) gpu = Math.Max(gpu ?? gv, gv);
                            break;
                        case HardwareType.Motherboard:
                            var b = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125)
                                .Select(s => (double?)s.Value).Max();
                            if (b is { } bv) board = Math.Max(board ?? bv, bv);
                            break;
                    }
                    fans.AddRange(Fans(sensors));
                }
                return new Reading(cpuHot, cpuAvg ?? cpuHot, gpu, board, cpuPower, fans);
            }
            catch (Exception ex)
            {
                Log.Error("Reading hardware sensors failed", ex);
                return null;
            }
        }
    }

    /// <summary>Pairs each fan speed sensor with its duty controller (same index on the same chip).</summary>
    private static IEnumerable<FanReading> Fans(List<ISensor> sensors)
    {
        var speeds = sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        var controls = sensors.Where(s => s.SensorType == SensorType.Control).ToList();
        foreach (var fan in speeds)
        {
            var control = controls.FirstOrDefault(c => c.Hardware == fan.Hardware && c.Index == fan.Index);
            if (fan.Value is null or <= 0 && control is null) continue;
            yield return new FanReading(fan.Identifier.ToString(), fan.Name, fan.Value, control?.Value, control?.Control is not null);
        }
        foreach (var control in controls.Where(c => c.Control is not null && !speeds.Any(f => f.Hardware == c.Hardware && f.Index == c.Index)))
            yield return new FanReading(control.Identifier.ToString(), control.Name, null, control.Value, true);
    }

    /// <summary>Sets a controllable fan to a software duty (%). False when it is not controllable.</summary>
    public static bool SetFanDuty(string fanId, int level)
    {
        lock (Gate)
        {
            if (FindControl(fanId) is not { } control) return false;
            try
            {
                control.SetSoftware(FanPolicy.DutyFor(level, control.MinSoftwareValue, control.MaxSoftwareValue));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Setting fan {fanId} failed", ex);
                return false;
            }
        }
    }

    /// <summary>Hands every fan we touched back to its automatic (firmware/BIOS) control.</summary>
    public static void RestoreAllFans(IEnumerable<string> fanIds)
    {
        lock (Gate)
        {
            foreach (var id in fanIds)
            {
                try { FindControl(id)?.SetDefault(); }
                catch (Exception ex) { Log.Error($"Restoring fan {id} failed", ex); }
            }
        }
    }

    private static IControl? FindControl(string fanId)
    {
        if (_computer is null) return null;
        foreach (var hw in _computer.Hardware)
        {
            foreach (var sensor in hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)))
            {
                if (sensor.Identifier.ToString() == fanId && sensor.Control is { } own) return own;
                if (sensor.SensorType != SensorType.Fan || sensor.Identifier.ToString() != fanId) continue;
                var sibling = sensor.Hardware.Sensors.FirstOrDefault(c => c.SensorType == SensorType.Control && c.Index == sensor.Index);
                if (sibling?.Control is { } c2) return c2;
            }
        }
        return null;
    }

    public static string StatusText(SensorStatus status) => status switch
    {
        SensorStatus.NeedsAdmin => "Run as administrator for temperatures",
        SensorStatus.NeedsPawnIo => "Install the PawnIO driver for CPU temperatures",
        SensorStatus.Starting => "Starting sensors…",
        SensorStatus.Failed => "Sensors unavailable on this PC",
        SensorStatus.DriverOff => "CPU temperatures need hardware sensors (Settings › Monitor)",
        _ => "Sensors unavailable on this PC",
    };
}
