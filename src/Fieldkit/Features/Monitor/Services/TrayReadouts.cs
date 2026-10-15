// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.Interop;
using Fieldkit.UI;
using DrawingColor = System.Drawing.Color;
using DrawingIcon = System.Drawing.Icon;

namespace Fieldkit.Features.Monitor.Services;

/// <summary>A reading that can sit in the notification area, with its owner feature and switch.</summary>
public sealed record ReadoutDef(ReadoutMetric Metric, string FeatureId, string Title, string Glyph, MonitorNeeds Needs, string Section)
{
    public string Key => $"{FeatureId}.tray.{char.ToLowerInvariant(Metric.ToString()[0])}{Metric.ToString()[1..]}";
}

/// <summary>
/// Extra notification-area icons showing chosen readings (upstream "menu bar readouts"), drawn
/// with <see cref="TrayIcon.RenderTextIcon"/>. Active while at least one feature that owns an
/// enabled reading is attached (its service runs); every icon and HICON is destroyed on detach.
/// </summary>
public static class TrayReadouts
{
    public static readonly IReadOnlyList<ReadoutDef> All =
    [
        new(ReadoutMetric.Cpu, "monitorCPU", "CPU", UI.Glyphs.Cpu, MonitorNeeds.Cpu, "system"),
        new(ReadoutMetric.CpuTemperature, "monitorCPU", "CPU temperature", Views.MonitorGlyphs.Thermometer, MonitorNeeds.Temperatures, "system"),
        new(ReadoutMetric.Gpu, "monitorGPU", "GPU", UI.Glyphs.Gpu, MonitorNeeds.Gpu, "system"),
        new(ReadoutMetric.GpuTemperature, "monitorGPU", "GPU temperature", Views.MonitorGlyphs.Thermometer, MonitorNeeds.Temperatures, "system"),
        new(ReadoutMetric.Memory, "monitorMemory", "Memory", UI.Glyphs.Memory, MonitorNeeds.Memory, "system"),
        new(ReadoutMetric.Battery, "monitorPower", "Battery", UI.Glyphs.Battery, MonitorNeeds.Power, "power"),
        new(ReadoutMetric.BatteryTime, "monitorPower", "Battery time remaining", UI.Glyphs.Clock, MonitorNeeds.Power, "power"),
        new(ReadoutMetric.BatteryTemperature, "monitorPower", "Battery temperature", Views.MonitorGlyphs.Thermometer, MonitorNeeds.Power, "power"),
        new(ReadoutMetric.Network, "monitorNetwork", "Network", UI.Glyphs.Globe, MonitorNeeds.Network, "network"),
        new(ReadoutMetric.DiskUsage, "monitorDisk", "Disk usage", UI.Glyphs.HardDrive, MonitorNeeds.Disk, "disk"),
        new(ReadoutMetric.DiskActivity, "monitorDisk", "Live activity", UI.Glyphs.ArrowsUpDown, MonitorNeeds.Disk, "disk"),
        new(ReadoutMetric.ConnectedDevices, "connectedDevices", "Connected Devices", UI.Glyphs.Usb, MonitorNeeds.Devices, "power"),
        new(ReadoutMetric.Power, "monitorPower", "Power", UI.Glyphs.Bolt, MonitorNeeds.Power, "power"),
        new(ReadoutMetric.FanSpeed, "fanControl", "Fan speed", UI.Glyphs.Fan, MonitorNeeds.Fans, "fanControl"),
    ];

    public static IEnumerable<string> KeysFor(string featureId) => All.Where(d => d.FeatureId == featureId).Select(d => d.Key);

    private static readonly HashSet<string> Attached = [];
    private static readonly Dictionary<string, (NotifyIcon Icon, DrawingIcon? Image, string Signature)> Icons = new(StringComparer.Ordinal);
    private static IDisposable? _lease;
    private static MonitorNeeds _leaseNeeds;
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

    private static List<ReadoutDef> Active() =>
        All.Where(d => Attached.Contains(d.FeatureId) && FeatureCatalog.IsAvailable(d.FeatureId) && Settings.GetBool(d.Key, false)).ToList();

    private static void Refresh()
    {
        var active = Active();
        if (active.Count == 0)
        {
            Shutdown();
            return;
        }
        if (!_subscribed)
        {
            _subscribed = true;
            MonitorSampler.Updated += OnSample;
            Settings.Changed += OnSettingChanged;
        }
        var needs = active.Aggregate(MonitorNeeds.None, (n, d) => n | d.Needs);
        if (_lease is null || needs != _leaseNeeds)
        {
            var previous = _lease;
            _lease = MonitorSampler.Acquire(needs, foreground: false);
            _leaseNeeds = needs;
            previous?.Dispose();
        }
        Render(MonitorSampler.Latest, active);
    }

    private static void Shutdown()
    {
        if (_subscribed)
        {
            MonitorSampler.Updated -= OnSample;
            Settings.Changed -= OnSettingChanged;
            _subscribed = false;
        }
        _lease?.Dispose();
        _lease = null;
        _leaseNeeds = MonitorNeeds.None;
        foreach (var key in Icons.Keys.ToList()) RemoveIcon(key);
    }

    private static void OnSettingChanged(string key)
    {
        if (key.Contains(".tray.", StringComparison.Ordinal) || key == MonitorKeys.TemperatureUnit || key == MonitorKeys.NetworkSpeedUnit)
            Ui.OnUi(Refresh);
    }

    private static void OnSample(MonitorSnapshot s) => Render(s, Active());

    private static void Render(MonitorSnapshot s, List<ReadoutDef> active)
    {
        var values = active.Select(d => Value(d, s)).ToList();
        var layout = TrayLayout.Layout(values,
            Settings.GetBool(MonitorKeys.TraySeparate, false),
            Settings.GetBool(MonitorKeys.TrayCombineTemperatures, true));
        var keep = new HashSet<string>(layout.Select(l => l.Key), StringComparer.Ordinal);
        foreach (var key in Icons.Keys.Where(k => !keep.Contains(k)).ToList()) RemoveIcon(key);
        foreach (var content in layout)
        {
            var signature = $"{content.Line1}\n{content.Line2}\n{content.Tint}\n{TrayIcon.TaskbarIsLight()}";
            if (!Icons.TryGetValue(content.Key, out var entry))
            {
                var icon = new NotifyIcon { Visible = false };
                var section = active.First(d => d.Metric == content.Metrics[0]).Section;
                icon.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left) PanelHost.Show(section);
                };
                entry = (icon, null, string.Empty);
            }
            if (entry.Signature != signature)
            {
                var image = TrayIcon.RenderTextIcon(content.Line1, content.Line2, Color(content.Tint));
                entry.Icon.Icon = image;
                if (entry.Image is { } old)
                {
                    Native.DestroyIcon(old.Handle);
                    old.Dispose();
                }
                entry = (entry.Icon, image, signature);
            }
            var tooltip = content.Tooltip.Length > 63 ? content.Tooltip[..63] : content.Tooltip;
            if (entry.Icon.Text != tooltip) entry.Icon.Text = tooltip;
            entry.Icon.Visible = true;
            Icons[content.Key] = entry;
        }
    }

    private static void RemoveIcon(string key)
    {
        if (!Icons.Remove(key, out var entry)) return;
        entry.Icon.Visible = false;
        entry.Icon.Dispose();
        if (entry.Image is { } image)
        {
            Native.DestroyIcon(image.Handle);
            image.Dispose();
        }
    }

    private static DrawingColor? Color(ReadoutTint tint) => tint switch
    {
        ReadoutTint.Green => DrawingColor.FromArgb(52, 199, 89),
        ReadoutTint.Yellow => DrawingColor.FromArgb(255, 204, 0),
        ReadoutTint.Red => DrawingColor.FromArgb(255, 69, 58),
        _ => null,
    };

    /// <summary>The short text a reading shows (public for tests and the settings preview).</summary>
    public static ReadoutValue Value(ReadoutDef d, MonitorSnapshot s)
    {
        var c = CultureInfo.CurrentCulture;
        var f = MonitorKeys.Fahrenheit;
        string Pct(double? v) => v is { } x ? MetricFormat.Percent(x) : "--";
        string Temp(double? v) => v is { } x ? MetricFormat.TemperatureCompact(x, f) : "--";
        ReadoutValue V(string label, string value, string tip, string? value2 = null, ReadoutTint tint = ReadoutTint.Default) =>
            new(d.Metric, label, value, $"{d.Title}: {tip}", value2, tint);

        switch (d.Metric)
        {
            case ReadoutMetric.Cpu: return V("CPU", Pct(s.CpuUsage), Pct(s.CpuUsage));
            case ReadoutMetric.CpuTemperature:
                return V("CPU", Temp(s.CpuTemperature), s.CpuTemperature is { } ct ? MetricFormat.Temperature(ct, f) : "unavailable");
            case ReadoutMetric.Gpu: return V("GPU", Pct(s.GpuUsage), Pct(s.GpuUsage));
            case ReadoutMetric.GpuTemperature:
                return V("GPU", Temp(s.GpuTemperature), s.GpuTemperature is { } gt ? MetricFormat.Temperature(gt, f) : "unavailable");
            case ReadoutMetric.Memory:
            {
                var committed = Settings.GetString(MonitorKeys.MemoryMetric, "used") == "committed";
                var fraction = s.MemoryFraction(committed);
                var tint = Settings.GetBool(MonitorKeys.TrayMemoryDot, false)
                    ? s.MemoryPressure switch
                    {
                        MemoryPressure.Normal => ReadoutTint.Green,
                        MemoryPressure.Warning => ReadoutTint.Yellow,
                        MemoryPressure.Critical => ReadoutTint.Red,
                        _ => ReadoutTint.Default,
                    }
                    : ReadoutTint.Default;
                return V("RAM", Pct(fraction), $"{Pct(fraction)} ({s.MemoryPressure})", tint: tint);
            }
            case ReadoutMetric.Battery:
                return V("BAT", s.Power?.ChargePercent is { } b ? $"{b}%" : "--",
                    s.Power?.ChargePercent is { } b2 ? $"{b2}%{(s.Power.Charging ? ", charging" : string.Empty)}" : "no battery");
            case ReadoutMetric.BatteryTime:
            {
                var secs = s.Power?.TimeRemainingSeconds;
                var text = secs is { } x ? $"{(int)(x / 3600)}:{(int)(x % 3600 / 60):00}" : s.Power?.PluggedIn == true ? "AC" : "--";
                return V("BAT", text, secs is { } y ? MetricFormat.BatteryTime(y) ?? "-" : "plugged in or calculating");
            }
            case ReadoutMetric.BatteryTemperature:
                return V("BAT", Temp(s.BatteryTemperature), s.BatteryTemperature is { } bt ? MetricFormat.Temperature(bt, f) : "unavailable");
            case ReadoutMetric.Network:
            {
                var bits = MonitorKeys.SpeedInBits;
                var down = "↓" + (s.NetDown is { } dn ? MetricFormat.RateCompact(dn, bits) : "-");
                var up = "↑" + (s.NetUp is { } un ? MetricFormat.RateCompact(un, bits) : "-");
                var tip = $"↓ {(s.NetDown is { } a ? MetricFormat.NetworkRate(a, bits) : "-")}  ↑ {(s.NetUp is { } b3 ? MetricFormat.NetworkRate(b3, bits) : "-")}";
                return Settings.GetBool(MonitorKeys.TrayUploadFirst, false) ? V("NET", up, tip, down) : V("NET", down, tip, up);
            }
            case ReadoutMetric.DiskUsage:
            {
                var drive = s.Drives.FirstOrDefault(x => x.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                            ?? s.Drives.FirstOrDefault();
                if (drive is null) return V("SSD", "--", "no drive");
                var style = Settings.GetString(MonitorKeys.TrayDiskStyle, "percent");
                var value = style switch
                {
                    "free" => ShortBytes(drive.FreeBytes),
                    "used" => ShortBytes(drive.UsedBytes),
                    _ => MetricFormat.Percent(drive.UsedFraction),
                };
                return V(drive.Name.TrimEnd(':'), value,
                    $"{drive.Name} {MetricFormat.Percent(drive.UsedFraction)} used, {MetricFormat.Bytes(drive.FreeBytes)} free");
            }
            case ReadoutMetric.DiskActivity:
                return V("DSK", "R" + (s.DiskRead is { } r ? MetricFormat.RateCompact(r, false) : "-"),
                    $"read {(s.DiskRead is { } r2 ? MetricFormat.BytesPerSec(r2) : "-")}, write {(s.DiskWrite is { } w2 ? MetricFormat.BytesPerSec(w2) : "-")}",
                    "W" + (s.DiskWrite is { } w ? MetricFormat.RateCompact(w, false) : "-"));
            case ReadoutMetric.ConnectedDevices:
                return V("USB", s.DevicesRead ? s.Devices.Count.ToString(c) : "-", s.DevicesRead ? $"{s.Devices.Count} connected" : "-");
            case ReadoutMetric.Power:
                return V("PWR", s.Power?.SystemWatts is { } pw ? MetricFormat.WattsCompact(pw) : "--",
                    s.Power?.SystemWatts is { } pw2 ? MetricFormat.Watts(pw2) : "unavailable");
            case ReadoutMetric.FanSpeed:
            {
                var rpm = s.Fans.Select(x => x.Rpm ?? 0).DefaultIfEmpty(0).Max();
                var text = rpm <= 0 ? "--" : rpm >= 1000 ? (rpm / 1000).ToString("0.0", c) + "k" : rpm.ToString("0", c);
                return V("FAN", text, rpm > 0 ? $"{rpm:0} RPM" : "unavailable");
            }
            default:
                return V(d.Title, "--", "-");
        }
    }

    private static string ShortBytes(ulong bytes)
    {
        var (value, unit) = MetricFormat.Scale(bytes);
        return (value < 10 ? value.ToString("0.0", CultureInfo.CurrentCulture) : value.ToString("0", CultureInfo.CurrentCulture)) + unit[0];
    }
}
