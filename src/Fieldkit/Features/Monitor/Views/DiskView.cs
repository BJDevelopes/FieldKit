// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Typography = System.Windows.Documents.Typography;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;
using static Fieldkit.Features.Monitor.Views.MonitorWidgets;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>The Disk section (upstream DiskSection): drive picker, usage, live activity and tools.</summary>
internal static class DiskView
{
    private static string? _selected;

    public static IReadOnlyList<PanelOption> Options() =>
    [
        new("Disk usage", MonitorKeys.DiskUsage),
        new("Live activity", MonitorKeys.DiskActivity),
        new("Graph", MonitorKeys.GraphDisk, Indented: true),
        new("Tools", MonitorKeys.DiskTools),
    ];

    public static FrameworkElement Build() =>
        MonitorPanel.Live("disk", () => MonitorNeeds.Disk, Render, Options(), MonitorKeys.GraphScale);

    private static (FrameworkElement, Action<MonitorSnapshot>) Render()
    {
        var root = new StackPanel();
        var body = new StackPanel();
        var selector = new WrapPanel();
        root.Children.Add(Ui.Stack(6, Ui.Text("Select disk", 12, FontWeights.Medium, "TextTertiary", wrap: false), selector));
        root.Children.Add(body);
        var empty = Note("No drives found.");
        string selectorKey = string.Empty;
        var updates = new List<Action<MonitorSnapshot, DriveReading>>();

        void BuildBody()
        {
            body.Children.Clear();
            updates.Clear();
            if (MonitorKeys.Shown(MonitorKeys.DiskUsage)) AddUsage();
            if (MonitorKeys.Shown(MonitorKeys.DiskActivity)) AddActivity();
            if (MonitorKeys.Shown(MonitorKeys.DiskTools)) AddTools();
        }

        void AddUsage()
        {
            var title = Ui.Text(string.Empty, 13, FontWeights.SemiBold, wrap: false);
            var tags = Ui.Row(4);
            var bar = new UsageBar();
            var used = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var free = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var totals = Ui.Text(string.Empty, 11.5, brush: "TextTertiary", wrap: false);
            var block = Ui.Stack(6, Header("Disk usage"),
                Ui.Split(Ui.Row(6, Ui.Icon(Glyphs.HardDrive, 12, "TextSecondary"), title), tags),
                bar, Ui.Split(used, free), totals);
            Divide(block);
            updates.Add((_, d) =>
            {
                title.Text = $"{d.Label} ({d.Name})";
                tags.Children.Clear();
                if (d.FileSystem is { } fs) tags.Children.Add(Ui.Badge(fs));
                Ui.Append(tags, 4, Ui.Badge(d.IsInternal ? "Internal" : "External"));
                bar.Set(d.UsedFraction);
                used.Text = $"{MetricFormat.Percent(d.UsedFraction)} used";
                free.Text = $"{MetricFormat.Bytes(d.FreeBytes)} available";
                totals.Text = $"{MetricFormat.Bytes(d.UsedBytes)} / {MetricFormat.Bytes(d.TotalBytes)}";
            });
        }

        void AddActivity()
        {
            var (readCol, readValue) = RateColumn(MonitorGlyphs.ArrowDown, "Accent", "Read");
            var (writeCol, writeValue) = RateColumn(MonitorGlyphs.ArrowUp, "Pink", "Write");
            var divider = new Border { Width = 1, Height = 30, Margin = new Thickness(10, 0, 10, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, "Separator");
            var read = Line("Accent", null);
            var write = Line("Pink", null);
            var session = Ui.Text(string.Empty, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
            var block = Ui.Stack(8, Header("Live activity"), Columns([Star, Auto, Star], readCol, divider, writeCol));
            FrameworkElement? graph = null;
            if (Settings.GetBool(MonitorKeys.GraphDisk, true)) Ui.Append(block, 8, graph = Graph(read, () => string.Empty, write));
            Ui.Append(block, 6, Ui.Split(Ui.Text("This session (all disks)", 12, brush: "TextTertiary", wrap: false), session));
            Divide(block);
            updates.Add((s, d) =>
            {
                readValue.Text = d.ReadBytesPerSec is { } r ? MetricFormat.BytesPerSec(r) : "Measuring…";
                writeValue.Text = d.WriteBytesPerSec is { } w ? MetricFormat.BytesPerSec(w) : "Measuring…";
                var peak = MetricFormat.GraphCeiling(Math.Max(1, Math.Max(
                    s.DiskReadHistory.DefaultIfEmpty(0).Max(), s.DiskWriteHistory.DefaultIfEmpty(0).Max())), 1024);
                read.Maximum = write.Maximum = peak;
                read.SetValues(s.DiskReadHistory);
                write.SetValues(s.DiskWriteHistory);
                if (graph is not null) SetCeiling(graph, MetricFormat.BytesPerSec(peak));
                session.Text = $"↓ {MetricFormat.Bytes(s.DiskTotalRead)}  ↑ {MetricFormat.Bytes(s.DiskTotalWritten)}";
            });
        }

        void AddTools()
        {
            var name = Ui.Text(string.Empty, 13, FontWeights.Medium, wrap: false);
            string? root = null;
            var open = Ui.Button("Open", () =>
            {
                if (root is null) return;
                PanelHost.Hide();
                Launch("explorer.exe", root);
            }, glyph: Glyphs.Folder);
            var storage = Ui.Button("Storage settings", () =>
            {
                PanelHost.Hide();
                Launch("ms-settings:storagesense", null);
            }, glyph: Glyphs.Settings);
            open.Padding = storage.Padding = new Thickness(10, 4, 12, 5);
            storage.HorizontalAlignment = HorizontalAlignment.Left;
            var block = Ui.Stack(8, Header("Tools"), Ui.Split(name, open), storage);
            Divide(block);
            updates.Add((_, d) =>
            {
                name.Text = $"{d.Label} ({d.Name})";
                root = d.Root;
            });
        }

        void Divide(FrameworkElement block)
        {
            body.Children.Add(Ui.Divider(9));
            body.Children.Add(block);
        }

        BuildBody();
        return (Card(root), s =>
        {
            var drives = s.Drives;
            body.Visibility = drives.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (drives.Count == 0)
            {
                if (!selector.Children.Contains(empty))
                {
                    selector.Children.Clear();
                    selector.Children.Add(empty);
                    selectorKey = string.Empty;
                }
                return;
            }
            if (_selected is null || drives.All(d => d.Name != _selected)) _selected = drives[0].Name;
            var key = string.Join("|", drives.Select(d => $"{d.Name}:{Math.Round(d.UsedFraction * 100)}")) + "#" + _selected;
            if (key != selectorKey)
            {
                selectorKey = key;
                selector.Children.Clear();
                foreach (var d in drives) selector.Children.Add(DriveChip(d, d.Name == _selected, () =>
                {
                    _selected = d.Name;
                    selectorKey = string.Empty;
                    MonitorSampler.Poke();
                }));
            }
            var current = drives.First(d => d.Name == _selected);
            foreach (var u in updates) u(s, current);
        });
    }

    private static FrameworkElement DriveChip(DriveReading d, bool selected, Action onClick)
    {
        var name = Ui.Text($"{d.Label} ({d.Name})", 12, FontWeights.SemiBold, wrap: false);
        name.MaxWidth = 110;
        var text = Ui.Stack(0, name, Ui.Text($"{MetricFormat.Percent(d.UsedFraction)} used", 10.5, brush: "TextSecondary", wrap: false));
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 5, 10, 6),
            Margin = new Thickness(0, 0, 6, 6),
            Child = Ui.Row(6, Ui.Icon(d.IsInternal ? Glyphs.HardDrive : Glyphs.Usb, 12, "TextSecondary"), text),
            MinWidth = 96,
        };
        chip.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
        return Clickable(chip, onClick);
    }

    private static TextBlock Header(string text) => Ui.Text(text, 12, FontWeights.Medium, "TextTertiary", wrap: false);

    private static (FrameworkElement Column, TextBlock Value) RateColumn(string glyph, string brush, string label)
    {
        var icon = Ui.Icon(glyph, 15, brush);
        var value = Ui.Text("Measuring…", 16, FontWeights.SemiBold, wrap: false);
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        var text = Ui.Stack(0, value, Ui.Text(label, 12, brush: "TextSecondary", wrap: false));
        text.Margin = new Thickness(8, 0, 0, 0);
        return (Columns([Auto, Star], icon, text), value);
    }

    private static void Launch(string target, string? args)
    {
        try
        {
            var info = args is null ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(target, $"\"{args}\"") { UseShellExecute = true };
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening {target} failed: {ex.Message}");
        }
    }
}
