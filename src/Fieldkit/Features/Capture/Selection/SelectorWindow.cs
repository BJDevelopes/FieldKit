// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// The overlay of one monitor: the frozen picture, the dim/selection/loupe drawing (in physical
/// pixels, so it stays exact at any scaling) and the mode bar. All state lives in <see cref="CaptureSelector"/>.
/// </summary>
internal sealed class SelectorWindow : Window
{
    private const int WM_DPICHANGED = 0x02E0;
    private readonly CaptureSelector _owner;
    private readonly Image _frozen = new() { Stretch = Stretch.Fill };
    private readonly SelectorSurface _surface;
    private readonly Grid _chromeHost = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 56) };
    private bool _closing;

    public SelectorMonitor Monitor { get; }

    public SelectorWindow(CaptureSelector owner, SelectorMonitor monitor, bool transparent, bool preview = false)
    {
        _owner = owner;
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = transparent;
        Background = transparent ? Brushes.Transparent : Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X / monitor.Scale;
        Top = monitor.Bounds.Y / monitor.Scale;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "Screen capture";

        _frozen.Source = new CroppedBitmap(owner.SnapshotSource, new Int32Rect(
            monitor.Bounds.X - owner.Virtual.X, monitor.Bounds.Y - owner.Virtual.Y, monitor.Bounds.Width, monitor.Bounds.Height));
        RenderOptions.SetBitmapScalingMode(_frozen, BitmapScalingMode.NearestNeighbor);
        _surface = new SelectorSurface(owner, monitor);
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.NearestNeighbor);

        var root = new Grid();
        root.Children.Add(_frozen);
        root.Children.Add(_surface);
        root.Children.Add(_chromeHost);
        Content = root;
        UpdateBackdrop();
        RebuildChrome();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            if (!preview) Place();
        };
        Loaded += (_, _) =>
        {
            if (!preview) Place();
        };

        _surface.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _surface.CaptureMouse();
            if (Native.GetCursorPos(out var p)) _owner.PointerPressed(p.X, p.Y);
        };
        _surface.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _surface.ReleaseMouseCapture();
            if (Native.GetCursorPos(out var p)) _owner.PointerReleased(p.X, p.Y);
        };
        _surface.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _owner.KeyPressed(Key.Escape, ModifierKeys.None);
        };
        MouseMove += (_, _) =>
        {
            if (Native.GetCursorPos(out var p)) _owner.PointerMoved(p.X, p.Y);
        };
        MouseWheel += (_, e) =>
        {
            e.Handled = true;
            _owner.Wheel(e.Delta);
        };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Tab) return;
            e.Handled = true;
            _owner.KeyPressed(key, Keyboard.Modifiers);
        };
        Deactivated += (_, _) => { /* Clicking another overlay moves focus there; nothing to do. */ };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
        {
            // WPF applies the suggested rectangle; put the overlay back on its exact monitor afterwards.
            Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        return IntPtr.Zero;
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _closing) return;
        var b = Monitor.Bounds;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
    }

    public void ActivateForKeyboard()
    {
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.GetForegroundWindow() != hwnd) WindowHelper.Activate(hwnd);
        Focus();
        Keyboard.Focus(this);
    }

    public void UpdateBackdrop() => _frozen.Visibility = _owner.IsFrozen ? Visibility.Visible : Visibility.Hidden;

    public void Refresh()
    {
        _surface.InvalidateVisual();
        var show = _owner.PointerMonitor == Monitor && !_owner.Dragging;
        _chromeHost.Visibility = show ? Visibility.Visible : Visibility.Hidden;
    }

    public void CloseQuietly()
    {
        _closing = true;
        try { Close(); }
        catch (Exception ex) { Log.Warn($"Closing a capture overlay failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ chrome

    public void RebuildChrome()
    {
        _chromeHost.Children.Clear();
        _chromeHost.Children.Add(SelectorChrome.Build(_owner));
    }
}

/// <summary>Draws dim, selection, window highlight, last-area ghost and the pixel loupe.</summary>
internal sealed class SelectorSurface : FrameworkElement
{
    private static readonly Color SelectionBlue = Color.FromRgb(46, 140, 255);
    private static readonly Color HighlightBlue = Color.FromRgb(89, 158, 255);
    private readonly CaptureSelector _owner;
    private readonly SelectorMonitor _monitor;

    public SelectorSurface(CaptureSelector owner, SelectorMonitor monitor)
    {
        _owner = owner;
        _monitor = monitor;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var s = dpi.DpiScaleX;
        var w = _monitor.Bounds.Width;
        var h = _monitor.Bounds.Height;
        var full = new Rect(0, 0, w, h);
        dc.PushTransform(new ScaleTransform(1 / s, 1 / s));

        // A nearly invisible fill keeps a transparent overlay hit-testable everywhere.
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, full);

        var dim = new SolidColorBrush(Color.FromArgb((byte)(255 * CaptureGeometry.DimAlpha(_owner.IsFrozen)), 0, 0, 0));
        var selection = _owner.Selection.Intersect(_monitor.Bounds).Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y);
        var hovered = _owner.HoveredWindow;
        if (!selection.IsEmpty)
        {
            var hole = ToRect(selection);
            dc.DrawGeometry(dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(hole, 8 * s, 8 * s)));
            DrawSelectionChrome(dc, hole, s, dpi.PixelsPerDip);
        }
        else if (hovered is not null && _owner.HighlightWindows)
        {
            var local = hovered.Bounds.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y);
            var r = ToRect(local);
            dc.DrawGeometry(dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r)));
            var inset = r;
            inset.Inflate(-1.25 * s, -1.25 * s);
            if (inset.Width > 0 && inset.Height > 0)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(36, HighlightBlue.R, HighlightBlue.G, HighlightBlue.B)),
                    new Pen(new SolidColorBrush(Color.FromArgb(242, HighlightBlue.R, HighlightBlue.G, HighlightBlue.B)), 2.5 * s), inset, 9 * s, 9 * s);
            }
        }
        else
        {
            dc.DrawRectangle(dim, null, full);
        }

        if (_owner.ShowLastRegion && selection.IsEmpty && !_owner.Dragging && _owner.Tool != CaptureTool.Color && _owner.LastRegion is { } last)
        {
            var ghost = last.Intersect(_monitor.Bounds).Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y);
            if (!ghost.IsEmpty)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(166, 255, 255, 255)), 1 * s) { DashStyle = new DashStyle([5, 4], 0) };
                dc.DrawRectangle(null, pen, ToRect(ghost));
            }
        }

        if (_owner.LoupeVisible && _owner.PointerMonitor == _monitor) DrawLoupe(dc, s, dpi.PixelsPerDip);
        dc.Pop();
    }

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private void DrawSelectionChrome(DrawingContext dc, Rect hole, double s, double ppd)
    {
        var outer = hole;
        outer.Inflate(1.5 * s, 1.5 * s);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, SelectionBlue.R, SelectionBlue.G, SelectionBlue.B)), 7 * s), outer, 9 * s, 9 * s);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(250, SelectionBlue.R, SelectionBlue.G, SelectionBlue.B)), 3 * s), outer, 9 * s, 9 * s);
        var inner = hole;
        inner.Inflate(0.5 * s, 0.5 * s);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)), 1 * s), inner, 8 * s, 8 * s);
        var sel = _owner.Selection;
        DrawBadge(dc, $"{sel.Width} \u00D7 {sel.Height}", new Point(hole.X + hole.Width / 2, hole.Bottom + 10 * s), s, ppd);
    }

    private void DrawBadge(DrawingContext dc, string text, Point anchor, double s, double ppd)
    {
        var ft = Text(text, 11 * s, ppd);
        var rect = new Rect(anchor.X - ft.Width / 2 - 7 * s, anchor.Y, ft.Width + 14 * s, ft.Height + 6 * s);
        var bw = _monitor.Bounds.Width;
        var bh = _monitor.Bounds.Height;
        if (rect.Bottom > bh - 6 * s) rect.Y = anchor.Y - rect.Height - 30 * s;
        rect.X = Math.Clamp(rect.X, 6 * s, Math.Max(6 * s, bw - rect.Width - 6 * s));
        rect.Y = Math.Clamp(rect.Y, 6 * s, Math.Max(6 * s, bh - rect.Height - 6 * s));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)), null, rect, 5 * s, 5 * s);
        dc.DrawText(ft, new Point(rect.X + 7 * s, rect.Y + 3 * s));
    }

    private static FormattedText Text(string text, double size, double ppd) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, Brushes.White, ppd);

    private void DrawLoupe(DrawingContext dc, double s, double ppd)
    {
        var snapshot = _owner.Snapshot;
        var px = _owner.Pointer.X - _owner.Virtual.X;
        var py = _owner.Pointer.Y - _owner.Virtual.Y;
        var side = CaptureGeometry.LoupeSampleSide(_owner.LoupeZoom);
        var source = CaptureGeometry.LoupeSampleRect(px, py, snapshot.Width, snapshot.Height, side);
        if (source.IsEmpty) return;
        var sample = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null,
            PixelOps.Crop(snapshot.Pixels, snapshot.Width, source), source.Width * 4);

        var frameSide = CaptureGeometry.LoupeFrameSide * s;
        var infoHeight = 24 * s;
        var gap = 6 * s;
        var localX = _owner.Pointer.X - _monitor.Bounds.X;
        var localY = _owner.Pointer.Y - _monitor.Bounds.Y;
        var (ox, oy) = CaptureGeometry.LoupeOrigin(localX, localY, frameSide, frameSide + gap + infoHeight,
            _monitor.Bounds.Width, _monitor.Bounds.Height, 16 * s, 8 * s);
        var frame = new Rect(ox, oy, frameSide, frameSide);
        var clip = new RectangleGeometry(frame, 9 * s, 9 * s);
        dc.PushClip(clip);
        dc.DrawImage(sample, frame);
        var cell = frameSide / source.Width;
        if (CaptureGeometry.LoupeGridVisible(CaptureGeometry.LoupeFrameSide, source.Width))
        {
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(72, 0, 0, 0)), 1);
            for (var i = 1; i < source.Width; i++)
            {
                var x = frame.X + i * cell;
                dc.DrawLine(gridPen, new Point(x, frame.Y), new Point(x, frame.Bottom));
                var y = frame.Y + i * cell;
                dc.DrawLine(gridPen, new Point(frame.X, y), new Point(frame.Right, y));
            }
        }
        // A ring around the target pixel (not lines through it) so its colour stays readable.
        var tx = frame.X + (px - source.X) * cell;
        var ty = frame.Y + (py - source.Y) * cell;
        var ring = new Rect(tx - 1, ty - 1, cell + 2, cell + 2);
        var reticle = new GeometryGroup();
        reticle.Children.Add(new RectangleGeometry(ring));
        reticle.Children.Add(new LineGeometry(new Point(ring.X + ring.Width / 2, frame.Y), new Point(ring.X + ring.Width / 2, ring.Y)));
        reticle.Children.Add(new LineGeometry(new Point(ring.X + ring.Width / 2, ring.Bottom), new Point(ring.X + ring.Width / 2, frame.Bottom)));
        reticle.Children.Add(new LineGeometry(new Point(frame.X, ring.Y + ring.Height / 2), new Point(ring.X, ring.Y + ring.Height / 2)));
        reticle.Children.Add(new LineGeometry(new Point(ring.Right, ring.Y + ring.Height / 2), new Point(frame.Right, ring.Y + ring.Height / 2)));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(194, 0, 0, 0)), 3), reticle);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1), reticle);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)), 1.5 * s), frame, 9 * s, 9 * s);

        // Readout bar: swatch, the value C would copy, and the position in display pixels.
        var (swatch, text) = _owner.LoupeReadout();
        var ft = Text(text, 10.5 * s, ppd);
        var swatchSide = 12 * s;
        var content = swatchSide + 7 * s + ft.Width;
        var barWidth = Math.Min(Math.Max(content + 20 * s, frameSide), _monitor.Bounds.Width - 12 * s);
        var bar = new Rect(frame.X + frame.Width / 2 - barWidth / 2, frame.Bottom + gap, barWidth, infoHeight);
        bar.X = Math.Clamp(bar.X, 6 * s, Math.Max(6 * s, _monitor.Bounds.Width - bar.Width - 6 * s));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)), null, bar, 7 * s, 7 * s);
        var cursor = bar.X + (bar.Width - content) / 2;
        var swatchRect = new Rect(cursor, bar.Y + (bar.Height - swatchSide) / 2, swatchSide, swatchSide);
        dc.DrawRectangle(new SolidColorBrush(swatch), new Pen(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)), 1), swatchRect);
        dc.DrawText(ft, new Point(cursor + swatchSide + 7 * s, bar.Y + (bar.Height - ft.Height) / 2));
    }
}
