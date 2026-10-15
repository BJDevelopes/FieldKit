// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Fieldkit.Features.Capture;

/// <summary>Camera preview settings keys.</summary>
public static class CameraSettings
{
    public const string Shortcut = "cameraPreview.shortcut";
    public const string ShortcutDefault = "Ctrl+Alt+Shift+W";
    public const string Device = "cameraPreview.deviceId";
    public const string Mirror = "cameraPreview.mirror";
    public const string Shape = "cameraPreview.shape";
    public const string Size = "cameraPreview.size";
    public const string CloseOnClickAway = "cameraPreview.closeOnClickAway";

    public static (double Width, double Height) Dimensions(string size, bool circle)
    {
        var w = size switch { "small" => 240.0, "large" => 480.0, _ => 320.0 };
        return circle ? (w * 0.75, w * 0.75) : (w, w * 0.75);
    }
}

/// <summary>Camera preview feature: a shortcut toggles the floating mirror; uninstalling closes it and frees the camera.</summary>
public sealed class CameraPreviewService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "cameraPreview";

    protected override void OnStart() => _hotkey = Hotkeys.Bind(CameraSettings.Shortcut, string.Empty, CameraPreviewWindow.Toggle);

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        CameraPreviewWindow.CloseCurrent();
    }
}

/// <summary>
/// The floating mirror (upstream CameraPreviewView): live camera image, round or rounded, mirrored,
/// with a camera picker on hover. Esc, a click elsewhere or switching apps closes it. Nothing is
/// recorded or leaves the PC.
/// </summary>
public sealed class CameraPreviewWindow : Window
{
    private static CameraPreviewWindow? _current;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Grid _status = new();
    private readonly Border _controls;
    private readonly ComboBox _cameras = new() { MinWidth = 150, MaxWidth = 220 };
    private readonly bool _preview;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _bitmap;
    private int _pendingFrame;
    private bool _closing;
    private bool _starting;
    private List<DeviceInformation> _devices = [];

    public static void Toggle()
    {
        if (_current is not null)
        {
            _current.Close();
            return;
        }
        Open();
    }

    public static void Open()
    {
        if (!FeatureCatalog.IsAvailable("cameraPreview")) return;
        PanelHost.Hide();
        if (_current is not null)
        {
            _current.Activate();
            return;
        }
        _current = new CameraPreviewWindow(preview: false);
        _current.Show();
        _current.Activate();
    }

    public static void CloseCurrent() => _current?.Close();

    private CameraPreviewWindow(bool preview)
    {
        _preview = preview;
        Title = "Camera preview";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Left = -32000;

        var circle = Settings.GetString(CameraSettings.Shape, "rounded") == "circle";
        var (w, h) = CameraSettings.Dimensions(Settings.GetString(CameraSettings.Size, "medium"), circle);
        if (Settings.GetBool(CameraSettings.Mirror, true))
        {
            _image.RenderTransformOrigin = new Point(0.5, 0.5);
            _image.RenderTransform = new ScaleTransform(-1, 1);
        }
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _cameras.SelectionChanged += async (_, _) =>
        {
            if (_preview || _starting || _cameras.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            Settings.Set(CameraSettings.Device, id);
            await RestartAsync();
        };
        var close = Ui.IconButton(Glyphs.Close, "Close", Close, 11, "TextOnAccent");
        _controls = new Border
        {
            Child = Ui.Row(6, Ui.Icon(Glyphs.Webcam, 12, "TextOnAccent"), _cameras, close),
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var surface = new Grid { Width = w, Height = h, Background = Brushes.Black, Cursor = Cursors.SizeAll };
        surface.Children.Add(_image);
        surface.Children.Add(_status);
        surface.Children.Add(_controls);
        surface.Clip = circle
            ? new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2)
            : new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
        var frame = new Border
        {
            Child = surface,
            CornerRadius = circle ? new CornerRadius(w / 2) : new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.4, Direction = 270 },
        };
        Content = frame;
        ShowStatus(null, "Starting the camera\u2026");

        surface.MouseEnter += (_, _) =>
        {
            if (_devices.Count > 1 || !_preview) _controls.Visibility = Visibility.Visible;
        };
        surface.MouseLeave += (_, _) => _controls.Visibility = Visibility.Collapsed;
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement fe && (fe.TemplatedParent is ComboBox or Button || fe is ComboBox)) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Deactivated += (_, _) =>
        {
            if (!_preview && IsVisible && !_cameras.IsDropDownOpen && Settings.GetBool(CameraSettings.CloseOnClickAway, true)) Close();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += async (_, _) =>
        {
            if (_preview) return;
            PlaceTopCenter();
            await StartAsync();
        };
        Closed += async (_, _) =>
        {
            _closing = true;
            if (ReferenceEquals(_current, this)) _current = null;
            await StopAsync();
        };
    }

    private void PlaceTopCenter()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var p);
        var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
        Native.GetWindowRect(hwnd, out var r);
        var scale = WindowHelper.GetScaleAt(p.X, p.Y);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Left + (work.Width - r.Width) / 2, work.Top + (int)(36 * scale), 0, 0, Native.SWP_NOSIZE);
    }

    private void ShowStatus(string? glyph, string? message, Action? action = null, string? actionTitle = null)
    {
        _status.Children.Clear();
        if (message is null)
        {
            _status.Visibility = Visibility.Collapsed;
            return;
        }
        _status.Visibility = Visibility.Visible;
        var text = new TextBlock { Text = message, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12.5, MaxWidth = 220 };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (glyph is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(Glyphs.FontFamily),
                FontSize = 26,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
        stack.Children.Add(text);
        if (action is not null)
        {
            var button = Ui.Button(actionTitle ?? "Open settings", action);
            button.Margin = new Thickness(0, 10, 0, 0);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(button);
        }
        _status.Children.Add(stack);
    }

    private async Task StartAsync()
    {
        if (_preview) return; // previews never touch a camera
        _starting = true;
        try
        {
            _devices = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).ToList();
            _cameras.Items.Clear();
            if (_devices.Count == 0)
            {
                ShowStatus(Glyphs.Webcam, "No camera detected");
                return;
            }
            var chosen = Settings.GetString(CameraSettings.Device, string.Empty);
            var device = _devices.FirstOrDefault(d => d.Id == chosen) ?? _devices[0];
            foreach (var d in _devices)
            {
                var item = new ComboBoxItem { Content = d.Name, Tag = d.Id };
                _cameras.Items.Add(item);
                if (d.Id == device.Id) _cameras.SelectedItem = item;
            }
            _cameras.Visibility = _devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            await OpenCameraAsync(device.Id);
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("\uE8B8", "Camera access for Fieldkit is turned off in Windows Settings.",
                () => Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Starting the camera preview failed", ex);
            ShowStatus(Glyphs.Webcam, "The camera is unavailable", () => _ = RestartAsync(), "Try again");
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task OpenCameraAsync(string deviceId)
    {
        ShowStatus(null, "Starting the camera\u2026");
        Exception? last = null;
        // Exclusive first (best format); shared when a call app already holds the camera.
        foreach (var mode in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
        {
            var capture = new MediaCapture();
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    SharingMode = mode,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
                var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview)
                             ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                             ?? throw new InvalidOperationException("The camera has no colour stream.");
                if (mode == MediaCaptureSharingMode.ExclusiveControl)
                {
                    // A modest format keeps the mirror light: about 720p at up to 30 fps.
                    var format = source.SupportedFormats
                        .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Width >= 320)
                        .OrderByDescending(f => f.VideoFormat.Width)
                        .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
                        .FirstOrDefault();
                    if (format is not null) await source.SetFormatAsync(format);
                }
                var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                reader.FrameArrived += OnFrameArrived;
                var status = await reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"Camera start: {status}");
                if (_closing)
                {
                    await reader.StopAsync();
                    reader.Dispose();
                    capture.Dispose();
                    return;
                }
                _capture = capture;
                _reader = reader;
                return;
            }
            catch (UnauthorizedAccessException)
            {
                capture.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                capture.Dispose();
            }
        }
        throw last ?? new InvalidOperationException("The camera is unavailable");
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_closing || Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
            {
                _pendingFrame = 0;
                return;
            }
            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && bitmap.BitmapAlphaMode != BitmapAlphaMode.Straight
                ? SoftwareBitmap.Copy(bitmap)
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var w = bgra.PixelWidth;
            var h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_closing) return;
                    if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    {
                        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                        _image.Source = _bitmap;
                        ShowStatus(null, null);
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
                }
                finally
                {
                    _pendingFrame = 0;
                }
            });
        }
        catch (Exception ex)
        {
            _pendingFrame = 0;
            Log.Warn($"Camera frame failed: {ex.Message}");
        }
    }

    private async Task RestartAsync()
    {
        await StopAsync();
        _bitmap = null;
        _image.Source = null;
        await StartAsync();
    }

    private async Task StopAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
                reader.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Stopping the camera reader failed: {ex.Message}");
        }
        capture?.Dispose();
    }

    /// <summary>Developer preview with a still picture instead of the camera.</summary>
    public static void RenderPreview(string path, RasterImage sample)
    {
        var window = new CameraPreviewWindow(preview: true) { Left = -20000, Top = -20000, ShowActivated = false };
        window._image.Source = sample.ToBitmapSource();
        window.ShowStatus(null, null);
        window._controls.Visibility = Visibility.Visible;
        window._cameras.Items.Add(new ComboBoxItem { Content = "Integrated Camera", Tag = "x" });
        window._cameras.SelectedIndex = 0;
        window.Show();
        PreviewRendering.SaveWindow(window, path);
        window._closing = true;
        window.Close();
    }
}
