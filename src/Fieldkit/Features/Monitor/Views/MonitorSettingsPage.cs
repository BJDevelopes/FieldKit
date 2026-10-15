// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.Features.Monitor.Services;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>
/// Settings › Monitor (upstream MonitorSettings): what the notification area shows and how, what
/// the panel shows, shared readings (interval, units, hardware sensors) and alerts.
/// </summary>
internal static class MonitorSettingsPage
{
    public static FrameworkElement Build()
    {
        var available = TrayReadouts.All.Where(d => FeatureCatalog.IsAvailable(d.FeatureId)).ToList();
        return Ui.Page("Monitor", "What the notification area and the panel show about your PC, and when to warn you.",
            Heading("In the notification area", Glyphs.Tray),
            TrayCard(available),
            TrayStyleCard(),
            Heading("In the panel", Glyphs.Layout),
            PanelCard(),
            FeatureCatalog.IsAvailable("fanControl") ? FanCard() : null,
            Heading("Readings and alerts", MonitorGlyphs.Sliders),
            ReadingsCard(),
            SensorsCard(),
            AlertsCard());
    }

    private static FrameworkElement Heading(string title, string glyph)
    {
        var row = Ui.Row(8, Ui.Icon(glyph, 16, "TextPrimary"), Ui.Text(title, 17, FontWeights.SemiBold));
        var stack = Ui.Stack(12, Ui.Divider(0), row);
        stack.Margin = new Thickness(0, 6, 0, -6);
        return stack;
    }

    // ------------------------------------------------------- notification area

    private static FrameworkElement TrayCard(IReadOnlyList<ReadoutDef> readouts)
    {
        var rows = new List<UIElement>
        {
            Ui.Caption("Choose what appears next to the icon in the notification area. Each reading is drawn into a small icon; Windows may keep new icons in the overflow until you drag them onto the taskbar."),
            Preview(),
        };
        if (readouts.Count == 0)
        {
            rows.Add(Ui.Caption("Install a System monitor feature in Settings › Features to add readings."));
            return Ui.Group(null, [.. rows]);
        }
        rows.Add(MonitorWidgets.TileGrid(3, readouts.Select(d => MonitorWidgets.Tile(d.Glyph, d.Title, d.Key)).ToList()));
        var options = Ui.Stack(8);
        if (readouts.Any(d => d.Metric == ReadoutMetric.Memory))
            Ui.Append(options, 8, Ui.Check("Memory: pressure colour (green, yellow, red)", MonitorKeys.TrayMemoryDot, false));
        if (readouts.Any(d => d.Metric == ReadoutMetric.Network))
            Ui.Append(options, 8, Ui.Check("Network: upload above download", MonitorKeys.TrayUploadFirst, false));
        if (readouts.Any(d => d.Metric == ReadoutMetric.DiskUsage))
            Ui.Append(options, 8, Ui.ChoiceRow("Disk usage shows", MonitorKeys.TrayDiskStyle,
                [("percent", "Used percentage"), ("free", "Available space"), ("used", "Used space")], "percent"));
        if (options.Children.Count > 0) rows.Add(options);
        return Ui.Group(null, [.. rows]);
    }

    private static FrameworkElement TrayStyleCard() => Ui.Group(null,
        Ui.ToggleRow("Combine usage and temperature",
            "When usage and temperature for the same item are enabled, show them in one icon.",
            MonitorKeys.TrayCombineTemperatures, true),
        Ui.ToggleRow("Separate metrics into their own items",
            "Gives each reading its own icon with a label. Off packs two readings per icon so fewer icons are used; usage and temperature stay together when combine is on.",
            MonitorKeys.TraySeparate, false));

    /// <summary>The icons as they would look now, drawn with the latest reading.</summary>
    private static FrameworkElement Preview()
    {
        var host = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        void Render()
        {
            host.Children.Clear();
            var active = TrayReadouts.All.Where(d => FeatureCatalog.IsAvailable(d.FeatureId) && Settings.GetBool(d.Key, false)).ToList();
            var layout = TrayLayout.Layout(active.Select(d => TrayReadouts.Value(d, MonitorSampler.Latest)).ToList(),
                Settings.GetBool(MonitorKeys.TraySeparate, false), Settings.GetBool(MonitorKeys.TrayCombineTemperatures, true));
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var icon in layout)
            {
                var image = Render16(icon);
                if (image is not null) strip.Children.Add(image);
            }
            if (strip.Children.Count == 0)
            {
                host.Children.Add(Ui.Text("Nothing is shown next to the icon.", 12.5, brush: "TextTertiary"));
                return;
            }
            var bar = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(TrayIcon.TaskbarIsLight() ? Color.FromRgb(238, 238, 238) : Color.FromRgb(32, 32, 32)),
                Child = strip,
            };
            host.Children.Add(bar);
        }
        Render();
        string[] keys = [.. TrayReadouts.All.Select(d => d.Key), MonitorKeys.TraySeparate, MonitorKeys.TrayCombineTemperatures,
            MonitorKeys.TrayMemoryDot, MonitorKeys.TrayUploadFirst, MonitorKeys.TrayDiskStyle, MonitorKeys.TemperatureUnit];
        // The preview stays live while the page is open, like upstream's menu bar preview.
        Ui.Track(host, () =>
        {
            IDisposable? lease = null;
            void Lease()
            {
                var needs = TrayReadouts.All.Where(d => FeatureCatalog.IsAvailable(d.FeatureId) && Settings.GetBool(d.Key, false))
                    .Aggregate(MonitorNeeds.None, (n, d) => n | d.Needs);
                var previous = lease;
                lease = needs == MonitorNeeds.None ? null : MonitorSampler.Acquire(needs, foreground: false);
                previous?.Dispose();
            }
            void OnSample(MonitorSnapshot _) => Render();
            Lease();
            var watch = Settings.Observe(() =>
            {
                Lease();
                Render();
            }, keys);
            MonitorSampler.Updated += OnSample;
            return new ActionDisposable(() =>
            {
                MonitorSampler.Updated -= OnSample;
                watch.Dispose();
                lease?.Dispose();
            });
        });
        return host;
    }

    private static Image? Render16(TrayIconContent content)
    {
        try
        {
            using var icon = TrayIcon.RenderTextIcon(content.Line1, content.Line2, content.Tint switch
            {
                ReadoutTint.Green => System.Drawing.Color.FromArgb(52, 199, 89),
                ReadoutTint.Yellow => System.Drawing.Color.FromArgb(255, 204, 0),
                ReadoutTint.Red => System.Drawing.Color.FromArgb(255, 69, 58),
                _ => null,
            });
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Native.DestroyIcon(icon.Handle);
            // Shown pixel-doubled so the tiny icon can be read here.
            var image = new Image
            {
                Source = source,
                Width = source.PixelWidth * 2,
                Height = source.PixelHeight * 2,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = content.Tooltip,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ panel

    private static FrameworkElement PanelCard()
    {
        var rows = new List<UIElement>();
        List<FrameworkElement>? items = null;
        void Flush()
        {
            if (items is { Count: > 0 }) rows.Add(MonitorWidgets.TileGrid(3, items));
            items = null;
        }
        void Section(string id, string title, string glyph, params string[] features)
        {
            Flush();
            if (!features.Any(FeatureCatalog.IsAvailable)) return;
            var head = Ui.Row(10, Ui.Icon(glyph, 15), Ui.Text(title, 13.5, FontWeights.SemiBold, wrap: false));
            rows.Add(Ui.Split(head, Ui.Toggle($"panel.show.{id}", true)));
            items = [];
        }
        void Item(string title, string key, bool feature = true)
        {
            if (!feature || items is null) return;
            items.Add(MonitorWidgets.Tile(GlyphFor(title), title, key, true));
        }
        bool A(string f) => FeatureCatalog.IsAvailable(f);

        Section("system", "System", Glyphs.Cpu, "monitorCPU", "monitorGPU", "monitorMemory", "monitorPower", "connectedDevices");
        Item("Temperatures", MonitorKeys.SysTemps, A("monitorCPU") || A("monitorGPU"));
        Item("CPU", MonitorKeys.SysCpu, A("monitorCPU"));
        Item("Per core", MonitorKeys.SysCpuCores, A("monitorCPU"));
        Item("CPU graph", MonitorKeys.GraphCpu, A("monitorCPU"));
        Item("GPU", MonitorKeys.SysGpu, A("monitorGPU"));
        Item("GPU graph", MonitorKeys.GraphGpu, A("monitorGPU"));
        Item("Battery", MonitorKeys.SysBattery, A("monitorPower"));
        Item("Battery graph", MonitorKeys.GraphBattery, A("monitorPower"));
        Item("Apps using significant energy", MonitorKeys.SysEnergyApps);
        Item("Memory", MonitorKeys.SysMemory, A("monitorMemory"));
        Item("Memory graph", MonitorKeys.GraphMemory, A("monitorMemory"));
        Item("Uptime", MonitorKeys.SysUptime);
        Item("Connected Devices", MonitorKeys.SysConnectedDevices, A("connectedDevices"));

        Section("network", "Network", Glyphs.Globe, "monitorNetwork");
        if (A("monitorNetwork"))
        {
            Item("Live speed", MonitorKeys.NetSpeed);
            Item("Network graph", MonitorKeys.GraphNetwork);
            Item("Totals", MonitorKeys.NetTotals);
            Item("IP addresses", MonitorKeys.NetAddresses);
            Item("Speed test", MonitorKeys.NetTest);
        }

        Section("disk", "Disk", Glyphs.HardDrive, "monitorDisk");
        if (A("monitorDisk"))
        {
            Item("Disk usage", MonitorKeys.DiskUsage);
            Item("Live activity", MonitorKeys.DiskActivity);
            Item("Disk graph", MonitorKeys.GraphDisk);
            Item("Tools", MonitorKeys.DiskTools);
        }

        Section("power", "Power", Glyphs.Bolt, "monitorPower", "connectedDevices");
        if (A("monitorPower"))
        {
            Item("System", MonitorKeys.PwrSystem);
            Item("Power graph", MonitorKeys.GraphPower);
            Item("Battery", MonitorKeys.PwrBattery);
            Item("Battery time remaining", MonitorKeys.PwrTimeRemaining);
            Item("Battery health", MonitorKeys.PwrHealth);
            Item("Battery temperature", MonitorKeys.PwrTemperature);
        }
        Item("Connected Devices", MonitorKeys.PwrDevices, A("connectedDevices"));
        Flush();

        rows.Add(Ui.Caption("The sliders button at the top of each panel section shows the same choices in place."));
        rows.Add(Ui.ToggleRow("Show graph scale", null, MonitorKeys.GraphScale, true));
        return Ui.Group(null, [.. rows]);
    }

    private static string GlyphFor(string title) => title switch
    {
        _ when title.EndsWith("graph", StringComparison.Ordinal) => MonitorGlyphs.Chart,
        "Temperatures" or "Battery temperature" => MonitorGlyphs.Thermometer,
        "CPU" or "Per core" => Glyphs.Cpu,
        "GPU" => Glyphs.Gpu,
        "Battery" or "Battery health" => Glyphs.Battery,
        "Battery time remaining" or "Uptime" => Glyphs.Clock,
        "Apps using significant energy" => Glyphs.Bolt,
        "Memory" => Glyphs.Memory,
        "Connected Devices" => Glyphs.Usb,
        "Live speed" => MonitorGlyphs.Gauge,
        "Totals" => Glyphs.ArrowsUpDown,
        "IP addresses" => Glyphs.Globe,
        "Speed test" => MonitorGlyphs.Gauge,
        "Disk usage" => Glyphs.HardDrive,
        "Live activity" => Glyphs.ArrowsUpDown,
        "Tools" => Glyphs.Tools,
        "System" => Glyphs.Bolt,
        _ => Glyphs.Check,
    };

    private static FrameworkElement FanCard()
    {
        var title = Ui.Row(8, Ui.Text("Fan Control", 13.5, FontWeights.Medium, wrap: false), Ui.Badge("Beta"));
        var left = Ui.Stack(2, title, Ui.Caption("Adds manual fan speeds and temperature curves to the tray panel."));
        return Ui.Group(null,
            Ui.Split(left, Ui.Toggle("panel.show.fanControl", true)),
            Ui.Caption("Beta. You may run into some bugs."));
    }

    // ---------------------------------------------------- readings and alerts

    private static FrameworkElement ReadingsCard()
    {
        var rows = new List<UIElement>
        {
            Split("Update every", FanControlView.Segmented(MonitorKeys.Interval, "2", [("1", "1 second"), ("2", "2 seconds"), ("5", "5 seconds")], intValue: true)),
            Split("Temperatures", FanControlView.Segmented(MonitorKeys.TemperatureUnit, "celsius", [("celsius", "°C"), ("fahrenheit", "°F")])),
        };
        if (FeatureCatalog.IsAvailable("monitorNetwork"))
            rows.Add(Split("Network speed unit", FanControlView.Segmented(MonitorKeys.NetworkSpeedUnit, "bytes", [("bytes", "MB/s"), ("bits", "Mbps")])));
        if (FeatureCatalog.IsAvailable("monitorMemory"))
            rows.Add(Split("Measure memory as", FanControlView.Segmented(MonitorKeys.MemoryMetric, "used", [("used", "Memory used"), ("committed", "Committed")]),
                "Committed counts what apps reserved, including the page file."));
        return Ui.Group(null, [.. rows]);
    }

    private static FrameworkElement Split(string title, FrameworkElement control, string? caption = null) =>
        Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)), control);

    /// <summary>The opt-in for kernel-level sensors, with what it needs and why.</summary>
    public static FrameworkElement SensorsCard()
    {
        var status = new ContentControl();
        void Render()
        {
            var pre = HardwareSensors.Prerequisite();
            var text = pre switch
            {
                SensorStatus.DriverOff => "Off. GPU temperature, battery and usage readings still work without it.",
                SensorStatus.NeedsAdmin => "Needs administrator rights: restart Fieldkit as administrator.",
                SensorStatus.NeedsPawnIo => "PawnIO is not installed. Install it from pawnio.eu, then reopen the panel.",
                _ => "Ready.",
            };
            var stack = Ui.Stack(8, Ui.Text(text, 12.5, brush: pre == SensorStatus.Ready ? "Green" : pre == SensorStatus.DriverOff ? "TextSecondary" : "Orange"));
            var buttons = Ui.Row(8);
            if (pre == SensorStatus.NeedsAdmin) buttons.Children.Add(Ui.Button("Restart as administrator", Shell.FeatureHubPage.RestartElevated));
            if (pre == SensorStatus.NeedsPawnIo) buttons.Children.Add(Ui.Button("Get PawnIO", () => Shell.FeatureHubPage.OpenUri("https://pawnio.eu")));
            if (buttons.Children.Count > 0) Ui.Append(stack, 8, buttons);
            status.Content = stack;
        }
        Render();
        Ui.Track(status, () => Settings.Observe(Render, MonitorKeys.HardwareSensors));
        return Ui.Group("Hardware sensors",
            Ui.ToggleRow("Read CPU, motherboard and fan sensors",
                "CPU temperatures, motherboard temperatures and fan speeds sit behind the processor and the board's controller, which Windows only lets a kernel driver read. Turning this on lets LibreHardwareMonitor use the signed PawnIO driver (installed separately) while Fieldkit runs as administrator. Fan Control needs it too.",
                MonitorKeys.HardwareSensors, false, _ => MonitorSampler.Poke()),
            status);
    }

    private static FrameworkElement AlertsCard()
    {
        var rows = new List<UIElement>();
        void Alert(string feature, string title, string key, string? thresholdKey = null, string? thresholdTitle = null,
            int min = 0, int max = 0, int fallback = 0, Func<double, string>? format = null, string? note = null)
        {
            if (!FeatureCatalog.IsAvailable(feature)) return;
            rows.Add(Ui.ToggleRow(title, note, key, false));
            if (thresholdKey is null) return;
            var slider = Ui.SliderRow(thresholdTitle!, thresholdKey, min, max, fallback, format, 5);
            slider.Margin = new Thickness(26, -4, 0, 0);
            void Sync() => slider.Visibility = Settings.GetBool(key, false) ? Visibility.Visible : Visibility.Collapsed;
            Sync();
            Ui.Track(slider, () => Settings.Observe(Sync, key));
            rows.Add(slider);
        }
        string Temp(double c) => MetricFormat.Temperature(c, MonitorKeys.Fahrenheit);

        Alert("monitorCPU", "High CPU", MonitorKeys.AlertCpu, MonitorKeys.AlertCpuThreshold, "CPU above", 50, 100, 90, v => $"{v:0}%");
        Alert("monitorCPU", "High CPU temperature", MonitorKeys.AlertCpuTemperature, MonitorKeys.AlertCpuTemperatureThreshold,
            "Temperature above", 70, 105, 90, Temp, "Needs hardware sensors.");
        Alert("monitorMemory", "Critical memory pressure", MonitorKeys.AlertMemory);
        Alert("monitorDisk", "Low disk space", MonitorKeys.AlertDisk, MonitorKeys.AlertDiskFreePercent, "Free space below", 5, 30, 10, v => $"{v:0}%");
        if (BatteryReader.HasBattery)
        {
            Alert("monitorPower", "High battery temperature", MonitorKeys.AlertBatteryTemperature, MonitorKeys.AlertBatteryTemperatureThreshold,
                "Temperature above", 30, 50, 40, Temp, "Only where the battery reports its temperature.");
            Alert("monitorPower", "Low battery", MonitorKeys.AlertBattery, MonitorKeys.AlertBatteryPercent, "Battery below", 5, 50, 15, v => $"{v:0}%");
        }
        if (rows.Count == 0) return Ui.Group("Alerts", Ui.Caption("Install a System monitor feature to set alerts."));
        rows.Add(Ui.ChoiceRow("Repeat the same alert after", MonitorKeys.AlertCooldown,
            [("2", "2 minutes"), ("5", "5 minutes"), ("15", "15 minutes"), ("30", "30 minutes"), ("60", "1 hour")], "15"));
        rows.Add(Ui.Caption("Alerts fire when their selected limits are reached. CPU use and temperature alerts ignore spikes shorter than about 12 seconds. The repeat setting only limits repeats of the same alert. Alerts appear as Windows notifications (Settings › General › Notifications)."));
        return Ui.Group("Alerts", [.. rows]);
    }
}
