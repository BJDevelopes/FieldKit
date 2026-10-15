// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>What the selector hands to the tool that asked for it.</summary>
public abstract record CaptureOutcome
{
    /// <summary>Pixels of an area, window or screen (screenshot and text).</summary>
    public sealed record Image(RasterImage Picture, PixelRect ScreenRect, IntPtr Window) : CaptureOutcome;

    /// <summary>Where to record: an area or a whole monitor, or a picked window.</summary>
    public sealed record Region(PixelRect ScreenRect, IntPtr Window, IntPtr Monitor) : CaptureOutcome;

    /// <summary>An area to capture while it scrolls.</summary>
    public sealed record Scrolling(PixelRect ScreenRect) : CaptureOutcome;

    public sealed record ColorSample(byte R, byte G, byte B) : CaptureOutcome;
}

/// <summary>One monitor of the session, in physical pixels.</summary>
internal sealed record SelectorMonitor(IntPtr Handle, PixelRect Bounds, double Scale);

/// <summary>
/// The shared capture surface: one borderless overlay per monitor showing a frozen photo of the
/// screen, where the person drags an area, clicks a window or presses Enter for the whole screen,
/// with a mode bar (Screenshot / Record / Text / Color) and a pixel loupe. Only one session at a time.
/// </summary>
public sealed class CaptureSelector
{
    private static CaptureSelector? _active;
    private static PixelRect? _lastRegion;

    private readonly Action<CaptureTool, CaptureOutcome> _route;
    private readonly List<SelectorWindow> _overlays = [];
    private readonly bool _freezeSetting;
    private bool _finished;
    private DispatcherTimer? _flashTimer;

    internal IReadOnlyList<CaptureTool> Tools { get; }
    internal CaptureTool Tool { get; private set; }
    internal bool ShowMenu { get; }
    internal RasterImage Snapshot { get; }
    internal BitmapSource SnapshotSource { get; }
    internal PixelRect Virtual { get; }
    internal IReadOnlyList<SelectorMonitor> Monitors { get; }
    internal IReadOnlyList<PickableWindow> Windows { get; }
    internal bool LoupeOn { get; private set; }
    internal double LoupeZoom { get; private set; }
    internal bool ScrollingOn { get; private set; }
    internal bool HighlightWindows { get; }
    internal bool ShowLastRegion { get; }
    internal (int X, int Y) Pointer { get; private set; }
    internal bool PointerKnown { get; private set; }
    internal bool Dragging { get; private set; }
    internal PixelRect Selection { get; private set; }
    internal string? Flash { get; private set; }
    private (int X, int Y) _dragStart;
    private SelectorMonitor? _dragMonitor;

    public static bool IsActive => _active is not null;

    /// <summary>The remembered drag area, if its monitor is still there.</summary>
    internal PixelRect? LastRegion =>
        _lastRegion is { } r && Monitors.Any(m => !m.Bounds.Intersect(r).IsEmpty) ? r : null;

    /// <summary>Frozen picture shown (always, except screenshot with freeze off).</summary>
    internal bool IsFrozen => Tool != CaptureTool.Screenshot || _freezeSetting;

    internal SelectorMonitor? PointerMonitor =>
        PointerKnown ? Monitors.FirstOrDefault(m => m.Bounds.Contains(Pointer.X, Pointer.Y)) : null;

    internal PickableWindow? HoveredWindow =>
        Tool == CaptureTool.Color || Dragging || !PointerKnown ? null : CaptureGeometry.WindowAt(Pointer.X, Pointer.Y, Windows);

    private CaptureSelector(IReadOnlyList<CaptureTool> tools, CaptureTool tool, bool showMenu, Action<CaptureTool, CaptureOutcome> route,
        IReadOnlyList<SelectorMonitor> monitors, RasterImage snapshot, IReadOnlyList<PickableWindow> windows, (int X, int Y)? pointer)
    {
        Tools = tools;
        Tool = tool;
        ShowMenu = showMenu;
        _route = route;
        _freezeSetting = Settings.GetBool(CaptureSettings.Freeze, true);
        HighlightWindows = Settings.GetBool(CaptureSettings.HighlightWindows, true);
        ShowLastRegion = Settings.GetBool(CaptureSettings.ShowLastRegion, true);
        LoupeOn = Settings.GetBool(CaptureSettings.LoupeStartsOn, false);
        LoupeZoom = CaptureGeometry.LoupeInitialZoom(
            Settings.GetBool(CaptureSettings.LoupeRememberZoom, false),
            Settings.GetDouble(CaptureSettings.LoupeDefaultZoom, 1),
            Settings.GetDouble(CaptureSettings.LoupeLastZoom, 1));
        Monitors = monitors;
        Virtual = Monitors.Aggregate(PixelRect.Empty, (acc, m) => acc.Union(m.Bounds));
        Snapshot = snapshot;
        SnapshotSource = Snapshot.ToBitmapSource();
        Windows = windows;
        if (pointer is { } p)
        {
            Pointer = p;
            PointerKnown = true;
        }
    }

    /// <summary>Photographs every monitor and lists the pickable windows.</summary>
    private static CaptureSelector FromScreen(IReadOnlyList<CaptureTool> tools, CaptureTool tool, bool showMenu, Action<CaptureTool, CaptureOutcome> route)
    {
        var monitors = WindowHelper.GetMonitors()
            .Select(m => new SelectorMonitor(m.Handle, PixelRect.FromLtrb(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom),
                WindowHelper.GetScaleAt(m.Bounds.Left + 1, m.Bounds.Top + 1)))
            .ToList();
        var virtualScreen = monitors.Aggregate(PixelRect.Empty, (acc, m) => acc.Union(m.Bounds));
        var hideOwn = Settings.GetBool(CaptureSettings.HideOwnWindows, true) || tool == CaptureTool.Recording;
        var withPointer = tool == CaptureTool.Screenshot && Settings.GetBool(CaptureSettings.IncludePointer, false);
        var snapshot = ScreenGrabber.Capture(virtualScreen, withPointer, hideOwn);
        var windows = ScreenGrabber.PickableWindows(!hideOwn, new HashSet<IntPtr>());
        (int, int)? pointer = Native.GetCursorPos(out var p) ? (p.X, p.Y) : null;
        return new CaptureSelector(tools, tool, showMenu, route, monitors, snapshot, windows, pointer);
    }

    /// <summary>
    /// Developer preview: one overlay over a synthetic screen (no real capture) with a hovered window,
    /// a drag selection and the loupe, saved as PNG.
    /// </summary>
    public static void RenderPreview(string path, RasterImage sample, CaptureTool tool = CaptureTool.Screenshot, bool dragging = false)
    {
        var monitor = new SelectorMonitor(IntPtr.Zero, new PixelRect(0, 0, sample.Width, sample.Height), 1);
        var windows = new List<PickableWindow> { new(IntPtr.Zero, new PixelRect(sample.Width / 12, sample.Height / 10, sample.Width * 6 / 10, sample.Height * 7 / 10), "Sample") };
        var session = new CaptureSelector([CaptureTool.Screenshot, CaptureTool.Recording, CaptureTool.Text, CaptureTool.Color], tool, true, (_, _) => { },
            [monitor], sample, windows, (sample.Width / 3, sample.Height / 2))
        {
            LoupeOn = true,
        };
        if (dragging)
        {
            session.Selection = new PixelRect(sample.Width / 5, sample.Height / 5, sample.Width / 3, sample.Height / 4);
        }
        // The overlay is sized in physical pixels; at 125 % that is fewer device-independent units.
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual());
        var window = new SelectorWindow(session, monitor, transparent: false, preview: true)
        {
            Left = -30000,
            Top = -30000,
            Width = sample.Width / dpi.DpiScaleX,
            Height = sample.Height / dpi.DpiScaleY,
            Topmost = false,
        };
        session._overlays.Add(window);
        window.Show();
        window.Refresh();
        PreviewRendering.SaveWindow(window, path);
        session._finished = true;
        window.CloseQuietly();
    }

    /// <summary>
    /// Opens the selector on <paramref name="initial"/>. Tools whose feature is uninstalled are not
    /// offered; the routing callback runs after the overlays are gone.
    /// </summary>
    public static void Begin(CaptureTool initial, bool fromShortcut, Action<CaptureTool, CaptureOutcome> route, bool scrolling = false)
    {
        if (_active is not null) return;
        var tools = CaptureSettings.AvailableTools();
        if (tools.Count == 0) return;
        var tool = tools.Contains(initial) ? initial : tools[0];
        var showMenu = !fromShortcut || Settings.GetBool(CaptureSettings.ShowMenuKey(tool), true);
        PanelHost.Hide();
        try
        {
            var session = FromScreen(tools, tool, showMenu, route);
            session.ScrollingOn = scrolling && tool == CaptureTool.Screenshot;
            _active = session;
            session.Present();
        }
        catch (Exception ex)
        {
            _active = null;
            Log.Error("Opening the capture selector failed", ex);
            Hud.Show(Glyphs.Warning, "The screen could not be captured");
        }
    }

    /// <summary>Closes any open selector without a result (feature switched off, app exit).</summary>
    public static void CancelActive() => _active?.Finish(null);

    private void Present()
    {
        foreach (var monitor in Monitors)
        {
            var window = new SelectorWindow(this, monitor, transparent: !_freezeSetting);
            _overlays.Add(window);
            window.Show();
        }
        // Keyboard goes to the overlay under the pointer.
        var target = _overlays.FirstOrDefault(o => o.Monitor == PointerMonitor) ?? _overlays.FirstOrDefault();
        target?.ActivateForKeyboard();
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var o in _overlays) o.Refresh();
    }

    // ------------------------------------------------------------------ input

    internal void PointerMoved(int x, int y)
    {
        Pointer = (x, y);
        PointerKnown = true;
        if (Dragging && _dragMonitor is not null)
        {
            var cx = Math.Clamp(x, _dragMonitor.Bounds.X, _dragMonitor.Bounds.Right);
            var cy = Math.Clamp(y, _dragMonitor.Bounds.Y, _dragMonitor.Bounds.Bottom);
            Selection = CaptureGeometry.IsDrag(_dragStart.X, _dragStart.Y, cx, cy)
                ? PixelRect.FromPoints(_dragStart.X, _dragStart.Y, cx, cy)
                : PixelRect.Empty;
        }
        Refresh();
    }

    internal void PointerPressed(int x, int y)
    {
        if (_finished) return;
        Pointer = (x, y);
        if (Tool == CaptureTool.Color)
        {
            ConfirmColor(x, y);
            return;
        }
        _dragMonitor = Monitors.FirstOrDefault(m => m.Bounds.Contains(x, y));
        _dragStart = (x, y);
        Dragging = true;
        Selection = PixelRect.Empty;
        Refresh();
    }

    internal void PointerReleased(int x, int y)
    {
        if (_finished || !Dragging) return;
        Dragging = false;
        var rect = Selection;
        Selection = PixelRect.Empty;
        if (!rect.IsEmpty && rect.Width >= 2 && rect.Height >= 2)
        {
            _lastRegion = rect;
            ConfirmRegion(rect, IntPtr.Zero);
            return;
        }
        // A click: the window under the pointer, or the whole screen when there is none.
        var window = CaptureGeometry.WindowAt(x, y, Windows);
        if (window is not null)
        {
            var bounds = window.Bounds.Intersect(Virtual);
            if (!bounds.IsEmpty)
            {
                ConfirmRegion(bounds, window.Handle);
                return;
            }
        }
        CaptureFullScreen();
    }

    internal void Wheel(int delta)
    {
        if (!LoupeVisible) return;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        LoupeZoom = CaptureGeometry.UsesSteppedZoom(Settings.GetBool(CaptureSettings.LoupeStepped, false), alt)
            ? CaptureGeometry.LoupeSteppedZoom(LoupeZoom, delta)
            : CaptureGeometry.LoupeFastZoom(LoupeZoom, delta);
        Settings.Set(CaptureSettings.LoupeLastZoom, LoupeZoom);
        Refresh();
    }

    internal bool LoupeVisible => Tool == CaptureTool.Color || LoupeOn;

    internal void KeyPressed(Key key, ModifierKeys modifiers)
    {
        if (_finished) return;
        switch (key)
        {
            case Key.Escape:
                Finish(null);
                return;
            case Key.Enter when Tool != CaptureTool.Color && !ScrollingOn:
                CaptureFullScreen();
                return;
            case >= Key.D1 and <= Key.D4:
            case >= Key.NumPad1 and <= Key.NumPad4:
            {
                var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
                var tool = (CaptureTool)index;
                if (Tools.Contains(tool)) SelectTool(tool);
                return;
            }
            case Key.Z when Tool != CaptureTool.Color:
                LoupeOn = !LoupeOn;
                Refresh();
                return;
            case Key.C when LoupeVisible && Tool != CaptureTool.Color:
                CopyColorUnderPointer();
                return;
            case Key.S when Tool == CaptureTool.Screenshot:
                ScrollingOn = !ScrollingOn;
                Refresh();
                return;
            case Key.R when Tool != CaptureTool.Color && LastRegion is { } last:
                ConfirmRegion(last, IntPtr.Zero);
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down when LoupeVisible && !Dragging:
            {
                var step = CaptureGeometry.NudgeStep((modifiers & ModifierKeys.Shift) != 0);
                var dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                var dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                var nx = Math.Clamp(Pointer.X + dx, Virtual.X, Virtual.Right - 1);
                var ny = Math.Clamp(Pointer.Y + dy, Virtual.Y, Virtual.Bottom - 1);
                if (Monitors.Any(m => m.Bounds.Contains(nx, ny)))
                {
                    Native.SetCursorPos(nx, ny);
                    PointerMoved(nx, ny);
                }
                return;
            }
            case Key.Space when Tool == CaptureTool.Color:
                ConfirmColor(Pointer.X, Pointer.Y);
                return;
        }
    }

    internal void SelectTool(CaptureTool tool)
    {
        if (!Tools.Contains(tool) || tool == Tool) return;
        var wasFrozen = IsFrozen;
        Tool = tool;
        if (tool != CaptureTool.Screenshot) ScrollingOn = false;
        foreach (var o in _overlays)
        {
            o.RebuildChrome();
            if (wasFrozen != IsFrozen) o.UpdateBackdrop();
        }
        Refresh();
    }

    internal void SetScrolling(bool on)
    {
        ScrollingOn = on && Tool == CaptureTool.Screenshot;
        foreach (var o in _overlays) o.RebuildChrome();
        Refresh();
    }

    // ---------------------------------------------------------- confirmation

    internal void CaptureFullScreen()
    {
        var monitor = PointerMonitor ?? Monitors.FirstOrDefault();
        if (monitor is null) return;
        if (Tool == CaptureTool.Recording)
        {
            Finish(new CaptureOutcome.Region(monitor.Bounds, IntPtr.Zero, monitor.Handle));
            return;
        }
        ConfirmRegion(monitor.Bounds, IntPtr.Zero);
    }

    private void ConfirmRegion(PixelRect rect, IntPtr window)
    {
        switch (Tool)
        {
            case CaptureTool.Recording:
            {
                var monitor = Monitors.OrderByDescending(m => m.Bounds.Intersect(rect).Area).First();
                // An area is recorded on one monitor; it is trimmed to the monitor holding most of it.
                var clipped = window == IntPtr.Zero ? rect.Intersect(monitor.Bounds) : rect;
                Finish(new CaptureOutcome.Region(clipped, window, monitor.Handle));
                return;
            }
            case CaptureTool.Screenshot when ScrollingOn:
                Finish(new CaptureOutcome.Scrolling(rect));
                return;
            case CaptureTool.Screenshot when !IsFrozen:
                FinishLive(rect, window);
                return;
            default:
            {
                var local = rect.Offset(-Virtual.X, -Virtual.Y);
                var image = Snapshot.Crop(local);
                var scaled = new RasterImage(image.Width, image.Height, image.Pixels)
                {
                    Scale = Monitors.OrderByDescending(m => m.Bounds.Intersect(rect).Area).First().Scale,
                };
                Finish(new CaptureOutcome.Image(scaled, rect, window));
                return;
            }
        }
    }

    private void ConfirmColor(int x, int y)
    {
        var (r, g, b) = Snapshot.PixelAt(x - Virtual.X, y - Virtual.Y);
        Finish(new CaptureOutcome.ColorSample(r, g, b));
    }

    private void CopyColorUnderPointer()
    {
        var (r, g, b) = Snapshot.PixelAt(Pointer.X - Virtual.X, Pointer.Y - Virtual.Y);
        var value = ColorPickerService.CopyQuietly(r, g, b);
        Flash = "\u2713 " + value;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            Flash = null;
            Refresh();
        };
        _flashTimer.Start();
        Refresh();
    }

    /// <summary>Live mode: the overlays leave the screen first, then the pixels are taken.</summary>
    private void FinishLive(PixelRect rect, IntPtr window)
    {
        if (_finished) return;
        _finished = true;
        CloseOverlays();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _active = null;
            try
            {
                var image = ScreenGrabber.Capture(rect, Settings.GetBool(CaptureSettings.IncludePointer, false),
                    Settings.GetBool(CaptureSettings.HideOwnWindows, true));
                _route(Tool, new CaptureOutcome.Image(image, rect, window));
            }
            catch (Exception ex)
            {
                Log.Error("Live capture failed", ex);
                Hud.Show(Glyphs.Warning, "The screen could not be captured");
            }
        };
        timer.Start();
    }

    private void Finish(CaptureOutcome? outcome)
    {
        if (_finished) return;
        _finished = true;
        _flashTimer?.Stop();
        CloseOverlays();
        _active = null;
        if (outcome is null) return;
        var tool = Tool;
        // Let the overlays disappear before the tool shows its own windows.
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { _route(tool, outcome); }
            catch (Exception ex) { Log.Error("Routing a capture failed", ex); }
        });
    }

    private void CloseOverlays()
    {
        foreach (var o in _overlays) o.CloseQuietly();
        _overlays.Clear();
    }

    // ----------------------------------------------------------------- render

    /// <summary>Loupe readout: the value C would copy and the position in display pixels.</summary>
    internal (Color Swatch, string Text) LoupeReadout()
    {
        var (r, g, b) = Snapshot.PixelAt(Pointer.X - Virtual.X, Pointer.Y - Virtual.Y);
        var monitor = PointerMonitor;
        var px = monitor is null ? Pointer.X : Pointer.X - monitor.Bounds.X;
        var py = monitor is null ? Pointer.Y : Pointer.Y - monitor.Bounds.Y;
        var value = ColorPickerService.Formatted(r, g, b);
        var text = Flash ?? $"{value}   {px}, {py}";
        return (Color.FromRgb(r, g, b), text);
    }

    /// <summary>Subtitle of the guide for the current tool (upstream wording).</summary>
    internal string Subtitle => Tool switch
    {
        CaptureTool.Screenshot => "Drag to select an area  \u00B7  Click a window to capture it  \u00B7  "
                                  + (ScrollingOn ? "Scrolling on" : "S toggles scrolling"),
        CaptureTool.Recording => "Choose what to record  \u00B7  Drag to select an area  \u00B7  Click a window to capture it",
        CaptureTool.Text => "Select an area of the screen and the recognized text is copied, ready to paste.",
        _ => "Grab the color of any pixel on screen and copy it in your favorite format.",
    };
}
