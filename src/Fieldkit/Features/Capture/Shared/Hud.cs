// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// A short confirmation bubble near the bottom of the screen with the pointer (upstream QuickToolHUD):
/// never takes focus, stays out of captures, fades away on its own.
/// </summary>
public static class Hud
{
    private static Window? _current;
    private static DispatcherTimer? _timer;

    public static void Show(string glyph, string message, Color? swatch = null, double seconds = 1.8) =>
        Ui.OnUi(() => Present(glyph, message, swatch, seconds));

    /// <summary>Countdown before a delayed capture or recording ("3", "2", "1").</summary>
    public static void ShowCountdown(int seconds) => Show(Glyphs.Timer, seconds.ToString(System.Globalization.CultureInfo.CurrentCulture), null, 1.05);

    public static void Hide() => Ui.OnUi(Close);

    private static void Present(string glyph, string message, Color? swatch, double seconds)
    {
        Close();
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (swatch is { } c)
        {
            content.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
            });
        }
        else
        {
            var icon = Ui.Icon(glyph, 16, "TextPrimary");
            icon.Margin = new Thickness(0, 0, 10, 0);
            content.Children.Add(icon);
        }
        var text = Ui.Text(message, 13.5, FontWeights.SemiBold, wrap: false);
        text.MaxWidth = 520;
        content.Children.Add(text);
        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 10, 18, 10),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = border,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Left = -32000,
            Top = -32000,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
            ScreenGrabber.ExcludeFromCapture(hwnd);
        };
        window.Loaded += (_, _) => PlaceBottomCenter(window);
        _current = window;
        window.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) =>
            {
                if (ReferenceEquals(_current, window)) Close();
                else window.Close();
            };
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        _timer.Start();
    }

    /// <summary>Places a window centred near the bottom of the work area under the pointer (physical pixels).</summary>
    public static void PlaceBottomCenter(Window window, int bottomMargin = 96)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetCursorPos(out var cursor);
        var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        Native.GetWindowRect(hwnd, out var rect);
        var x = work.Left + (work.Width - rect.Width) / 2;
        var y = work.Bottom - rect.Height - (int)(bottomMargin * WindowHelper.GetScaleAt(cursor.X, cursor.Y));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static void Close()
    {
        _timer?.Stop();
        _timer = null;
        _current?.Close();
        _current = null;
    }
}
