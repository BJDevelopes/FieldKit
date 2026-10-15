// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Fans;

/// <summary>Live fan control state, shared by the service and the panel.</summary>
public static class FanController
{
    /// <summary>True while a manual level or curve is being applied to at least one fan.</summary>
    public static bool IsControlling { get; internal set; }

    /// <summary>The cooling level (%) last sent to the fans.</summary>
    public static int? CurrentLevel { get; internal set; }

    /// <summary>Why control last stopped by itself (shown in the panel), or null.</summary>
    public static string? Message { get; internal set; }

    /// <summary>Set when the user applied a configuration since the app started.</summary>
    public static bool AppliedThisSession { get; internal set; }

    /// <summary>Raised on the UI thread when the state above changes.</summary>
    public static event Action? Changed;

    internal static void Raise() => Ui.OnUi(() => Changed?.Invoke());

    private static FanMode ParseMode(string value) => value switch
    {
        "manual" => FanMode.Manual,
        "curve" => FanMode.Curve,
        _ => FanMode.System,
    };

    /// <summary>The mode chosen in the editor.</summary>
    public static FanMode Mode => ParseMode(Settings.GetString(MonitorKeys.FanMode, "system"));

    /// <summary>The mode last applied (what the fans follow while controlling).</summary>
    public static FanMode AppliedMode => ParseMode(Settings.GetString(MonitorKeys.FanAppliedMode, "system"));

    /// <summary>The curves being edited.</summary>
    public static IReadOnlyList<FanCurve> Curves =>
        FanPolicy.DecodeCurves(Settings.GetString(MonitorKeys.FanCurves, string.Empty)) ?? [FanPolicy.DefaultCurve];

    public static IReadOnlyList<FanCurve> AppliedCurves =>
        FanPolicy.DecodeCurves(Settings.GetString(MonitorKeys.FanAppliedCurves, string.Empty)) ?? [FanPolicy.DefaultCurve];

    public static int AppliedLevel => FanPolicy.SteppedLevel(Settings.GetInt(MonitorKeys.FanAppliedLevel, FanPolicy.DefaultLevel));

    /// <summary>Applies the editor's mode (the service runs while <see cref="MonitorKeys.FanApplied"/> is on).</summary>
    public static void Apply()
    {
        Message = null;
        AppliedThisSession = true;
        var mode = Mode;
        Settings.Set(MonitorKeys.FanAppliedMode, mode.ToString().ToLowerInvariant());
        Settings.Set(MonitorKeys.FanAppliedLevel, FanPolicy.SteppedLevel(Settings.GetInt(MonitorKeys.FanManualLevel, FanPolicy.DefaultLevel)));
        Settings.Set(MonitorKeys.FanAppliedCurves, FanPolicy.EncodeCurves(Curves));
        Settings.Set(MonitorKeys.FanApplied, mode != FanMode.System);
        FeatureRuntime.Reevaluate("fanControl");
        Raise();
    }

    /// <summary>Hands the fans back to automatic control.</summary>
    public static void ReturnToSystem(string? reason = null)
    {
        Message = reason;
        Settings.Set(MonitorKeys.FanApplied, false);
        FeatureRuntime.Reevaluate("fanControl");
        Raise();
    }

    public static IReadOnlyDictionary<FanTemperatureSource, double> Temperatures(MonitorSnapshot s)
    {
        var map = new Dictionary<FanTemperatureSource, double>();
        if (s.CpuTemperature is { } hot) map[FanTemperatureSource.HottestCpu] = hot;
        if (s.AverageCpuTemperature is { } avg) map[FanTemperatureSource.AverageCpu] = avg;
        if (s.GpuTemperature is { } gpu) map[FanTemperatureSource.HottestGpu] = gpu;
        if (s.MotherboardTemperature is { } board) map[FanTemperatureSource.Motherboard] = board;
        return map;
    }

    public static string SourceTitle(FanTemperatureSource source) => source switch
    {
        FanTemperatureSource.HottestCpu => "Hottest CPU",
        FanTemperatureSource.AverageCpu => "Average CPU",
        FanTemperatureSource.HottestGpu => "Hottest GPU",
        FanTemperatureSource.Motherboard => "Motherboard",
        _ => source.ToString(),
    };
}

/// <summary>
/// Applies a manual level or temperature curve to every controllable fan through LHM while
/// <see cref="MonitorKeys.FanApplied"/> is on, and always hands the fans back to automatic
/// control when it stops (switched off, feature uninstalled, sleep, app exit) or when a safety
/// rule trips (sensor lost, temperature too high).
/// </summary>
public sealed class FanControlService : FeatureService
{
    private const double SafetyCelsius = 95;
    private IDisposable? _lease;
    private readonly HashSet<string> _touched = [];
    private int _missingTemperature;
    private bool _suspended;

    public override string FeatureId => "fanControl";
    public override IReadOnlyList<string> EnableKeys => [MonitorKeys.FanApplied, MonitorKeys.FanResume];
    public override bool EnabledByDefault => false;

    public override bool ShouldRun() =>
        Settings.GetBool(MonitorKeys.FanApplied, false)
        && FanController.AppliedMode != FanMode.System
        && (FanController.AppliedThisSession || Settings.GetBool(MonitorKeys.FanResume, false));

    protected override void OnStart()
    {
        _missingTemperature = 0;
        _lease = MonitorSampler.Acquire(MonitorNeeds.Fans | MonitorNeeds.Temperatures, foreground: false);
        MonitorSampler.Updated += OnSample;
        SystemEvents.PowerModeChanged += OnPowerMode;
        FanController.IsControlling = true;
        FanController.Raise();
    }

    protected override void OnStop()
    {
        MonitorSampler.Updated -= OnSample;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        Restore();
        FanController.IsControlling = false;
        FanController.CurrentLevel = null;
        _lease?.Dispose();
        _lease = null;
        FanController.Raise();
    }

    private void Restore()
    {
        if (_touched.Count == 0) return;
        HardwareSensors.RestoreAllFans(_touched);
        Log.Info($"Fan control: returned {_touched.Count} fan(s) to automatic control");
        _touched.Clear();
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        Ui.OnUi(() =>
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _suspended = true;
                Restore();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _suspended = false;
                if (!Settings.GetBool(MonitorKeys.FanResume, false)) FanController.ReturnToSystem();
            }
        });
    }

    private void OnSample(MonitorSnapshot s)
    {
        if (!IsRunning || _suspended) return;
        var status = s.Sensors;
        if (status is SensorStatus.NeedsAdmin or SensorStatus.NeedsPawnIo or SensorStatus.DriverOff or SensorStatus.Failed)
        {
            FanController.ReturnToSystem(HardwareSensors.StatusText(status));
            return;
        }
        if (status != SensorStatus.Ready) return;
        var controllable = s.Fans.Where(f => f.Controllable).ToList();
        if (controllable.Count == 0) return;

        var temps = FanController.Temperatures(s);
        var hottest = temps.Values.DefaultIfEmpty(0).Max();
        if (hottest >= SafetyCelsius && (FanController.CurrentLevel ?? 0) < FanPolicy.MaximumLevel)
        {
            FanController.ReturnToSystem("Returned to system control because the temperature rose too high.");
            return;
        }

        int? level;
        if (FanController.AppliedMode == FanMode.Manual)
        {
            level = FanController.AppliedLevel;
        }
        else
        {
            level = FanPolicy.CurveLevel(FanController.AppliedCurves, temps, FanController.CurrentLevel);
            if (level is null)
            {
                if (++_missingTemperature >= 3)
                    FanController.ReturnToSystem("The selected temperature sensor stopped responding. Fan control returned to System.");
                return;
            }
            _missingTemperature = 0;
        }

        foreach (var fan in controllable)
        {
            if (HardwareSensors.SetFanDuty(fan.Id, level.Value)) _touched.Add(fan.Id);
        }
        if (_touched.Count == 0)
        {
            FanController.ReturnToSystem("The fans returned to system control because the requested control could not be verified.");
            return;
        }
        if (FanController.CurrentLevel != level)
        {
            FanController.CurrentLevel = level;
            FanController.Raise();
        }
    }
}
