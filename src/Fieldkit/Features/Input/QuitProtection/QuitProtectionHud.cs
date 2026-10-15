// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.QuitProtection;

/// <summary>
/// The confirmation pill (upstream QuitProtectionHUD): a dark rounded panel near the bottom of
/// the screen holding the pointer, with a title, a hint and, for hold-to-confirm, a progress bar
/// that fills over the hold. It never takes focus, never appears in Alt+Tab and lets clicks
/// through. Like upstream it is always dark, whatever the theme, so it reads over any app.
/// </summary>
internal sealed class QuitProtectionHud : Window
{
    private static readonly Brush PillBrush = Frozen(Color.FromArgb(0xF2, 0x17, 0x17, 0x17));
    private static readonly Brush PillBorder = Frozen(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TitleBrush = Frozen(Colors.White);
    private static readonly Brush DetailBrush = Frozen(Color.FromArgb(0xAD, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FillBrush = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly Border _track;
    private readonly ScaleTransform _fillScale = new(0, 1);

    public QuitProtectionHud()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;

        _title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush, TextAlignment = TextAlignment.Center };
        _detail = new TextBlock { FontSize = 11, Foreground = DetailBrush, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
        var fill = new Border
        {
            Background = FillBrush,
            CornerRadius = new CornerRadius(1.5),
            RenderTransform = _fillScale,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        _track = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = TrackBrush,
            Margin = new Thickness(12, 7, 12, 0),
            Child = fill,
            ClipToBounds = true,
        };
        var stack = new StackPanel { Children = { _title, _detail, _track } };
        Content = new Border
        {
            MinWidth = 300,
            MinHeight = 48,
            Padding = new Thickness(24, 7, 24, 8),
            CornerRadius = new CornerRadius(24),
            Background = PillBrush,
            BorderBrush = PillBorder,
            BorderThickness = new Thickness(1),
            Child = stack,
            Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Shows (or updates) the pill; with <paramref name="hold"/> the bar fills over that time.</summary>
    public void ShowMessage(string title, string detail, TimeSpan? hold)
    {
        _title.Text = title;
        _detail.Text = detail;
        _track.Visibility = hold is null ? Visibility.Collapsed : Visibility.Visible;
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _fillScale.ScaleX = 0;
        if (hold is { } duration)
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, duration));
        if (!IsVisible) Show();
        UpdateLayout();
        PlaceAtBottomOfPointerScreen();
    }

    public void HideMessage()
    {
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hide();
    }

    private void PlaceAtBottomOfPointerScreen()
    {
        Native.GetCursorPos(out var cursor);
        var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        var scale = WindowHelper.GetScaleAt(cursor.X, cursor.Y);
        var width = ActualWidth > 0 ? ActualWidth : 320;
        var height = ActualHeight > 0 ? ActualHeight : 68;
        Left = (work.Left + work.Width / 2.0) / scale - width / 2;
        Top = work.Bottom / scale - height - 18 + 10;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
