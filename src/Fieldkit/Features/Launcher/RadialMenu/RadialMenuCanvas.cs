// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>Callbacks of the visual editor.</summary>
public sealed record RadialCanvasActions(
    Action<RadialMenuItem> Select,
    Action<RadialMenuItem, RadialMenuItem> Reorder,
    Action<RadialMenuItem> Remove,
    Action<RadialMenuItem> OpenSubmenu,
    Action Back,
    Action Add,
    Action? Reset);

/// <summary>
/// The interactive wheel in Settings (upstream <c>RadialMenuVisualCanvas</c>): the real layout with
/// exact angles and colours on a dark stage. Click a chip to edit it, drag it onto another slice to
/// swap them, right-click for more, click the hub to leave a submenu (or add to an empty wheel).
/// The stage is dark in both themes, like upstream, so its colours are fixed.
/// </summary>
public sealed class RadialMenuCanvas : Border
{
    private const double StageHeight = 330;
    private const double Area = 300;
    private const double WheelDiameter = 250;
    private const double RingRadius = 92;
    private const double HubDiameter = 68;
    private const double DeadZoneRadius = 36;
    private const double ChipSize = 44;
    private static readonly Point Center = new(Area / 2, Area / 2);
    private static readonly Color StageColor = Color.FromRgb(0x14, 0x17, 0x1C);
    private static readonly Color ChipColor = Color.FromRgb(0x2E, 0x30, 0x38);
    private static readonly Color HubColor = Color.FromRgb(0x1F, 0x21, 0x29);

    private readonly RadialCatalog _catalog;
    private readonly RadialCanvasActions _actions;
    private readonly Canvas _canvas = new() { Width = Area, Height = Area, Background = Brushes.Transparent };
    private readonly Grid _header = new() { Margin = new Thickness(14, 10, 14, 0), Height = 22 };
    private readonly Grid _stage = new();

    private IReadOnlyList<RadialMenuItem> _items = [];
    private RadialMenuItem? _openSubmenu;
    private Color _color;
    private int? _hovered;
    private int? _pressed;
    private Point _pressPoint;
    private int? _dragging;
    private int? _target;
    private Point _dragPosition;

    public RadialMenuCanvas(RadialCatalog catalog, RadialCanvasActions actions)
    {
        _catalog = catalog;
        _actions = actions;
        Height = StageHeight;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(0.8);
        BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
        ClipToBounds = true;
        Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.2, Color = Colors.Black };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(_header);
        _canvas.HorizontalAlignment = HorizontalAlignment.Center;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        _canvas.Margin = new Thickness(0, -6, 0, 0);
        Grid.SetRow(_canvas, 1);
        layout.Children.Add(_canvas);
        _stage.Children.Add(layout);
        Child = _stage;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.LostMouseCapture += (_, _) => CancelDrag();
    }

    public void Update(IReadOnlyList<RadialMenuItem> items, RadialMenuColor color, RadialMenuItem? openSubmenu)
    {
        _items = items;
        _openSubmenu = openSubmenu;
        var accent = TryFindResource("AccentColor") is Color c ? c : Color.FromRgb(0x0A, 0x84, 0xFF);
        _color = RadialColors.Resolve(color, dark: true, accent);
        _hovered = null;
        CancelDrag();
        Background = StageBrush();
        BuildHeader();
        Redraw();
    }

    private Brush StageBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(StageColor), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        group.Children.Add(new GeometryDrawing(new RadialGradientBrush(Color.FromArgb(38, _color.R, _color.G, _color.B), Color.FromArgb(0, _color.R, _color.G, _color.B))
        {
            RadiusX = 0.35,
            RadiusY = 0.6,
        }, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        FrameworkElement left;
        if (_openSubmenu is { } submenu)
        {
            var chevron = Ui.Icon(Glyphs.ChevronRight, 11);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new ScaleTransform(-1, 1);
            chevron.Foreground = new SolidColorBrush(_color);
            var name = Ui.Text(_catalog.DisplayName(submenu), 12, FontWeights.Medium, wrap: false);
            name.Foreground = new SolidColorBrush(_color);
            left = Ui.Clickable(Ui.Row(4, chevron, name), _actions.Back, RadialStrings.BackButton);
        }
        else
        {
            var title = Ui.Text(RadialStrings.PageTitle.ToUpperInvariant(), 11, FontWeights.SemiBold, wrap: false);
            title.Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255));
            left = title;
        }
        left.HorizontalAlignment = HorizontalAlignment.Left;
        left.VerticalAlignment = VerticalAlignment.Center;
        _header.Children.Add(left);
        if (_actions.Reset is { } reset && _openSubmenu is null)
        {
            var text = Ui.Text(RadialStrings.ResetActionsButton, 12, wrap: false);
            text.Foreground = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255));
            var button = Ui.Clickable(text, () =>
            {
                var answer = MessageBox.Show(Window.GetWindow(this)!, RadialStrings.ResetActionsConfirmMessage,
                    RadialStrings.ResetActionsConfirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.OK) reset();
            }, RadialStrings.ResetActionsConfirm);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.VerticalAlignment = VerticalAlignment.Center;
            _header.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- drawing

    private sealed record ChipView(Grid Root, Ellipse Disc, FrameworkElement Icon);

    private readonly List<ChipView> _chipViews = [];
    private readonly Path _wedge = new() { IsHitTestVisible = false };
    private Grid? _hub;
    private Grid? _floating;

    /// <summary>Rebuilds every element (items, colour or level changed).</summary>
    private void Redraw()
    {
        _canvas.Children.Clear();
        _chipViews.Clear();
        var plate = new Ellipse
        {
            Width = WheelDiameter,
            Height = WheelDiameter,
            Fill = new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)),
            StrokeThickness = 0.8,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black },
            IsHitTestVisible = false,
        };
        Place(plate, Center, WheelDiameter);

        var count = _items.Count;
        foreach (var angle in RadialGeometry.GuideAngles(count))
        {
            var a = RadialGeometry.PointAt(angle, DeadZoneRadius, Center);
            var b = RadialGeometry.PointAt(angle, WheelDiameter / 2 - 2, Center);
            _canvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(23, 255, 255, 255)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }
        _canvas.Children.Add(_wedge);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var item = _items[i];
            var view = Chip(item);
            var chip = view.Root;
            chip.Cursor = Cursors.Hand;
            chip.ToolTip = _catalog.DisplayName(item);
            chip.MouseEnter += (_, _) =>
            {
                if (_dragging is not null || _hovered == index) return;
                _hovered = index;
                Refresh();
            };
            chip.MouseLeave += (_, _) =>
            {
                if (_dragging is not null || _hovered != index || _pressed is not null) return;
                _hovered = null;
                Refresh();
            };
            chip.MouseLeftButtonDown += (_, e) =>
            {
                _pressed = index;
                _pressPoint = e.GetPosition(_canvas);
                _canvas.CaptureMouse();
                e.Handled = true;
            };
            chip.ContextMenu = ChipMenu(item);
            Place(chip, RadialGeometry.ChipCenter(i, count, RingRadius, Center), ChipSize);
            _chipViews.Add(view);
        }

        _hub = Hub();
        Place(_hub, Center, HubDiameter);
        _floating = null;
        Refresh();
    }

    /// <summary>Updates highlight, drag and hub state in place (never rebuilds the chips under the pointer).</summary>
    private void Refresh()
    {
        var count = _items.Count;
        var active = _target ?? _hovered;
        if (active is { } idx && idx < count)
        {
            var (center, width) = RadialGeometry.SliceAngles(idx, count);
            _wedge.Data = RadialWheelView.WedgeGeometry(width, DeadZoneRadius, WheelDiameter / 2 - 2, Center);
            _wedge.Fill = new SolidColorBrush(Color.FromArgb((byte)(255 * (_target is not null ? 0.32 : 0.22)), _color.R, _color.G, _color.B));
            _wedge.RenderTransform = new RotateTransform(center * 180 / Math.PI, Center.X, Center.Y);
            _wedge.Visibility = Visibility.Visible;
        }
        else
        {
            _wedge.Visibility = Visibility.Collapsed;
        }

        for (var i = 0; i < _chipViews.Count; i++)
        {
            var view = _chipViews[i];
            var lit = _target == i || _hovered == i;
            view.Disc.Fill = new SolidColorBrush(lit ? _color : ChipColor);
            view.Disc.Stroke = new SolidColorBrush(lit ? Color.FromArgb(128, 255, 255, 255) : Color.FromArgb(46, 255, 255, 255));
            view.Disc.StrokeThickness = lit ? 1.4 : 0.8;
            view.Icon.Opacity = lit ? 1 : 0.9;
            view.Root.Opacity = _dragging == i ? 0.25 : 1;
            view.Root.RenderTransform = _hovered == i && _dragging != i
                ? new ScaleTransform(1.08, 1.08, ChipSize / 2, ChipSize / 2)
                : Transform.Identity;
        }

        if (_hub is not null)
        {
            if (_hub.Children.Count > 1) _hub.Children.RemoveAt(1);
            _hub.Children.Add(HubContent());
        }

        if (_dragging is { } dragged && dragged < count)
        {
            if (_floating is null)
            {
                var view = Chip(_items[dragged]);
                view.Disc.Fill = new SolidColorBrush(_color);
                _floating = view.Root;
                _floating.IsHitTestVisible = false;
                _floating.RenderTransform = new ScaleTransform(1.15, 1.15, ChipSize / 2, ChipSize / 2);
                _floating.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.6, Color = _color };
                _canvas.Children.Add(_floating);
            }
            Canvas.SetLeft(_floating, _dragPosition.X - ChipSize / 2);
            Canvas.SetTop(_floating, _dragPosition.Y - ChipSize / 2);
        }
        else if (_floating is not null)
        {
            _canvas.Children.Remove(_floating);
            _floating = null;
        }
    }

    private void Place(FrameworkElement element, Point center, double size)
    {
        Canvas.SetLeft(element, center.X - size / 2);
        Canvas.SetTop(element, center.Y - size / 2);
        _canvas.Children.Add(element);
    }

    private ChipView Chip(RadialMenuItem item)
    {
        var chip = new Grid { Width = ChipSize, Height = ChipSize, Background = Brushes.Transparent };
        var disc = new Ellipse
        {
            Fill = new SolidColorBrush(ChipColor),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        chip.Children.Add(disc);
        // On the dark stage glyphs are always light ("highlighted" picks the on-accent white).
        var icon = RadialIcons.Visual(item, _catalog, 26, 16, highlighted: true);
        chip.Children.Add(icon);
        return new ChipView(chip, disc, icon);
    }
    private ContextMenu ChipMenu(RadialMenuItem item)
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = RadialStrings.EditButton };
        edit.Click += (_, _) => _actions.Select(item);
        menu.Items.Add(edit);
        if (item.Kind == RadialItemKind.Submenu)
        {
            var open = new MenuItem { Header = RadialStrings.EditActionsButton };
            open.Click += (_, _) => _actions.OpenSubmenu(item);
            menu.Items.Add(open);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = RadialStrings.DeleteButton };
        remove.Click += (_, _) => _actions.Remove(item);
        menu.Items.Add(remove);
        return menu;
    }

    private Grid Hub()
    {
        var hub = new Grid { Width = HubDiameter, Height = HubDiameter, Background = Brushes.Transparent };
        hub.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(HubColor),
            Stroke = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255)),
            StrokeThickness = 0.8,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.3, Color = Colors.Black },
        });
        hub.Children.Add(HubContent());
        if (_openSubmenu is not null || _items.Count == 0)
        {
            hub.Cursor = Cursors.Hand;
            hub.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_openSubmenu is not null) _actions.Back();
                else _actions.Add();
            };
        }
        return hub;
    }

    private FrameworkElement HubContent()
    {
        var white = new SolidColorBrush(Colors.White);
        var dim = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255));
        TextBlock Label(string text, double size, FontWeight weight, Brush brush, int lines)
        {
            var t = Ui.Text(text, size, weight);
            t.Foreground = brush;
            t.TextAlignment = TextAlignment.Center;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxHeight = lines * (size + 3.5);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }
        StackPanel Centered(params UIElement[] children)
        {
            var s = Ui.Stack(2, children);
            s.VerticalAlignment = VerticalAlignment.Center;
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.Margin = new Thickness(5, 0, 5, 0);
            s.IsHitTestVisible = false;
            return s;
        }
        TextBlock Glyph(string glyph, double size)
        {
            var g = Ui.Icon(glyph, size);
            g.Foreground = new SolidColorBrush(_color);
            return g;
        }

        if (_target is { } target && target < _items.Count)
            return Centered(Glyph(Glyphs.ArrowsLeftRight, 13), Label(_catalog.DisplayName(_items[target]), 9, FontWeights.SemiBold, white, 2));
        if (_hovered is { } hovered && hovered < _items.Count)
        {
            var item = _items[hovered];
            return Centered(Label(_catalog.DisplayName(item), 10, FontWeights.SemiBold, white, 2),
                Label(RadialStrings.KindTitle(item.Kind), 8, FontWeights.Medium, new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)), 2));
        }
        if (_openSubmenu is not null)
        {
            var chevron = Glyph(Glyphs.ChevronRight, 12);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new ScaleTransform(-1, 1);
            return Centered(chevron, Label(RadialStrings.BackButton, 9, FontWeights.Medium, dim, 1));
        }
        if (_items.Count == 0)
            return Centered(Glyph(Glyphs.Add, 14), Label(RadialStrings.AddButton, 8, FontWeights.Medium, dim, 2));
        var mark = RadialWheelView.BrandMark(24, "TextOnAccent");
        mark.Opacity = 0.85;
        mark.IsHitTestVisible = false;
        return mark;
    }

    // ------------------------------------------------------------------ input

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        if (_dragging is null && (p - _pressPoint).Length <= 6) return;
        _dragging = pressed;
        _dragPosition = p;
        _target = RadialGeometry.DropSlot(p, Center, _items.Count);
        Refresh();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressed;
        var dragging = _dragging;
        var target = _target;
        _pressed = null;
        _dragging = null;
        _target = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        if (pressed is not { } from || from >= _items.Count) return;
        if (dragging is null)
        {
            _actions.Select(_items[from]);
        }
        else if (target is { } to && to != from && to < _items.Count)
        {
            _actions.Reorder(_items[from], _items[to]);
        }
        else
        {
            Refresh();
        }
    }

    private void CancelDrag()
    {
        if (_dragging is null && _pressed is null) return;
        _pressed = null;
        _dragging = null;
        _target = null;
        Refresh();
    }

    /// <summary>Preview of the canvas (hovering the third chip) for review (STA tests).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var none = new RadialCanvasActions(_ => { }, (_, _) => { }, _ => { }, _ => { }, () => { }, () => { }, () => { });
        var catalog = new RadialCatalog([], [], _ => true);
        var items = RadialWheelView.SampleItems();
        var root = new RadialMenuCanvas(catalog, none) { Width = 420 };
        root.Update(items, RadialMenuColor.Accent, null);
        root._hovered = 2;
        root.Refresh();
        var sub = new RadialMenuCanvas(catalog, none) { Width = 420, Margin = new Thickness(20, 0, 0, 0) };
        sub.Update(items[3].Children, RadialMenuColor.Purple, items[3]);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20) };
        row.Children.Add(root);
        row.Children.Add(sub);
        PreviewRenderer.Render(row, path, 900, 380, dark);
    }
}
