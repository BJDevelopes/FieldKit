// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Typography = System.Windows.Documents.Typography;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.Features.Monitor.Services;
using Fieldkit.UI;
using static Fieldkit.Features.Monitor.Views.MonitorWidgets;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>The Network section (upstream NetworkSection): live speed, graph, session totals, local IP, speed test.</summary>
internal static class NetworkView
{
    public static IReadOnlyList<PanelOption> Options() =>
    [
        new("Live speed", MonitorKeys.NetSpeed),
        new("Graph", MonitorKeys.GraphNetwork, Indented: true),
        new("Totals", MonitorKeys.NetTotals),
        new("IP addresses", MonitorKeys.NetAddresses),
        new("Speed test", MonitorKeys.NetTest),
    ];

    public static FrameworkElement Build() =>
        MonitorPanel.Live("network", () => MonitorNeeds.Network, Render, Options(),
            MonitorKeys.NetworkSpeedUnit, MonitorKeys.GraphScale);

    private static (FrameworkElement, Action<MonitorSnapshot>) Render()
    {
        var card = new StackPanel();
        var updates = new List<Action<MonitorSnapshot>>();
        var bits = MonitorKeys.SpeedInBits;

        if (MonitorKeys.Shown(MonitorKeys.NetSpeed))
        {
            var (downCol, downValue) = RateColumn(MonitorGlyphs.ArrowDown, "Accent", "Download");
            var (upCol, upValue) = RateColumn(MonitorGlyphs.ArrowUp, "Green", "Upload");
            var divider = new Border { Width = 1, Height = 30, Margin = new Thickness(10, 0, 10, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, "Separator");
            var unit = Ui.Button(bits ? "bit/s" : "B/s", () => Settings.Set(MonitorKeys.NetworkSpeedUnit, bits ? "bytes" : "bits"));
            unit.Padding = new Thickness(7, 2, 7, 3);
            unit.FontSize = 11.5;
            unit.ToolTip = $"Network speed unit · {(bits ? "bit/s → B/s" : "B/s → bit/s")}";
            var row = Columns([Star, Auto, Star, Auto], downCol, divider, upCol, unit);
            var block = Ui.Stack(8, row);
            var down = Line("Accent", null);
            var up = Line("Green", null);
            FrameworkElement? graph = null;
            if (Settings.GetBool(MonitorKeys.GraphNetwork, true))
                Ui.Append(block, 8, graph = Graph(down, () => string.Empty, up));
            AddBlock(card, block);
            updates.Add(s =>
            {
                downValue.Text = s.NetDown is { } d ? MetricFormat.NetworkRate(d, bits) : "Measuring…";
                upValue.Text = s.NetUp is { } u ? MetricFormat.NetworkRate(u, bits) : "Measuring…";
                var peak = MetricFormat.NetworkGraphCeiling(Math.Max(1, Math.Max(
                    s.NetDownHistory.DefaultIfEmpty(0).Max(), s.NetUpHistory.DefaultIfEmpty(0).Max())), bits);
                down.Maximum = up.Maximum = peak;
                down.SetValues(s.NetDownHistory);
                up.SetValues(s.NetUpHistory);
                if (graph is not null) SetCeiling(graph, MetricFormat.NetworkRate(peak, bits));
            });
        }

        if (MonitorKeys.Shown(MonitorKeys.NetTotals))
        {
            var value = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var boot = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var bootLabel = Ui.Text("Since startup", 12, brush: "TextTertiary", wrap: false);
            bootLabel.ToolTip = "Counted by Windows since each connected adapter came up, usually since the PC started.";
            AddBlock(card, Ui.Stack(4,
                Ui.Split(Ui.Text("This session", 12, brush: "TextTertiary", wrap: false), value),
                Ui.Split(bootLabel, boot)));
            updates.Add(s =>
            {
                value.Text = s.NetTotalDown is { } d && s.NetTotalUp is { } u
                    ? $"↓{MetricFormat.Bytes(d)}  ↑{MetricFormat.Bytes(u)}" : string.Empty;
                boot.Text = s.NetSinceBootDown is { } bd && s.NetSinceBootUp is { } bu
                    ? $"↓{MetricFormat.Bytes(bd)}  ↑{MetricFormat.Bytes(bu)}" : string.Empty;
            });
        }

        if (MonitorKeys.Shown(MonitorKeys.NetAddresses))
        {
            var value = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary");
            value.TextAlignment = TextAlignment.Right;
            var block = Ui.Split(Ui.Text("Local IPv4", 12, brush: "TextTertiary", wrap: false), value);
            AddBlock(card, block);
            var lastRead = DateTime.MinValue;
            updates.Add(_ =>
            {
                if ((DateTime.UtcNow - lastRead).TotalSeconds < 5) return;
                lastRead = DateTime.UtcNow;
                Task.Run(NetworkReader.LocalAddresses).ContinueWith(t =>
                {
                    if (!t.IsFaulted) value.Text = string.Join("\n", t.Result);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
        }

        if (MonitorKeys.Shown(MonitorKeys.NetTest)) AddBlock(card, SpeedTestBlock());

        if (card.Children.Count == 0) card.Children.Add(Ui.Caption("Everything in this section is hidden. Use the sliders button to show it again."));
        return (Card(card), s =>
        {
            foreach (var u in updates) u(s);
        });
    }

    private static (FrameworkElement Column, TextBlock Value) RateColumn(string glyph, string brush, string label)
    {
        var icon = Ui.Icon(glyph, 15, brush);
        var value = Ui.Text("Measuring…", 16, FontWeights.SemiBold, wrap: false);
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        var text = Ui.Stack(0, value, Ui.Text(label, 12, brush: "TextSecondary", wrap: false));
        text.Margin = new Thickness(8, 0, 0, 0);
        return (Columns([Auto, Star], icon, text), value);
    }

    private static FrameworkElement SpeedTestBlock()
    {
        var host = new ContentControl();
        void Render()
        {
            var result = Ui.Text(string.Empty, 13, FontWeights.SemiBold, wrap: false);
            if (SpeedTest.DownloadMbps is { } d && SpeedTest.UploadMbps is { } u)
                result.Text = $"↓{MetricFormat.Mbps(d)} ↑{MetricFormat.Mbps(u)} Mbps";
            else if (SpeedTest.DownloadMbps is { } d2)
                result.Text = $"↓{MetricFormat.Mbps(d2)} Mbps";
            FrameworkElement action = SpeedTest.IsRunning
                ? Ui.Row(8, Ui.Icon(MonitorGlyphs.Gauge, 13, "TextSecondary"), Ui.Text("Testing…", 13, brush: "TextSecondary", wrap: false))
                : Ui.Button(SpeedTest.DownloadMbps is null ? "Speed test" : "Test again", SpeedTest.Start, glyph: MonitorGlyphs.Gauge);
            if (action is Button b) b.Padding = new Thickness(10, 5, 12, 6);
            var stack = Ui.Stack(6, Ui.Split(action, result));
            if (SpeedTest.State == SpeedTest.Phase.Failed)
                Ui.Append(stack, 4, Ui.Text("Test failed", 11.5, brush: "Orange"));
            else if (SpeedTest.LatencyMs is { } latency)
                Ui.Append(stack, 4, Note($"Latency: {Math.Round(latency)} ms"));
            if (SpeedTest.DownloadMbps is null && !SpeedTest.IsRunning)
                Ui.Append(stack, 4, Note("Measures against speed.cloudflare.com only when you start it."));
            host.Content = stack;
        }
        Render();
        Ui.Track(host, () =>
        {
            SpeedTest.Changed += Render;
            return new ActionDisposable(() => SpeedTest.Changed -= Render);
        });
        return host;
    }
}
