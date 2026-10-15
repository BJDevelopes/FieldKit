// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;

namespace Fieldkit.Features.Monitor.Services;

/// <summary>
/// Optional monitor notifications (port of upstream MonitorAlertService): sustained high CPU, hot
/// CPU, critical memory pressure, low disk space, low battery and hot battery. All off by default;
/// CPU and temperature alerts ignore spikes under ~12 s; the same alert repeats at most once per
/// the chosen interval. Runs only while an alert of an attached feature is on.
/// </summary>
public static class MonitorAlerts
{
    public sealed record AlertDef(string Key, string FeatureId, string Title, MonitorNeeds Needs);

    public static readonly IReadOnlyList<AlertDef> All =
    [
        new(MonitorKeys.AlertCpu, "monitorCPU", "High CPU", MonitorNeeds.Cpu),
        new(MonitorKeys.AlertCpuTemperature, "monitorCPU", "High CPU temperature", MonitorNeeds.Temperatures),
        new(MonitorKeys.AlertMemory, "monitorMemory", "Critical memory pressure", MonitorNeeds.Memory),
        new(MonitorKeys.AlertDisk, "monitorDisk", "Low disk space", MonitorNeeds.Disk),
        new(MonitorKeys.AlertBatteryTemperature, "monitorPower", "High battery temperature", MonitorNeeds.Power),
        new(MonitorKeys.AlertBattery, "monitorPower", "Low battery", MonitorNeeds.Power),
    ];

    public static IEnumerable<string> KeysFor(string featureId) => All.Where(a => a.FeatureId == featureId).Select(a => a.Key);

    private static readonly HashSet<string> Attached = [];
    private static readonly SustainedAlertGate CpuGate = new(), CpuTemperatureGate = new(), BatteryTemperatureGate = new();
    private static readonly Dictionary<string, DateTime> LastSent = [];
    private static IDisposable? _lease;
    private static MonitorNeeds _needs;
    private static bool _subscribed;

    public static void Attach(string featureId)
    {
        Attached.Add(featureId);
        Refresh();
    }

    public static void Detach(string featureId)
    {
        Attached.Remove(featureId);
        Refresh();
    }

    private static bool On(AlertDef a) =>
        Attached.Contains(a.FeatureId) && FeatureCatalog.IsAvailable(a.FeatureId) && Settings.GetBool(a.Key, false)
        && (a.FeatureId != "monitorPower" || BatteryReader.HasBattery);

    private static void Refresh()
    {
        var active = All.Where(On).ToList();
        if (active.Count == 0)
        {
            if (_subscribed)
            {
                MonitorSampler.Updated -= Evaluate;
                Settings.Changed -= OnSetting;
                _subscribed = false;
            }
            _lease?.Dispose();
            _lease = null;
            _needs = MonitorNeeds.None;
            CpuGate.Reset();
            CpuTemperatureGate.Reset();
            BatteryTemperatureGate.Reset();
            return;
        }
        if (!_subscribed)
        {
            _subscribed = true;
            MonitorSampler.Updated += Evaluate;
            Settings.Changed += OnSetting;
        }
        var needs = active.Aggregate(MonitorNeeds.None, (n, a) => n | a.Needs);
        if (_lease is null || needs != _needs)
        {
            var previous = _lease;
            _lease = MonitorSampler.Acquire(needs, foreground: false);
            _needs = needs;
            previous?.Dispose();
        }
    }

    private static void OnSetting(string key)
    {
        if (key.Contains(".alert.", StringComparison.Ordinal)) UI.Ui.OnUi(Refresh);
    }

    private static void Evaluate(MonitorSnapshot s)
    {
        var f = MonitorKeys.Fahrenheit;
        bool IsOn(string key) => All.First(a => a.Key == key) is var def && On(def);

        if (IsOn(MonitorKeys.AlertCpu))
        {
            var threshold = AlertPolicy.CpuThreshold(Settings.GetInt(MonitorKeys.AlertCpuThreshold, 90));
            if (CpuGate.ShouldAlert(s.CpuUsage, threshold / 100.0, s.CpuReadAt))
                Send("cpu", "High CPU", $"CPU stayed above {threshold}% for a few seconds.");
        }
        else CpuGate.Reset();

        if (IsOn(MonitorKeys.AlertCpuTemperature))
        {
            var threshold = AlertPolicy.CpuTemperatureThreshold(Settings.GetInt(MonitorKeys.AlertCpuTemperatureThreshold, 90));
            if (CpuTemperatureGate.ShouldAlert(s.CpuTemperature, threshold, s.CpuTemperatureReadAt) && s.CpuTemperature is { } t)
                Send("cpuTemperature", "Hot CPU", $"CPU reached {MetricFormat.Temperature(t, f)}.");
        }
        else CpuTemperatureGate.Reset();

        if (IsOn(MonitorKeys.AlertBatteryTemperature))
        {
            var threshold = AlertPolicy.BatteryTemperatureThreshold(Settings.GetInt(MonitorKeys.AlertBatteryTemperatureThreshold, 40));
            if (BatteryTemperatureGate.ShouldAlert(s.BatteryTemperature, threshold, s.BatteryTemperatureReadAt) && s.BatteryTemperature is { } t)
                Send("batteryTemperature", "Hot battery", $"Battery reached {MetricFormat.Temperature(t, f)}.");
        }
        else BatteryTemperatureGate.Reset();

        if (IsOn(MonitorKeys.AlertMemory) && s.MemoryPressure == MemoryPressure.Critical)
            Send("memory", "Critical memory", "Memory pressure reached the critical level.");

        if (IsOn(MonitorKeys.AlertDisk))
        {
            var threshold = AlertPolicy.DiskFreePercent(Settings.GetInt(MonitorKeys.AlertDiskFreePercent, 10));
            if (AlertPolicy.LowDisk(s.Drives.Select(d => ($"{d.Label} ({d.Name})", d.TotalBytes, d.FreeBytes)), threshold) is { } low)
                Send("disk", "Low disk space", $"{low.Name} has less than {low.Threshold}% free.");
        }

        if (IsOn(MonitorKeys.AlertBattery) && s.Power is { } p)
        {
            var threshold = AlertPolicy.BatteryPercent(Settings.GetInt(MonitorKeys.AlertBatteryPercent, 15));
            if (AlertPolicy.LowBattery(p.HasBattery, p.Charging, p.PluggedIn, p.ChargePercent, threshold) is { } charge)
                Send("battery", "Low battery", $"Battery is at {charge}%.");
        }
    }

    private static void Send(string kind, string title, string body)
    {
        var minutes = AlertPolicy.Cooldown(int.TryParse(Settings.GetString(MonitorKeys.AlertCooldown, "15"), out var m) ? m : 15);
        var now = DateTime.UtcNow;
        if (!AlertPolicy.CooledDown(LastSent.TryGetValue(kind, out var last) ? last : null, now, minutes)) return;
        LastSent[kind] = now;
        Notifier.Show(title, body, warning: true);
    }
}
