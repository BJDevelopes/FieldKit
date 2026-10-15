// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// The wheel itself (upstream <c>RadialMenuView</c>): a disc with one chip per action, a wedge
/// under the pointed slice and a hub that names the selection, leads back out of a submenu or
/// shows the brand mark. Pure drawing: the session decides what is highlighted.
/// </summary>
public sealed class RadialWheelView : Grid
{
    private const double Size = RadialLayout.PanelSize;
    private static readonly Point Center = new(Size / 2, Size / 2);

    private readonly RadialCatalog _catalog;
    private readonly bool _dark;
    private readonly bool _animates;
    private readonly Canvas _guides = new() { IsHitTestVisible = false };
    private readonly Path _wedge = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly RotateTransform _wedgeRotation = new(0, Size / 2, Size / 2);
    private readonly Canvas _ring = new();
    private readonly Grid _hubFace = new() { Width = RadialLayout.HubDiameter, Height = RadialLayout.HubDiameter, IsHitTestVisible = false };
    private readonly ScaleTransform _scale = new(1, 1, Size / 2, Size / 2);
    private readonly List<Chip> _chips = [];

    private List<RadialMenuItem> _items = [];
    private string? _backTitle;
    private int? _highlighted;
    private Color _color;
    private RadialNowPlayingState _nowPlaying = RadialNowPlayingState.NothingPlaying;
    private ImageSource? _nowPlayingIcon;
    private double _wedgeAngle;
    private bool _wedgeShown;
    private bool _open;

    /// <param name="dark">Theme of the surface (profile colors have per-theme values).</param>
    /// <param name="animates">False for previews and when Windows animations are off.</param>
    public RadialWheelView(RadialCatalog catalog, bool dark, bool animates)
    {
        _catalog = catalog;
        _dark = dark;
        _animates = animates;
        Width = Size;
        Height = Size;
        RenderTransform = _scale;
        SnapsToDevicePixels = true;

        Children.Add(BuildBackplate());
        Children.Add(_guides);
        _wedge.RenderTransform = _wedgeRotation;
        Children.Add(_wedge);
        Children.Add(_ring);
        Children.Add(BuildHub());
    }

    /// <summary>Accent for "accent" profiles: the theme's, read from the resources this view sits in.</summary>
    private Color AccentColor => TryFindResource("AccentColor") is Color c ? c
        : _dark ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x00, 0x7A, 0xFF);

    /// <summary>Shows a wheel level. <paramref name="backTitle"/> is null on the root wheel and the
    /// submenu's name (possibly empty) inside one.</summary>
    public void SetItems(IReadOnlyList<RadialMenuItem> items, string? backTitle, RadialMenuColor color)
    {
        _color = RadialColors.Resolve(color, _dark, AccentColor);
        _items = items.ToList();
        _backTitle = backTitle;
        _highlighted = null;
        _wedgeShown = false;
        _wedge.BeginAnimation(OpacityProperty, null);
        _wedge.Opacity = 0;
        BuildGuides();
        BuildChips();
        UpdateHub();
        if (_open) Bloom();
    }

    public void SetNowPlaying(RadialNowPlayingState state, ImageSource? icon)
    {
        _nowPlaying = state;
        _nowPlayingIcon = icon;
        for (var i = 0; i < _chips.Count; i++)
        {
            if (_items[i].MediaKey == RadialMediaKey.NowPlaying) RefreshChip(i);
        }
        UpdateHub();
    }

    public void SetHighlight(int? index)
    {
        if (index is { } i && (i < 0 || i >= _items.Count)) index = null;
        if (index == _highlighted) return;
        var previous = _highlighted;
        _highlighted = index;
        if (previous is { } p && p < _chips.Count) RefreshChip(p);
        if (index is { } n) RefreshChip(n);
        SyncWedge();
        UpdateHub();
    }

    /// <summary>The wheel grows into the screen and its chips are thrown out of the hub one after another.</summary>
    public void PlayOpen()
    {
        _open = true;
        if (!_animates)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }
        var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromSeconds(0.27)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Bloom();
    }

    /// <summary>Folds the chips back in and settles the wheel; <paramref name="done"/> runs when finished.</summary>
    public void PlayClose(Action done)
    {
        _open = false;
        if (!_animates)
        {
            done();
            return;
        }
        var shrink = new DoubleAnimation(0.9, TimeSpan.FromSeconds(0.13)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) => done();
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
        for (var i = 0; i < _chips.Count; i++)
        {
            var delay = TimeSpan.FromSeconds(RadialGeometry.OpenDelay(i, _chips.Count) * 0.25);
            var fold = new DoubleAnimation(0, TimeSpan.FromSeconds(0.1)) { BeginTime = delay };
            _chips[i].Root.BeginAnimation(OpacityProperty, fold);
        }
    }

    // ---------------------------------------------------------------- layers

    private UIElement BuildBackplate()
    {
        var d = RadialLayout.WheelDiameter;
        var disc = new Ellipse { Width = d, Height = d, Opacity = 0.97 };
        disc.SetResourceReference(Shape.FillProperty, "PanelBackground");
        disc.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 48,
            ShadowDepth = 8,
            Direction = 270,
            Opacity = _dark ? 0.55 : 0.22,
        };
        // Glass is glass because of its edge: lit at the top, gone by the bottom.
        var rim = new Ellipse
        {
            Width = d,
            Height = d,
            StrokeThickness = 1.2,
            Stroke = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb((byte)(_dark ? 70 : 230), 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6)],
                new Point(0.5, 0), new Point(0.5, 1)),
        };
        var border = new Ellipse { Width = d, Height = d, StrokeThickness = 0.8, Opacity = _dark ? 0.7 : 0.9 };
        border.SetResourceReference(Shape.StrokeProperty, "PanelBorder");
        var plate = new Grid { Width = d, Height = d, IsHitTestVisible = false };
        plate.Children.Add(disc);
        plate.Children.Add(rim);
        plate.Children.Add(border);
        return plate;
    }

    private void BuildGuides()
    {
        _guides.Children.Clear();
        var inner = RadialLayout.DeadZoneRadius + 7;
        var outer = RadialLayout.WheelDiameter / 2 - 7;
        foreach (var angle in RadialGeometry.GuideAngles(_items.Count))
        {
            var a = RadialGeometry.PointAt(angle, inner, Center);
            var b = RadialGeometry.PointAt(angle, outer, Center);
            var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 1, Opacity = _dark ? 0.09 : 0.07 };
            line.SetResourceReference(Shape.StrokeProperty, "TextPrimary");
            _guides.Children.Add(line);
        }
    }

    private UIElement BuildHub()
    {
        var plate = new Ellipse
        {
            Width = RadialLayout.HubDiameter,
            Height = RadialLayout.HubDiameter,
            Opacity = _dark ? 0.075 : 0.045,
        };
        plate.SetResourceReference(Shape.FillProperty, "TextPrimary");
        var hub = new Grid
        {
            Width = RadialLayout.HubDiameter,
            Height = RadialLayout.HubDiameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        hub.Children.Add(plate);
        hub.Children.Add(_hubFace);
        return hub;
    }

    private void UpdateHub()
    {
        _hubFace.Children.Clear();
        FrameworkElement face;
        if (_highlighted is { } index && index < _items.Count)
        {
            var label = Ui.Text(_catalog.DisplayName(_items[index], _nowPlaying), 11, FontWeights.SemiBold);
            label.TextAlignment = TextAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.MaxHeight = 3 * 14.5;
            label.LineHeight = 14;
            label.Margin = new Thickness(8, 0, 8, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            face = label;
        }
        else if (_backTitle is not null)
        {
            var chevron = Ui.Icon(Glyphs.ChevronRight, 12, "TextSecondary");
            chevron.RenderTransform = new ScaleTransform(-1, 1, 6, 6);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            var name = Ui.Text(_backTitle.Length == 0 ? RadialStrings.KindSubmenu : _backTitle, 9, FontWeights.Medium, "TextSecondary");
            name.TextAlignment = TextAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxHeight = 2 * 12.5;
            name.Margin = new Thickness(8, 0, 8, 0);
            var stack = Ui.Stack(3, chevron, name);
            stack.VerticalAlignment = VerticalAlignment.Center;
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            face = stack;
        }
        else
        {
            face = BrandMark(28);
        }
        _hubFace.Children.Add(face);
        if (_animates && _open)
        {
            face.Opacity = 0;
            face.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.14)));
        }
    }

    /// <summary>The Fieldkit mark (four tiles), in the text colour so it reads in both themes.</summary>
    public static FrameworkElement BrandMark(double width, string brush = "TextPrimary")
    {
        var gap = width * 0.12;
        var tile = (width - gap) / 2;
        var canvas = new Canvas { Width = width, Height = width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 2; col++)
        {
            var r = new Rectangle { Width = tile, Height = tile, RadiusX = tile * 0.3, RadiusY = tile * 0.3, Opacity = row == col ? 0.95 : 0.55 };
            r.SetResourceReference(Shape.FillProperty, brush);
            Canvas.SetLeft(r, col * (tile + gap));
            Canvas.SetTop(r, row * (tile + gap));
            canvas.Children.Add(r);
        }
        return canvas;
    }

    // ------------------------------------------------------------------ chips

    private sealed class Chip
    {
        public required Grid Root { get; init; }
        public required Ellipse Disc { get; init; }
        public required Grid IconHost { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Offset { get; init; }
        public required DropShadowEffect Shadow { get; init; }
    }

    private void BuildChips()
    {
        _ring.Children.Clear();
        _chips.Clear();
        var s = RadialLayout.ChipSize;
        for (var i = 0; i < _items.Count; i++)
        {
            var disc = new Ellipse { Width = s, Height = s, StrokeThickness = 0.8 };
            var shadow = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = _dark ? 0.45 : 0.16, Color = Colors.Black };
            disc.Effect = shadow;
            var iconHost = new Grid { Width = s, Height = s };
            var scale = new ScaleTransform(1, 1, s / 2, s / 2);
            var offset = new TranslateTransform();
            var root = new Grid { Width = s, Height = s, RenderTransform = new TransformGroup { Children = { scale, offset } } };
            root.Children.Add(disc);
            root.Children.Add(iconHost);
            var c = RadialGeometry.ChipCenter(i, _items.Count, RadialLayout.RingRadius, Center);
            Canvas.SetLeft(root, c.X - s / 2);
            Canvas.SetTop(root, c.Y - s / 2);
            _ring.Children.Add(root);
            _chips.Add(new Chip { Root = root, Disc = disc, IconHost = iconHost, Scale = scale, Offset = offset, Shadow = shadow });
            RefreshChip(i);
        }
    }

    private void RefreshChip(int index)
    {
        var chip = _chips[index];
        var item = _items[index];
        var lit = _highlighted == index;
        if (lit)
        {
            chip.Disc.Fill = new SolidColorBrush(_color);
            chip.Disc.Stroke = Brushes.Transparent;
            // Pointed at, the chip's shadow takes the wheel's colour.
            chip.Shadow.Color = _color;
            chip.Shadow.Opacity = 0.45;
            chip.Shadow.BlurRadius = 26;
            chip.Shadow.ShadowDepth = 4;
        }
        else
        {
            chip.Disc.SetResourceReference(Shape.FillProperty, _dark ? "ControlBackground" : "CardBackground");
            chip.Disc.SetResourceReference(Shape.StrokeProperty, "CardBorder");
            chip.Shadow.Color = Colors.Black;
            chip.Shadow.Opacity = _dark ? 0.45 : 0.16;
            chip.Shadow.BlurRadius = 10;
            chip.Shadow.ShadowDepth = 2;
        }
        chip.IconHost.Children.Clear();
        chip.IconHost.Children.Add(RadialIcons.Visual(item, _catalog, 34, 20, lit, _nowPlayingIcon));
        var target = lit ? 1.14 : 1.0;
        if (_animates)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromSeconds(0.24)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            chip.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            chip.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        else
        {
            chip.Scale.ScaleX = chip.Scale.ScaleY = target;
        }
    }

    /// <summary>Chips fly out of the hub one after another, clockwise from twelve o'clock.</summary>
    private void Bloom()
    {
        if (!_animates) return;
        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            var (dx, dyUp) = RadialGeometry.UnitPosition(i, _chips.Count);
            var travel = RadialLayout.RingRadius - RadialLayout.DeadZoneRadius * 0.2;
            var delay = TimeSpan.FromSeconds(RadialGeometry.OpenDelay(i, _chips.Count));
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromSeconds(0.36);
            chip.Offset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-dx * travel, 0, duration) { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
            chip.Offset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dyUp * travel, 0, duration) { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
            chip.Root.Opacity = 0;
            chip.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)) { BeginTime = delay });
        }
    }

    // ------------------------------------------------------------------ wedge

    private void SyncWedge()
    {
        var count = _items.Count;
        if (count == 0 || _highlighted is not { } index)
        {
            if (!_wedgeShown) return;
            _wedgeShown = false;
            FadeWedge(0);
            return;
        }
        var (center, width) = RadialGeometry.SliceAngles(index, count);
        _wedge.Data = WedgeGeometry(width, RadialLayout.DeadZoneRadius, RadialLayout.WheelDiameter / 2 - 4, Center);
        _wedge.Fill = WedgeBrush();
        if (!_wedgeShown)
        {
            // Nothing to sweep from: the highlight simply appears under the pointer that armed it.
            _wedgeAngle = center;
            _wedgeRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _wedgeRotation.Angle = Degrees(center);
            _wedgeShown = true;
            FadeWedge(1);
            return;
        }
        _wedgeAngle = RadialGeometry.ShortestTarget(_wedgeAngle, center);
        if (_animates)
        {
            _wedgeRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(Degrees(_wedgeAngle), TimeSpan.FromSeconds(0.2)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _wedgeRotation.Angle = Degrees(_wedgeAngle);
        }
    }

    private void FadeWedge(double to)
    {
        if (_animates) _wedge.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.12)));
        else _wedge.Opacity = to;
    }

    /// <summary>Strongest out at the chip, almost gone by the hub: the wedge points outward, the way the gesture does.</summary>
    private Brush WedgeBrush() => new RadialGradientBrush(
        [
            new GradientStop(Color.FromArgb((byte)(255 * (_dark ? 0.08 : 0.05)), _color.R, _color.G, _color.B), RadialLayout.DeadZoneRadius / (RadialLayout.WheelDiameter / 2)),
            new GradientStop(Color.FromArgb((byte)(255 * (_dark ? 0.30 : 0.20)), _color.R, _color.G, _color.B), 1),
        ])
    {
        MappingMode = BrushMappingMode.Absolute,
        Center = Center,
        GradientOrigin = Center,
        RadiusX = RadialLayout.WheelDiameter / 2,
        RadiusY = RadialLayout.WheelDiameter / 2,
    };

    /// <summary>An annular sector centred on 12 o'clock (rotated into place by the caller).</summary>
    public static Geometry WedgeGeometry(double sliceAngle, double innerRadius, double outerRadius, Point center)
    {
        var half = Math.Min(sliceAngle, 2 * Math.PI - 0.0001) / 2;
        var large = sliceAngle > Math.PI;
        var figure = new PathFigure { StartPoint = RadialGeometry.PointAt(-half, innerRadius, center), IsClosed = true };
        figure.Segments.Add(new LineSegment(RadialGeometry.PointAt(-half, outerRadius, center), true));
        figure.Segments.Add(new ArcSegment(RadialGeometry.PointAt(half, outerRadius, center), new Size(outerRadius, outerRadius), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(RadialGeometry.PointAt(half, innerRadius, center), true));
        figure.Segments.Add(new ArcSegment(RadialGeometry.PointAt(-half, innerRadius, center), new Size(innerRadius, innerRadius), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static double Degrees(double radians) => radians * 180 / Math.PI;

    // ---------------------------------------------------------------- preview

    /// <summary>Sample wheel used by previews: media, tools, a folder, a link and a submenu.</summary>
    public static List<RadialMenuItem> SampleItems() =>
    [
        new(RadialItemKind.Media, "playPause"),
        new(RadialItemKind.Media, "nextTrack"),
        new(RadialItemKind.Url, "https://github.com"),
        new(RadialItemKind.Submenu) { Name = "Work", Children =
        [
            new(RadialItemKind.Shortcut, "Ctrl+Shift+T") { Name = "Reopen tab" },
            new(RadialItemKind.App, "C:\\Windows\\notepad.exe"),
            new(RadialItemKind.File, "C:\\Windows") { SymbolName = "folder.fill" },
            new(RadialItemKind.Url, "example.com") { SymbolName = "globe" },
            new(RadialItemKind.Shortcut, "Win+Shift+S") { SymbolName = "camera.fill", Name = "Snip" },
        ] },
        new(RadialItemKind.File, "~\\Downloads"),
        new(RadialItemKind.Media, "nowPlaying"),
        new(RadialItemKind.Shortcut, "Ctrl+Alt+Delete") { SymbolName = "lock.fill" },
        new(RadialItemKind.Media, "previousTrack"),
    ];

    /// <summary>
    /// Renders two frames side by side for review without opening the overlay: the root wheel with
    /// a highlighted slice, and its submenu open (hub leads back) with a child highlighted.
    /// </summary>
    public static void RenderPreview(string path, bool dark)
    {
        var catalog = new RadialCatalog([], [], _ => true);
        var items = SampleItems();
        var root = new RadialWheelView(catalog, dark, animates: false);
        root.SetItems(items, null, RadialMenuColor.Accent);
        root.SetNowPlaying(RadialNowPlayingState.Playing(new RadialNowPlayingSnapshot("Midnight City", "M83", null, null, null)), null);
        root.SetHighlight(2);

        var sub = new RadialWheelView(catalog, dark, animates: false);
        sub.SetItems(items[3].Children, items[3].Name, RadialMenuColor.Purple);
        sub.SetHighlight(4);
        var subIdle = new RadialWheelView(catalog, dark, animates: false);
        subIdle.SetItems(items[3].Children, items[3].Name, RadialMenuColor.Orange);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(root);
        row.Children.Add(sub);
        row.Children.Add(subIdle);
        PreviewRenderer.Render(row, path, Size * 3, Size, dark);
    }
}
