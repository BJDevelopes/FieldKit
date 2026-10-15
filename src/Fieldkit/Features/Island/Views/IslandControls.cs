// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Timer;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Views;

/// <summary>
/// The island is black in both themes (it stands in for the camera housing), so its colours are fixed here
/// rather than theme keys. Tints follow the system palette the theme uses.
/// </summary>
public static class IslandPalette
{
    public static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0, 0, 0)));
    public static readonly Brush Text = Freeze(new SolidColorBrush(Colors.White));
    public static readonly Brush Secondary = Freeze(new SolidColorBrush(Color.FromArgb(158, 255, 255, 255)));
    public static readonly Brush Tertiary = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
    public static readonly Brush Card = Freeze(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)));
    public static readonly Brush CardHover = Freeze(new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)));
    public static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));
    public static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(255, 159, 10)));
    public static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(48, 209, 88)));
    public static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(255, 69, 58)));
    public static readonly Brush Mint = Freeze(new SolidColorBrush(Color.FromRgb(99, 230, 226)));
    public static readonly Brush Purple = Freeze(new SolidColorBrush(Color.FromRgb(191, 90, 242)));
    public static readonly Brush Pink = Freeze(new SolidColorBrush(Color.FromRgb(255, 55, 95)));
    public static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(255, 214, 10)));
    public static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(10, 132, 255)));
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new(Glyphs.FontFamily);

    public static Brush Tint(string? key) => key switch
    {
        "Orange" => Orange,
        "Green" => Green,
        "Red" => Red,
        "Mint" => Mint,
        "Purple" => Purple,
        "Pink" => Pink,
        "Yellow" => Yellow,
        "Accent" or "Blue" => Blue,
        _ => Text,
    };

    public static Brush Rgb(double r, double g, double b, double a = 1) =>
        Freeze(new SolidColorBrush(Color.FromArgb((byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255))));

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Small builders for the island's dark surfaces.</summary>
public static class IslandUi
{
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = false, bool mono = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush ?? IslandPalette.Text,
            FontFamily = IslandPalette.Font,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono) System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    public static TextBlock Icon(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = IslandPalette.Icons,
        FontSize = size,
        Foreground = brush ?? IslandPalette.Text,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>A borderless round button with a glyph that brightens on hover.</summary>
    public static Border Button(string glyph, string tooltip, Action onClick, double size = 32, double glyphSize = 15, Brush? brush = null, Brush? plate = null)
    {
        var icon = Icon(glyph, glyphSize, brush);
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = plate ?? Brushes.Transparent,
            Child = icon,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
        };
        var rest = b.Background;
        b.MouseEnter += (_, _) => b.Background = IslandPalette.CardHover;
        b.MouseLeave += (_, _) => b.Background = rest;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Ui.Guard(onClick); };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return b;
    }

    /// <summary>A text pill button.</summary>
    public static Border Pill(string text, Action onClick, Brush? foreground = null, Brush? background = null, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            var i = Icon(glyph, 12, foreground);
            i.Margin = new Thickness(0, 0, 6, 0);
            content.Children.Add(i);
        }
        content.Children.Add(Text(text, 12.5, FontWeights.SemiBold, foreground));
        var b = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 5, 12, 6),
            Background = background ?? IslandPalette.Card,
            Child = content,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var rest = b.Background;
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Ui.Guard(onClick); };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _ = rest;
        return b;
    }

    public static Border Card(UIElement child, Thickness? padding = null, double radius = 14) => new()
    {
        CornerRadius = new CornerRadius(radius),
        Background = IslandPalette.Card,
        Padding = padding ?? new Thickness(12, 10, 12, 10),
        Child = child,
    };

    /// <summary>A rounded image (album art) or a placeholder with a music note.</summary>
    public static Border Artwork(BitmapSource? art, double size, double radiusFactor = 0.19)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * radiusFactor),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            ClipToBounds = true,
        };
        if (art is not null)
            border.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        else
            border.Child = Icon(Glyphs.MusicNote, size * 0.3, IslandPalette.Tertiary);
        return border;
    }

    /// <summary>A thin progress capsule.</summary>
    public static Grid Meter(double fraction, double height = 4, Brush? fill = null, double width = double.NaN)
    {
        var grid = new Grid { Height = height, Width = width, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = IslandPalette.Track });
        var bar = new Border { CornerRadius = new CornerRadius(height / 2), Background = fill ?? IslandPalette.Text, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(bar);
        grid.SizeChanged += (_, _) => bar.Width = Math.Max(height, grid.ActualWidth * Math.Clamp(fraction, 0, 1));
        bar.Width = double.IsNaN(width) ? height : Math.Max(height, width * Math.Clamp(fraction, 0, 1));
        grid.Tag = (Action<double>)(f =>
        {
            fraction = f;
            bar.Width = Math.Max(height, (grid.ActualWidth > 0 ? grid.ActualWidth : double.IsNaN(width) ? 0 : width) * Math.Clamp(f, 0, 1));
        });
        return grid;
    }

    public static void SetMeter(Grid meter, double fraction) => (meter.Tag as Action<double>)?.Invoke(fraction);

    /// <summary>Runs <paramref name="refresh"/> now and every <paramref name="interval"/> while loaded.</summary>
    public static void Live(FrameworkElement owner, TimeSpan interval, Action refresh)
    {
        Ui.Guard(refresh);
        Ui.Every(owner, interval, refresh);
    }

    /// <summary>Stacks with spacing.</summary>
    public static StackPanel Row(double spacing, params UIElement?[] children) => Ui.Row(spacing, children);
    public static StackPanel Stack(double spacing, params UIElement?[] children) => Ui.Stack(spacing, children);
}

/// <summary>
/// The island's equalizer bars: live levels when the Live equalizer hears the music, otherwise upstream's
/// synthetic motion; dots while paused. Animated at about 30 fps only while visible.
/// </summary>
public sealed class EqualizerBars : FrameworkElement
{
    private readonly IslandContext _ctx;
    private readonly int _count;
    private readonly double _barWidth;
    private readonly double _height;
    private readonly Brush _brush;
    private double _lastFrame;

    public EqualizerBars(IslandContext ctx, int count, double barWidth, double height, Brush? brush = null)
    {
        _ctx = ctx;
        _count = count;
        _barWidth = barWidth;
        _height = height;
        _brush = brush ?? IslandPalette.Text;
        Width = EqualizerMath.RowWidth(count, barWidth);
        Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        if (!ctx.IsPreview)
        {
            Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
            Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = _ctx.AnimationTime();
        if (t - _lastFrame < 1 / 30.0) return;
        _lastFrame = t;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var playing = _ctx.Music() is { IsPlaying: true };
        var levels = playing ? _ctx.EqualizerLevels() : null;
        var t = _ctx.AnimationTime();
        for (var i = 0; i < _count; i++)
        {
            double h;
            if (!playing) h = _barWidth;
            else if (levels is { Length: > 0 }) h = EqualizerMath.LiveHeight(levels[EqualizerMath.BandForBar(i, _count, levels.Length)], _height, _barWidth);
            else h = EqualizerMath.SyntheticHeight(i, _count, t, _height, _barWidth);
            var x = i * _barWidth * 1.85;
            dc.DrawRoundedRectangle(_brush, null, new Rect(x, (_height - h) / 2, _barWidth, h), _barWidth / 2, _barWidth / 2);
        }
    }
}

/// <summary>
/// The island's slim level slider (volume, playback position): a 4 px track with a white fill and a knob shown
/// while hovered or dragged. Raises <see cref="ValueChanged"/> while dragging and <see cref="Committed"/> on release.
/// </summary>
public sealed class LevelSlider : FrameworkElement
{
    private double _value;
    private bool _dragging;
    private readonly Brush _fill;

    public event Action<double>? ValueChanged;
    public event Action<double>? Committed;

    public LevelSlider(Brush? fill = null)
    {
        _fill = fill ?? IslandPalette.Text;
        Height = 16;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public bool IsDragging => _dragging;

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _value) < 1e-6) return;
            _value = v;
            InvalidateVisual();
        }
    }

    private void SetFromPoint(Point p)
    {
        Value = ActualWidth <= 0 ? 0 : p.X / ActualWidth;
        ValueChanged?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(_value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = _value + (e.Delta > 0 ? 1 : -1) / 16.0;
        ValueChanged?.Invoke(_value);
        Committed?.Invoke(_value);
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawRoundedRectangle(IslandPalette.Track, null, new Rect(0, h / 2 - 2, w, 4), 2, 2);
        dc.DrawRoundedRectangle(_fill, null, new Rect(0, h / 2 - 2, Math.Max(4, w * _value), 4), 2, 2);
        if (_dragging || IsMouseOver) dc.DrawEllipse(IslandPalette.Text, null, new Point(Math.Clamp(w * _value, 6, Math.Max(6, w - 6)), h / 2), 6, 6);
    }
}

/// <summary>A progress ring (agent limits, resting AI limits).</summary>
public sealed class Ring : FrameworkElement
{
    private readonly double _fraction;
    private readonly Brush _brush;
    private readonly double _thickness;

    public Ring(double fraction, double size, Brush brush, double thickness = 2.5)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        _brush = brush;
        _thickness = thickness;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = (Math.Min(ActualWidth, ActualHeight) - _thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(IslandPalette.Track, _thickness), c, r, r);
        if (_fraction <= 0) return;
        if (_fraction >= 1)
        {
            dc.DrawEllipse(null, new Pen(_brush, _thickness), c, r, r);
            return;
        }
        var angle = _fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(r, r), 0, _fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(_brush, _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}

/// <summary>
/// Upstream's minute ruler for the timer: one tick per minute (14 px apart), labels every 5, a centre pointer.
/// Drag, click, scroll or use the arrow keys; the value is saved to the timer minutes setting.
/// </summary>
public sealed class TimerRuler : FrameworkElement
{
    private const double Spacing = 14;
    private int _value;
    private double? _dragStart;
    private int _dragValue;
    private bool _dragged;

    public event Action<int>? ValueChanged;

    public TimerRuler(int value)
    {
        _value = Math.Clamp(value, 1, 180);
        Height = 64;
        Focusable = true;
        Cursor = Cursors.SizeWE;
        ToolTip = "Minutes";
    }

    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 1, 180);
            if (v == _value) return;
            _value = v;
            InvalidateVisual();
            ValueChanged?.Invoke(v);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this).X;
        _dragValue = _value;
        _dragged = false;
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } s) return;
        var dx = e.GetPosition(this).X - s;
        if (Math.Abs(dx) > 3) _dragged = true;
        Value = (int)Math.Round(_dragValue - dx / Spacing);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragStart is { } s && !_dragged) Value = (int)Math.Round(_value + (s - ActualWidth / 2) / Spacing);
        _dragStart = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = _value + (e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Down: Value = _value - 1; break;
            case Key.Right or Key.Up: Value = _value + 1; break;
            case Key.Home: Value = 1; break;
            case Key.End: Value = 180; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var mid = w / 2;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var m = 1; m <= 180; m++)
        {
            var x = mid + (m - _value) * Spacing;
            if (x < -10 || x > w + 10) continue;
            var edge = Math.Min(x, w - x) / (w * 0.12);
            var fade = Math.Clamp(edge, 0, 1);
            var on = m <= _value;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * fade * (on ? 1 : 0.35)), 255, 159, 10));
            var tall = m % 5 == 0 || m == 1;
            dc.DrawRoundedRectangle(brush, null, new Rect(x - 2, 8, 4, tall ? 28 : 20), 2, 2);
            if (!tall) continue;
            var ft = new FormattedText(TimerFormat.RulerLabel(m), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(IslandPalette.Font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 12,
                new SolidColorBrush(Color.FromArgb((byte)(255 * fade * 0.8), 255, 255, 255)), dip);
            dc.DrawText(ft, new Point(x - ft.Width / 2, 40));
        }
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(mid - 5, h - 2), true, true);
            g.LineTo(new Point(mid + 5, h - 2), true, false);
            g.LineTo(new Point(mid, h - 9), true, false);
        }
        dc.DrawGeometry(IslandPalette.Orange, null, tri);
    }
}

/// <summary>
/// The companion: a small vector body (ball, egg, rounded square, pill, or robot) with expressive eyes,
/// blinking at rest and hopping when it reacts (a simplified port of upstream's layer rig).
/// </summary>
public sealed class MascotView : FrameworkElement
{
    private readonly bool _animate;
    private double _blinkAt;
    private double _blinkUntil;
    private double _reactionStart = double.NegativeInfinity;
    private MascotReaction? _reaction;
    private readonly Random _random = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public MascotView(double size, bool animate, MascotMood mood = MascotMood.Idle)
    {
        Width = Height = size;
        _animate = animate;
        Mood = mood;
        VerticalAlignment = VerticalAlignment.Center;
        _blinkAt = 2.6 + _random.NextDouble() * 3.8;
        if (animate)
        {
            // Only a blink every few seconds and a short hop on reactions move: a timer wakes for those alone.
            // (CompositionTarget.Rendering would keep the whole island redrawing at 60 frames a second.)
            _frames = new DispatcherTimer(DispatcherPriority.Render);
            _frames.Tick += (_, _) => OnFrame();
            Loaded += (_, _) => ScheduleFrame();
            Unloaded += (_, _) => _frames.Stop();
        }
    }

    private readonly DispatcherTimer? _frames;

    public MascotMood Mood { get; set; }

    public void React(MascotReaction reaction)
    {
        _reaction = reaction;
        _reactionStart = _clock.Elapsed.TotalSeconds;
        InvalidateVisual();
        ScheduleFrame();
    }

    private void OnFrame()
    {
        var t = _clock.Elapsed.TotalSeconds;
        if (t >= _blinkAt)
        {
            _blinkUntil = t + 0.17;
            _blinkAt = t + 2.6 + _random.NextDouble() * 3.8;
        }
        InvalidateVisual();
        ScheduleFrame();
    }

    /// <summary>30 frames a second during a reaction; otherwise one wake to open the eyes and one for the next blink.</summary>
    private void ScheduleFrame()
    {
        if (_frames is null || !IsLoaded) return;
        var t = _clock.Elapsed.TotalSeconds;
        var reacting = _reaction is { } r && t - _reactionStart < MascotLogic.Length(r) + 0.05;
        var next = reacting ? 1 / 30.0 : t < _blinkUntil ? _blinkUntil - t : _blinkAt - t;
        _frames.Stop();
        _frames.Interval = TimeSpan.FromSeconds(Math.Max(1 / 60.0, next));
        _frames.Start();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var t = _clock.Elapsed.TotalSeconds;
        var mood = Mood;
        var hop = 0.0;
        if (_reaction is { } r && t - _reactionStart < MascotLogic.Length(r))
        {
            mood = MascotLogic.Mood(r);
            hop = MascotLogic.Hop(r, t - _reactionStart) * 0.22 * s;
        }
        else _reaction = null;

        var palette = MascotLogic.Palette(Core.Settings.GetString(IslandKeys.MascotPalette, "pearl"));
        var robot = Core.Settings.GetString(IslandKeys.MascotStyle, "minimal") == "robot";
        var shape = Core.Settings.GetString(IslandKeys.MascotShape, "ball");
        var light = Color.FromRgb((byte)(palette.Light.R * 255), (byte)(palette.Light.G * 255), (byte)(palette.Light.B * 255));
        var shade = Color.FromRgb((byte)(palette.Shade.R * 255), (byte)(palette.Shade.G * 255), (byte)(palette.Shade.B * 255));
        var fill = new LinearGradientBrush(light, shade, 90);
        var face = MascotLogic.Face(mood);
        dc.PushTransform(new TranslateTransform(0, -hop));
        dc.PushTransform(new RotateTransform(face.Tilt * 180 / Math.PI, s / 2, s * 0.9));

        (double X, double Y) eyeL, eyeR;
        double ew, eh;
        if (robot)
        {
            var shadeBrush = new SolidColorBrush(shade);
            dc.DrawRoundedRectangle(shadeBrush, null, R(s, .05, .50, .08, .20), .035 * s, .035 * s);
            dc.DrawRoundedRectangle(shadeBrush, null, R(s, .87, .50, .08, .20), .035 * s, .035 * s);
            dc.DrawRoundedRectangle(shadeBrush, null, R(s, .475, .12, .05, .16), .025 * s, .025 * s);
            dc.DrawEllipse(new SolidColorBrush(mood == MascotMood.Sleepy ? shade : light), null, new Point(.5 * s, .11 * s), .07 * s, .07 * s);
            dc.DrawRoundedRectangle(fill, null, R(s, .12, .26, .76, .68), .20 * s, .20 * s);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 23, 28)), null, R(s, .20, .44, .60, .38), .15 * s, .15 * s);
            (eyeL, eyeR, ew, eh) = ((.36, .63), (.64, .63), .085, .16);
        }
        else
        {
            switch (shape)
            {
                case "egg":
                    dc.DrawGeometry(fill, null, Egg(s));
                    (eyeL, eyeR, ew, eh) = ((.375, .55), (.625, .55), .095, .19);
                    break;
                case "squircle":
                    dc.DrawGeometry(fill, null, Squircle(s));
                    (eyeL, eyeR, ew, eh) = ((.35, .53), (.65, .53), .10, .20);
                    break;
                case "pill":
                    dc.DrawRoundedRectangle(fill, null, R(s, .05, .28, .90, .60), .30 * s, .30 * s);
                    (eyeL, eyeR, ew, eh) = ((.37, .57), (.63, .57), .09, .19);
                    break;
                default:
                    dc.DrawEllipse(fill, null, new Point(.5 * s, .54 * s), .42 * s, .42 * s);
                    (eyeL, eyeR, ew, eh) = ((.36, .50), (.64, .50), .10, .20);
                    break;
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), null, new Point(.31 * s, .29 * s), .06 * s, .04 * s);
        }

        var ink = robot ? new SolidColorBrush(light) : new SolidColorBrush(Color.FromRgb(18, 18, 20));
        var blink = _animate && t < _blinkUntil && _reaction is null ? 0.1 : 1.0;
        var gx = face.GazeX * s;
        var gy = face.GazeY * s;
        DrawEye(dc, face.Left, eyeL.X * s + gx, eyeL.Y * s + gy, ew * s, eh * s, blink, ink, mirror: false);
        DrawEye(dc, face.Right, eyeR.X * s + gx, eyeR.Y * s + gy, ew * s, eh * s, blink, ink, mirror: true);
        dc.Pop();
        dc.Pop();
    }

    private static Rect R(double s, double x, double y, double w, double h) => new(x * s, y * s, w * s, h * s);

    private static void DrawEye(DrawingContext dc, MascotEye eye, double cx, double cy, double w, double h, double blink, Brush ink, bool mirror)
    {
        switch (eye)
        {
            case MascotEye.Happy:
            {
                var pen = new Pen(ink, w * 0.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx - w * 0.9, cy + h * 0.1), false, false);
                    c.QuadraticBezierTo(new Point(cx, cy - h * 0.55), new Point(cx + w * 0.9, cy + h * 0.1), true, false);
                }
                dc.DrawGeometry(null, pen, g);
                return;
            }
            case MascotEye.Heart:
            {
                var g = new StreamGeometry();
                var r = w * 0.9;
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx, cy + r), true, true);
                    c.BezierTo(new Point(cx - r * 1.4, cy), new Point(cx - r * 0.8, cy - r * 1.1), new Point(cx, cy - r * 0.35), true, false);
                    c.BezierTo(new Point(cx + r * 0.8, cy - r * 1.1), new Point(cx + r * 1.4, cy), new Point(cx, cy + r), true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 55, 95)), null, g);
                return;
            }
            case MascotEye.Closed:
                dc.DrawRoundedRectangle(ink, null, new Rect(cx - w * 0.75, cy + h * 0.12 - w * 0.22, w * 1.5, w * 0.45), w * 0.22, w * 0.22);
                return;
            case MascotEye.Sleepy:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx - w * 0.62, cy + h * 0.02), true, true);
                    c.LineTo(new Point(cx + w * 0.62, cy + h * 0.02), true, false);
                    c.ArcTo(new Point(cx - w * 0.62, cy + h * 0.02), new Size(w * 0.62, h * 0.38), 0, false, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(ink, null, g);
                return;
            }
            case MascotEye.Wide:
                dc.DrawEllipse(ink, null, new Point(cx, cy - h * 0.02), w * 0.8 * (blink < 1 ? 1 : 1), w * 0.8 * blink);
                return;
            case MascotEye.Squint:
                dc.DrawRoundedRectangle(ink, null, new Rect(cx - w / 2, cy + h * 0.08 - h * 0.275 * blink, w, h * 0.55 * blink), w / 2, w / 2);
                return;
            case MascotEye.Determined:
            {
                dc.PushClip(new RectangleGeometry(new Rect(cx - w, cy - h * (mirror ? 0.3 : 0.02), w * 2, h * 2)));
                dc.DrawRoundedRectangle(ink, null, new Rect(cx - w / 2, cy - h / 2 * blink, w, h * blink), w / 2, w / 2);
                dc.Pop();
                return;
            }
            default:
                dc.DrawRoundedRectangle(ink, null, new Rect(cx - w / 2, cy - h / 2 * blink, w, h * blink), w / 2, Math.Min(w / 2, h * blink / 2));
                return;
        }
    }

    private static Geometry Egg(double s)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            Point P(double x, double y) => new(x * s, y * s);
            c.BeginFigure(P(.5, .10), true, true);
            c.BezierTo(P(.71, .10), P(.86, .36), P(.86, .60), true, false);
            c.BezierTo(P(.86, .80), P(.70, .96), P(.5, .96), true, false);
            c.BezierTo(P(.30, .96), P(.14, .80), P(.14, .60), true, false);
            c.BezierTo(P(.14, .36), P(.29, .10), P(.5, .10), true, false);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Squircle(double s)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (var i = 0; i < 72; i++)
            {
                var a = i / 72.0 * 2 * Math.PI;
                var cos = Math.Cos(a);
                var sin = Math.Sin(a);
                var x = .5 + .43 * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2 / 3.4);
                var y = .55 + .40 * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2 / 3.4);
                if (i == 0) c.BeginFigure(new Point(x * s, y * s), true, true);
                else c.LineTo(new Point(x * s, y * s), true, false);
            }
        }
        g.Freeze();
        return g;
    }
}
