// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Media;
using Fieldkit.UI;
using Windows.Media.Editing;
using Windows.Storage;

namespace Fieldkit.Features.Capture;

/// <summary>
/// The recording editor (upstream RecorderEditorView): preview, trim, cut out parts, automatic and
/// manual zooms, privacy blurs, per-track volume, export speed; save as MP4 or GIF, copy, or delete.
/// Effects other than trim and cuts are rendered by the local ffmpeg.
/// </summary>
public sealed class RecorderEditorWindow : Window
{
    private static readonly List<RecorderEditorWindow> OpenEditors = [];
    private readonly RecordingResult _result;
    private readonly RecordingEdit _edit;
    private readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    private readonly Grid _videoHost = new() { ClipToBounds = true };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _zoomScale = new();
    private readonly TranslateTransform _zoomShift = new();
    private readonly TimelineView _timeline;
    private readonly TextBlock _timeText = Ui.Text("0:00.0", 12.5, FontWeights.SemiBold, wrap: false);
    private readonly ContentControl _inspector = new();
    private readonly Button _playButton;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ProgressBar _progress = new() { Height = 4, Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    private CancellationTokenSource? _export;
    private bool _playing;
    private bool _saved;
    private bool _pickingSpot;
    private bool _pickingBlur;
    private Point _blurStart;
    private Rectangle? _blurDraft;
    internal object? Selected { get; private set; }
    internal TimeRange? Selection { get; set; }
    internal double Position { get; private set; }

    internal RecordingEdit Edit => _edit;
    internal RecordingResult Result => _result;

    public static void Open(RecordingResult result)
    {
        var editor = new RecorderEditorWindow(result);
        OpenEditors.Add(editor);
        editor.Show();
        editor.Activate();
    }

    public static void CloseAll()
    {
        foreach (var e in OpenEditors.ToList())
        {
            // Never lose a take: an unsaved one is kept unedited in the recordings folder.
            if (!e._saved)
            {
                e._player.Close();
                RecorderController.SaveDirect(e._result);
            }
            e._saved = true;
            e.Close();
        }
    }

    private RecorderEditorWindow(RecordingResult result, bool preview = false)
    {
        _result = result;
        _edit = RecordingEdit.ForDuration(result.Duration);
        _edit.ZoomAmount = Settings.GetDouble(CaptureSettings.RecorderZoomAmount, RecordingEditMath.DefaultZoomAmount);
        if (Settings.GetBool(CaptureSettings.RecorderAutomaticZoom, true))
            _edit.Zooms = RecordingEditMath.AutomaticZooms(result.Clicks, result.Duration, _edit.ZoomAmount);

        Title = "Recording";
        Width = 1180;
        Height = 820;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        _player.RenderTransformOrigin = new Point(0, 0);
        _player.RenderTransform = new TransformGroup { Children = { _zoomScale, _zoomShift } };
        _videoHost.Children.Add(_player);
        _videoHost.Children.Add(_overlay);
        _videoHost.SizeChanged += (_, _) => UpdateOverlay();
        var videoFrame = new Border { Child = _videoHost, Margin = new Thickness(18, 14, 18, 8), CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = Brushes.Black };
        _overlay.MouseLeftButtonDown += OnOverlayDown;
        _overlay.MouseMove += OnOverlayMove;
        _overlay.MouseLeftButtonUp += OnOverlayUp;

        _playButton = Ui.IconButton(Glyphs.Play, "Play", TogglePlay, 16, "TextPrimary");
        var transport = Ui.Split(Ui.Row(10, _playButton, _timeText), Ui.Row(10, SpeedControl()));
        transport.Margin = new Thickness(18, 0, 18, 6);

        _timeline = new TimelineView(this) { Height = 236, Margin = new Thickness(18, 0, 18, 6) };

        var inspectorHost = new Border { Child = _inspector, Padding = new Thickness(18, 4, 18, 8), MinHeight = 46 };
        var bottom = BuildBottomBar();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(videoFrame);
        Grid.SetRow(transport, 1);
        root.Children.Add(transport);
        Grid.SetRow(_timeline, 2);
        root.Children.Add(_timeline);
        Grid.SetRow(inspectorHost, 3);
        root.Children.Add(inspectorHost);
        Grid.SetRow(_progress, 4);
        root.Children.Add(_progress);
        Grid.SetRow(bottom, 5);
        root.Children.Add(bottom);
        Content = root;

        RebuildInspector();
        if (!preview && File.Exists(result.Path))
        {
            _player.Source = new Uri(result.Path);
            _player.MediaOpened += (_, _) =>
            {
                _player.Pause();
                Seek(0);
            };
            _player.Play();
            _ = LoadThumbnailsAsync();
        }
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        KeyDown += OnKey;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _tick.Stop();
            _export?.Cancel();
            _player.Close();
            OpenEditors.Remove(this);
            // The master lives in the private cache; it goes when its editor closes.
            if (!preview) RecorderController.TryDelete(result.Path);
        };
    }

    // ------------------------------------------------------------ playback

    private void TogglePlay()
    {
        if (_playing)
        {
            _player.Pause();
            _playing = false;
        }
        else
        {
            var kept = RecordingEditMath.KeptSegments(_edit);
            if (kept.Count == 0) return;
            if (Position >= kept[^1].End - 0.05 || Position < kept[0].Start) Seek(kept[0].Start);
            _player.Play();
            _playing = true;
        }
        if (_playButton.Content is TextBlock t) t.Text = _playing ? Glyphs.Pause : Glyphs.Play;
    }

    internal void Seek(double seconds)
    {
        Position = Math.Clamp(seconds, 0, _result.Duration);
        _player.Position = TimeSpan.FromSeconds(Position);
        UpdateZoomPreview();
        UpdateOverlay();
        UpdateTime();
        _timeline.InvalidateVisual();
    }

    private void OnTick()
    {
        if (!_playing) return;
        Position = _player.Position.TotalSeconds;
        // Playback follows the edit: cut parts are skipped and the trim end stops it.
        var kept = RecordingEditMath.KeptSegments(_edit);
        var inside = kept.FirstOrDefault(s => Position >= s.Start - 0.02 && Position < s.End);
        if (inside.Duration <= 0)
        {
            var next = kept.FirstOrDefault(s => s.Start > Position);
            if (next.Duration > 0) Seek(next.Start);
            else
            {
                TogglePlay();
                Seek(kept.Count > 0 ? kept[^1].End : 0);
            }
        }
        UpdateZoomPreview();
        UpdateOverlay();
        UpdateTime();
        _timeline.InvalidateVisual();
    }

    private void UpdateTime()
    {
        var exportPos = RecordingEditMath.SourceToExport(_edit, Position) ?? 0;
        _timeText.Text = $"{Format(exportPos)} / {Format(RecordingEditMath.ExportDuration(_edit))}";
    }

    internal static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    /// <summary>The preview shows the zoom the export will apply at this moment.</summary>
    private void UpdateZoomPreview()
    {
        var zooms = RecordingEditMath.NormalizedZooms(_edit.Zooms, _edit.Duration);
        var progress = RecordingEditMath.ZoomProgress(Position, zooms);
        var active = zooms.FirstOrDefault(z => Position >= z.Start - 0.001 && Position <= z.End + RecordingEditMath.ZoomRampOut);
        var amount = active is null ? 1 : 1 + (active.Amount - 1) * progress;
        var (vx, vy, _, _) = RecordingEditMath.Viewport(amount, active?.FocusX ?? 0.5, active?.FocusY ?? 0.5);
        var w = _player.ActualWidth;
        var h = _player.ActualHeight;
        _zoomScale.ScaleX = _zoomScale.ScaleY = amount;
        _zoomShift.X = -vx * w * amount;
        _zoomShift.Y = -vy * h * amount;
    }

    // ------------------------------------------------------------- overlay

    /// <summary>The video's drawn rectangle inside the host (letterboxed).</summary>
    private Rect VideoRect()
    {
        var hostW = _videoHost.ActualWidth;
        var hostH = _videoHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0 || _result.Width <= 0) return Rect.Empty;
        var s = Math.Min(hostW / _result.Width, hostH / _result.Height);
        var w = _result.Width * s;
        var h = _result.Height * s;
        return new Rect((hostW - w) / 2, (hostH - h) / 2, w, h);
    }

    private void UpdateOverlay()
    {
        _overlay.Children.Clear();
        var video = VideoRect();
        if (video.IsEmpty) return;
        foreach (var blur in _edit.Blurs)
        {
            var active = Position >= blur.Start && Position <= blur.End;
            var selected = ReferenceEquals(Selected, blur);
            if (!active && !selected) continue;
            var rect = new Rectangle
            {
                Width = blur.Width * video.Width,
                Height = blur.Height * video.Height,
                Fill = new SolidColorBrush(Color.FromArgb(150, 90, 90, 90)),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 184, 196)),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
            };
            Canvas.SetLeft(rect, video.X + blur.X * video.Width);
            Canvas.SetTop(rect, video.Y + blur.Y * video.Height);
            _overlay.Children.Add(rect);
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 184, 196)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 2, 10, 3),
                Child = new TextBlock { Text = "Privacy Blur", Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold, FontSize = 11.5 },
            };
            Canvas.SetLeft(label, video.X + blur.X * video.Width + 12);
            Canvas.SetTop(label, video.Y + blur.Y * video.Height - 12);
            _overlay.Children.Add(label);
        }
        if (Selected is ZoomSegment zoom && !_pickingSpot)
        {
            var dot = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(160, 10, 132, 255)) };
            Canvas.SetLeft(dot, video.X + zoom.FocusX * video.Width - 7);
            Canvas.SetTop(dot, video.Y + zoom.FocusY * video.Height - 7);
            _overlay.Children.Add(dot);
        }
        if (_blurDraft is not null) _overlay.Children.Add(_blurDraft);
        _overlay.Cursor = _pickingSpot || _pickingBlur ? Cursors.Cross : Cursors.Arrow;
    }

    private Point? Normalized(Point p)
    {
        var v = VideoRect();
        if (v.IsEmpty) return null;
        return new Point(Math.Clamp((p.X - v.X) / v.Width, 0, 1), Math.Clamp((p.Y - v.Y) / v.Height, 0, 1));
    }

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_pickingSpot && Selected is ZoomSegment zoom && Normalized(p) is { } n)
        {
            ReplaceZoom(zoom, zoom with { FocusX = n.X, FocusY = n.Y });
            _pickingSpot = false;
            RebuildInspector();
            return;
        }
        if (_pickingBlur)
        {
            _blurStart = p;
            _blurDraft = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(90, 0, 184, 196)) };
            _overlay.CaptureMouse();
            return;
        }
        TogglePlay();
    }

    private void OnOverlayMove(object sender, MouseEventArgs e)
    {
        if (_blurDraft is null) return;
        var p = e.GetPosition(_overlay);
        var r = new Rect(_blurStart, p);
        _blurDraft.Width = r.Width;
        _blurDraft.Height = r.Height;
        Canvas.SetLeft(_blurDraft, r.X);
        Canvas.SetTop(_blurDraft, r.Y);
        UpdateOverlay();
    }

    private void OnOverlayUp(object sender, MouseButtonEventArgs e)
    {
        if (_blurDraft is null) return;
        _overlay.ReleaseMouseCapture();
        var a = Normalized(_blurStart);
        var b = Normalized(e.GetPosition(_overlay));
        _blurDraft = null;
        _pickingBlur = false;
        if (a is { } p1 && b is { } p2 && Math.Abs(p2.X - p1.X) > 0.01 && Math.Abs(p2.Y - p1.Y) > 0.01)
        {
            var start = Position;
            var blur = new BlurRegion(start, Math.Min(_edit.Duration, start + 3), Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y));
            _edit.Blurs.Add(blur);
            Selected = blur;
            Changed();
        }
        RebuildInspector();
        UpdateOverlay();
    }

    // ------------------------------------------------------------ editing

    internal void Select(object? item)
    {
        Selected = item;
        _pickingSpot = false;
        RebuildInspector();
        UpdateOverlay();
        _timeline.InvalidateVisual();
    }

    internal void Changed()
    {
        _saved = false;
        UpdateZoomPreview();
        UpdateOverlay();
        UpdateTime();
        _timeline.InvalidateVisual();
    }

    internal void ReplaceZoom(ZoomSegment old, ZoomSegment updated)
    {
        var i = _edit.Zooms.IndexOf(old);
        if (i < 0) return;
        _edit.Zooms[i] = updated;
        if (ReferenceEquals(Selected, old)) Selected = updated;
        Changed();
    }

    internal void ReplaceBlur(BlurRegion old, BlurRegion updated)
    {
        var i = _edit.Blurs.IndexOf(old);
        if (i < 0) return;
        _edit.Blurs[i] = updated;
        if (ReferenceEquals(Selected, old)) Selected = updated;
        Changed();
    }

    internal void AddZoomAt(double t)
    {
        var zoom = new ZoomSegment(Math.Max(0, t - 0.3), Math.Min(_edit.Duration, t + 2.2), 0.5, 0.5, _edit.ZoomAmount);
        _edit.Zooms.Add(zoom);
        _edit.Zooms = RecordingEditMath.NormalizedZooms(_edit.Zooms, _edit.Duration);
        Select(_edit.Zooms.FirstOrDefault(z => z.Start <= t + 0.01 && z.End >= t - 0.01));
        Changed();
    }

    private void CutSelection()
    {
        if (Selection is not { } range || range.Duration < RecordingEditMath.MinimumCut) return;
        _edit.Cuts.Add(range);
        _edit.Cuts = RecordingEditMath.NormalizedCuts(_edit.Cuts, _edit.Duration);
        Selection = null;
        Changed();
        RebuildInspector();
    }

    private void RebuildInspector()
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Add(UIElement e)
        {
            if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 14, 3);
            row.Children.Add(e);
        }
        switch (Selected)
        {
            case ZoomSegment zoom:
                Add(Ui.Text("This zoom", 13, FontWeights.SemiBold, wrap: false));
                Add(InlineSlider("How close", zoom.Amount, RecordingEditMath.MinimumZoomAmount, RecordingEditMath.MaximumZoomAmount,
                    v => ReplaceZoom((ZoomSegment)Selected!, ((ZoomSegment)Selected!) with { Amount = v }), v => $"{v:0.0}\u00D7"));
                Add(Ui.Button("Pick a spot", () =>
                {
                    _pickingSpot = true;
                    Hud.Show(Glyphs.Info, "Click the picture to aim it");
                    UpdateOverlay();
                }));
                Add(Ui.Button("Remove", () =>
                {
                    _edit.Zooms.Remove((ZoomSegment)Selected!);
                    Select(null);
                    Changed();
                }));
                Add(Ui.Button("All options", () => Select(null)));
                break;
            case BlurRegion:
                Add(Ui.Text("This blur", 13, FontWeights.SemiBold, wrap: false));
                Add(Ui.Caption("Hidden for as long as its block lasts on the timeline."));
                Add(Ui.Button("Choose the area", () =>
                {
                    _edit.Blurs.Remove((BlurRegion)Selected!);
                    Selected = null;
                    _pickingBlur = true;
                    Hud.Show(Glyphs.Info, "Drag over what should stay hidden");
                    UpdateOverlay();
                }));
                Add(Ui.Button("Remove", () =>
                {
                    _edit.Blurs.Remove((BlurRegion)Selected!);
                    Select(null);
                    Changed();
                }));
                Add(Ui.Button("All options", () => Select(null)));
                break;
            default:
            {
                var cut = Ui.Button("Cut out", CutSelection, glyph: Glyphs.Cut);
                cut.IsEnabled = Selection is { Duration: >= RecordingEditMath.MinimumCut };
                cut.ToolTip = "Drag across the film to pick a part to remove";
                Add(cut);
                if (_edit.Cuts.Count > 0) Add(Ui.Button("Restore cuts", () =>
                {
                    _edit.Cuts.Clear();
                    Changed();
                    RebuildInspector();
                }));
                Add(Ui.Button("Add a zoom", () => AddZoomAt(Position), glyph: SelectorChrome.ZoomGlyph));
                Add(Ui.Button("Blur an area", () =>
                {
                    _pickingBlur = true;
                    Hud.Show(Glyphs.Info, "Drag over what should stay hidden");
                    UpdateOverlay();
                }, glyph: "\uED1A"));
                Add(Ui.Button(_result.Clicks.Count > 0 ? "Back to one per click" : "Create automatic zooms", () =>
                {
                    _edit.Zooms = RecordingEditMath.AutomaticZooms(_result.Clicks, _edit.Duration, _edit.ZoomAmount);
                    if (_edit.Zooms.Count == 0) Hud.Show(Glyphs.Info, "This recording has no clicks to zoom on. Zooms placed by hand still work.");
                    Changed();
                }));
                Add(InlineSlider("How close", _edit.ZoomAmount, RecordingEditMath.MinimumZoomAmount, RecordingEditMath.MaximumZoomAmount, v =>
                {
                    _edit.ZoomAmount = v;
                    Settings.Set(CaptureSettings.RecorderZoomAmount, v);
                    _edit.Zooms = _edit.Zooms.Select(z => z with { Amount = v }).ToList();
                    Changed();
                }, v => $"{v:0.0}\u00D7"));
                if (_result.HasSystemAudio) Add(AudioControls("PC sound", () => _edit.SystemAudioRemoved, v => _edit.SystemAudioRemoved = v, () => _edit.SystemAudioVolume, v => _edit.SystemAudioVolume = v));
                if (_result.HasMicrophone) Add(AudioControls("Microphone", () => _edit.MicrophoneRemoved, v => _edit.MicrophoneRemoved = v, () => _edit.MicrophoneVolume, v => _edit.MicrophoneVolume = v));
                break;
            }
        }
        _inspector.Content = row;
    }

    private FrameworkElement AudioControls(string title, Func<bool> removed, Action<bool> setRemoved, Func<double> volume, Action<double> setVolume)
    {
        var toggle = Ui.Button(removed() ? "Restore" : "Remove", () =>
        {
            setRemoved(!removed());
            Changed();
            RebuildInspector();
        });
        var slider = InlineSlider("Volume", volume(), 0, 2, v =>
        {
            setVolume(v);
            Changed();
        }, v => $"{v * 100:0}%");
        slider.IsEnabled = !removed();
        return Ui.Row(8, Ui.Text(title, 12.5, FontWeights.SemiBold, wrap: false), toggle, slider);
    }

    private static FrameworkElement InlineSlider(string label, double value, double min, double max, Action<double> onChange, Func<double, string> format)
    {
        var text = Ui.Text(format(value), 12, FontWeights.Medium, "TextSecondary", wrap: false);
        text.MinWidth = 38;
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = format(e.NewValue);
            onChange(e.NewValue);
        };
        return Ui.Row(8, Ui.Text(label, 12.5, FontWeights.Medium, "TextSecondary", wrap: false), slider, text);
    }

    private FrameworkElement SpeedControl()
    {
        var combo = new ComboBox { MinWidth = 90, ToolTip = "Applies to video, GIF and copies. The editing preview stays at 1\u00D7; the original recording is unchanged." };
        foreach (var s in RecordingEditMath.SpeedPresets) combo.Items.Add(new ComboBoxItem { Content = $"{s:0.##}\u00D7", Tag = s });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (double)i.Tag == 1);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem item) return;
            _edit.Speed = RecordingEditMath.SanitizedSpeed((double)item.Tag);
            Changed();
        };
        return Ui.Row(8, Ui.Icon("\uEC4A", 14, "TextSecondary"), Ui.Text("Export speed", 12.5, FontWeights.Medium, "TextSecondary", wrap: false), combo);
    }

    // -------------------------------------------------------------- export

    private FrameworkElement BuildBottomBar()
    {
        var delete = Ui.Button("Delete", () =>
        {
            if (MessageBox.Show(this, _saved ? "Saved and copied files will stay where they are." : "It has not been saved anywhere yet.",
                    "Delete this recording?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _saved = true;
            Close();
        }, glyph: Glyphs.Delete);
        var copyGif = Ui.Button("Copy as GIF", () => _ = ExportAsync(gif: true, ExportTarget.Copy));
        var saveGif = Ui.Button("Save as GIF", () => _ = ExportAsync(gif: true, ExportTarget.Folder));
        var copy = Ui.Button("Copy", () => _ = ExportAsync(gif: false, ExportTarget.Copy), glyph: Glyphs.Copy);
        var saveAs = Ui.Button("Save as\u2026", () => _ = ExportAsync(gif: false, ExportTarget.Choose));
        var save = Ui.Button("Save", () => _ = ExportAsync(gif: false, ExportTarget.Folder), accent: true, glyph: "\uE74E");
        var cancel = Ui.Button("Cancel", () => _export?.Cancel());
        cancel.Visibility = Visibility.Collapsed;
        _progress.IsVisibleChanged += (_, _) => cancel.Visibility = _progress.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        var bar = Ui.Split(Ui.Row(8, delete), Ui.Row(8, cancel, copyGif, saveGif, copy, saveAs, save));
        bar.Margin = new Thickness(18, 8, 18, 14);
        return bar;
    }

    private enum ExportTarget { Folder, Choose, Copy }

    /// <summary>Whether the edit changes anything (unchanged takes are saved as the master itself).</summary>
    private bool IsUntouched =>
        _edit.Cuts.Count == 0 && _edit.TrimStart <= 0.001 && _edit.TrimEnd >= _edit.Duration - 0.001 && _edit.Zooms.Count == 0
        && _edit.Blurs.Count == 0 && Math.Abs(_edit.Speed - 1) < 0.001 && !_edit.SystemAudioRemoved && !_edit.MicrophoneRemoved
        && Math.Abs(_edit.SystemAudioVolume - 1) < 0.001 && Math.Abs(_edit.MicrophoneVolume - 1) < 0.001;

    private async Task ExportAsync(bool gif, ExportTarget target)
    {
        if (_export is not null) return;
        if (_player.NaturalDuration.HasTimeSpan) _player.Pause();
        _playing = false;
        var ext = gif ? "gif" : "mp4";
        string? destination;
        var folder = CaptureSettings.RecordingFolderPath;
        if (target == ExportTarget.Choose)
        {
            var dialog = new SaveFileDialog
            {
                FileName = CaptureFileNaming.DefaultFileName("Recording", DateTime.Now, ext),
                Filter = gif ? "GIF (*.gif)|*.gif" : "MP4 video (*.mp4)|*.mp4",
                InitialDirectory = Directory.Exists(folder) ? folder : null,
            };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        else if (target == ExportTarget.Copy)
        {
            var shared = CaptureSettings.PrivateFolder("Shared");
            destination = System.IO.Path.Combine(shared, CaptureFileNaming.DefaultFileName("Recording", DateTime.Now, ext));
        }
        else
        {
            Directory.CreateDirectory(folder);
            destination = System.IO.Path.Combine(folder, CaptureFileNaming.UniqueFileName(
                CaptureFileNaming.DefaultFileName("Recording", DateTime.Now, ext), n => File.Exists(System.IO.Path.Combine(folder, n))));
        }

        var needsFfmpeg = gif || !(IsUntouched || OnlyTrimsAndCuts);
        if (needsFfmpeg && !Ffmpeg.IsAvailable)
        {
            ShowFfmpegNeeded();
            return;
        }
        if (gif && RecordingEditMath.ExportDuration(_edit) <= 0) return;

        _export = new CancellationTokenSource();
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        var temp = destination + ".part";
        try
        {
            if (!gif && IsUntouched)
            {
                File.Copy(_result.Path, temp, overwrite: true);
            }
            else if (!needsFfmpeg && !Ffmpeg.IsAvailable)
            {
                await ExportWithCompositionAsync(temp, _export.Token);
            }
            else
            {
                var tracks = new List<AudioTrackEdit>();
                var index = 0;
                if (_result.HasSystemAudio) tracks.Add(new AudioTrackEdit(index++, _edit.SystemAudioVolume, _edit.SystemAudioRemoved));
                if (_result.HasMicrophone) tracks.Add(new AudioTrackEdit(index, _edit.MicrophoneVolume, _edit.MicrophoneRemoved));
                var args = FfmpegArgs.RecordingExport(_result.Path, temp, _edit, _result.Width, _result.Height, _result.Fps, tracks, gif,
                    RecordingEditMath.GifLongEdge(Settings.GetString(CaptureSettings.RecorderGifSize, "medium")),
                    Settings.GetInt(CaptureSettings.RecorderGifFrameRate, 12)) ?? throw new InvalidOperationException("Nothing is left after the cuts.");
                // The temp name ends in .part; tell ffmpeg the real container.
                args.Insert(args.Count - 1, "-f");
                args.Insert(args.Count - 1, gif ? "gif" : "mp4");
                var progress = new Progress<double>(v => _progress.Value = v);
                await Ffmpeg.RunAsync(args, RecordingEditMath.ExportDuration(_edit), progress, _export.Token);
            }
            File.Move(temp, destination, overwrite: true);
            _saved = true;
            if (target == ExportTarget.Copy)
            {
                CaptureClipboard.CopyFile(destination);
                Hud.Show(Glyphs.Copy, "Recording copied");
            }
            else
            {
                RecentCaptures.AddRecording(destination);
                Hud.Show(Glyphs.Check, $"Saved to {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(destination))}");
            }
        }
        catch (OperationCanceledException)
        {
            RecorderController.TryDelete(temp);
        }
        catch (Exception ex)
        {
            Log.Error("Exporting the recording failed", ex);
            RecorderController.TryDelete(temp);
            Hud.Show(Glyphs.Warning, "The recording could not be saved");
        }
        finally
        {
            _export?.Dispose();
            _export = null;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private bool OnlyTrimsAndCuts =>
        _edit.Zooms.Count == 0 && _edit.Blurs.Count == 0 && Math.Abs(_edit.Speed - 1) < 0.001
        && Math.Abs(_edit.SystemAudioVolume - 1) < 0.001 && Math.Abs(_edit.MicrophoneVolume - 1) < 0.001
        && !(_edit.SystemAudioRemoved && _edit.MicrophoneRemoved);

    /// <summary>Trim and cuts without ffmpeg, through Windows.Media.Editing (one audio track kept).</summary>
    private async Task ExportWithCompositionAsync(string output, CancellationToken ct)
    {
        var source = await StorageFile.GetFileFromPathAsync(_result.Path);
        var composition = new MediaComposition();
        foreach (var segment in RecordingEditMath.KeptSegments(_edit))
        {
            var clip = await MediaClip.CreateFromFileAsync(source);
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(segment.Start);
            clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromSeconds(Math.Min(segment.End, clip.OriginalDuration.TotalSeconds));
            if (_edit.SystemAudioRemoved && _result.HasMicrophone && clip.EmbeddedAudioTracks.Count > 1) clip.SelectedEmbeddedAudioTrackIndex = 1;
            composition.Clips.Add(clip);
        }
        File.WriteAllBytes(output, []);
        var target = await StorageFile.GetFileFromPathAsync(output);
        var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise);
        render.Progress = (_, p) => Dispatcher.BeginInvoke(() => _progress.Value = p / 100);
        using (ct.Register(() => render.Cancel()))
        {
            var result = await render;
            if (result != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException($"Rendering failed: {result}");
        }
    }

    private void ShowFfmpegNeeded()
    {
        var window = new Window
        {
            Title = "Install ffmpeg",
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily,
        };
        window.SetResourceReference(BackgroundProperty, "WindowBackground");
        var card = Ffmpeg.MissingCard(() => window.Close());
        card.Margin = new Thickness(18);
        card.MaxWidth = 460;
        window.Content = Ui.Stack(0, card, Ui.Caption("Zooms, blurs, volume, speed and GIFs are rendered by ffmpeg. Trims and cuts work without it."));
        ((FrameworkElement)window.Content).Margin = new Thickness(0, 0, 0, 18);
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        window.ShowDialog();
    }

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_result.Path);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            const int count = 10;
            var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds(_result.Duration * (i + 0.5) / count)).ToList();
            var h = 54;
            var w = Math.Max(16, (int)(h * (_result.Width / (double)Math.Max(1, _result.Height))));
            var streams = await composition.GetThumbnailsAsync(times, w, h, VideoFramePrecision.NearestKeyFrame);
            var images = new List<BitmapSource>();
            foreach (var stream in streams)
            {
                using var s = stream.AsStream();
                var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                images.Add(frame);
            }
            _timeline.Thumbnails = images;
            _timeline.InvalidateVisual();
        }
        catch (Exception ex)
        {
            Log.Warn($"Timeline thumbnails failed: {ex.Message}");
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Delete or Key.Back when Selected is ZoomSegment z:
                _edit.Zooms.Remove(z);
                Select(null);
                Changed();
                break;
            case Key.Delete or Key.Back when Selected is BlurRegion b:
                _edit.Blurs.Remove(b);
                Select(null);
                Changed();
                break;
            case Key.Delete or Key.Back when Selection is not null:
                CutSelection();
                break;
            case Key.Left:
                Seek(Position - ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 1.0 / Math.Max(1, _result.Fps)));
                break;
            case Key.Right:
                Seek(Position + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 1.0 / Math.Max(1, _result.Fps)));
                break;
            case Key.Escape:
                if (Selected is not null || Selection is not null)
                {
                    Selection = null;
                    Select(null);
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_saved) return;
        var answer = MessageBox.Show(this, "It has not been saved anywhere yet.", "Delete this recording?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }

    /// <summary>Developer preview of the editor with a sample timeline (no video file).</summary>
    public static void RenderPreview(string path)
    {
        var clicks = new List<RecordedClick> { new(1.2, 0.3, 0.4), new(2.0, 0.35, 0.42), new(6.5, 0.7, 0.6) };
        var result = new RecordingResult(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "preview-none.mp4"), 10, 1280, 720, 30, true, true, clicks);
        var editor = new RecorderEditorWindow(result, preview: true) { Left = -20000, Top = -20000, ShowActivated = false, _saved = true };
        editor._edit.Blurs.Add(new BlurRegion(2.4, 7.1, 0.35, 0.3, 0.37, 0.25));
        editor._edit.Cuts.Add(new TimeRange(8.2, 9.0));
        editor.Show();
        editor.Seek(3);
        PreviewRendering.SaveWindow(editor, path);
        editor.Close();
    }
}

/// <summary>Ruler, film (trim and cuts), zoom, blur and audio lanes with a playhead.</summary>
internal sealed class TimelineView : FrameworkElement
{
    private const double LabelWidth = 86;
    private const double RulerHeight = 22;
    private const double LaneHeight = 38;
    private const double LaneGap = 8;
    private readonly RecorderEditorWindow _owner;
    private Drag _drag;
    private double _dragOrigin;
    private object? _dragItem;
    private double _dragItemStart;
    private double _dragItemEnd;

    private enum Drag { None, Seek, Select, TrimStart, TrimEnd, MoveItem, ItemStart, ItemEnd }

    public List<BitmapSource> Thumbnails { get; set; } = [];

    public TimelineView(RecorderEditorWindow owner)
    {
        _owner = owner;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    private double Duration => Math.Max(0.01, _owner.Edit.Duration);
    private double TrackLeft => LabelWidth;
    private double TrackWidth => Math.Max(10, ActualWidth - LabelWidth - 4);
    private double X(double t) => TrackLeft + t / Duration * TrackWidth;
    private double T(double x) => Math.Clamp((x - TrackLeft) / TrackWidth * Duration, 0, Duration);
    private static double LaneTop(int lane) => RulerHeight + 6 + lane * (LaneHeight + LaneGap);

    private static Brush B(string key) => ThemeManager.Brush(key);

    protected override void OnRender(DrawingContext dc)
    {
        var edit = _owner.Edit;
        dc.DrawRoundedRectangle(B("CardBackground"), new Pen(B("CardBorder"), 1), new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        var face = new Typeface("Segoe UI");
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
            new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                bold ? new Typeface(face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : face, size, brush, ppd);

        // Ruler.
        var step = Duration <= 12 ? 1 : Duration <= 60 ? 5 : Duration <= 300 ? 30 : 60;
        for (double t = 0; t <= Duration + 0.001; t += step)
        {
            var x = X(t);
            dc.DrawLine(new Pen(B("Separator"), 1), new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            dc.DrawText(Text(RecorderEditorWindow.Format(t), 10, B("TextTertiary")), new Point(x + 3, 4));
        }

        string[] labels = ["Film", "Zoom", "Blur", "Audio"];
        for (var lane = 0; lane < labels.Length; lane++)
        {
            var top = LaneTop(lane);
            dc.DrawText(Text(labels[lane], 11.5, B("TextSecondary"), true), new Point(12, top + 11));
            dc.DrawRoundedRectangle(B("ControlBackground"), null, new Rect(TrackLeft, top, TrackWidth, LaneHeight), 7, 7);
        }

        // Film: thumbnails, trimmed ends and cuts.
        var filmTop = LaneTop(0);
        if (Thumbnails.Count > 0)
        {
            var cellW = TrackWidth / Thumbnails.Count;
            dc.PushClip(new RectangleGeometry(new Rect(TrackLeft, filmTop, TrackWidth, LaneHeight), 7, 7));
            for (var i = 0; i < Thumbnails.Count; i++)
                dc.DrawImage(Thumbnails[i], new Rect(TrackLeft + i * cellW, filmTop, cellW, LaneHeight));
            dc.Pop();
        }
        var dim = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        dc.DrawRectangle(dim, null, new Rect(TrackLeft, filmTop, Math.Max(0, X(edit.TrimStart) - TrackLeft), LaneHeight));
        dc.DrawRectangle(dim, null, new Rect(X(edit.TrimEnd), filmTop, Math.Max(0, TrackLeft + TrackWidth - X(edit.TrimEnd)), LaneHeight));
        foreach (var cut in edit.Cuts)
        {
            var r = new Rect(X(cut.Start), filmTop, Math.Max(2, X(cut.End) - X(cut.Start)), LaneHeight);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 69, 58)), null, r);
            dc.DrawLine(new Pen(Brushes.White, 1.5), r.TopLeft, r.BottomRight);
        }
        if (_owner.Selection is { } sel)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 10, 132, 255)), new Pen(B("Accent"), 1.5),
                new Rect(X(sel.Start), filmTop, Math.Max(1, X(sel.End) - X(sel.Start)), LaneHeight));
        }
        var handle = B("Yellow");
        dc.DrawRoundedRectangle(handle, null, new Rect(X(edit.TrimStart) - 3, filmTop - 2, 6, LaneHeight + 4), 3, 3);
        dc.DrawRoundedRectangle(handle, null, new Rect(X(edit.TrimEnd) - 3, filmTop - 2, 6, LaneHeight + 4), 3, 3);

        // Zooms.
        var zoomTop = LaneTop(1);
        if (edit.Zooms.Count == 0)
            dc.DrawText(Text("Click here to add a zoom", 11, B("TextTertiary")), new Point(TrackLeft + 10, zoomTop + 11));
        foreach (var z in edit.Zooms)
        {
            var selected = ReferenceEquals(_owner.Selected, z);
            var r = new Rect(X(z.Start), zoomTop + 3, Math.Max(6, X(z.End) - X(z.Start)), LaneHeight - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(selected ? (byte)230 : (byte)160, 10, 132, 255)), selected ? new Pen(Brushes.White, 1.5) : null, r, 6, 6);
            if (r.Width > 50) dc.DrawText(Text($"Zoom {z.Amount:0.0}\u00D7", 11, Brushes.White, true), new Point(r.X + 8, r.Y + 8));
        }

        // Blurs.
        var blurTop = LaneTop(2);
        if (edit.Blurs.Count == 0)
            dc.DrawText(Text("Click here to add a blur", 11, B("TextTertiary")), new Point(TrackLeft + 10, blurTop + 11));
        foreach (var b in edit.Blurs)
        {
            var selected = ReferenceEquals(_owner.Selected, b);
            var r = new Rect(X(b.Start), blurTop + 3, Math.Max(6, X(b.End) - X(b.Start)), LaneHeight - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(selected ? (byte)200 : (byte)140, 0, 150, 160)),
                new Pen(new SolidColorBrush(Color.FromRgb(0, 184, 196)), selected ? 2 : 1), r, 6, 6);
            if (r.Width > 90)
                dc.DrawText(Text($"Blur Region ({RecorderEditorWindow.Format(b.Start)} - {RecorderEditorWindow.Format(b.End)})", 11, Brushes.White, true), new Point(r.X + 8, r.Y + 8));
        }

        // Audio tracks (state only; volume lives in the inspector).
        var audioTop = LaneTop(3);
        var res = _owner.Result;
        var parts = new List<string>();
        if (res.HasSystemAudio) parts.Add(edit.SystemAudioRemoved ? "PC sound (removed)" : $"PC sound {edit.SystemAudioVolume * 100:0}%");
        if (res.HasMicrophone) parts.Add(edit.MicrophoneRemoved ? "Microphone (removed)" : $"Microphone {edit.MicrophoneVolume * 100:0}%");
        dc.DrawText(Text(parts.Count == 0 ? "No sound recorded" : string.Join("   \u00B7   ", parts), 11, B("TextSecondary")), new Point(TrackLeft + 10, audioTop + 11));

        // Playhead.
        var px = X(_owner.Position);
        dc.DrawLine(new Pen(B("TextPrimary"), 2), new Point(px, RulerHeight - 2), new Point(px, ActualHeight - 4));
        dc.DrawEllipse(B("TextPrimary"), null, new Point(px, RulerHeight - 2), 5, 5);
    }

    private int LaneAt(double y)
    {
        for (var lane = 0; lane < 4; lane++)
        {
            var top = LaneTop(lane);
            if (y >= top - LaneGap / 2 && y <= top + LaneHeight + LaneGap / 2) return lane;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X < TrackLeft - 6) return;
        var edit = _owner.Edit;
        var t = T(p.X);
        var lane = p.Y < RulerHeight + 4 ? -2 : LaneAt(p.Y);
        _dragOrigin = t;
        CaptureMouse();
        switch (lane)
        {
            case -2:
                _drag = Drag.Seek;
                _owner.Seek(t);
                return;
            case 0:
                if (Math.Abs(p.X - X(edit.TrimStart)) <= 7) _drag = Drag.TrimStart;
                else if (Math.Abs(p.X - X(edit.TrimEnd)) <= 7) _drag = Drag.TrimEnd;
                else
                {
                    _drag = Drag.Select;
                    _owner.Selection = null;
                    _owner.Seek(t);
                }
                return;
            case 1:
            {
                var zoom = edit.Zooms.FirstOrDefault(z => t >= z.Start && t <= z.End);
                if (zoom is null)
                {
                    _owner.AddZoomAt(t);
                    _drag = Drag.None;
                    return;
                }
                BeginItemDrag(zoom, zoom.Start, zoom.End, p);
                _owner.Select(zoom);
                return;
            }
            case 2:
            {
                var blur = edit.Blurs.FirstOrDefault(b => t >= b.Start && t <= b.End);
                if (blur is null)
                {
                    _owner.Select(null);
                    _owner.Seek(t);
                    _drag = Drag.Seek;
                    return;
                }
                BeginItemDrag(blur, blur.Start, blur.End, p);
                _owner.Select(blur);
                return;
            }
            default:
                _drag = Drag.Seek;
                _owner.Seek(t);
                return;
        }
    }

    private void BeginItemDrag(object item, double start, double end, Point p)
    {
        _dragItem = item;
        _dragItemStart = start;
        _dragItemEnd = end;
        _drag = Math.Abs(p.X - X(start)) <= 6 ? Drag.ItemStart : Math.Abs(p.X - X(end)) <= 6 ? Drag.ItemEnd : Drag.MoveItem;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var t = T(p.X);
        var edit = _owner.Edit;
        switch (_drag)
        {
            case Drag.Seek:
                _owner.Seek(t);
                break;
            case Drag.Select:
                _owner.Selection = new TimeRange(Math.Min(_dragOrigin, t), Math.Max(_dragOrigin, t));
                _owner.Changed();
                break;
            case Drag.TrimStart:
                edit.TrimStart = Math.Clamp(t, 0, edit.TrimEnd - RecordingEditMath.MinimumSegment);
                _owner.Seek(edit.TrimStart);
                _owner.Changed();
                break;
            case Drag.TrimEnd:
                edit.TrimEnd = Math.Clamp(t, edit.TrimStart + RecordingEditMath.MinimumSegment, edit.Duration);
                _owner.Seek(edit.TrimEnd);
                _owner.Changed();
                break;
            case Drag.MoveItem or Drag.ItemStart or Drag.ItemEnd when _dragItem is not null:
            {
                var delta = t - _dragOrigin;
                var start = _dragItemStart;
                var end = _dragItemEnd;
                if (_drag == Drag.MoveItem)
                {
                    delta = Math.Clamp(delta, -start, edit.Duration - end);
                    start += delta;
                    end += delta;
                }
                else if (_drag == Drag.ItemStart) start = Math.Clamp(start + delta, 0, end - RecordingEditMath.MinimumSegment);
                else end = Math.Clamp(end + delta, start + RecordingEditMath.MinimumSegment, edit.Duration);
                if (_dragItem is ZoomSegment z)
                {
                    var updated = z with { Start = start, End = end };
                    _owner.ReplaceZoom(z, updated);
                    _dragItem = updated;
                }
                else if (_dragItem is BlurRegion b)
                {
                    var updated = b with { Start = start, End = end };
                    _owner.ReplaceBlur(b, updated);
                    _dragItem = updated;
                }
                break;
            }
            default:
            {
                var lane = LaneAt(p.Y);
                var nearTrim = lane == 0 && (Math.Abs(p.X - X(edit.TrimStart)) <= 7 || Math.Abs(p.X - X(edit.TrimEnd)) <= 7);
                Cursor = nearTrim ? Cursors.SizeWE : Cursors.Arrow;
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_drag == Drag.Select && _owner.Selection is { Duration: < 0.05 }) _owner.Selection = null;
        if (_drag is Drag.MoveItem or Drag.ItemStart or Drag.ItemEnd && _dragItem is ZoomSegment)
            _owner.Edit.Zooms = RecordingEditMath.NormalizedZooms(_owner.Edit.Zooms, _owner.Edit.Duration);
        _drag = Drag.None;
        _dragItem = null;
        _owner.Select(_owner.Selected is ZoomSegment z && !_owner.Edit.Zooms.Contains(z) ? null : _owner.Selected);
        _owner.Changed();
    }
}
