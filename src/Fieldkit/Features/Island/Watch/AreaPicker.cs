// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.Interop;

namespace Fieldkit.Features.Island.Watch;

/// <summary>
/// "Choose what to watch": a dimmed overlay over the display under the pointer where a rectangle is dragged.
/// Esc or a click without dragging cancels. Returns the area in physical pixels.
/// </summary>
public sealed class AreaPicker : Window
{
    private readonly Action<Native.RECT?> _done;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
    private readonly Native.RECT _monitor;
    private Point? _start;
    private bool _finished;

    private AreaPicker(Native.RECT monitor, Action<Native.RECT?> done)
    {
        _monitor = monitor;
        _done = done;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(_selection);
        var hint = new TextBlock
        {
            Text = "Choose what to watch",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 40, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
        };
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_canvas);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_start is not { } s) return;
            var p = e.GetPosition(_canvas);
            Canvas.SetLeft(_selection, Math.Min(s.X, p.X));
            Canvas.SetTop(_selection, Math.Min(s.Y, p.Y));
            _selection.Width = Math.Abs(p.X - s.X);
            _selection.Height = Math.Abs(p.Y - s.Y);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            if (_start is not { } s) { Finish(null); return; }
            var p = e.GetPosition(_canvas);
            var dpi = VisualTreeHelper.GetDpi(this);
            var x1 = (int)Math.Round(Math.Min(s.X, p.X) * dpi.DpiScaleX) + _monitor.Left;
            var y1 = (int)Math.Round(Math.Min(s.Y, p.Y) * dpi.DpiScaleY) + _monitor.Top;
            var x2 = (int)Math.Round(Math.Max(s.X, p.X) * dpi.DpiScaleX) + _monitor.Left;
            var y2 = (int)Math.Round(Math.Max(s.Y, p.Y) * dpi.DpiScaleY) + _monitor.Top;
            Finish(x2 - x1 >= 8 && y2 - y1 >= 8 ? new Native.RECT(x1, y1, x2, y2) : null);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
        };
        Deactivated += (_, _) => Finish(null);
    }

    private void Finish(Native.RECT? rect)
    {
        if (_finished) return;
        _finished = true;
        Close();
        _done(rect);
    }

    public static void Pick(Action<Native.RECT?> done)
    {
        Native.GetCursorPos(out var p);
        var monitor = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        var picker = new AreaPicker(monitor, done);
        picker.Show();
        picker.Activate();
    }
}
