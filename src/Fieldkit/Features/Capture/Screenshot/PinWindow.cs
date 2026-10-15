// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>A screenshot pinned above every window (upstream ScreenshotPinController).</summary>
public sealed class PinWindow : Window
{
    private static readonly List<PinWindow> Open = [];
    private readonly RasterImage _image;
    private readonly Image _view;
    private double _zoom = 1;
    private bool _clickThrough;

    public static int Count => Open.Count;

    public static void Pin(RasterImage image)
    {
        var pin = new PinWindow(image);
        Open.Add(pin);
        pin.Show();
    }

    public static void CloseAll()
    {
        foreach (var pin in Open.ToList()) pin.Close();
    }

    private PinWindow(RasterImage image)
    {
        _image = image;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Pinned screenshot";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _view = new Image { Source = image.ToBitmapSource(), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Child = _view,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Direction = 270 },
            Cursor = Cursors.SizeAll,
        };
        Content = frame;
        ApplyZoom();

        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ScreenshotEditorWindow.Open(_image);
                Close();
                return;
            }
            DragMove();
        };
        MouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1);
            else
            {
                _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 4);
                ApplyZoom();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) CaptureClipboard.CopyImage(_image);
        };
        ContextMenu = BuildMenu();
        Closed += (_, _) => Open.Remove(this);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) =>
        {
            // Start near the pointer, offset for each further pin.
            Native.GetCursorPos(out var p);
            var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
            var scale = WindowHelper.GetScaleAt(p.X, p.Y);
            var offset = (Open.Count - 1) % 8 * 24;
            Left = work.Left / scale + 48 + offset;
            Top = work.Top / scale + 48 + offset;
        };
    }

    private void ApplyZoom()
    {
        var scale = Math.Max(1, _image.Scale);
        _view.Width = _image.Width / scale * _zoom;
        _view.Height = _image.Height / scale * _zoom;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Ui.Guard(onClick);
            return item;
        }
        menu.Items.Add(Item("Copy", () =>
        {
            if (CaptureClipboard.CopyImage(_image)) Hud.Show(Glyphs.Copy, "Screenshot copied");
        }));
        menu.Items.Add(Item("Save", () => ScreenshotFlow.ShowResultHud(ScreenshotFlow.Save(_image), false)));
        menu.Items.Add(Item("Edit", () =>
        {
            ScreenshotEditorWindow.Open(_image);
            Close();
        }));
        var opacity = new MenuItem { Header = "Opacity" };
        foreach (var value in new[] { 1.0, 0.8, 0.6, 0.4 })
        {
            var v = value;
            opacity.Items.Add(Item($"{v * 100:0}%", () => Opacity = v));
        }
        menu.Items.Add(opacity);
        menu.Items.Add(Item("Ignore clicks", () =>
        {
            _clickThrough = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
            Hud.Show(Glyphs.Pin, "Close all pins brings it back under control");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", Close));
        menu.Items.Add(Item("Close all pins", CloseAll));
        return menu;
    }

    public bool IgnoresClicks => _clickThrough;
}
