// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>Helpers for borderless overlay windows placed in physical pixels.</summary>
public static class OverlayPlacement
{
    /// <summary>Adds tool-window / no-activate (and optionally click-through) styles.</summary>
    public static void MakeOverlay(Window window, bool clickThrough, bool excludeFromCapture)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        if (excludeFromCapture) EnergyNative.SetWindowDisplayAffinity(hwnd, EnergyNative.WDA_EXCLUDEFROMCAPTURE);
    }

    public static void Cover(Window window, Native.RECT bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Centres a window of a DIP size on a monitor, at a vertical fraction of its height.</summary>
    public static void Center(Window window, Native.RECT bounds, double widthDip, double heightDip, double verticalFraction = 0.5)
    {
        var scale = WindowHelper.GetScaleAt(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var w = (int)Math.Round(widthDip * scale);
        var h = (int)Math.Round(heightDip * scale);
        var x = bounds.Left + (bounds.Width - w) / 2;
        var y = bounds.Top + (int)((bounds.Height - h) * verticalFraction);
        var hwnd = new WindowInteropHelper(window).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}

/// <summary>A click-through black layer that dims one display (software dimming).</summary>
public sealed class DimOverlay : Window
{
    private Native.RECT _bounds;

    public DimOverlay(Native.RECT bounds)
    {
        _bounds = bounds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        Title = "Fieldkit dimming";
        SourceInitialized += (_, _) =>
        {
            OverlayPlacement.MakeOverlay(this, clickThrough: true, excludeFromCapture: true);
            OverlayPlacement.Cover(this, _bounds);
        };
    }

    public void Apply(Native.RECT bounds, double opacity)
    {
        Opacity = Math.Clamp(opacity, 0, 0.95);
        if (!IsVisible) Show();
        if (IsLoaded)
        {
            _bounds = bounds;
            OverlayPlacement.Cover(this, bounds);
        }
    }
}

/// <summary>The brightness on-screen display (upstream BrightnessOSD).</summary>
public sealed class BrightnessOsd : Window
{
    private static BrightnessOsd? _instance;
    private readonly TextBlock _percent;
    private readonly StackPanel _segments = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1) };

    private BrightnessOsd()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 196;
        Height = 154;
        Title = "Brightness";
        var sun = new TextBlock
        {
            Text = Glyphs.Brightness,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = 39,
            Foreground = new SolidColorBrush(Color.FromArgb(209, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _percent = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < 16; i++)
            _segments.Children.Add(new Border { Width = 7.6, Height = 7, Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0), CornerRadius = new CornerRadius(1.5) });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(sun);
        stack.Children.Add(_percent);
        _segments.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_segments);
        Content = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(225, 32, 32, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
        SourceInitialized += (_, _) => OverlayPlacement.MakeOverlay(this, clickThrough: true, excludeFromCapture: true);
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) =>
            {
                if (!_hide.IsEnabled) Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public static void ShowLevel(Native.RECT displayBounds, double level)
    {
        _instance ??= new BrightnessOsd();
        _instance.Update(displayBounds, level);
    }

    public static void CloseInstance()
    {
        _instance?.Close();
        _instance = null;
    }

    /// <summary>The OSD content for a level, detached from any window (for previews and tests).</summary>
    public static FrameworkElement BuildPreview(double level)
    {
        var osd = new BrightnessOsd();
        osd.Paint(level);
        var content = (FrameworkElement)osd.Content;
        osd.Content = null;
        osd.Close();
        content.Width = 196;
        content.Height = 154;
        return content;
    }

    private void Update(Native.RECT bounds, double level)
    {
        Paint(level);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        OverlayPlacement.Center(this, bounds, Width, Height);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        _hide.Stop();
        _hide.Start();
    }

    private void Paint(double level)
    {
        _percent.Text = $"{DisplayMath.WholePercent(level)}%";
        var filled = DisplayMath.FilledSegments(level);
        for (var i = 0; i < _segments.Children.Count; i++)
        {
            ((Border)_segments.Children[i]).Background = new SolidColorBrush(i < filled
                ? Color.FromArgb(179, 255, 255, 255)
                : Color.FromArgb(31, 255, 255, 255));
        }
    }
}
