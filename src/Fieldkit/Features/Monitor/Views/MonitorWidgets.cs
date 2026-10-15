// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Typography = System.Windows.Documents.Typography;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>Icon code points used only by the monitor (checked with --render-glyph-range).</summary>
internal static class MonitorGlyphs
{
    public const string Thermometer = "";
    public const string Heart = "";
    public const string ArrowDown = "";
    public const string ArrowUp = "";
    public const string Gauge = "";
    public const string Sliders = "";
    public const string Chart = "";
}

/// <summary>Small building blocks shared by the monitor sections (upstream SystemSection helpers).</summary>
internal static class MonitorWidgets
{
    public const double LabelWidth = 58;
    public const double ValueWidth = 44;

    /// <summary>"Temperatures", "Hardware usage", "Memory" (10.5 semibold secondary upstream).</summary>
    public static TextBlock Subsection(string text)
    {
        var t = Ui.Text(text, 12.5, FontWeights.SemiBold, "TextSecondary", wrap: false);
        t.Margin = new Thickness(0, 0, 0, 2);
        return t;
    }

    /// <summary>The panel card that holds a section's blocks, separated by dividers.</summary>
    public static Border Card(StackPanel blocks) => Ui.Card(blocks, new Thickness(14, 12, 14, 12));

    /// <summary>Adds a block to a card stack with a divider before it (when not first).</summary>
    public static void AddBlock(StackPanel stack, UIElement block)
    {
        if (stack.Children.Count > 0) stack.Children.Add(Ui.Divider(9));
        stack.Children.Add(block);
    }

    public static TextBlock Value(string text, double size = 13, string brush = "TextPrimary")
    {
        var t = Ui.Text(text, size, FontWeights.SemiBold, brush, wrap: false);
        t.TextAlignment = TextAlignment.Right;
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    public static TextBlock Chevron(bool expanded, bool interactive = true)
    {
        var c = Ui.Icon(expanded ? Glyphs.ChevronDown : Glyphs.ChevronRight, 9, "TextSecondary");
        c.Width = 12;
        c.Opacity = interactive ? 1 : 0.35;
        return c;
    }

    /// <summary>Makes an element a transparent, hand-cursor click target.</summary>
    public static T Clickable<T>(T element, Action onClick) where T : FrameworkElement
    {
        if (element is Panel p && p.Background is null) p.Background = Brushes.Transparent;
        if (element is Border b && b.Background is null) b.Background = Brushes.Transparent;
        element.Cursor = Cursors.Hand;
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onClick);
        };
        return element;
    }

    /// <summary>A grid with fixed/auto/star columns and the given children in order.</summary>
    public static Grid Columns(GridLength[] widths, params UIElement?[] children)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is not { } child) continue;
            Grid.SetColumn(child, i);
            if (child is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(child);
        }
        return g;
    }

    public static GridLength Px(double v) => new(v);
    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);

    /// <summary>A graph with an optional ceiling label ("100%") like upstream graphCeilingLabel.</summary>
    public static FrameworkElement Graph(Sparkline line, Func<string>? ceiling = null, Sparkline? overlay = null)
    {
        var grid = new Grid { Height = 32, Margin = new Thickness(0, 2, 0, 0) };
        line.Height = 32;
        grid.Children.Add(line);
        if (overlay is not null)
        {
            overlay.Height = 32;
            grid.Children.Add(overlay);
        }
        if (ceiling is not null && Settings.GetBool(MonitorKeys.GraphScale, true))
        {
            var label = Ui.Text(ceiling(), 9.5, brush: "TextTertiary", wrap: false);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(0, -3, 0, 0);
            var rule = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Top, StrokeDashArray = [2, 3], StrokeThickness = 1 };
            rule.SetResourceReference(Shape.StrokeProperty, "Separator");
            grid.Children.Add(rule);
            grid.Children.Add(label);
            grid.Tag = label;
        }
        return grid;
    }

    public static void SetCeiling(FrameworkElement graph, string text)
    {
        if (graph.Tag is TextBlock label) label.Text = text;
    }

    public static Sparkline Line(string brushKey, double? maximum = 1) =>
        new() { BrushKey = brushKey, Maximum = maximum, Capacity = MonitorSampler.HistoryCapacity };

    /// <summary>One app row of a breakdown list: icon, name, value.</summary>
    public static FrameworkElement ProcessRow(ProcessUsageRow row, string value, double leading)
    {
        var icon = AppIcons.Element(AppIcons.ForPath(row.Path), 16, Glyphs.Grid);
        var name = Ui.Text(row.Name, 12.5, wrap: false);
        name.Margin = new Thickness(8, 0, 8, 0);
        var g = Columns([Auto, Star, Auto], icon, name, Ui.Text(value, 12.5, FontWeights.Medium, "TextSecondary", wrap: false));
        g.Margin = new Thickness(leading, 0, 0, 0);
        g.ToolTip = row.Name;
        return g;
    }

    /// <summary>A settings tile (upstream MonitorToken): icon, title and a switch bound to a setting.</summary>
    public static FrameworkElement Tile(string glyph, string title, string key, bool defaultValue = false, string? caption = null)
    {
        var icon = Ui.Icon(glyph, 15, "TextSecondary");
        icon.Width = 20;
        var text = Ui.Stack(1, Ui.Text(title, 13, FontWeights.Medium, wrap: false), caption is null ? null : Ui.Text(caption, 11, brush: "TextTertiary", wrap: false));
        text.Margin = new Thickness(8, 0, 8, 0);
        var grid = Columns([Auto, Star, Auto], icon, text, Ui.Toggle(key, defaultValue, small: true));
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = grid,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        return card;
    }

    /// <summary>Tiles in equal columns with 8 px gaps.</summary>
    public static FrameworkElement TileGrid(int columns, IReadOnlyList<FrameworkElement> tiles)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns };
        foreach (var t in tiles)
        {
            t.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(t);
        }
        grid.Margin = new Thickness(0, 0, -8, -8);
        return grid;
    }

    /// <summary>A short caption in the tertiary colour.</summary>
    public static TextBlock Note(string text, double leading = 0)
    {
        var t = Ui.Text(text, 11.5, brush: "TextTertiary");
        t.Margin = new Thickness(leading, 0, 0, 0);
        return t;
    }

    /// <summary>A row with an icon, label (+caption) and a value on the right (upstream PowerSection row).</summary>
    public static FrameworkElement ValueRow(string glyph, string iconBrush, string label, out TextBlock value, out TextBlock caption)
    {
        var icon = Ui.Icon(glyph, 14, iconBrush);
        icon.Width = 20;
        caption = Ui.Text(string.Empty, 11, brush: "TextSecondary", wrap: false);
        caption.Visibility = Visibility.Collapsed;
        var text = Ui.Stack(1, Ui.Text(label, 13, brush: "TextPrimary", wrap: false), caption);
        text.Margin = new Thickness(8, 0, 8, 0);
        value = Value(string.Empty, 15);
        value.MinWidth = 50;
        return Columns([Auto, Star, Auto], icon, text, value);
    }
}

/// <summary>Thin capacity bar (upstream UsageBar): accent under 60 %, yellow under 85 %, red above.</summary>
internal sealed class UsageBar : Grid
{
    /// <summary>The shown fill (0..1). Animated towards each new reading so bars glide instead of jumping.</summary>
    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(UsageBar),
        new PropertyMetadata(0.0, (d, e) => ((UsageBar)d).ApplyFraction((double)e.NewValue)));

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    private readonly Border _fill;
    private readonly ColumnDefinition _on = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _off = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly string? _tint;

    public UsageBar(string? tint = null)
    {
        _tint = tint;
        Height = 6;
        VerticalAlignment = VerticalAlignment.Center;
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBackground");
        SetColumnSpan(track, 2);
        _fill = new Border { CornerRadius = new CornerRadius(3), MinWidth = 4 };
        ColumnDefinitions.Add(_on);
        ColumnDefinitions.Add(_off);
        Children.Add(track);
        Children.Add(_fill);
        Set(0);
    }

    public void Set(double? fraction)
    {
        var f = Math.Clamp(fraction ?? 0, 0, 1);
        if (IsLoaded && fraction is not null && _fill.Visibility == Visibility.Visible)
            BeginAnimation(FractionProperty, new DoubleAnimation(f, TimeSpan.FromMilliseconds(280)) { EasingFunction = Ease });
        else
        {
            BeginAnimation(FractionProperty, null);
            Fraction = f;
        }
        _fill.Visibility = fraction is null ? Visibility.Hidden : Visibility.Visible;
        _fill.SetResourceReference(Border.BackgroundProperty, _tint ?? (f < 0.6 ? "Accent" : f < 0.85 ? "Yellow" : "Red"));
    }

    public void SetTint(string key) => _fill.SetResourceReference(Border.BackgroundProperty, key);

    private void ApplyFraction(double f)
    {
        _on.Width = new GridLength(f, GridUnitType.Star);
        _off.Width = new GridLength(1 - f, GridUnitType.Star);
    }
}

/// <summary>Traffic-light pill for memory pressure (upstream PressureIndicator).</summary>
internal sealed class PressureBadge : Border
{
    private readonly Border _background = new() { CornerRadius = new CornerRadius(10), Opacity = 0.15 };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = Ui.Text("-", 12, FontWeights.Medium, wrap: false);

    public PressureBadge()
    {
        _label.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 9, 3) };
        row.Children.Add(_dot);
        row.Children.Add(_label);
        var grid = new Grid();
        grid.Children.Add(_background);
        grid.Children.Add(row);
        Child = grid;
        VerticalAlignment = VerticalAlignment.Center;
        Set(MemoryPressure.Unknown);
    }

    public void Set(MemoryPressure pressure)
    {
        var (key, text) = pressure switch
        {
            MemoryPressure.Normal => ("Green", "Normal"),
            MemoryPressure.Warning => ("Yellow", "Caution"),
            MemoryPressure.Critical => ("Red", "Critical"),
            _ => ("TextSecondary", "-"),
        };
        _background.SetResourceReference(Border.BackgroundProperty, key);
        _dot.SetResourceReference(Shape.FillProperty, key);
        _label.SetResourceReference(TextBlock.ForegroundProperty, key);
        _label.Text = text;
    }
}
