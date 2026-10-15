// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;
using static Fieldkit.Features.Monitor.Views.MonitorWidgets;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>
/// The Power section (upstream PowerSection): charge and state, system draw with graph, battery
/// flow, time remaining, health and cycles, battery temperature, and the connected devices list.
/// Rows the hardware cannot report are hidden.
/// </summary>
internal static class PowerView
{
    private static bool Power => FeatureCatalog.IsAvailable("monitorPower");
    private static bool Devices => FeatureCatalog.IsAvailable("connectedDevices");

    public static IReadOnlyList<PanelOption> Options()
    {
        var list = new List<PanelOption>();
        if (Power)
        {
            list.Add(new("Charge", MonitorKeys.SysBattery));
            list.Add(new("System", MonitorKeys.PwrSystem));
            list.Add(new("Graph", MonitorKeys.GraphPower, Indented: true));
            list.Add(new("Battery", MonitorKeys.PwrBattery));
            list.Add(new("Battery time remaining", MonitorKeys.PwrTimeRemaining));
            list.Add(new("Battery health", MonitorKeys.PwrHealth));
            list.Add(new("Battery temperature", MonitorKeys.PwrTemperature));
        }
        if (Devices) list.Add(new("Connected Devices", MonitorKeys.PwrDevices));
        return list;
    }

    private static MonitorNeeds Needs() =>
        (Power ? MonitorNeeds.Power : 0) | (Devices && MonitorKeys.Shown(MonitorKeys.PwrDevices) ? MonitorNeeds.Devices : 0);

    public static FrameworkElement Build() =>
        MonitorPanel.Live("power", Needs, Render, Options(), MonitorKeys.TemperatureUnit, MonitorKeys.GraphScale);

    private static (FrameworkElement, Action<MonitorSnapshot>) Render()
    {
        var card = new StackPanel();
        var rows = new StackPanel();
        var updates = new List<Action<MonitorSnapshot>>();
        var unavailable = Note("Power metrics unavailable on this PC");
        unavailable.Visibility = Visibility.Collapsed;

        if (Power)
        {
            card.Children.Add(rows);
            card.Children.Add(unavailable);
            var entries = new List<(FrameworkElement Row, Func<MonitorSnapshot, bool> Visible)>();

            if (MonitorKeys.Shown(MonitorKeys.SysBattery))
            {
                var bar = new UsageBar("Green");
                var value = Value("-", 13);
                value.Width = ValueWidth;
                var icon = Ui.Icon(Glyphs.Battery, 12, "TextSecondary");
                icon.Width = 14;
                var label = Ui.Text("Battery", 13, brush: "TextSecondary", wrap: false);
                label.Margin = new Thickness(6, 0, 0, 0);
                label.Width = LabelWidth;
                bar.Margin = new Thickness(8, 0, 8, 0);
                var state = Note(string.Empty);
                state.Margin = new Thickness(20, 0, 0, 0);
                var line = Line("Green");
                var block = Ui.Stack(4, Columns([Auto, Auto, Star, Auto], icon, label, bar, value), state);
                if (Settings.GetBool(MonitorKeys.GraphBattery, true)) Ui.Append(block, 4, Graph(line, () => "100%"));
                entries.Add((block, s => s.Power?.ChargePercent is not null));
                updates.Add(s =>
                {
                    if (s.Power is not { } p) return;
                    var charge = p.ChargePercent;
                    bar.Set(charge / 100.0);
                    bar.SetTint(charge < 20 ? "Red" : charge < 40 ? "Yellow" : "Green");
                    value.Text = charge is { } c ? $"{c}%" : "-";
                    icon.Text = p.PluggedIn ? Glyphs.Plug : Glyphs.Battery;
                    state.Text = (p.Charging ? "Charging" : p.PluggedIn ? "Plugged in" : "On battery")
                                 + (p.SaverOn ? " · Energy saver" : string.Empty);
                    line.SetValues(s.BatteryHistory);
                });
            }

            if (MonitorKeys.Shown(MonitorKeys.PwrSystem))
            {
                var row = ValueRow(Glyphs.Bolt, "Orange", "System", out var value, out _);
                var line = Line("Orange", null);
                var block = Ui.Stack(6, row);
                FrameworkElement? graph = null;
                if (Settings.GetBool(MonitorKeys.GraphPower, true)) Ui.Append(block, 4, graph = Graph(line, () => string.Empty));
                entries.Add((block, s => s.Power?.SystemWatts is not null));
                updates.Add(s =>
                {
                    if (s.Power?.SystemWatts is not { } w) return;
                    value.Text = MetricFormat.Watts(w);
                    var peak = MetricFormat.GraphCeiling(Math.Max(1, s.SystemPowerHistory.DefaultIfEmpty(0).Max()), 1000);
                    line.Maximum = peak;
                    line.SetValues(s.SystemPowerHistory);
                    if (graph is not null) SetCeiling(graph, MetricFormat.Watts(peak));
                });
            }

            if (MonitorKeys.Shown(MonitorKeys.PwrBattery))
            {
                var row = ValueRow(Glyphs.Plug, "Green", "Battery", out var value, out var caption);
                caption.Visibility = Visibility.Visible;
                var icon = (TextBlock)((Grid)row).Children[0];
                entries.Add((row, s => s.Power is { HasBattery: true, BatteryWatts: not null }));
                updates.Add(s =>
                {
                    if (s.Power?.BatteryWatts is not { } flow) return;
                    value.Text = MetricFormat.Watts(Math.Abs(flow));
                    caption.Text = flow > 0.05 ? "Charging" : flow < -0.05 ? "On battery" : "Not charging";
                    icon.Text = flow > 0.05 ? Glyphs.Plug : Glyphs.Battery;
                    icon.SetResourceReference(TextBlock.ForegroundProperty, flow > 0.05 ? "Green" : "TextSecondary");
                });
            }

            if (MonitorKeys.Shown(MonitorKeys.PwrTimeRemaining))
            {
                var row = ValueRow(Glyphs.Clock, "Green", "Battery time remaining", out var value, out var caption);
                caption.Visibility = Visibility.Visible;
                entries.Add((row, s => s.Power is { HasBattery: true, PluggedIn: false, Charging: false }));
                updates.Add(s =>
                {
                    var text = s.Power?.TimeRemainingSeconds is { } secs ? MetricFormat.BatteryTime(secs) : null;
                    value.Text = text ?? "...";
                    caption.Text = text is null ? "Calculating…" : "System estimate";
                });
            }

            if (MonitorKeys.Shown(MonitorKeys.PwrHealth))
            {
                var row = ValueRow(MonitorGlyphs.Heart, "Pink", "Battery health", out var value, out var caption);
                entries.Add((row, s => s.Power?.HealthPercent is not null));
                updates.Add(s =>
                {
                    if (s.Power is not { HealthPercent: { } h } p) return;
                    value.Text = $"{Math.Round(h)}%";
                    caption.Text = p.CycleCount is { } cycles ? $"{cycles} cycles" : p.FullChargeCapacityMWh is { } full && p.DesignCapacityMWh is { } design
                        ? $"{full / 1000.0:0.#} of {design / 1000.0:0.#} Wh" : string.Empty;
                    caption.Visibility = caption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }

            if (MonitorKeys.Shown(MonitorKeys.PwrTemperature))
            {
                var row = ValueRow(MonitorGlyphs.Thermometer, "TextSecondary", "Battery temperature", out var value, out _);
                entries.Add((row, s => s.BatteryTemperature is not null));
                updates.Add(s =>
                {
                    if (s.BatteryTemperature is { } t) value.Text = MetricFormat.Temperature(t, MonitorKeys.Fahrenheit);
                });
            }

            string? layout = null;
            updates.Insert(0, s =>
            {
                var signature = string.Concat(entries.Select(e => e.Visible(s) ? '1' : '0')) + (s.Power is null ? "n" : "p");
                if (signature == layout) return;
                layout = signature;
                var any = false;
                rows.Children.Clear();
                foreach (var (row, visible) in entries)
                {
                    if (!visible(s)) continue;
                    if (rows.Children.Count > 0) rows.Children.Add(Ui.Divider(8));
                    if (row.Parent is Panel old) old.Children.Remove(row);
                    rows.Children.Add(row);
                    any = true;
                }
                unavailable.Visibility = any || s.Power is null ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        if (Devices && MonitorKeys.Shown(MonitorKeys.PwrDevices))
        {
            var list = Ui.Stack(5);
            var block = Ui.Stack(6, Subsection("Connected Devices"), list);
            AddBlock(card, block);
            IReadOnlyList<DeviceReading>? shown = null;
            updates.Add(s =>
            {
                if (ReferenceEquals(shown, s.Devices) && s.DevicesRead) return;
                shown = s.Devices;
                DeviceRows.Fill(list, s.Devices, s.DevicesRead);
            });
        }

        if (card.Children.Count == 0) card.Children.Add(Ui.Caption("Everything in this section is hidden. Use the sliders button to show it again."));
        return (Card(card), s =>
        {
            foreach (var u in updates) u(s);
        });
    }
}

/// <summary>The USB/Bluetooth device rows shared by the System and Power sections.</summary>
internal static class DeviceRows
{
    public static void Fill(StackPanel list, IReadOnlyList<DeviceReading> devices, bool read)
    {
        list.Children.Clear();
        if (!read)
        {
            list.Children.Add(Note("Measuring…"));
            return;
        }
        if (devices.Count == 0)
        {
            list.Children.Add(Note("No external devices connected"));
            return;
        }
        foreach (var d in devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var icon = Ui.Icon(Glyph(d), 12, "TextSecondary");
            icon.Width = 16;
            var name = Ui.Text(d.Name, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            name.Margin = new Thickness(6, 0, 8, 0);
            var value = d.BatteryPercent is { } b
                ? Ui.Text($"{b}%", 12.5, FontWeights.SemiBold, b < 20 ? "Red" : "TextPrimary", wrap: false)
                : null;
            var row = Columns([Auto, Star, Auto], icon, name, value);
            row.ToolTip = d.Kind == DeviceKind.Bluetooth ? $"{d.Name} (Bluetooth)" : $"{d.Name} (USB)";
            list.Children.Add(row);
        }
    }

    private static string Glyph(DeviceReading d)
    {
        if (d.Kind == DeviceKind.Bluetooth)
        {
            var n = d.Name.ToLowerInvariant();
            if (n.Contains("mouse")) return Glyphs.Mouse;
            if (n.Contains("keyboard")) return Glyphs.Keyboard;
            if (n.Contains("bud") || n.Contains("head") || n.Contains("pods") || n.Contains("ear")) return Glyphs.Headphones;
            return Glyphs.Bluetooth;
        }
        return d.Category switch
        {
            "Mouse" => Glyphs.Mouse,
            "Keyboard" => Glyphs.Keyboard,
            "Camera" or "Image" => Glyphs.Webcam,
            "MEDIA" or "AudioEndpoint" => Glyphs.Headphones,
            "DiskDrive" or "WPD" => Glyphs.HardDrive,
            _ => Glyphs.Usb,
        };
    }
}
