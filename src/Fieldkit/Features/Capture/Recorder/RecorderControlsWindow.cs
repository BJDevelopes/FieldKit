// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Recording controls (upstream RecorderIndicator): a small pill with the timer, pause/resume, stop
/// and delete, plus a dashed outline around a recorded area. Both stay out of the recording.
/// </summary>
public sealed class RecorderControlsWindow : Window
{
    private readonly ScreenRecording? _recording;
    private readonly TextBlock _time = Ui.Text("00:00", 13, FontWeights.SemiBold, wrap: false);
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58)) };
    private readonly Button _pause;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Window? _outline;

    internal RecorderControlsWindow(ScreenRecording? recording)
    {
        _recording = recording;
        Title = "Recording controls";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Left = -32000;

        _time.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _time.MinWidth = 46;
        _pause = Ui.IconButton(Glyphs.Pause, "Pause recording", RecorderController.TogglePause, 14, "TextPrimary");
        var stop = Ui.IconButton(Glyphs.Stop, "Stop recording", () => RecorderController.StopIfActive(), 14, "Red");
        var discard = Ui.IconButton(Glyphs.Delete, "Delete", () =>
        {
            if (MessageBox.Show("It has not been saved anywhere yet.", "Delete this recording?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                RecorderController.Discard();
        }, 13, "TextSecondary");
        var row = Ui.Row(8, _dot, _time, _pause, stop, discard);
        var pill = new Border
        {
            Child = row,
            Padding = new Thickness(14, 6, 8, 6),
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10),
            Cursor = Cursors.SizeAll,
            ToolTip = "Recording controls",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35, Direction = 270 },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        pill.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        pill.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { TemplatedParent: Button }) DragMove();
        };
        Content = pill;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
            ScreenGrabber.ExcludeFromCapture(hwnd);
        };
        Loaded += (_, _) => Place();
        _timer.Tick += (_, _) => Refresh();
        if (recording is not null) _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _outline?.Close();
        };
        if (recording is not null) ShowOutline(recording.ScreenRect);
    }

    public void Refresh()
    {
        if (_recording is null) return;
        var t = _recording.Elapsed;
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        var paused = _recording.IsPaused;
        _dot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1 : 0.55);
        if (_pause.Content is TextBlock icon) icon.Text = paused ? Glyphs.Play : Glyphs.Pause;
        _pause.ToolTip = paused ? "Resume recording" : "Pause recording";
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var r);
        var area = _recording?.ScreenRect ?? new PixelRect(0, 0, 1280, 720);
        var work = WindowHelper.GetWorkAreaAt(area.X + area.Width / 2, area.Y + area.Height / 2);
        var x = Math.Clamp(area.X + (area.Width - r.Width) / 2, work.Left, Math.Max(work.Left, work.Right - r.Width));
        // Above the area when there is room, otherwise just inside the top of the work area.
        var y = area.Y - r.Height - 6 >= work.Top ? area.Y - r.Height - 6 : work.Top + 6;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void ShowOutline(PixelRect area)
    {
        var monitorBounds = WindowHelper.GetMonitors().Select(m => PixelRect.FromLtrb(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom));
        if (monitorBounds.Any(m => m == area)) return; // whole screen: nothing to outline
        var rect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 69, 58)),
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            Content = rect,
            Left = -32000,
            Width = 10,
            Height = 10,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
            ScreenGrabber.ExcludeFromCapture(hwnd);
            const int pad = 3;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, area.X - pad, area.Y - pad, area.Width + pad * 2, area.Height + pad * 2, Native.SWP_NOACTIVATE);
        };
        _outline = window;
        window.Show();
    }

    /// <summary>Developer preview of the pill (no recording involved).</summary>
    public static void RenderPreview(string path)
    {
        var window = new RecorderControlsWindow(null) { Left = -20000, Top = -20000 };
        window._time.Text = "01:23";
        window.Show();
        PreviewRendering.SaveWindow(window, path);
        window.Close();
    }
}
