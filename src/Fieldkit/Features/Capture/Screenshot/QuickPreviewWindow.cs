// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// The floating thumbnail after a capture (upstream ScreenshotQuickPreviewController): copy, save,
/// edit, pin, share or drag it into another app; Esc dismisses.
/// </summary>
public sealed class QuickPreviewWindow : Window
{
    private static QuickPreviewWindow? _current;
    private readonly RasterImage _image;
    private string? _savedPath;
    private DispatcherTimer? _dismiss;

    public static readonly IReadOnlyList<(string Value, string Label)> Positions =
    [
        ("automatic", "Automatic"),
        ("topLeft", "Top left"),
        ("topRight", "Top right"),
        ("bottomLeft", "Bottom left"),
        ("bottomRight", "Bottom right"),
    ];

    public static void Show(RasterImage image, string? savedPath, int? autoDismissSeconds)
    {
        _current?.Close();
        var window = new QuickPreviewWindow(image, savedPath);
        _current = window;
        window.Show();
        if (autoDismissSeconds is > 0) window.StartDismissTimer(autoDismissSeconds.Value);
    }

    public static void CloseCurrent() => _current?.Close();

    private QuickPreviewWindow(RasterImage image, string? savedPath)
    {
        _image = image;
        _savedPath = savedPath;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = Settings.GetBool(CaptureSettings.PreviewFocus, true);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "Screenshot";
        Left = -32000;
        Top = -32000;
        Content = Build();
        KeyDown += OnKey;
        MouseEnter += (_, _) => _dismiss?.Stop();
        MouseLeave += (_, _) => _dismiss?.Start();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) => Place();
        Closed += (_, _) =>
        {
            _dismiss?.Stop();
            ShareSheet.Forget(new WindowInteropHelper(this).Handle);
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    private void StartDismissTimer(int seconds)
    {
        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dismiss.Tick += (_, _) => Close();
        _dismiss.Start();
    }

    private FrameworkElement Build()
    {
        var thumb = new Image { Source = _image.ToBitmapSource(), Stretch = Stretch.Uniform, MaxWidth = 260, MaxHeight = 170 };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        var thumbHost = new Border
        {
            Child = thumb,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            ToolTip = "Drag and drop",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Point? down = null;
        thumbHost.MouseLeftButtonDown += (_, e) =>
        {
            down = e.GetPosition(this);
            if (e.ClickCount == 2) Edit();
        };
        thumbHost.MouseMove += (_, e) =>
        {
            if (down is null || e.LeftButton != MouseButtonState.Pressed) return;
            if ((e.GetPosition(this) - down.Value).Length < 6) return;
            down = null;
            // Drag the capture out as a PNG file into any app.
            var path = _savedPath ?? ScreenshotFlow.TemporaryFile(_image);
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(thumbHost, data, DragDropEffects.Copy);
        };
        thumbHost.MouseLeftButtonUp += (_, _) => down = null;

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        void Add(string glyph, string tip, Action action)
        {
            var b = Ui.IconButton(glyph, tip, action, 15, "TextPrimary");
            b.Margin = new Thickness(2, 0, 2, 0);
            buttons.Children.Add(b);
        }
        Add(Glyphs.Copy, "Copy", Copy);
        Add("\uE74E", "Save", Save);
        Add(Glyphs.Edit, "Edit", Edit);
        Add(Glyphs.Pin, "Pin to screen", () =>
        {
            PinWindow.Pin(_image);
            Close();
        });
        Add(Glyphs.Share, "Share", () => ShareSheet.ShareFile(new WindowInteropHelper(this).Handle, _savedPath ?? ScreenshotFlow.TemporaryFile(_image), "Screenshot"));
        Add(Glyphs.Delete, "Discard", Close);

        var close = Ui.IconButton(Glyphs.Close, "Close", Close, 11, "TextSecondary");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -6, -6, 0);

        var stack = new StackPanel();
        stack.Children.Add(thumbHost);
        stack.Children.Add(buttons);
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(close);
        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            MinWidth = 220,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        return card;
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var p);
        var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
        Native.GetWindowRect(hwnd, out var r);
        var position = Settings.GetString(CaptureSettings.PreviewPosition, "automatic");
        var margin = (int)(8 * WindowHelper.GetScaleAt(p.X, p.Y));
        var left = position is "topLeft" or "bottomLeft" ? work.Left + margin : work.Right - r.Width - margin;
        var top = position is "topLeft" or "topRight" ? work.Top + margin : work.Bottom - r.Height - margin;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | (ShowActivated ? 0 : Native.SWP_NOACTIVATE));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.S when ctrl:
                Save();
                break;
            case Key.Enter or Key.E:
                Edit();
                break;
            case Key.Delete or Key.Back:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Copy()
    {
        if (CaptureClipboard.CopyImage(_image)) Hud.Show(Glyphs.Copy, "Screenshot copied");
        Close();
    }

    private void Save()
    {
        _savedPath ??= ScreenshotFlow.Save(_image);
        ScreenshotFlow.ShowResultHud(_savedPath, false);
        Close();
    }

    private void Edit()
    {
        ScreenshotEditorWindow.Open(_image);
        Close();
    }
}
