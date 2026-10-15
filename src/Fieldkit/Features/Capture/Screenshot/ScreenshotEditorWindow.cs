// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// The screenshot editor (upstream ScreenshotEditorView): arrows, shapes, pen, highlighter, text,
/// stickers, numbered steps, pixelate/blur/erase, solid blocks, crop, background and watermark;
/// then copy, save, pin, share or open with another app.
/// </summary>
public sealed class ScreenshotEditorWindow : Window
{
    private static readonly List<ScreenshotEditorWindow> OpenEditors = [];

    private RasterImage _base;
    private RasterImage _redacted;
    private BitmapSource _redactedSource;
    private List<Annotation> _marks = [];
    private readonly Stack<(RasterImage Base, List<Annotation> Marks)> _undo = new();
    private readonly Stack<(RasterImage Base, List<Annotation> Marks)> _redo = new();
    private readonly EditorCanvas _canvas;
    private readonly Canvas _overlay = new();
    private readonly Grid _stage = new();
    private readonly StackPanel _toolRail = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _inspector = new();
    private AnnotationTool _tool;
    private AnnotationColor _color;
    private StrokeSize _stroke;
    private int _textSize;
    private int _blurLevel;
    private BlurStyle _blurStyle;
    private ArrowStyle _arrowStyle;
    private Sticker _sticker;
    private Annotation? _selected;
    private Annotation? _draft;
    private Point _dragStart;
    private Point _lastPoint;
    private bool _dragging;
    private Rect? _crop;
    private TextBox? _textEditor;
    private Annotation? _editingText;
    private bool _unsaved = true;

    internal BackdropStyle Backdrop { get; private set; }
    internal WatermarkStyle Watermark { get; private set; }
    internal bool Shadows => Settings.GetBool(CaptureSettings.Shadows, false);

    public static void Open(RasterImage image)
    {
        var editor = new ScreenshotEditorWindow(image);
        OpenEditors.Add(editor);
        editor.Show();
        editor.Activate();
    }

    public static void CloseAll()
    {
        foreach (var e in OpenEditors.ToList())
        {
            e._unsaved = false;
            e.Close();
        }
    }

    private ScreenshotEditorWindow(RasterImage image)
    {
        _base = image;
        _redacted = image;
        _redactedSource = image.ToBitmapSource();
        _tool = ParseEnum(Settings.GetString(CaptureSettings.LastTool, "arrow"), AnnotationTool.Arrow);
        if (_tool == AnnotationTool.Crop) _tool = AnnotationTool.Arrow;
        _color = ParseEnum(Settings.GetString(CaptureSettings.LastColor, "red"), AnnotationColor.Red);
        _stroke = ParseEnum(Settings.GetString(CaptureSettings.LastStroke, "medium"), StrokeSize.Medium);
        _textSize = AnnotationMath.SanitizedTextSize(Settings.GetInt(CaptureSettings.LastTextSize, AnnotationMath.DefaultTextSize));
        _blurLevel = Math.Max(AnnotationMath.SanitizedBlurLevel(Settings.GetInt(CaptureSettings.LastBlurLevel, 3)), 3);
        _blurStyle = ParseEnum(Settings.GetString(CaptureSettings.LastBlurStyle, "pixelate"), BlurStyle.Pixelate);
        _arrowStyle = ParseEnum(Settings.GetString(CaptureSettings.LastArrowStyle, "filled"), ArrowStyle.Filled);
        _sticker = ParseEnum(Settings.GetString(CaptureSettings.LastSticker, "check"), Sticker.Check);
        Backdrop = BackdropStyle.Parse(Settings.GetString(CaptureSettings.Backdrop, string.Empty));
        Watermark = WatermarkStyle.Parse(Settings.GetString(CaptureSettings.Watermark, string.Empty));

        Title = "Screenshot";
        Width = 1120;
        Height = 780;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        UseLayoutRounding = true;

        _canvas = new EditorCanvas(this);
        _stage.Children.Add(_canvas);
        _stage.Children.Add(_overlay);
        var viewbox = new Viewbox { Child = _stage, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(24) };
        var stageHost = new Border { Child = viewbox, ClipToBounds = true };
        stageHost.SetResourceReference(Border.BackgroundProperty, "SidebarBackground");
        RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.HighQuality);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = BuildTopBar();
        root.Children.Add(top);
        var inspectorHost = new Border { Child = _inspector, Padding = new Thickness(14, 6, 14, 8), MinHeight = 44 };
        Grid.SetRow(inspectorHost, 1);
        root.Children.Add(inspectorHost);
        Grid.SetRow(stageHost, 2);
        root.Children.Add(stageHost);
        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        Content = root;

        UpdateStageSize();
        RebuildToolRail();
        RebuildInspector();

        _canvas.MouseLeftButtonDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseLeftButtonUp += OnCanvasUp;
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            OpenEditors.Remove(this);
            ShareSheet.Forget(new WindowInteropHelper(this).Handle);
        };
    }

    private static T ParseEnum<T>(string raw, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(raw, true, out var v) && Enum.IsDefined(v) ? v : fallback;

    private static string Camel<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    // ---------------------------------------------------------------- layout

    internal double ImageOffset => Backdrop.IsVisible ? DecorationMath.Padding(_base.Width, _base.Height, Backdrop.Padding) : 0;

    private void UpdateStageSize()
    {
        var size = DecorationMath.Canvas(_base.Width, _base.Height, Backdrop);
        _stage.Width = size.Width;
        _stage.Height = size.Height;
        _canvas.InvalidateVisual();
    }

    private Point ImagePoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        return new Point(p.X - ImageOffset, p.Y - ImageOffset);
    }

    internal void Draw(DrawingContext dc)
    {
        // A spotlight being dragged already darkens the picture around it.
        IReadOnlyList<Annotation> marks = _draft is { Tool: AnnotationTool.Spotlight } spot ? [.. _marks, spot] : _marks;
        ScreenshotRenderer.DrawComposition(dc, _redactedSource, _base.Width, _base.Height, marks, _base.Scale, Shadows, Backdrop, Watermark, DrawOverlay);
    }

    private void DrawOverlay(DrawingContext dc)
    {
        var scale = Math.Max(1, _base.Scale);
        if (_draft is not null)
        {
            if (_draft.Tool == AnnotationTool.Pixelate)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 120, 120, 120)), new Pen(Brushes.White, 1 * scale) { DashStyle = DashStyles.Dash }, _draft.Bounds);
            else if (_draft.Tool == AnnotationTool.CopyText)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 46, 140, 255)), new Pen(new SolidColorBrush(Color.FromRgb(46, 140, 255)), 1.5 * scale) { DashStyle = DashStyles.Dash }, _draft.Bounds);
            else
                ScreenshotRenderer.DrawAnnotation(dc, _draft, _base.Scale, Shadows);
        }
        if (_selected is not null && _marks.Contains(_selected) && _editingText != _selected)
        {
            var r = AnnotationMath.Extent(_selected);
            r.Inflate(4 * scale, 4 * scale);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(46, 140, 255)), 1.5 * scale) { DashStyle = DashStyles.Dash }, r);
        }
        if (_crop is { } crop)
        {
            var full = new Rect(0, 0, _base.Width, _base.Height);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(crop)));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1.5 * scale), crop);
        }
    }

    // --------------------------------------------------------------- toolbar

    private FrameworkElement BuildTopBar()
    {
        var undo = Ui.IconButton(Glyphs.Undo, "Undo (Ctrl+Z)", Undo, 15, "TextPrimary");
        var redo = Ui.IconButton("\uE7A6", "Redo (Ctrl+Y)", Redo, 15, "TextPrimary");
        var background = Ui.Button("Background", ShowBackgroundPopup, glyph: Glyphs.Wallpaper);
        var watermark = Ui.Button("Watermark", ShowWatermarkPopup, glyph: "\uE8D2");
        var right = Ui.Row(6, undo, redo, background, watermark);
        var bar = Ui.Split(new ScrollViewer
        {
            Content = _toolRail,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }, right);
        bar.Margin = new Thickness(12, 10, 12, 4);
        return bar;
    }

    public static string ToolGlyph(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => Glyphs.Pointer,
        AnnotationTool.Arrow => "\uE72A",
        AnnotationTool.Pixelate => "\uED1A",
        AnnotationTool.Crop => "\uE7A8",
        AnnotationTool.Text => "\uE8D2",
        AnnotationTool.Sticker => Glyphs.Smile,
        AnnotationTool.Rect => "\uE739",
        AnnotationTool.Highlight => "\uE7E6",
        AnnotationTool.Freehand => "\uEE56",
        AnnotationTool.Line => "\uE738",
        AnnotationTool.Ellipse => "\uEA3A",
        AnnotationTool.Counter => "\uF146",
        AnnotationTool.Spotlight => "\uEA80", // Lightbulb
        AnnotationTool.CopyText => Glyphs.TextScan,
        _ => "\uE73B",
    };

    public static string ToolTitle(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => "Select",
        AnnotationTool.Arrow => "Arrow",
        AnnotationTool.Pixelate => "Blur",
        AnnotationTool.Crop => "Crop",
        AnnotationTool.Text => "Text",
        AnnotationTool.Sticker => "Sticker",
        AnnotationTool.Rect => "Rectangle",
        AnnotationTool.Highlight => "Highlighter",
        AnnotationTool.Freehand => "Pen",
        AnnotationTool.Line => "Line",
        AnnotationTool.Ellipse => "Ellipse",
        AnnotationTool.Counter => "Number",
        AnnotationTool.Spotlight => "Spotlight",
        AnnotationTool.CopyText => "Copy text",
        _ => "Solid block",
    };

    private void RebuildToolRail()
    {
        _toolRail.Children.Clear();
        var orderRaw = Settings.GetString(CaptureSettings.ToolOrder, AnnotationMath.DefaultToolOrder);
        var shortcuts = Settings.GetBool(CaptureSettings.ToolShortcuts, true);
        foreach (var tool in AnnotationMath.OrderedTools(orderRaw))
        {
            var digit = AnnotationMath.DigitForTool(tool, orderRaw, shortcuts);
            var rb = new RadioButton
            {
                Content = ToolGlyph(tool),
                Tag = tool,
                IsChecked = tool == _tool,
                GroupName = "editorTools" + GetHashCode(),
                ToolTip = digit is null ? ToolTitle(tool) : $"{ToolTitle(tool)} ({digit})",
                Width = 38,
                Height = 34,
                Margin = new Thickness(0, 0, 2, 0),
            };
            rb.SetResourceReference(StyleProperty, "TabButton");
            var t = tool;
            rb.Checked += (_, _) => SelectTool(t);
            _toolRail.Children.Add(rb);
        }
    }

    private void SelectTool(AnnotationTool tool)
    {
        CommitText();
        if (_tool == tool) return;
        _tool = tool;
        if (tool != AnnotationTool.Crop) _crop = null;
        if (tool != AnnotationTool.Select) _selected = null;
        if (tool != AnnotationTool.Crop) Settings.Set(CaptureSettings.LastTool, Camel(tool));
        foreach (var rb in _toolRail.Children.OfType<RadioButton>())
            rb.IsChecked = rb.Tag is AnnotationTool t && t == tool;
        _canvas.Cursor = tool == AnnotationTool.Select ? Cursors.Arrow : tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        RebuildInspector();
        _canvas.InvalidateVisual();
    }

    // ------------------------------------------------------------- inspector

    private void RebuildInspector()
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Add(UIElement e)
        {
            if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 18, 2);
            row.Children.Add(e);
        }
        var tool = _selected?.Tool ?? _tool;
        var usesColor = tool is not (AnnotationTool.Pixelate or AnnotationTool.Crop or AnnotationTool.Select or AnnotationTool.Sticker or AnnotationTool.Redact
            or AnnotationTool.Spotlight or AnnotationTool.CopyText);
        if (usesColor) Add(Labeled("Color", ColorSwatches()));
        if (tool is AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Rect or AnnotationTool.Ellipse or AnnotationTool.Freehand)
            Add(Labeled("Thickness", StrokePicker()));
        if (tool == AnnotationTool.Arrow) Add(Labeled("Arrow style", ArrowStylePicker()));
        if (tool is AnnotationTool.Text or AnnotationTool.Counter) Add(Labeled("Font size", TextSizePicker()));
        if (tool == AnnotationTool.Pixelate)
        {
            Add(Labeled("Blur style", BlurStylePicker()));
            Add(Labeled("Blur strength", BlurStrengthPicker()));
        }
        if (tool == AnnotationTool.Sticker) Add(Labeled("Sticker", StickerPicker()));
        if (tool == AnnotationTool.Crop)
        {
            Add(Ui.Caption("Drag over the part to keep, then press Crop."));
            var apply = Ui.Button("Crop", ApplyCrop, accent: true);
            apply.IsEnabled = _crop is not null;
            Add(apply);
            Add(Ui.Button("Cancel", () =>
            {
                _crop = null;
                SelectTool(AnnotationTool.Arrow);
            }));
        }
        if (tool == AnnotationTool.Spotlight) Add(Ui.Caption("Drag over what matters; everything else is dimmed. Draw more than one to light several areas."));
        if (tool == AnnotationTool.CopyText) Add(Ui.Caption("Drag over the text you want and it is copied. Blurred areas stay hidden."));
        if (tool is not (AnnotationTool.Crop or AnnotationTool.Pixelate or AnnotationTool.Select or AnnotationTool.Spotlight or AnnotationTool.CopyText))
        {
            var shadows = new CheckBox { Content = Ui.Text("Shadows", 13), IsChecked = Shadows, VerticalAlignment = VerticalAlignment.Center };
            shadows.Click += (_, _) =>
            {
                Settings.Set(CaptureSettings.Shadows, shadows.IsChecked == true);
                _canvas.InvalidateVisual();
            };
            Add(shadows);
        }
        if (_selected is not null)
        {
            Add(Ui.Button("Bring forward", () => Reorder(+1)));
            Add(Ui.Button("Send backward", () => Reorder(-1)));
            Add(Ui.IconButton(Glyphs.Delete, "Delete", DeleteSelected, 14, "TextPrimary"));
        }
        if (row.Children.Count == 0) Add(Ui.Caption(tool == AnnotationTool.Select ? "Click a mark to move it. Delete removes it." : ToolTitle(tool)));
        _inspector.Content = row;
    }

    private static FrameworkElement Labeled(string label, UIElement control)
    {
        var text = Ui.Text(label, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(8, text, control);
    }

    private FrameworkElement ColorSwatches()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Enum.GetValues<AnnotationColor>())
        {
            var color = c;
            var selected = (_selected?.Color ?? _color) == c;
            var dot = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ScreenshotRenderer.ToColor(c)),
                BorderThickness = new Thickness(selected ? 3 : 1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = c.ToString(),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "CardBorder");
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _color = color;
                Settings.Set(CaptureSettings.LastColor, Camel(color));
                ChangeSelected(a => a.Color = color);
                RebuildInspector();
            };
            panel.Children.Add(dot);
        }
        return panel;
    }

    private FrameworkElement Segments<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> onPick) where T : struct, Enum
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var v = value;
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = EqualityComparer<T>.Default.Equals(value, current), Padding = new Thickness(10, 3, 10, 3) };
            rb.SetResourceReference(StyleProperty, "SegmentButton");
            rb.Checked += (_, _) => onPick(v);
            panel.Children.Add(rb);
        }
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return border;
    }

    private FrameworkElement StrokePicker() => Segments(
        [(StrokeSize.Small, "S"), (StrokeSize.Medium, "M"), (StrokeSize.Large, "L")], _selected?.Stroke ?? _stroke, v =>
        {
            _stroke = v;
            Settings.Set(CaptureSettings.LastStroke, Camel(v));
            ChangeSelected(a => a.Stroke = v);
        });

    private FrameworkElement BlurStylePicker() => Segments(
        [(BlurStyle.Pixelate, "Pixelate"), (BlurStyle.Blur, "Blur"), (BlurStyle.Erase, "Erase")], _selected?.BlurStyle ?? _blurStyle, v =>
        {
            _blurStyle = v;
            Settings.Set(CaptureSettings.LastBlurStyle, Camel(v));
            ChangeSelected(a => a.BlurStyle = v);
        });

    private FrameworkElement BlurStrengthPicker()
    {
        var slider = new Slider { Minimum = 1, Maximum = 5, Width = 120, IsSnapToTickEnabled = true, TickFrequency = 1, Value = _selected?.BlurLevel ?? _blurLevel };
        slider.ValueChanged += (_, e) =>
        {
            _blurLevel = (int)e.NewValue;
            Settings.Set(CaptureSettings.LastBlurLevel, _blurLevel);
            ChangeSelected(a => a.BlurLevel = _blurLevel);
        };
        return slider;
    }

    private FrameworkElement ArrowStylePicker()
    {
        var combo = new ComboBox { MinWidth = 130 };
        foreach (var (v, l) in new[] { (ArrowStyle.Filled, "Solid"), (ArrowStyle.Outline, "Outline"), (ArrowStyle.Open, "Open"), (ArrowStyle.DoubleEnded, "Double-ended"), (ArrowStyle.Scribbly, "Scribbly") })
            combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        var current = _selected?.ArrowStyle ?? _arrowStyle;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (ArrowStyle)i.Tag == current);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem item) return;
            _arrowStyle = (ArrowStyle)item.Tag;
            Settings.Set(CaptureSettings.LastArrowStyle, Camel(_arrowStyle));
            ChangeSelected(a => a.ArrowStyle = _arrowStyle);
        };
        return combo;
    }

    private FrameworkElement StickerPicker()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var s in Enum.GetValues<Sticker>())
        {
            var st = s;
            var selected = (_selected?.Sticker ?? _sticker) == s;
            var b = new Border
            {
                Child = new TextBlock { Text = AnnotationMath.StickerGlyph(s), FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center },
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(1, 0, 1, 0),
            };
            if (selected) b.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            b.MouseLeftButtonUp += (_, _) =>
            {
                _sticker = st;
                Settings.Set(CaptureSettings.LastSticker, Camel(st));
                ChangeSelected(a => a.Sticker = st);
                RebuildInspector();
            };
            panel.Children.Add(b);
        }
        return panel;
    }

    private FrameworkElement TextSizePicker()
    {
        var value = Ui.Text((_selected?.TextSize ?? _textSize).ToString(System.Globalization.CultureInfo.CurrentCulture), 13, FontWeights.SemiBold, wrap: false);
        value.MinWidth = 26;
        value.TextAlignment = TextAlignment.Center;
        void Step(bool up)
        {
            var current = _selected?.TextSize ?? _textSize;
            if (AnnotationMath.SteppedTextSize(current, up) is not { } next) return;
            _textSize = next;
            Settings.Set(CaptureSettings.LastTextSize, next);
            ChangeSelected(a =>
            {
                a.TextSize = next;
                if (a.Tool == AnnotationTool.Text) a.Bounds = TextBounds(a, a.Bounds.TopLeft);
                if (a.Tool == AnnotationTool.Counter)
                {
                    var d = AnnotationMath.CounterDiameter(next, _base.Scale);
                    var c = new Point(a.Bounds.X + a.Bounds.Width / 2, a.Bounds.Y + a.Bounds.Height / 2);
                    a.Bounds = new Rect(c.X - d / 2, c.Y - d / 2, d, d);
                }
            });
            if (_textEditor is not null) _textEditor.FontSize = next * Math.Max(1, _base.Scale);
            value.Text = next.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
        return Ui.Row(2, Ui.IconButton(Glyphs.Remove, "Smaller", () => Step(false), 12, "TextPrimary"), value,
            Ui.IconButton(Glyphs.Add, "Larger", () => Step(true), 12, "TextPrimary"));
    }

    // -------------------------------------------------------------- bottom bar

    private FrameworkElement BuildBottomBar()
    {
        var recent = Ui.Button("Recent captures", RecentCapturesWindow.Open, glyph: Glyphs.Clock);
        var copyText = Ui.Button("Copy text", () => ScreenTextService.Recognize(RenderResult(), fromEditor: true), glyph: Glyphs.TextScan);
        var openWith = Ui.Button("Open with\u2026", () => ScreenshotFlow.OpenWith(RenderResult()), glyph: "\uE8A7");
        var share = Ui.Button("Share", () => ShareSheet.ShareFile(new WindowInteropHelper(this).Handle, ScreenshotFlow.TemporaryFile(RenderResult()), "Screenshot"), glyph: Glyphs.Share);
        var pin = Ui.Button("Pin to screen", () => PinWindow.Pin(RenderResult()), glyph: Glyphs.Pin);
        var saveAs = Ui.Button("Save As\u2026", () =>
        {
            if (ScreenshotFlow.SaveAs(RenderResult(), this) is not null) _unsaved = false;
        });
        var save = Ui.Button("Save", Save, glyph: "\uE74E");
        var copy = Ui.Button("Copy", Copy, accent: true, glyph: Glyphs.Copy);
        var left = Ui.Row(8, recent, copyText, openWith);
        var right = Ui.Row(8, share, pin, saveAs, save, copy);
        var bar = Ui.Split(left, right);
        bar.Margin = new Thickness(14, 10, 14, 12);
        return bar;
    }

    private RasterImage RenderResult()
    {
        CommitText();
        return ScreenshotRenderer.Render(_base, _marks, Shadows, Backdrop, Watermark);
    }

    private void Copy()
    {
        if (!CaptureClipboard.CopyImage(RenderResult())) return;
        _unsaved = false;
        Hud.Show(Glyphs.Copy, "Screenshot copied");
    }

    private void Save()
    {
        var path = ScreenshotFlow.Save(RenderResult());
        if (path is null) return;
        _unsaved = false;
        ScreenshotFlow.ShowResultHud(path, false);
    }

    // ------------------------------------------------------------------ input

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        var p = ImagePoint(e);
        if (_textEditor is not null)
        {
            CommitText();
            if (_tool == AnnotationTool.Text) return;
        }
        _dragStart = p;
        _lastPoint = p;
        _dragging = true;
        _canvas.CaptureMouse();
        var scale = Math.Max(1, _base.Scale);
        switch (_tool)
        {
            case AnnotationTool.Select:
            {
                _selected = AnnotationMath.TopMostAt(_marks, p, 6 * scale);
                if (_selected is not null && e.ClickCount == 2 && _selected.Tool == AnnotationTool.Text)
                {
                    _dragging = false;
                    _canvas.ReleaseMouseCapture();
                    BeginTextEdit(_selected.Bounds.TopLeft, _selected);
                    return;
                }
                if (_selected is not null) PushUndo();
                RebuildInspector();
                break;
            }
            case AnnotationTool.Text:
            {
                _dragging = false;
                _canvas.ReleaseMouseCapture();
                var existing = AnnotationMath.TopMostAt(_marks.Where(m => m.Tool == AnnotationTool.Text).ToList(), p, 2);
                BeginTextEdit(existing?.Bounds.TopLeft ?? p, existing);
                return;
            }
            case AnnotationTool.Counter:
            {
                var d = AnnotationMath.CounterDiameter(_textSize, _base.Scale);
                PushUndo();
                _marks.Add(new Annotation
                {
                    Tool = AnnotationTool.Counter,
                    Bounds = new Rect(p.X - d / 2, p.Y - d / 2, d, d),
                    Color = _color,
                    TextSize = _textSize,
                    Number = AnnotationMath.NextCounterNumber(_marks),
                });
                MarkChanged();
                _dragging = false;
                _canvas.ReleaseMouseCapture();
                return;
            }
            default:
                _draft = new Annotation
                {
                    Tool = _tool,
                    Color = _color,
                    Stroke = _stroke,
                    TextSize = _textSize,
                    BlurLevel = _blurLevel,
                    BlurStyle = _blurStyle,
                    ArrowStyle = _arrowStyle,
                    Sticker = _sticker,
                    Bounds = new Rect(p, p),
                    Points = [p, p],
                };
                break;
        }
        _canvas.InvalidateVisual();
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = ImagePoint(e);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_tool == AnnotationTool.Select)
        {
            if (_selected is not null)
            {
                _selected.Translate(p.X - _lastPoint.X, p.Y - _lastPoint.Y);
                _lastPoint = p;
                if (_selected.Tool == AnnotationTool.Pixelate) RefreshRedactions();
                _canvas.InvalidateVisual();
            }
            return;
        }
        if (_tool == AnnotationTool.Crop)
        {
            var r = AnnotationMath.ClampToImage(AnnotationMath.NormalizedRect(_dragStart, p), _base.Width, _base.Height);
            _crop = r.IsEmpty ? null : r;
            _canvas.InvalidateVisual();
            return;
        }
        if (_draft is null) return;
        switch (_draft.Tool)
        {
            case AnnotationTool.Freehand:
                if ((p - _lastPoint).Length >= 1.5)
                {
                    _draft.Points.Add(p);
                    _lastPoint = p;
                }
                break;
            case AnnotationTool.Arrow or AnnotationTool.Line:
            {
                var end = p;
                if (shift)
                {
                    // Snap to 45° steps.
                    var v = p - _dragStart;
                    var angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                    end = _dragStart + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
                }
                _draft.Points = [_dragStart, end];
                break;
            }
            default:
            {
                var r = AnnotationMath.NormalizedRect(_dragStart, p);
                if (shift)
                {
                    var side = Math.Max(r.Width, r.Height);
                    r = new Rect(p.X < _dragStart.X ? _dragStart.X - side : _dragStart.X, p.Y < _dragStart.Y ? _dragStart.Y - side : _dragStart.Y, side, side);
                }
                _draft.Bounds = r;
                break;
            }
        }
        _canvas.InvalidateVisual();
    }

    private void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();
        var p = ImagePoint(e);
        if (_tool == AnnotationTool.Select)
        {
            if (_selected is not null)
            {
                if ((p - _dragStart).Length < 1 && _undo.Count > 0) _undo.Pop(); // a click, nothing moved
                else MarkChanged();
            }
            _canvas.InvalidateVisual();
            return;
        }
        if (_tool == AnnotationTool.Crop)
        {
            RebuildInspector();
            return;
        }
        var draft = _draft;
        _draft = null;
        if (draft is null) return;
        if (draft.Tool == AnnotationTool.CopyText)
        {
            _canvas.InvalidateVisual();
            if (!AnnotationMath.IsTap(_dragStart, p)) CopyTextFrom(draft.Bounds);
            return;
        }
        var tap = draft.Tool == AnnotationTool.Freehand
            ? draft.Points.Count < 3 && AnnotationMath.IsTap(_dragStart, p)
            : AnnotationMath.IsTap(_dragStart, p);
        if (draft.Tool == AnnotationTool.Sticker)
        {
            // A click drops a sticker at its default size; a drag sets the size.
            var side = tap ? 56 * Math.Max(1, _base.Scale) : Math.Max(draft.Bounds.Width, draft.Bounds.Height);
            var center = tap ? p : new Point(draft.Bounds.X + draft.Bounds.Width / 2, draft.Bounds.Y + draft.Bounds.Height / 2);
            draft.Bounds = new Rect(center.X - side / 2, center.Y - side / 2, side, side);
            tap = false;
        }
        if (tap)
        {
            _canvas.InvalidateVisual();
            return;
        }
        if (AnnotationMath.DragsRect(draft.Tool))
        {
            draft.Bounds = AnnotationMath.ClampToImage(draft.Bounds, _base.Width, _base.Height);
            if (draft.Bounds.IsEmpty || draft.Bounds.Width < 2 || draft.Bounds.Height < 2)
            {
                _canvas.InvalidateVisual();
                return;
            }
        }
        PushUndo();
        _marks.Add(draft);
        MarkChanged();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textEditor is not null) return; // the text box handles its own keys
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.S when ctrl && shift:
                if (ScreenshotFlow.SaveAs(RenderResult(), this) is not null) _unsaved = false;
                break;
            case Key.S when ctrl:
                Save();
                break;
            case Key.Delete or Key.Back when _selected is not null:
                DeleteSelected();
                break;
            case Key.Enter when _crop is not null:
                ApplyCrop();
                break;
            case Key.Escape:
                if (_crop is not null)
                {
                    _crop = null;
                    SelectTool(AnnotationTool.Arrow);
                }
                else if (_selected is not null)
                {
                    _selected = null;
                    RebuildInspector();
                    _canvas.InvalidateVisual();
                }
                else
                {
                    Close();
                }
                break;
            case >= Key.D1 and <= Key.D9 when !ctrl:
            {
                var tool = AnnotationMath.ToolForDigit(e.Key - Key.D0, Settings.GetString(CaptureSettings.ToolOrder, AnnotationMath.DefaultToolOrder),
                    Settings.GetBool(CaptureSettings.ToolShortcuts, true));
                if (tool is { } t) SelectTool(t);
                break;
            }
            default:
                return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------- text

    private Rect TextBounds(Annotation a, Point origin)
    {
        var ft = ScreenshotRenderer.LayoutText(a, _base.Scale);
        return new Rect(origin, new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, 4), ft.Height));
    }

    private void BeginTextEdit(Point at, Annotation? existing)
    {
        CommitText();
        _editingText = existing;
        var scale = Math.Max(1, _base.Scale);
        var color = existing?.Color ?? _color;
        var size = existing?.TextSize ?? _textSize;
        var box = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            FontSize = size * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ScreenshotRenderer.ToColor(color)),
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            BorderThickness = new Thickness(1 * scale),
            BorderBrush = Brushes.White,
            AcceptsReturn = true,
            MinWidth = 60 * scale,
            Padding = new Thickness(0),
            CaretBrush = new SolidColorBrush(ScreenshotRenderer.ToColor(color)),
        };
        var off = ImageOffset;
        Canvas.SetLeft(box, at.X + off);
        Canvas.SetTop(box, at.Y + off);
        box.Tag = at;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelText();
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                CommitText();
            }
        };
        _overlay.Children.Add(box);
        _textEditor = box;
        _canvas.InvalidateVisual();
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitText()
    {
        if (_textEditor is null) return;
        var box = _textEditor;
        var at = (Point)box.Tag;
        var text = box.Text.TrimEnd();
        _overlay.Children.Remove(box);
        _textEditor = null;
        var existing = _editingText;
        _editingText = null;
        if (existing is not null)
        {
            PushUndo();
            if (text.Length == 0) _marks.Remove(existing);
            else
            {
                existing.Text = text;
                existing.Bounds = TextBounds(existing, existing.Bounds.TopLeft);
            }
            MarkChanged();
            return;
        }
        if (text.Length == 0)
        {
            _canvas.InvalidateVisual();
            return;
        }
        var mark = new Annotation { Tool = AnnotationTool.Text, Text = text, Color = _color, TextSize = _textSize };
        mark.Bounds = TextBounds(mark, at);
        PushUndo();
        _marks.Add(mark);
        MarkChanged();
    }

    private void CancelText()
    {
        if (_textEditor is null) return;
        _overlay.Children.Remove(_textEditor);
        _textEditor = null;
        _editingText = null;
        _canvas.InvalidateVisual();
    }

    // ------------------------------------------------------------- mutations

    private void ChangeSelected(Action<Annotation> change)
    {
        if (_selected is null || !_marks.Contains(_selected)) return;
        PushUndo();
        change(_selected);
        MarkChanged();
    }

    private void Reorder(int delta)
    {
        if (_selected is null) return;
        var i = _marks.IndexOf(_selected);
        var j = Math.Clamp(i + delta, 0, _marks.Count - 1);
        if (i < 0 || i == j) return;
        PushUndo();
        _marks.RemoveAt(i);
        _marks.Insert(j, _selected);
        MarkChanged();
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        PushUndo();
        _marks.Remove(_selected);
        _selected = null;
        MarkChanged();
        RebuildInspector();
    }

    /// <summary>
    /// Recognises the text in one box of the screenshot and copies it. Blur marks are applied first, so
    /// text someone hid cannot be copied back out of the picture.
    /// </summary>
    private void CopyTextFrom(Rect box)
    {
        var rect = new PixelRect((int)Math.Floor(box.X), (int)Math.Floor(box.Y), (int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height))
            .Intersect(new PixelRect(0, 0, _base.Width, _base.Height));
        if (rect.Width < 4 || rect.Height < 4) return;
        var area = ScreenshotRenderer.ApplyRedactions(_base, _marks).Crop(rect);
        ScreenTextService.Recognize(area, fromEditor: true);
    }

    private void ApplyCrop()
    {
        if (_crop is not { } crop) return;
        var rect = new PixelRect((int)Math.Round(crop.X), (int)Math.Round(crop.Y), (int)Math.Round(crop.Width), (int)Math.Round(crop.Height))
            .Intersect(new PixelRect(0, 0, _base.Width, _base.Height));
        if (rect.Width < 2 || rect.Height < 2) return;
        PushUndo();
        _base = _base.Crop(rect);
        foreach (var m in _marks) m.Translate(-rect.X, -rect.Y);
        _crop = null;
        MarkChanged();
        UpdateStageSize();
        SelectTool(AnnotationTool.Arrow);
    }

    private void PushUndo()
    {
        _undo.Push((_base, _marks.Select(m => m.Clone()).ToList()));
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push((_base, _marks.Select(m => m.Clone()).ToList()));
        (_base, _marks) = _undo.Pop();
        AfterHistory();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push((_base, _marks.Select(m => m.Clone()).ToList()));
        (_base, _marks) = _redo.Pop();
        AfterHistory();
    }

    private void AfterHistory()
    {
        _selected = null;
        _unsaved = true;
        RefreshRedactions();
        UpdateStageSize();
        RebuildInspector();
    }

    private void MarkChanged()
    {
        _unsaved = true;
        RefreshRedactions();
        _canvas.InvalidateVisual();
    }

    private void RefreshRedactions()
    {
        _redacted = ScreenshotRenderer.ApplyRedactions(_base, _marks);
        _redactedSource = _redacted.ToBitmapSource();
    }

    // ------------------------------------------------------- background etc.

    private void ShowBackgroundPopup()
    {
        var content = Ui.Stack(10);
        var presets = new WrapPanel();
        void Choose(BackdropStyle style)
        {
            Backdrop = style;
            Settings.Set(CaptureSettings.Backdrop, style.ToJson());
            UpdateStageSize();
        }
        Border Swatch(Brush fill, string tip, Action onClick, bool selected)
        {
            var b = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = fill, Margin = new Thickness(3), Cursor = Cursors.Hand, ToolTip = tip, BorderThickness = new Thickness(selected ? 3 : 1) };
            b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "CardBorder");
            b.MouseLeftButtonUp += (_, _) => Ui.Guard(onClick);
            return b;
        }
        var none = Swatch(Brushes.Transparent, "None", () => Choose(Backdrop with { Kind = BackdropKind.None }), Backdrop.Kind == BackdropKind.None);
        none.Child = Ui.Icon(Glyphs.Close, 12, "TextSecondary");
        presets.Children.Add(none);
        foreach (var preset in Enum.GetValues<BackdropPreset>().Where(p => p != BackdropPreset.None))
        {
            var stops = DecorationMath.PresetStops(preset);
            var brush = new LinearGradientBrush(Color.FromRgb(stops[0].R, stops[0].G, stops[0].B), Color.FromRgb(stops[1].R, stops[1].G, stops[1].B), 45);
            var p = preset;
            presets.Children.Add(Swatch(brush, preset.ToString(), () => Choose(Backdrop with { Kind = BackdropKind.Preset, Preset = p }),
                Backdrop.Kind == BackdropKind.Preset && Backdrop.Preset == p));
        }
        var wallpaper = ScreenshotRenderer.WallpaperPath();
        if (wallpaper is not null)
        {
            var w = Swatch(Brushes.Gray, "Wallpaper", () => Choose(Backdrop with { Kind = BackdropKind.Wallpaper }), Backdrop.Kind == BackdropKind.Wallpaper);
            w.Child = Ui.Icon(Glyphs.Wallpaper, 14, "TextOnAccent");
            presets.Children.Add(w);
        }
        var image = Swatch(Brushes.Transparent, "Image\u2026", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic" };
            if (dialog.ShowDialog(this) == true) Choose(Backdrop with { Kind = BackdropKind.Image, ImagePath = dialog.FileName });
        }, Backdrop.Kind == BackdropKind.Image);
        image.Child = Ui.Icon(Glyphs.Photo, 14, "TextSecondary");
        presets.Children.Add(image);
        content.Children.Add(Ui.Text("Background", 13.5, FontWeights.SemiBold));
        content.Children.Add(presets);

        var solid = new TextBox { Text = Backdrop.Colors.FirstOrDefault() ?? "#3378F5", Width = 90, ToolTip = "Solid (#RRGGBB)" };
        var second = new TextBox { Text = Backdrop.Colors.Skip(1).FirstOrDefault() ?? "#73D4FA", Width = 90, ToolTip = "Gradient end (#RRGGBB)" };
        content.Children.Add(Ui.Row(8,
            Ui.Button("Solid", () => Choose(Backdrop with { Kind = BackdropKind.Solid, Colors = [solid.Text.Trim(), second.Text.Trim()] })),
            solid,
            Ui.Button("Gradient", () => Choose(Backdrop with { Kind = BackdropKind.Gradient, Colors = [solid.Text.Trim(), second.Text.Trim()] })),
            second));
        content.Children.Add(PopupSlider("Margin", Backdrop.Padding, v => Choose(Backdrop with { Padding = v })));
        content.Children.Add(PopupSlider("Corners", Backdrop.CornerRadius, v => Choose(Backdrop with { CornerRadius = v })));
        content.Children.Add(PopupSlider("Blur", Backdrop.Blur, v => Choose(Backdrop with { Blur = v })));
        var shadow = new CheckBox { Content = Ui.Text("Shadows", 13), IsChecked = Backdrop.Shadow };
        shadow.Click += (_, _) => Choose(Backdrop with { Shadow = shadow.IsChecked == true });
        content.Children.Add(shadow);
        ShowPopup(content);
    }

    private void ShowWatermarkPopup()
    {
        var content = Ui.Stack(10);
        void Apply(WatermarkStyle style)
        {
            Watermark = style;
            Settings.Set(CaptureSettings.Watermark, style.ToJson());
            _canvas.InvalidateVisual();
        }
        var enabled = new CheckBox { Content = Ui.Text("Watermark", 13.5, FontWeights.SemiBold), IsChecked = Watermark.Enabled };
        enabled.Click += (_, _) => Apply(Watermark with { Enabled = enabled.IsChecked == true });
        content.Children.Add(enabled);
        var text = new TextBox { Text = Watermark.Text, Width = 260, ToolTip = "Watermark text" };
        text.TextChanged += (_, _) => Apply(Watermark with { Text = text.Text, Enabled = true });
        content.Children.Add(text);
        var imageButton = Ui.Button(string.IsNullOrEmpty(Watermark.ImagePath) ? "Image\u2026" : Path.GetFileName(Watermark.ImagePath), () =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
            if (dialog.ShowDialog(this) == true) Apply(Watermark with { ImagePath = dialog.FileName, Enabled = true });
        });
        var clearImage = Ui.IconButton(Glyphs.Close, "Remove", () => Apply(Watermark with { ImagePath = null }), 11);
        content.Children.Add(Ui.Row(6, imageButton, clearImage));
        var positions = new UniformGrid { Columns = 3, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var pos in Enum.GetValues<WatermarkPosition>())
        {
            var p = pos;
            var cell = new Border { Height = 24, Margin = new Thickness(2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, ToolTip = pos.ToString() };
            cell.SetResourceReference(Border.BackgroundProperty, Watermark.Position == pos ? "Accent" : "ControlBackground");
            cell.MouseLeftButtonUp += (_, _) =>
            {
                Apply(Watermark with { Position = p });
                foreach (Border c in positions.Children) c.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
                cell.SetResourceReference(Border.BackgroundProperty, "Accent");
            };
            positions.Children.Add(cell);
        }
        content.Children.Add(Labeled("Position", positions));
        content.Children.Add(PopupSlider("Size", Watermark.Size, v => Apply(Watermark with { Size = v })));
        content.Children.Add(PopupSlider("Opacity", Watermark.Opacity, v => Apply(Watermark with { Opacity = Math.Max(0.05, v) })));
        content.Children.Add(PopupSlider("Rotation", (Watermark.Rotation + 45) / 90, v => Apply(Watermark with { Rotation = v * 90 - 45 })));
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Enum.GetValues<AnnotationColor>())
        {
            var color = c;
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(2), Cursor = Cursors.Hand, Background = new SolidColorBrush(ScreenshotRenderer.ToColor(c)), BorderThickness = new Thickness(1) };
            dot.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            dot.MouseLeftButtonUp += (_, _) => Apply(Watermark with { Color = color });
            colors.Children.Add(dot);
        }
        content.Children.Add(Labeled("Color", colors));
        ShowPopup(content);
    }

    private static FrameworkElement PopupSlider(string label, double value, Action<double> onChange)
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(value, 0, 1), Width = 180 };
        slider.ValueChanged += (_, e) => onChange(e.NewValue);
        var text = Ui.Text(label, 12.5, FontWeights.Medium, "TextSecondary", wrap: false);
        text.Width = 70;
        return Ui.Row(8, text, slider);
    }

    private void ShowPopup(FrameworkElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), MinWidth = 300 };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        var popup = new Popup
        {
            Child = border,
            StaysOpen = false,
            Placement = PlacementMode.Mouse,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.IsOpen = true;
    }

    // ------------------------------------------------------------------ close

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitText();
        if (!_unsaved) return;
        var answer = MessageBox.Show(this, "It was not copied or saved yet.", "Discard this screenshot?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }

    /// <summary>Renders the editor with sample marks to a PNG (developer preview, used by tests).</summary>
    public static void RenderPreview(string path, RasterImage sample, bool spotlight = false)
    {
        var editor = new ScreenshotEditorWindow(sample) { _unsaved = false, Left = -20000, Top = -20000, ShowActivated = false };
        var s = Math.Max(1, sample.Scale);
        editor._marks.AddRange(
        [
            new Annotation { Tool = AnnotationTool.Arrow, Points = [new Point(60 * s, 60 * s), new Point(220 * s, 140 * s)], Color = AnnotationColor.Red },
            new Annotation { Tool = AnnotationTool.Rect, Bounds = new Rect(240 * s, 40 * s, 160 * s, 90 * s), Color = AnnotationColor.Blue },
            new Annotation { Tool = AnnotationTool.Counter, Bounds = new Rect(30 * s, 170 * s, 30 * s, 30 * s), Number = 1, Color = AnnotationColor.Orange },
            new Annotation { Tool = AnnotationTool.Pixelate, Bounds = new Rect(260 * s, 170 * s, 120 * s, 60 * s) },
            new Annotation { Tool = AnnotationTool.Highlight, Bounds = new Rect(80 * s, 200 * s, 140 * s, 22 * s), Color = AnnotationColor.Yellow },
        ]);
        var text = new Annotation { Tool = AnnotationTool.Text, Text = "Click here", Color = AnnotationColor.Green };
        text.Bounds = editor.TextBounds(text, new Point(70 * s, 240 * s));
        editor._marks.Add(text);
        if (spotlight) editor._marks.Insert(0, new Annotation { Tool = AnnotationTool.Spotlight, Bounds = new Rect(230 * s, 30 * s, 180 * s, 110 * s) });
        editor.RefreshRedactions();
        editor.Show();
        editor.UpdateLayout();
        PreviewRendering.SaveWindow(editor, path);
        editor.Close();
    }
}

/// <summary>Draws the editor's picture; all coordinates are canvas pixels.</summary>
internal sealed class EditorCanvas(ScreenshotEditorWindow owner) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        owner.Draw(dc);
    }
}
