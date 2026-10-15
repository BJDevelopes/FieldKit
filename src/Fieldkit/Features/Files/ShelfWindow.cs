// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Interop;
using Fieldkit.UI;
using Path = System.IO.Path;

namespace Fieldkit.Features.Files;

/// <summary>
/// The floating shelf (upstream ShelfView + ShelfTilesView): a 304 px card with a header (title, item
/// count, Keep open, close), a 188 px tile area or the dashed "Drag items here" well, and a footer with
/// the hint, Share and the trash. Never takes focus. Accepts drops of files, pictures, links and text;
/// tiles drag back out. Hides itself after 5 s of idleness unless something holds it open.
/// </summary>
public sealed class ShelfWindow : FloatingWindow
{
    public const double PanelWidth = 304;
    public const double TileAreaHeight = 188;
    private const double TileWidth = 78;
    private const double TileHeight = 88;
    private const double TileSpacing = 10;
    private const double TileRowSpacing = 6;
    private const double TileInset = 4;
    private const double DragThreshold = 4;

    // Segoe Fluent Icons not in UI/Glyphs (verified with --render-glyph-range).
    private const string PinFilledGlyph = "\uE841";

    private readonly ShelfCollection _shelf;
    private readonly bool _isPreview;
    private readonly Grid _root = new();
    private readonly TextBlock _title;
    private readonly ContentControl _countHost = new();
    private readonly Border _pinButton;
    private readonly TextBlock _pinIcon;
    private readonly ContentControl _tilesHost = new() { Height = TileAreaHeight };
    private readonly Grid _footer = new() { MinHeight = 30 };
    private readonly Border _shareButton;
    private readonly Border _trashButton;
    private readonly DispatcherTimer _autoHide = new() { Interval = ShelfRules.AutoHideDelay };
    private readonly Dictionary<string, ImageSource?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _hovered = [];

    private bool _pinned;
    private bool _pointerInside;
    private bool _dropTargeted;
    private int _interactions;
    private bool _internalDrag;
    private bool _fading;
    private Point? _pressPoint;
    private Guid? _pressItem;

    public ShelfWindow(ShelfCollection shelf, bool isPreview = false) : base(noActivate: true, cornerRadius: 18)
    {
        _shelf = shelf;
        _isPreview = isPreview;
        Title = "Shelf";
        SizeToContent = SizeToContent.WidthAndHeight;
        HideOnDeactivate = false;
        CloseOnEscape = false;
        AllowDrop = true;
        Card.Width = PanelWidth;

        _title = Ui.Text("Shelf", 12, FontWeights.SemiBold, wrap: false);
        var trayIcon = Ui.Icon(Glyphs.Tray, 12, "TextSecondary");
        var titleRow = Ui.Row(7, trayIcon, _title, _countHost);
        _pinIcon = Ui.Icon(Glyphs.Pin, 12, "TextSecondary");
        _pinButton = CircleButton(_pinIcon, TogglePin);
        var close = CircleButton(Ui.Icon(Glyphs.Close, 12, "TextSecondary"), CloseClicked);
        close.ToolTip = "Close";
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titleRow);
        Grid.SetColumn(_pinButton, 1);
        header.Children.Add(_pinButton);
        _pinButton.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(close, 2);
        close.Margin = new Thickness(7, 0, 0, 0);
        header.Children.Add(close);
        // The header is the move handle (upstream WindowMoveHandle); tiles stay free for item drags.
        titleRow.Background = Brushes.Transparent;
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
            NoteInteraction();
        };

        var hint = Ui.Text("Click to select. Drag out to use or right-click for more actions.", 10, brush: "TextTertiary");
        hint.VerticalAlignment = VerticalAlignment.Center;
        _shareButton = FooterButton(Glyphs.Share, "Share", "Accent", ShareAll);
        _trashButton = FooterButton(Glyphs.Delete, "Clear all", "Red", Trash);
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.Children.Add(hint);
        Grid.SetColumn(_shareButton, 1);
        _shareButton.Margin = new Thickness(8, 0, 0, 0);
        _footer.Children.Add(_shareButton);
        Grid.SetColumn(_trashButton, 2);
        _trashButton.Margin = new Thickness(8, 0, 0, 0);
        _footer.Children.Add(_trashButton);

        _root.Margin = new Thickness(13);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(header);
        Grid.SetRow(_tilesHost, 1);
        _tilesHost.Margin = new Thickness(0, 11, 0, 0);
        _root.Children.Add(_tilesHost);
        Grid.SetRow(_footer, 2);
        _footer.Margin = new Thickness(0, 11, 0, 0);
        _root.Children.Add(_footer);
        Body = _root;

        MouseEnter += (_, _) => SetPointerInside(true);
        MouseLeave += (_, _) => SetPointerInside(false);
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        _autoHide.Tick += (_, _) => AutoHideIfIdle();
        _shelf.Changed += OnShelfChanged;
        Closed += (_, _) =>
        {
            _shelf.Changed -= OnShelfChanged;
            _autoHide.Stop();
        };
        Refresh();
    }

    /// <summary>Keep open: the shelf neither hides by itself nor closes after a drop.</summary>
    public bool IsPinned => _pinned;

    private bool HoldOpen =>
        ShelfRules.ShouldHoldOpen(_pinned, _shelf.Count, _pointerInside || PointerOverWindow(), _dropTargeted, _interactions > 0);

    // ------------------------------------------------------------ showing

    /// <summary>Shows the shelf centred under the pointer (upstream position(_:)), without taking focus.</summary>
    public void ShowAtPointer()
    {
        CancelFade();
        Native.GetCursorPos(out var cursor);
        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            Show();
        }
        UpdateLayout();
        var scale = ScaleAt(cursor.X, cursor.Y);
        var cardWidth = (ActualWidth - 2 * ShadowMargin) * scale;
        MoveCardTo((int)Math.Round(cursor.X - cardWidth / 2), (int)Math.Round(cursor.Y + 16 * scale));
        // Back on top of other always-on-top windows, still without taking focus.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        ScheduleAutoHide();
    }

    /// <summary>Hides and forgets the transient state (upstream hide()); items stay.</summary>
    public void HidePanel()
    {
        _autoHide.Stop();
        CancelFade();
        _pinned = false;
        _pointerInside = false;
        _dropTargeted = false;
        _interactions = 0;
        _hovered.Clear();
        RefreshPin();
        SetDropChrome(false);
        Hide();
    }

    public override void Dismiss() => HidePanel();

    /// <summary>The close button: empties the shelf first when "Clear when closed" is on.</summary>
    private void CloseClicked()
    {
        if (Settings.GetBool("shelf.clearOnClose", false)) _shelf.Clear();
        HidePanel();
    }

    private void TogglePin()
    {
        if (!IsVisible) return;
        _pinned = !_pinned;
        RefreshPin();
        NoteInteraction();
    }

    // ------------------------------------------------------------ auto hide

    private void NoteInteraction()
    {
        CancelFade();
        ScheduleAutoHide();
    }

    private void ScheduleAutoHide()
    {
        _autoHide.Stop();
        if (_isPreview || !IsVisible || HoldOpen) return;
        _autoHide.Start();
    }

    private void AutoHideIfIdle()
    {
        _autoHide.Stop();
        if (!IsVisible) return;
        if (HoldOpen)
        {
            ScheduleAutoHide();
            return;
        }
        _fading = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (!_fading) return;
            _fading = false;
            if (HoldOpen)
            {
                CancelFade();
                ScheduleAutoHide();
                return;
            }
            HidePanel();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void CancelFade()
    {
        _fading = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void SetPointerInside(bool inside)
    {
        _pointerInside = inside;
        if (inside)
        {
            CancelFade();
            _autoHide.Stop();
        }
        else
        {
            ScheduleAutoHide();
        }
    }

    private bool PointerOverWindow()
    {
        if (!IsVisible || Handle == IntPtr.Zero) return false;
        Native.GetCursorPos(out var p);
        return Native.GetWindowRect(Handle, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    private IDisposable BeginInteraction()
    {
        _interactions++;
        CancelFade();
        _autoHide.Stop();
        return new ActionDisposable(() =>
        {
            _interactions = Math.Max(0, _interactions - 1);
            ScheduleAutoHide();
        });
    }

    // ------------------------------------------------------------ drawing

    private void OnShelfChanged()
    {
        Refresh();
        NoteInteraction();
    }

    private void Refresh()
    {
        var items = _shelf.Items;
        var selected = _shelf.Selection.Count;
        _title.Text = selected == 0 ? "Shelf" : $"{selected} selected";
        _countHost.Content = items.Count == 0 ? null : CountBadge(items.Count);
        _footer.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _trashButton.ToolTip = selected == 0 ? "Clear all" : "Remove selected";
        _shareButton.IsEnabled = items.Count > 0 && ShelfShare.IsSupported;
        _shareButton.Opacity = _shareButton.IsEnabled ? 1 : 0.4;
        _tilesHost.Content = items.Count == 0 ? EmptyState() : Tiles(items);
        _hovered.RemoveWhere(id => _shelf.Find(id) is null);
        foreach (var path in _thumbnails.Keys.Where(p => items.All(i => !string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))).ToList())
            _thumbnails.Remove(path);
        RefreshPin();
    }

    private void RefreshPin()
    {
        _pinIcon.Text = _pinned ? PinFilledGlyph : Glyphs.Pin;
        _pinIcon.SetResourceReference(TextBlock.ForegroundProperty, _pinned ? "Accent" : "TextSecondary");
        _pinButton.SetResourceReference(Border.BackgroundProperty, _pinned ? "AccentSubtle" : "ControlBackground");
        _pinButton.SetResourceReference(Border.BorderBrushProperty, _pinned ? "Accent" : "ControlBorder");
        _pinButton.ToolTip = _pinned ? "Allow closing after use" : "Keep open";
    }

    private static Border CountBadge(int count)
    {
        var badge = Ui.Badge(count.ToString(System.Globalization.CultureInfo.CurrentCulture), foreground: "TextPrimary");
        if (badge.Child is TextBlock tb)
        {
            tb.FontWeight = FontWeights.Bold;
            tb.FontSize = 11;
        }
        badge.CornerRadius = new CornerRadius(9);
        badge.Padding = new Thickness(6, 0, 6, 1);
        return badge;
    }

    private static FrameworkElement EmptyState()
    {
        var dashes = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3.3],
            SnapsToDevicePixels = true,
        };
        dashes.SetResourceReference(Shape.StrokeProperty, "TextTertiary");
        var label = Ui.Text("Drag items here", 12, brush: "TextSecondary");
        label.TextAlignment = TextAlignment.Center;
        var stack = Ui.Stack(8, Ui.Icon(Glyphs.Download, 21, "TextSecondary"), label);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(dashes);
        grid.Children.Add(stack);
        return grid;
    }

    private FrameworkElement Tiles(IReadOnlyList<ShelfItem> items)
    {
        // Two full rows fit the 188 px area without scrolling (each tile carries its own bottom gap).
        var wrap = new WrapPanel { Margin = new Thickness(TileInset, 2, 0, 2 - TileRowSpacing) };
        foreach (var item in items) wrap.Children.Add(Tile(item));
        return new ScrollViewer
        {
            Content = wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    private FrameworkElement Tile(ShelfItem item)
    {
        var selected = _shelf.Selection.Contains(item.Id);
        var canvas = new Canvas { Width = TileWidth, Height = TileHeight };

        var well = new Border { Width = 64, Height = 50, CornerRadius = new CornerRadius(8), ClipToBounds = true };
        well.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        well.Child = TileFace(item);
        Canvas.SetLeft(well, 7);
        Canvas.SetTop(well, 6);
        canvas.Children.Add(well);

        var label = Ui.Text(ShelfRules.MiddleTruncate(item.Title.ReplaceLineEndings(" "), 26), 10, brush: "TextSecondary");
        label.Width = 72;
        label.MaxHeight = 27;
        label.TextAlignment = TextAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.LineHeight = 13;
        label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Canvas.SetLeft(label, 3);
        Canvas.SetTop(label, 59);
        canvas.Children.Add(label);

        if (selected)
        {
            var check = Ui.Icon(ClipboardHistoryWindow.CheckCircleGlyph, 16, "Accent");
            check.Width = 16;
            check.Height = 16;
            Canvas.SetLeft(check, 4);
            Canvas.SetTop(check, 4);
            canvas.Children.Add(check);
        }

        var hovered = _hovered.Contains(item.Id);
        FrameworkElement? pin = null;
        if (item.IsPinned)
        {
            pin = Ui.Icon(PinFilledGlyph, 12, "Accent");
            var badge = new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(8.5), Child = pin };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            pin = badge;
            Canvas.SetLeft(pin, 58);
            Canvas.SetTop(pin, 4);
            pin.Visibility = hovered ? Visibility.Hidden : Visibility.Visible;
            canvas.Children.Add(pin);
        }

        var remove = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            Child = Ui.Icon(Glyphs.Close, 7, "TextPrimary"),
            ToolTip = "Remove",
            Cursor = Cursors.Hand,
            Visibility = hovered ? Visibility.Visible : Visibility.Hidden,
        };
        remove.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundHover");
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(() => _shelf.Remove([item.Id]));
        };
        Canvas.SetLeft(remove, 58);
        Canvas.SetTop(remove, 4);
        canvas.Children.Add(remove);

        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(selected ? 2 : 0),
            Margin = new Thickness(0, 0, TileSpacing, TileRowSpacing),
            Child = canvas,
            ToolTip = ShelfRules.Tooltip(item),
            Background = Brushes.Transparent,
        };
        if (selected)
        {
            tile.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
            // The border draws inside the tile; keep the content where it was.
            canvas.Margin = new Thickness(-2);
        }
        tile.MouseEnter += (_, _) =>
        {
            _hovered.Add(item.Id);
            remove.Visibility = Visibility.Visible;
            if (pin is not null) pin.Visibility = Visibility.Hidden;
        };
        tile.MouseLeave += (_, _) =>
        {
            _hovered.Remove(item.Id);
            remove.Visibility = Visibility.Hidden;
            if (pin is not null) pin.Visibility = Visibility.Visible;
        };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _pressPoint = e.GetPosition(this);
            _pressItem = item.Id;
            NoteInteraction();
        };
        tile.MouseMove += (_, e) =>
        {
            if (_pressPoint is not { } start || _pressItem != item.Id || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) <= DragThreshold && Math.Abs(now.Y - start.Y) <= DragThreshold) return;
            _pressPoint = null;
            _pressItem = null;
            Ui.Guard(() => BeginItemDrag(tile, item));
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressItem != item.Id) return;
            _pressPoint = null;
            _pressItem = null;
            _shelf.Click(item.Id, extend: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        tile.ContextMenu = TileMenu(item);
        return tile;
    }

    private UIElement TileFace(ShelfItem item)
    {
        if (item.Kind == ShelfItemKind.Image || (item.Kind == ShelfItemKind.File && ShelfRules.IsImageFile(item.Path)))
        {
            if (Thumbnail(item.Path!) is { } thumb)
            {
                var img = new Image { Source = thumb, Stretch = Stretch.UniformToFill, Margin = new Thickness(4) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = img };
            }
        }
        if (item.Kind == ShelfItemKind.File && AppIcons.ForPath(item.Path) is { } icon)
        {
            var img = new Image { Source = icon, Width = 32, Height = 32, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        var glyph = item.Kind switch
        {
            ShelfItemKind.Text => Glyphs.Document,
            ShelfItemKind.Link => Glyphs.Link,
            ShelfItemKind.Image => Glyphs.Photo,
            _ when item.Path is not null && Directory.Exists(item.Path) => Glyphs.Folder,
            _ => Glyphs.Document,
        };
        return Ui.Icon(glyph, 22, "TextSecondary");
    }

    private ImageSource? Thumbnail(string path)
    {
        if (_thumbnails.TryGetValue(path, out var cached)) return cached;
        var thumb = File.Exists(path) ? ClipboardImageCodec.Thumbnail(path, 160) : null;
        _thumbnails[path] = thumb;
        return thumb;
    }

    private void SetDropChrome(bool targeted)
    {
        Card.BorderThickness = new Thickness(targeted ? 2 : 1);
        _root.Margin = new Thickness(targeted ? 12 : 13);
        if (targeted) Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else Card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
    }

    private static Border CircleButton(TextBlock icon, Action onClick)
    {
        var b = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Child = icon,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        b.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onClick);
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.8;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        return b;
    }

    private static Border FooterButton(string glyph, string tooltip, string hoverBrush, Action onClick)
    {
        var icon = Ui.Icon(glyph, 12, "TextSecondary");
        var b = new Border
        {
            Width = 42,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = icon,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
        };
        ToolTipService.SetShowOnDisabled(b, true);
        b.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        b.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        b.MouseEnter += (_, _) =>
        {
            if (!b.IsEnabled) return;
            icon.SetResourceReference(TextBlock.ForegroundProperty, hoverBrush);
            b.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundHover");
        };
        b.MouseLeave += (_, _) =>
        {
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            b.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (b.IsEnabled) Ui.Guard(onClick);
        };
        return b;
    }

    // ------------------------------------------------------------ actions

    private void Trash() => _shelf.RemoveSelectedOrClear();

    private void ShareAll() => Share(ShelfRules.SelectionOrEverything(_shelf.Items, _shelf.Selection));

    private void Share(IReadOnlyList<ShelfItem> items)
    {
        var living = LivingItems(items);
        if (living.Count == 0) return;
        NoteInteraction();
        if (!ShelfShare.Show(Handle, living)) Notifier.Show("Shelf", "Windows could not open the share sheet.", warning: true);
    }

    private ContextMenu TileMenu(ShelfItem item)
    {
        var menu = new ContextMenu();
        IDisposable? interaction = null;
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => Ui.Guard(action);
            return mi;
        }
        menu.Opened += (_, _) =>
        {
            interaction ??= BeginInteraction();
            menu.Items.Clear();
            var current = _shelf.Find(item.Id) ?? item;
            var targets = ShelfRules.ActionTargets(_shelf.Items, _shelf.Selection, current);
            var files = targets.Where(t => t.HoldsFile).ToList();
            var links = targets.Where(t => t.Kind == ShelfItemKind.Link).ToList();
            menu.Items.Add(Item(current.IsPinned ? "Unpin" : "Pin", () => _shelf.TogglePin(current.Id)));
            menu.Items.Add(new Separator());
            if (files.Count > 0 || links.Count > 0)
                menu.Items.Add(Item("Open", () => Open(files.Concat(links).ToList())));
            if (files.Count > 0)
                menu.Items.Add(Item("Open with…", () => OpenWith(files[0]), files.Count == 1 && File.Exists(files[0].Path)));
            menu.Items.Add(Item("Copy", () => Copy(targets)));
            if (ShelfShare.IsSupported) menu.Items.Add(Item("Share", () => Share(targets)));
            if (files.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Show in File Explorer", () => Reveal(files[0])));
            }
            menu.Items.Add(new Separator());
            var remove = Item(targets.Count == 1 ? "Remove" : $"Remove {targets.Count} items",
                () => _shelf.Remove(targets.Select(t => t.Id).ToList()));
            remove.SetResourceReference(ForegroundProperty, "Red");
            menu.Items.Add(remove);
        };
        menu.Closed += (_, _) =>
        {
            interaction?.Dispose();
            interaction = null;
        };
        menu.Items.Add(new MenuItem { Header = "Pin" }); // placeholder so the menu opens
        return menu;
    }

    private void Open(IReadOnlyList<ShelfItem> items)
    {
        foreach (var item in LivingItems(items))
        {
            var target = item.Kind == ShelfItemKind.Link ? item.Text : item.Path;
            if (string.IsNullOrEmpty(target)) continue;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Warn($"Opening a shelf item failed: {ex.Message}"); }
        }
    }

    private static void OpenWith(ShelfItem item)
    {
        if (item.Path is null) return;
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {item.Path}") { UseShellExecute = false })?.Dispose();
    }

    private static void Reveal(ShelfItem item)
    {
        if (item.Path is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true })?.Dispose();
    }

    private void Copy(IReadOnlyList<ShelfItem> items)
    {
        var living = LivingItems(items);
        if (living.Count == 0) return;
        var paths = living.Where(i => i.HoldsFile).Select(i => i.Path!).ToList();
        var text = ShelfRules.JoinedText(living);
        var image = living.Count == 1 && living[0].Kind == ShelfItemKind.Image ? living[0].Path : null;
        _ = ClipboardLane.Run(access =>
        {
            if (image is not null) access.WriteImage(File.ReadAllBytes(image), OwnWriteKind.Other);
            else if (paths.Count > 0) access.WriteFiles(paths, OwnWriteKind.Other);
            else if (text is not null) access.WriteText(text, OwnWriteKind.Other);
        });
    }

    /// <summary>The items whose payload still exists; items whose file is truly gone leave the shelf.</summary>
    private List<ShelfItem> LivingItems(IReadOnlyList<ShelfItem> items)
    {
        var living = new List<ShelfItem>();
        var dead = new List<Guid>();
        foreach (var item in items)
        {
            if (!item.HoldsFile || File.Exists(item.Path) || Directory.Exists(item.Path)) living.Add(item);
            else if (!ShelfStore.FileSurvives(item.Path!)) dead.Add(item.Id);
        }
        if (dead.Count > 0)
        {
            _shelf.Remove(dead);
            Notifier.Show("Shelf", dead.Count == 1 ? "The file no longer exists" : $"{dead.Count} files no longer exist");
        }
        return living;
    }

    // ------------------------------------------------------------ drag out

    private void BeginItemDrag(FrameworkElement source, ShelfItem item)
    {
        var current = _shelf.Find(item.Id);
        if (current is null) return;
        var living = LivingItems(ShelfRules.ActionTargets(_shelf.Items, _shelf.Selection, current));
        if (living.Count == 0) return;
        var data = BuildDataObject(living);
        DragDropEffects result;
        using (BeginInteraction())
        {
            _internalDrag = true;
            try { result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
            finally { _internalDrag = false; }
        }
        CompleteDrag(living, result != DragDropEffects.None);
    }

    private void CompleteDrag(IReadOnlyList<ShelfItem> dragged, bool accepted)
    {
        var removable = ShelfRules.RemovableAfterDrag(dragged);
        if (ShelfRules.ShouldRemoveAfterDrag(accepted, removable.Count, Settings.GetBool("shelf.removeAfterDrop", true)))
            _shelf.Remove(removable);
        if (ShelfRules.ShouldCloseAfterDrag(accepted, dragged.Count, Settings.GetBool("shelf.closeAfterDrop", false), _pinned))
            HidePanel();
    }

    /// <summary>
    /// Files go out as a file list, notes and links as text (a single link also as a URL), a single
    /// stored picture also as pixels. Always copy: the shelf never lets a destination move a file away.
    /// </summary>
    public static DataObject BuildDataObject(IReadOnlyList<ShelfItem> items)
    {
        var data = new DataObject();
        var paths = items.Where(i => i.HoldsFile).Select(i => i.Path!).ToArray();
        if (paths.Length > 0)
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths);
            data.SetFileDropList(list);
        }
        if (ShelfRules.JoinedText(items) is { } text)
        {
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
        }
        if (items.Count == 1 && items[0].Kind == ShelfItemKind.Link && items[0].Text is { } url)
            data.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes(url + "\0")));
        if (items.Count == 1 && items[0].Kind == ShelfItemKind.Image && items[0].Path is { } png
            && ClipboardImageCodec.Decode(File.ReadAllBytes(png)) is { } bitmap)
            data.SetImage(bitmap);
        return data;
    }

    // ------------------------------------------------------------ drop in

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var accept = !_internalDrag && CanAccept(e.Data);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        if (accept != _dropTargeted)
        {
            _dropTargeted = accept;
            SetDropChrome(accept);
            if (accept) NoteInteraction();
            else ScheduleAutoHide();
        }
        if (accept)
        {
            CancelFade();
            _autoHide.Stop();
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // DragLeave also bubbles up from child elements; only leaving the card counts.
        var p = e.GetPosition(Card);
        if (p.X >= 0 && p.Y >= 0 && p.X < Card.ActualWidth && p.Y < Card.ActualHeight) return;
        EndDropTarget();
    }

    private void EndDropTarget()
    {
        if (!_dropTargeted) return;
        _dropTargeted = false;
        SetDropChrome(false);
        ScheduleAutoHide();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        EndDropTarget();
        if (_internalDrag)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var added = false;
        Ui.Guard(() => added = Accept(e.Data));
        e.Effects = added ? DragDropEffects.Copy : DragDropEffects.None;
        NoteInteraction();
    }

    private bool CanAccept(IDataObject data)
    {
        var drop = ReadDrop(data);
        if (!ShelfRules.CanAccept(drop)) return false;
        var count = drop.Files is { Count: > 0 } files ? files.Count : 1;
        return ShelfRules.CanAdd(_shelf.Count, count);
    }

    /// <summary>Adds what a drop carries; false when there was nothing usable or no room.</summary>
    public bool Accept(IDataObject data)
    {
        var drop = ReadDrop(data);
        var items = ShelfRules.Classify(drop, _shelf.ShelvedPaths());
        if (items.Count == 0) return drop.Files is { Count: > 0 }; // only files already on the shelf
        if (!ShelfRules.CanAdd(_shelf.Count, items.Count))
        {
            System.Media.SystemSounds.Beep.Play();
            return false;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != ShelfItemKind.Image) continue;
            var png = ReadPng(data);
            var path = png is null ? null : ShelfStore.WriteImage(png);
            if (path is null) return false;
            items[i] = items[i] with { Path = path };
        }
        return _shelf.Add(items);
    }

    private static ShelfDropData ReadDrop(IDataObject data)
    {
        string[]? files = null;
        string? text = null;
        string? url = null;
        var hasBitmap = false;
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) files = data.GetData(DataFormats.FileDrop) as string[];
        }
        catch { /* the source went away */ }
        if (files is { Length: > 0 }) return new ShelfDropData(files);
        try
        {
            hasBitmap = data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(DataFormats.Bitmap);
        }
        catch { hasBitmap = false; }
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
                url = Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
        }
        catch { /* ignore */ }
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText)) text = data.GetData(DataFormats.UnicodeText) as string;
        }
        catch { /* ignore */ }
        return new ShelfDropData(null, text, url, hasBitmap);
    }

    /// <summary>The dropped picture as PNG bytes: a "PNG" stream, a DIB or a bitmap, whichever exists.</summary>
    private static byte[]? ReadPng(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png && png.Length > 0)
                return png.ToArray();
        }
        catch { /* fall through */ }
        try
        {
            if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib
                && ClipboardImageCodec.DibToPng(dib.ToArray()) is { } image)
                return image.Png;
        }
        catch { /* fall through */ }
        try
        {
            if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap) return ClipboardImageCodec.EncodePng(bitmap);
        }
        catch { /* nothing usable */ }
        return null;
    }

    // ------------------------------------------------------------ preview

    /// <summary>Renders the shelf with sample items (a file, a picture, a note, a link) to a PNG.</summary>
    public static void RenderPreview(string path, bool light = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "fieldkit-previews", "shelf-samples");
        Directory.CreateDirectory(folder);
        var document = Path.Combine(folder, "Quarterly report final version.txt");
        File.WriteAllText(document, "Sample");
        var picture = Path.Combine(folder, "Sunset.png");
        File.WriteAllBytes(picture, SamplePicture());
        var shelf = new ShelfCollection();
        var link = new ShelfItem { Kind = ShelfItemKind.Link, Text = "https://github.com/vorssaint/vorssaint-utils", Title = "github.com" };
        shelf.Add(
        [
            new ShelfItem { Kind = ShelfItemKind.File, Path = document, Title = ShelfRules.FileTitle(document) },
            new ShelfItem { Kind = ShelfItemKind.Image, Path = picture, Title = "Image", IsPinned = true },
            new ShelfItem { Kind = ShelfItemKind.Text, Text = "Meeting notes\nBring the slides", Title = "Meeting notes" },
            link,
        ]);
        shelf.Click(link.Id, extend: false);
        Render(path, shelf, light);
    }

    /// <summary>Renders the empty shelf ("Drag items here") to a PNG.</summary>
    public static void RenderEmptyPreview(string path, bool light = false) => Render(path, new ShelfCollection(), light);

    private static void Render(string path, ShelfCollection shelf, bool light)
    {
        var window = new ShelfWindow(shelf, isPreview: true) { Left = -20000, Top = -20000 };
        // PreviewRendering saves the card at its own size; without the shadow margin nothing is cut off.
        window.Card.Margin = new Thickness(0);
        PreviewRendering.ApplyTheme(window, light ? true : null);
        window.Show();
        window.Refresh();
        PreviewRendering.Save(window, path);
        window.Close();
    }

    private static byte[] SamplePicture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(255, 170, 90), Color.FromRgb(120, 60, 160), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, 160, 120));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 230, 120)), null, new Point(80, 78), 26, 26);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 30, 70)), null, new Rect(0, 86, 160, 34));
        }
        var bitmap = new RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ClipboardImageCodec.EncodePng(bitmap);
    }
}
