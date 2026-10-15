// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Typography = System.Windows.Documents.Typography;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>
/// The top apps of one resource, refreshed on a worker thread at the monitor interval while
/// visible (upstream breakdownList / EnergyAppsBreakdown).
/// </summary>
internal sealed class BreakdownList : StackPanel
{
    private const int Limit = 15;
    private readonly BreakdownKind _kind;
    private readonly double _leading;
    private readonly string _emptyText;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _busy;
    private bool _hasRows;

    public BreakdownList(BreakdownKind kind, double leading, string? emptyText = null)
    {
        _kind = kind;
        _leading = leading;
        _emptyText = emptyText ?? "Measuring…";
        Margin = new Thickness(0, 2, 0, 2);
        Children.Add(MonitorWidgets.Note("Measuring…", leading));
        Loaded += (_, _) => Refresh(force: true);
    }

    /// <summary>Called on every sample; refreshes when the interval elapsed.</summary>
    public void Tick()
    {
        var interval = _kind == BreakdownKind.Memory ? 4 : MonitorKeys.SanitizedInterval();
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds >= interval * 0.8) Refresh(force: false);
    }

    private void Refresh(bool force)
    {
        if (_busy || (!force && !IsLoaded)) return;
        _busy = true;
        _lastRefresh = DateTime.UtcNow;
        var kind = _kind;
        var gpu = MonitorSampler.GpuPerProcess;
        Task.Run(() =>
        {
            var rows = ProcessUsage.Top(kind, Limit, gpu);
            // The first CPU reading only primes the deltas: take a second one shortly after.
            if (rows.Count == 0 && kind is BreakdownKind.Cpu or BreakdownKind.Energy && !_hasRows)
            {
                Thread.Sleep(700);
                rows = ProcessUsage.Top(kind, Limit, gpu);
            }
            return rows;
        }).ContinueWith(t =>
        {
            _busy = false;
            if (t.IsFaulted) return;
            Show(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Show(List<ProcessUsageRow> rows)
    {
        if (rows.Count == 0 && _hasRows && _kind != BreakdownKind.Energy) return; // keep the last list rather than flicker
        Children.Clear();
        _hasRows = rows.Count > 0;
        if (rows.Count == 0)
        {
            Children.Add(MonitorWidgets.Note(_emptyText, _leading));
            return;
        }
        foreach (var row in rows)
        {
            var value = _kind == BreakdownKind.Memory
                ? MetricFormat.Memory(row.Value)
                : row.Value.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            var element = MonitorWidgets.ProcessRow(row, value, _leading);
            if (Children.Count > 0) element.Margin = new Thickness(element.Margin.Left, 4, 0, 0);
            Children.Add(element);
        }
    }
}

/// <summary>One bar per logical core, grouped by core class (upstream CPUCoreMatrix).</summary>
internal sealed class CoreMatrix : Border
{
    private readonly List<(Rectangle Fill, double Height)> _bars = [];
    private readonly WrapPanel _groups = new();
    private int _count = -1;

    public CoreMatrix()
    {
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8);
        Margin = new Thickness(0, 4, 0, 2);
        SetResourceReference(BorderBrushProperty, "CardBorder");
        SetResourceReference(BackgroundProperty, "ControlBackground");
        Child = _groups;
        ToolTip = "Each bar is one logical core. Its fill height shows utilization, not remaining performance.";
    }

    public void Set(IReadOnlyList<double?> usage, IReadOnlyList<CoreGroup> groups)
    {
        if (usage.Count != _count) Build(usage.Count, groups);
        for (var i = 0; i < _bars.Count && i < usage.Count; i++)
        {
            var (fill, height) = _bars[i];
            fill.Height = Math.Max(0, Math.Clamp(usage[i] ?? 0, 0, 1) * height);
        }
    }

    private void Build(int count, IReadOnlyList<CoreGroup> groups)
    {
        _count = count;
        _bars.Clear();
        _groups.Children.Clear();
        if (count == 0) return;
        var effective = groups.Count > 0 && groups.Sum(g => g.Indices.Count) == count
            ? groups
            : [new CoreGroup("Cores", Enumerable.Range(0, count).ToList())];
        var barWidth = count <= 16 ? 10 : count <= 24 ? 8 : 6;
        const double barHeight = 40;
        var byIndex = new (Rectangle, double)[count];
        foreach (var group in effective)
        {
            var bars = new WrapPanel { MaxWidth = 280 };
            foreach (var index in group.Indices)
            {
                var fill = new Rectangle { VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };
                fill.SetResourceReference(Shape.FillProperty, "TextPrimary");
                fill.Opacity = 0.78;
                var cell = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    CornerRadius = new CornerRadius(2),
                    BorderThickness = new Thickness(0.75),
                    Margin = new Thickness(0, 0, 3, 3),
                    Child = fill,
                    ClipToBounds = true,
                    ToolTip = $"Core {index + 1}",
                };
                cell.SetResourceReference(BorderBrushProperty, "TextTertiary");
                bars.Children.Add(cell);
                if (index < count) byIndex[index] = (fill, barHeight);
            }
            var name = group.Name switch
            {
                "Performance" => "Performance",
                "Efficiency" => "Efficiency",
                _ => group.Name,
            };
            var caption = Ui.Text($"{name} ×{group.Indices.Count}", 11.5, brush: "TextSecondary", wrap: false);
            var stack = Ui.Stack(3, bars, caption);
            stack.Margin = new Thickness(0, 0, 12, 4);
            _groups.Children.Add(stack);
        }
        _bars.AddRange(byIndex.Where(b => b.Item1 is not null));
    }
}

/// <summary>A temperature tile (upstream temperatureCell).</summary>
internal sealed class TemperatureTile : Border
{
    private readonly TextBlock _value;

    public TemperatureTile(string glyph, string label)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(4, 7, 4, 8);
        SetResourceReference(BackgroundProperty, "ControlBackground");
        var head = Ui.Row(5, Ui.Icon(glyph, 11, "TextSecondary"), Ui.Text(label, 12, brush: "TextSecondary", wrap: false));
        head.HorizontalAlignment = HorizontalAlignment.Center;
        _value = Ui.Text("-", 18, FontWeights.SemiBold, wrap: false);
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        Child = Ui.Stack(3, head, _value);
    }

    public void Set(double? celsius) =>
        _value.Text = celsius is { } c ? MetricFormat.Temperature(c, MonitorKeys.Fahrenheit) : "-";
}
