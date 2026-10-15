// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;
using static Fieldkit.Features.Monitor.Views.MonitorWidgets;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>
/// The System section (upstream SystemSection): temperatures, hardware usage with history graphs
/// and per-app breakdowns (CPU expands to the per-core matrix), battery, apps using significant
/// energy, memory pressure, uptime and connected devices.
/// </summary>
internal static class SystemView
{
    private static bool Cpu => FeatureCatalog.IsAvailable("monitorCPU");
    private static bool Gpu => FeatureCatalog.IsAvailable("monitorGPU");
    private static bool Memory => FeatureCatalog.IsAvailable("monitorMemory");
    private static bool Power => FeatureCatalog.IsAvailable("monitorPower");
    private static bool Devices => FeatureCatalog.IsAvailable("connectedDevices");

    public static IReadOnlyList<PanelOption> Options()
    {
        var list = new List<PanelOption>();
        if (Cpu || Gpu) list.Add(new("Temperatures", MonitorKeys.SysTemps));
        if (Cpu)
        {
            list.Add(new("CPU", MonitorKeys.SysCpu));
            list.Add(new("Per core", MonitorKeys.SysCpuCores, Indented: true));
            list.Add(new("Graph", MonitorKeys.GraphCpu, Indented: true));
        }
        if (Gpu)
        {
            list.Add(new("GPU", MonitorKeys.SysGpu));
            list.Add(new("Graph", MonitorKeys.GraphGpu, Indented: true));
        }
        if (Power && BatteryReader.HasBattery)
        {
            list.Add(new("Battery", MonitorKeys.SysBattery));
            list.Add(new("Graph", MonitorKeys.GraphBattery, Indented: true));
        }
        if (Power || Cpu) list.Add(new("Apps using significant energy", MonitorKeys.SysEnergyApps));
        if (Memory)
        {
            list.Add(new("Memory", MonitorKeys.SysMemory));
            list.Add(new("Graph", MonitorKeys.GraphMemory, Indented: true));
        }
        list.Add(new("Uptime", MonitorKeys.SysUptime));
        if (Devices) list.Add(new("Connected Devices", MonitorKeys.SysConnectedDevices));
        list.Add(new("Separate metrics into their own items", MonitorKeys.TraySeparate, false,
            "Separates active readouts in the notification area and keeps usage and temperature together when combine is on."));
        return list;
    }

    private static MonitorNeeds Needs()
    {
        var n = MonitorNeeds.None;
        if (Cpu && Shown(MonitorKeys.SysCpu)) n |= MonitorNeeds.Cpu | (Shown(MonitorKeys.SysCpuCores) ? MonitorNeeds.CpuCores : 0);
        if (Gpu && Shown(MonitorKeys.SysGpu)) n |= MonitorNeeds.Gpu;
        if ((Cpu || Gpu) && Shown(MonitorKeys.SysTemps)) n |= MonitorNeeds.Temperatures;
        if (Power) n |= MonitorNeeds.Power;
        if (Memory && Shown(MonitorKeys.SysMemory)) n |= MonitorNeeds.Memory;
        if (Devices && Shown(MonitorKeys.SysConnectedDevices)) n |= MonitorNeeds.Devices;
        if (Shown(MonitorKeys.SysEnergyApps)) n |= MonitorNeeds.Gpu;
        return n;
    }

    private static bool Shown(string key) => MonitorKeys.Shown(key);

    public static FrameworkElement Build() =>
        MonitorPanel.Live("system", Needs, Render, Options(), MonitorKeys.TemperatureUnit, MonitorKeys.MemoryMetric,
            MonitorKeys.GraphScale, MonitorKeys.HardwareSensors);

    private static (FrameworkElement, Action<MonitorSnapshot>) Render()
    {
        var card = new StackPanel();
        var updates = new List<Action<MonitorSnapshot>>();
        BreakdownKind? expanded = null;
        var cpuAppsOpen = true;
        var rerenderHooks = new List<Action>();
        var chevrons = new List<Action>();

        // ---- Temperatures
        if ((Cpu || Gpu) && Shown(MonitorKeys.SysTemps))
        {
            TemperatureTile? cpuTile = null, gpuTile = null, batteryTile = null;
            var row = new Grid();
            void AddTile(TemperatureTile tile)
            {
                if (row.Children.Count > 0) tile.Margin = new Thickness(8, 0, 0, 0);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
                Grid.SetColumn(tile, row.Children.Count);
                row.Children.Add(tile);
            }
            if (Cpu) AddTile(cpuTile = new TemperatureTile(Glyphs.Cpu, "CPU"));
            if (Gpu) AddTile(gpuTile = new TemperatureTile(Glyphs.Gpu, "GPU"));
            // The battery tile appears only where the battery reports a temperature.
            if (Power && BatteryReader.HasBattery) AddTile(batteryTile = new TemperatureTile(Glyphs.Battery, "Battery"));
            var note = Note(string.Empty);
            var block = Ui.Stack(6, Subsection("Temperatures"), row, note);
            AddBlock(card, block);
            updates.Add(s =>
            {
                cpuTile?.Set(s.CpuTemperature);
                gpuTile?.Set(s.GpuTemperature);
                if (batteryTile is not null)
                {
                    batteryTile.Set(s.BatteryTemperature);
                    var shown = s.BatteryTemperature is not null;
                    batteryTile.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                    row.ColumnDefinitions[Grid.GetColumn(batteryTile)].Width = shown ? Star : new GridLength(0);
                }
                var missing = Cpu && s.CpuTemperature is null;
                note.Text = missing ? HardwareSensors.StatusText(s.Sensors == SensorStatus.NotRequested
                    ? HardwareSensors.Prerequisite() : s.Sensors) : string.Empty;
                note.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        // ---- Hardware usage (CPU, GPU, battery, energy apps)
        var usage = Ui.Stack(6);
        var taskManager = Ui.IconButton(Glyphs.Grid, "Open Task Manager", OpenTaskManager, 12);
        taskManager.Padding = new Thickness(3);
        usage.Children.Add(Ui.Split(Subsection("Hardware usage"), taskManager));

        if (Cpu && Shown(MonitorKeys.SysCpu))
        {
            var (row, bar, value) = UsageRow("CPU", expanded == BreakdownKind.Cpu, () => Toggle(BreakdownKind.Cpu));
            Ui.Append(usage, 6, row);
            var line = Line("Accent");
            if (Settings.GetBool(MonitorKeys.GraphCpu, true)) Ui.Append(usage, 2, Graph(line, () => "100%"));
            var detail = Ui.Stack(4);
            Ui.Append(usage, 0, detail);
            CoreMatrix? matrix = null;
            BreakdownList? apps = null;
            void BuildDetail()
            {
                detail.Children.Clear();
                matrix = null;
                apps = null;
                if (expanded != BreakdownKind.Cpu) return;
                if (Shown(MonitorKeys.SysCpuCores))
                {
                    detail.Children.Add(matrix = new CoreMatrix());
                    var header = Clickable(Ui.Row(6, Chevron(cpuAppsOpen), Subsection("Apps using CPU")), () =>
                    {
                        cpuAppsOpen = !cpuAppsOpen;
                        BuildDetail();
                    });
                    Ui.Append(detail, 4, header);
                }
                if (!Shown(MonitorKeys.SysCpuCores) || cpuAppsOpen)
                    Ui.Append(detail, 2, apps = new BreakdownList(BreakdownKind.Cpu, 20));
            }
            BuildDetail();
            updates.Add(s =>
            {
                bar.Set(s.CpuUsage);
                value.Text = s.CpuUsage is { } c ? MetricFormat.Percent(c) : "-";
                line.SetValues(s.CpuHistory);
                matrix?.Set(s.CoreUsage, s.CoreGroups);
                apps?.Tick();
            });
            rerenderHooks.Add(BuildDetail);
        }

        if (Gpu && Shown(MonitorKeys.SysGpu))
        {
            var (row, bar, value) = UsageRow("GPU", expanded == BreakdownKind.Gpu, () => Toggle(BreakdownKind.Gpu));
            Ui.Append(usage, 6, row);
            var line = Line("Cyan");
            if (Settings.GetBool(MonitorKeys.GraphGpu, true)) Ui.Append(usage, 2, Graph(line, () => "100%"));
            var detail = Ui.Stack(4);
            Ui.Append(usage, 0, detail);
            BreakdownList? apps = null;
            void BuildDetail()
            {
                detail.Children.Clear();
                apps = null;
                if (expanded == BreakdownKind.Gpu) detail.Children.Add(apps = new BreakdownList(BreakdownKind.Gpu, 20));
            }
            BuildDetail();
            updates.Add(s =>
            {
                bar.Set(s.GpuUsage);
                value.Text = s.GpuUsage is { } g ? MetricFormat.Percent(g) : "-";
                line.SetValues(s.GpuHistory);
                apps?.Tick();
            });
            rerenderHooks.Add(BuildDetail);
        }

        if (Power && BatteryReader.HasBattery && Shown(MonitorKeys.SysBattery))
        {
            var bar = new UsageBar("Green");
            var value = Value("-", 13);
            value.Width = ValueWidth;
            var icon = Ui.Icon(Glyphs.Battery, 12, "TextSecondary");
            icon.Width = 12;
            var label = Ui.Text("Battery", 13, brush: "TextSecondary", wrap: false);
            label.Margin = new Thickness(6, 0, 0, 0);
            label.Width = LabelWidth;
            bar.Margin = new Thickness(8, 0, 8, 0);
            Ui.Append(usage, 6, Columns([Auto, Auto, Star, Auto], icon, label, bar, value));
            var line = Line("Green");
            if (Settings.GetBool(MonitorKeys.GraphBattery, true)) Ui.Append(usage, 2, Graph(line, () => "100%"));
            updates.Add(s =>
            {
                var charge = s.Power?.ChargePercent;
                bar.Set(charge / 100.0);
                bar.SetTint(charge < 20 ? "Red" : charge < 40 ? "Yellow" : "Green");
                value.Text = charge is { } c ? $"{c}%" : "-";
                icon.Text = s.Power is { PluggedIn: true } ? Glyphs.Plug : Glyphs.Battery;
                line.SetValues(s.BatteryHistory);
            });
        }

        if (Shown(MonitorKeys.SysEnergyApps))
        {
            var detail = Ui.Stack(4);
            BreakdownList? apps = null;
            var energyChevron = Chevron(false);
            chevrons.Add(() => energyChevron.Text = expanded == BreakdownKind.Energy ? Glyphs.ChevronDown : Glyphs.ChevronRight);
            var header = Clickable(Ui.Row(6, energyChevron, Ui.Text("Apps using significant energy", 13, brush: "TextSecondary", wrap: false)),
                () => Toggle(BreakdownKind.Energy));
            Ui.Append(usage, 8, header);
            Ui.Append(usage, 0, detail);
            void BuildDetail()
            {
                detail.Children.Clear();
                apps = null;
                if (expanded == BreakdownKind.Energy)
                    detail.Children.Add(apps = new BreakdownList(BreakdownKind.Energy, 18, "No significant energy use"));
            }
            BuildDetail();
            updates.Add(_ => apps?.Tick());
            rerenderHooks.Add(BuildDetail);
        }
        if (usage.Children.Count > 1) AddBlock(card, usage);

        // ---- Memory
        if (Memory && Shown(MonitorKeys.SysMemory))
        {
            var committedMetric = Settings.GetString(MonitorKeys.MemoryMetric, "used") == "committed";
            var badge = new PressureBadge { Margin = new Thickness(8, 0, 0, 0) };
            var amount = Ui.Text(string.Empty, 13, FontWeights.Medium, "TextSecondary", wrap: false);
            var memoryChevron = Chevron(false);
            chevrons.Add(() => memoryChevron.Text = expanded == BreakdownKind.Memory ? Glyphs.ChevronDown : Glyphs.ChevronRight);
            var pressureLabel = Ui.Text("Pressure", 13, brush: "TextSecondary", wrap: false);
            pressureLabel.Margin = new Thickness(6, 0, 0, 0);
            var head = Clickable(Columns([Auto, Auto, Auto, Star, Auto],
                memoryChevron, pressureLabel,
                badge, null, amount), () => Toggle(BreakdownKind.Memory));
            var committed = SecondaryRow("Committed", out var committedValue);
            var cached = SecondaryRow("Cached files", out var cachedValue);
            var swap = SecondaryRow("Page file used", out var swapValue);
            var line = Line("Mint");
            var block = Ui.Stack(6, Subsection("Memory"), head, committed, cached, swap);
            if (Settings.GetBool(MonitorKeys.GraphMemory, true)) Ui.Append(block, 2, Graph(line, () => "100%"));
            var detail = Ui.Stack(4);
            Ui.Append(block, 0, detail);
            BreakdownList? apps = null;
            void BuildDetail()
            {
                detail.Children.Clear();
                apps = null;
                if (expanded == BreakdownKind.Memory) detail.Children.Add(apps = new BreakdownList(BreakdownKind.Memory, 20));
            }
            BuildDetail();
            AddBlock(card, block);
            updates.Add(s =>
            {
                badge.Set(s.MemoryPressure);
                var used = committedMetric ? s.MemoryCommitted : s.MemoryUsed;
                var total = committedMetric ? s.MemoryCommitLimit : s.MemoryTotal;
                amount.Text = used is { } u && total is { } t ? $"{MetricFormat.Memory(u)} / {MetricFormat.Memory(t)}" : string.Empty;
                SetSecondary(committed, committedValue, committedMetric ? null : s.MemoryCommitted);
                SetSecondary(cached, cachedValue, s.MemoryCached);
                SetSecondary(swap, swapValue, s.MemorySwapUsed);
                line.SetValues(s.MemoryHistory);
                apps?.Tick();
            });
            rerenderHooks.Add(BuildDetail);
        }

        // ---- Uptime
        if (Shown(MonitorKeys.SysUptime))
        {
            var text = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            AddBlock(card, Ui.Row(6, Ui.Icon(Glyphs.Clock, 11, "TextSecondary"), text));
            updates.Add(_ => text.Text = $"Up for {MetricFormat.Uptime((long)(MonitorNative.GetTickCount64() / 1000))}");
        }

        // ---- Connected devices
        if (Devices && Shown(MonitorKeys.SysConnectedDevices))
        {
            var open = false;
            var count = Ui.Text("-", 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var chevron = Chevron(false);
            var list = Ui.Stack(4);
            list.Visibility = Visibility.Collapsed;
            var head = Clickable(Columns([Auto, Star, Auto, Auto],
                Ui.Row(6, Ui.Icon(Glyphs.Usb, 12, "TextSecondary"), Ui.Text("Connected Devices", 12.5, FontWeights.Medium, wrap: false)),
                null, count, chevron), () =>
            {
                open = !open;
                list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? Glyphs.ChevronDown : Glyphs.ChevronRight;
            });
            count.Margin = new Thickness(0, 0, 6, 0);
            AddBlock(card, Ui.Stack(6, head, list));
            IReadOnlyList<DeviceReading>? shown = null;
            updates.Add(s =>
            {
                count.Text = s.DevicesRead ? s.Devices.Count.ToString() : "-";
                if (ReferenceEquals(shown, s.Devices)) return;
                shown = s.Devices;
                DeviceRows.Fill(list, s.Devices, s.DevicesRead);
            });
        }

        if (card.Children.Count == 0) card.Children.Add(Ui.Caption("Everything in this section is hidden. Use the sliders button to show it again."));

        void Toggle(BreakdownKind kind)
        {
            expanded = expanded == kind ? null : kind;
            foreach (var hook in rerenderHooks) hook();
            foreach (var chevronUpdate in chevrons) chevronUpdate();
            if (expanded is null) ProcessUsage.Reset();
        }

        var view = Card(card);
        view.Unloaded += (_, _) => ProcessUsage.Reset();
        return (view, s =>
        {
            foreach (var u in updates) u(s);
        });

        (FrameworkElement Row, UsageBar Bar, TextBlock Value) UsageRow(string label, bool isOpen, Action onClick)
        {
            var chevron = Chevron(isOpen);
            var text = Ui.Text(label, 13, brush: "TextSecondary", wrap: false);
            text.Width = LabelWidth;
            text.Margin = new Thickness(6, 0, 0, 0);
            var bar = new UsageBar { Margin = new Thickness(8, 0, 8, 0) };
            var value = Value("-", 13);
            value.Width = ValueWidth;
            var row = Clickable(Columns([Auto, Auto, Star, Auto], chevron, text, bar, value), onClick);
            var kind = label == "CPU" ? BreakdownKind.Cpu : BreakdownKind.Gpu;
            chevrons.Add(() => chevron.Text = expanded == kind ? Glyphs.ChevronDown : Glyphs.ChevronRight);
            return (row, bar, value);
        }
    }

    private static FrameworkElement SecondaryRow(string title, out TextBlock value)
    {
        value = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
        var row = Ui.Split(Ui.Text(title, 12.5, brush: "TextSecondary", wrap: false), value);
        row.Margin = new Thickness(18, 0, 0, 0);
        row.Visibility = Visibility.Collapsed;
        return row;
    }

    private static void SetSecondary(FrameworkElement row, TextBlock value, ulong? bytes)
    {
        row.Visibility = bytes is null ? Visibility.Collapsed : Visibility.Visible;
        if (bytes is { } b) value.Text = MetricFormat.Memory(b);
    }

    private static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Opening Task Manager failed: {ex.Message}"); }
    }
}
