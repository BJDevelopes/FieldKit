// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// A small pill shown above the notification area for a moment ("Microphone muted",
/// "Headphones (WH-1000XM4)"): the counterpart of the upstream HUD. Click-through, never
/// takes focus, out of Alt+Tab. One shared window, reused.
/// </summary>
internal static class SoundHud
{
    private static Window? _window;
    private static TextBlock? _icon;
    private static TextBlock? _text;
    private static DispatcherTimer? _hide;

    public static void Show(string glyph, string message, string iconBrush = "Accent") => Ui.OnUi(() =>
    {
        EnsureWindow();
        _icon!.Text = glyph;
        _icon.SetResourceReference(TextBlock.ForegroundProperty, iconBrush);
        _text!.Text = message;
        var window = _window!;
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        // Bottom-right, above the taskbar's notification area.
        var work = SystemParameters.WorkArea;
        window.Left = work.Right - window.ActualWidth - 16;
        window.Top = work.Bottom - window.ActualHeight - 16;
        _hide!.Stop();
        _hide.Start();
    });

    private static void EnsureWindow()
    {
        if (_window is not null) return;
        _icon = Ui.Icon(Glyphs.Speaker, 18);
        _text = Ui.Text(string.Empty, 13.5, FontWeights.SemiBold, wrap: false);
        _text.MaxWidth = 320;
        var pill = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 18, 10),
            Margin = new Thickness(10),
            Child = Ui.Row(10, _icon, _text),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.35 },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        pill.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = pill,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
        };
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (_window.Opacity <= 0.01) _window.Hide();
            };
            _window.BeginAnimation(UIElement.OpacityProperty, fade);
        };
    }
}
