// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;

namespace Fieldkit.UI;

/// <summary>
/// A history graph line with a soft gradient fill underneath (the System tab graphs).
/// Set <see cref="Values"/> (oldest first) and call <see cref="InvalidateVisual"/>, or use <see cref="Push"/>.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> _values = [];

    public Sparkline()
    {
        Height = 36;
        SnapsToDevicePixels = true;
    }

    /// <summary>Theme brush key for the line (e.g. "Accent", "Green", "Cyan").</summary>
    public string BrushKey { get; set; } = "Accent";

    /// <summary>Fixed maximum; null scales to the largest sample.</summary>
    public double? Maximum { get; set; } = 100;

    /// <summary>How many samples fit across the width.</summary>
    public int Capacity { get; set; } = 60;

    public IReadOnlyList<double> Values => _values;

    public void SetValues(IEnumerable<double> values)
    {
        _values.Clear();
        _values.AddRange(values.TakeLast(Capacity));
        InvalidateVisual();
    }

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity) _values.RemoveRange(0, _values.Count - Capacity);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var color = ThemeManager.Color(BrushKey);

        var separator = ThemeManager.Brush("Separator");
        dc.DrawRectangle(separator, null, new Rect(0, h - 1, w, 1));
        if (_values.Count < 2) return;

        var max = Maximum ?? Math.Max(1e-9, _values.Max());
        var step = w / (Capacity - 1);
        var startX = w - step * (_values.Count - 1);
        var points = _values.Select((v, i) =>
            new Point(startX + i * step, h - 2 - Math.Clamp(v / max, 0, 1) * (h - 4))).ToList();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, h), true, true);
            ctx.LineTo(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), false, true);
            ctx.LineTo(new Point(points[^1].X, h), false, false);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(70, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(new SolidColorBrush(color), 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
