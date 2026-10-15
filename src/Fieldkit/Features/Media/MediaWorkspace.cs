// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Capture;
using Fieldkit.UI;

namespace Fieldkit.Features.Media;

/// <summary>Settings keys of the Media tools (upstream defaults).</summary>
public static class MediaSettings
{
    public const string LastTool = "mediaTools.lastTool";
    public const string VideoQuality = "mediaTools.videoQuality";
    public const string VideoMaxDimension = "mediaTools.videoMaxDimension";
    public const string VideoFps = "mediaTools.videoFPS";
    public const string VideoKeepAudio = "mediaTools.videoKeepAudio";
    public const string VideoCodec = "mediaTools.videoCodec";
    public const string VideoSizing = "mediaTools.videoSizing";
    public const string VideoTargetMb = "mediaTools.videoTargetMegabytes";
    public const string VideoStripMetadata = "mediaTools.videoStripMetadata";
    public const string GifWidth = "mediaTools.gifWidth";
    public const string GifFps = "mediaTools.gifFPS";
    public const string GifQuality = "mediaTools.gifQuality";
    public const string GifLoops = "mediaTools.gifLoops";
    public const string GifSizing = "mediaTools.gifSizing";
    public const string GifTargetMb = "mediaTools.gifTargetMegabytes";
    public const string ImageFormat = "mediaTools.imageFormat";
    public const string ImageQuality = "mediaTools.imageQuality";
    public const string ImageResize = "mediaTools.imageResizeKind";
    public const string ImageMaxDimension = "mediaTools.imageMaxDimension";
    public const string ImageWidth = "mediaTools.imageResizeWidth";
    public const string ImageHeight = "mediaTools.imageResizeHeight";
    public const string ImageExactMode = "mediaTools.imageExactResizeMode";
    public const string ImageWatermark = "mediaTools.imageWatermarkKind";
    public const string ImageWatermarkText = "mediaTools.imageWatermarkText";
    public const string ImageWatermarkLogo = "mediaTools.imageWatermarkLogoPath";
    public const string ImageWatermarkPosition = "mediaTools.imageWatermarkPosition";
    public const string ImageWatermarkOpacity = "mediaTools.imageWatermarkOpacity";
    public const string ImageWatermarkMargin = "mediaTools.imageWatermarkMargin";
    public const string ImageWatermarkScale = "mediaTools.imageWatermarkScale";
    public const string ImageRename = "mediaTools.imageRenamePattern";
    public const string ImageBackground = "mediaTools.imageBackground";
    public const string ImageStripMetadata = "mediaTools.imageStripMetadata";
    public const string ImagePreserveDate = "mediaTools.imagePreserveModificationDate";
    public const string ImageSubfolder = "mediaTools.imageSaveInSubfolder";
    public const string TextRemoveLineBreaks = "mediaTools.textRemoveLineBreaks";

    public static T Enum<T>(string key, T fallback) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(Settings.GetString(key, fallback.ToString()), true, out var v) ? v : fallback;

    public static ImageOptions ImageOptions() => new()
    {
        Format = Enum(ImageFormat, ImageOutputFormat.Jpeg),
        Quality = Settings.GetDouble(ImageQuality, 0.72),
        Resize = Enum(ImageResize, ImageResizeKind.MaxDimension),
        MaxDimension = (int)Settings.GetDouble(ImageMaxDimension, 1600),
        Width = (int)Settings.GetDouble(ImageWidth, 1600),
        Height = (int)Settings.GetDouble(ImageHeight, 1200),
        ExactMode = Enum(ImageExactMode, Media.ImageExactMode.Stretch),
        Watermark = Enum(ImageWatermark, ImageWatermarkKind.Off),
        WatermarkText = Settings.GetString(ImageWatermarkText, string.Empty),
        WatermarkLogoPath = Settings.GetString(ImageWatermarkLogo, string.Empty),
        WatermarkPosition = Enum(ImageWatermarkPosition, Media.ImageWatermarkPosition.BottomRight),
        WatermarkOpacity = Settings.GetDouble(ImageWatermarkOpacity, 0.45),
        WatermarkMargin = (int)Settings.GetDouble(ImageWatermarkMargin, 32),
        WatermarkScale = Settings.GetDouble(ImageWatermarkScale, 0.18),
        RenamePattern = Settings.GetString(ImageRename, string.Empty),
        Background = Enum(ImageBackground, Media.ImageBackground.Transparent),
        StripMetadata = Settings.GetBool(ImageStripMetadata, true),
        PreserveModificationDate = Settings.GetBool(ImagePreserveDate, false),
        SaveInSubfolder = Settings.GetBool(ImageSubfolder, false),
    };
}

/// <summary>
/// The Media workspace (upstream MediaWorkspaceView): Video (compress and trim), GIF, Image (batch
/// convert, resize, compress, watermark) and Text (extract text from images). All local.
/// </summary>
public sealed class MediaWorkspace : ContentControl
{
    private static readonly (string Value, string Label, string Glyph)[] Tools =
    [
        ("video", "Video", "\uE714"),
        ("gif", "GIF", "\uF4A9"),
        ("image", "Image", Glyphs.Photo),
        ("text", "Text", Glyphs.TextScan),
    ];

    private readonly List<string> _files = [];
    private readonly StackPanel _options = new();
    private readonly ContentControl _fileArea = new();
    private readonly ContentControl _result = new();
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _start;
    private readonly Button _cancel;
    private readonly TextBox _start1 = new() { Width = 70 };
    private readonly TextBox _end1 = new() { Width = 70 };
    private string _tool;
    private CancellationTokenSource? _job;

    public MediaWorkspace()
    {
        _tool = Settings.GetString(MediaSettings.LastTool, "video");
        if (Tools.All(t => t.Value != _tool)) _tool = "video";
        _start = Ui.Button("Compress video", () => _ = RunAsync(), accent: true);
        _cancel = Ui.Button("Cancel", () => _job?.Cancel());
        _cancel.Visibility = Visibility.Collapsed;
        Content = Build();
        Rebuild();
        Ffmpeg.AvailabilityChanged += OnFfmpegChanged;
        Unloaded += (_, _) => Ffmpeg.AvailabilityChanged -= OnFfmpegChanged;
    }

    private void OnFfmpegChanged() => Ui.OnUi(Rebuild);

    private FrameworkElement Build()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label, glyph) in Tools)
        {
            var rb = new RadioButton { Content = Ui.Row(6, Ui.Icon(glyph, 13, "TextPrimary"), Ui.Text(label, 13, wrap: false)), GroupName = "mediaTools" + GetHashCode(), IsChecked = value == _tool };
            rb.SetResourceReference(StyleProperty, "SegmentButton");
            var v = value;
            rb.Checked += (_, _) =>
            {
                _tool = v;
                Settings.Set(MediaSettings.LastTool, v);
                _files.Clear();
                _result.Content = null;
                Rebuild();
            };
            tabs.Children.Add(rb);
        }
        var tabHost = new Border { Child = tabs, CornerRadius = new CornerRadius(9), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
        tabHost.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        var header = Ui.Split(tabHost, Ui.Row(6, Ui.Icon(Glyphs.Lock, 12, "TextSecondary"), Ui.Text("Local. No network.", 12, brush: "TextSecondary", wrap: false)));
        var actions = Ui.Split(_progress, Ui.Row(8, _cancel, _start));
        _progress.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Stack(14, header, _fileArea, _options, actions, _result);
    }

    private void Rebuild()
    {
        _fileArea.Content = DropZone();
        _options.Children.Clear();
        var needsFfmpeg = _tool is "video" or "gif";
        if (needsFfmpeg && !Ffmpeg.IsAvailable)
        {
            _options.Children.Add(Ffmpeg.MissingCard(Rebuild));
            _start.IsEnabled = false;
        }
        else
        {
            _start.IsEnabled = _files.Count > 0 && _job is null;
            var group = _tool switch
            {
                "video" => VideoOptions(),
                "gif" => GifOptions(),
                "image" => ImageOptionsUi(),
                _ => TextOptions(),
            };
            _options.Children.Add(group);
        }
        _start.Content = _tool switch
        {
            "video" => "Compress video",
            "gif" => "Make GIF",
            "image" => "Process image",
            _ => "Extract text",
        };
    }

    // ------------------------------------------------------------------ files

    private FrameworkElement DropZone()
    {
        var multi = _tool is "image" or "text";
        var title = _files.Count switch
        {
            0 => "Drop a file here or click to choose one.",
            1 => Path.GetFileName(_files[0]),
            _ => $"{_files.Count} files selected",
        };
        var icon = Ui.Icon(_files.Count == 0 ? Glyphs.Add : Glyphs.Document, 22, "Accent");
        var caption = _files.Count == 0
            ? Ui.Caption(_tool switch
            {
                "video" or "gif" => "MP4, MOV, MKV, AVI, WebM and other videos ffmpeg can read.",
                _ => "JPEG, PNG, TIFF, BMP, GIF, HEIC and WebP (with their Windows extensions).",
            })
            : Ui.Caption(string.Join(", ", _files.Take(4).Select(Path.GetFileName)) + (_files.Count > 4 ? "\u2026" : string.Empty));
        var stack = Ui.Stack(6, icon, Ui.Text(title, 14, FontWeights.SemiBold), caption);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var c in stack.Children.OfType<FrameworkElement>()) c.HorizontalAlignment = HorizontalAlignment.Center;
        var zone = new Border
        {
            Child = stack,
            Padding = new Thickness(16, 18, 16, 18),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
            AllowDrop = true,
            Cursor = Cursors.Hand,
        };
        zone.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        zone.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        zone.MouseLeftButtonUp += (_, _) => Choose(multi);
        zone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        zone.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) SetFiles(files);
        };
        return zone;
    }

    private void Choose(bool multi)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = multi,
            Filter = _tool is "video" or "gif"
                ? "Videos|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm;*.wmv;*.flv;*.mpg;*.mpeg;*.ts|All files|*.*"
                : "Images|" + string.Join(";", ImageProcessor.InputExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetFiles(dialog.FileNames);
    }

    private void SetFiles(IEnumerable<string> files)
    {
        _files.Clear();
        var list = files.Where(File.Exists).ToList();
        if (_tool is "video" or "gif") list = list.Take(1).ToList();
        else list = list.Where(ImageProcessor.IsImage).ToList();
        _files.AddRange(list);
        _result.Content = null;
        Rebuild();
    }

    // ---------------------------------------------------------------- options

    private static readonly IReadOnlyList<(string, string)> Compression =
    [
        ("0.88", "Low \u00B7 High quality, large file size"),
        ("0.68", "Medium \u00B7 Balanced quality and file size"),
        ("0.28", "High \u00B7 Low quality, small file size"),
    ];

    private static FrameworkElement QualityChoice(string title, string key, double fallback)
    {
        // Snap a stored value to the nearest of the three compression levels (upstream behaviour).
        var current = Settings.GetDouble(key, fallback);
        var nearest = new[] { 0.88, 0.68, 0.28 }.OrderBy(v => Math.Abs(v - current)).First();
        var combo = new ComboBox { MinWidth = 260 };
        foreach (var (value, label) in Compression) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == nearest.ToString("0.00", CultureInfo.InvariantCulture));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item) Settings.Set(key, double.Parse((string)item.Tag, CultureInfo.InvariantCulture));
        };
        return Ui.Split(Ui.Text(title, 13.5, FontWeights.Medium), combo);
    }

    private FrameworkElement TrimRow()
    {
        _start1.ToolTip = "Seconds from the start (empty = beginning)";
        _end1.ToolTip = "Seconds from the start (empty = end)";
        return Ui.Split(Ui.Stack(2, Ui.Text("Trim", 13.5, FontWeights.Medium), Ui.Caption("In seconds. Leave empty to keep the whole clip.")),
            Ui.Row(8, Ui.Text("Start", 12.5, brush: "TextSecondary", wrap: false), _start1, Ui.Text("End", 12.5, brush: "TextSecondary", wrap: false), _end1));
    }

    private static double ParseSeconds(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        if (TimeSpan.TryParseExact(text, [@"m\:ss", @"m\:ss\.f", @"h\:mm\:ss", @"h\:mm\:ss\.f"], CultureInfo.InvariantCulture, out var ts)) return ts.TotalSeconds;
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Max(0, s) : 0;
    }

    private FrameworkElement VideoOptions()
    {
        var sizing = Settings.GetString(MediaSettings.VideoSizing, "resolution");
        return Ui.Group(null,
            TrimRow(),
            QualityChoice("Compression", MediaSettings.VideoQuality, 0.68),
            Ui.ChoiceRow("Size", MediaSettings.VideoSizing, [("resolution", "Resolution"), ("targetSize", "File size")], "resolution", null, _ => Rebuild()),
            sizing == "targetSize"
                ? Ui.SliderRow("Target size", MediaSettings.VideoTargetMb, 1, 512, 20, v => $"{v:0} MB", 1, "Resolution adapts to stay under the limit.")
                : Ui.SliderRow("Size", MediaSettings.VideoMaxDimension, 640, 3840, 1280, v => $"{v:0} px", 320),
            Ui.SliderRow("FPS", MediaSettings.VideoFps, 12, 60, 30, v => $"{v:0}", 1),
            Ui.ChoiceRow("Codec", MediaSettings.VideoCodec, [("h264", "H.264"), ("hevc", "HEVC")], "h264"),
            Ui.ToggleRow("Keep audio", null, MediaSettings.VideoKeepAudio, true),
            Ui.ToggleRow("Remove metadata", null, MediaSettings.VideoStripMetadata, true));
    }

    private FrameworkElement GifOptions()
    {
        var sizing = Settings.GetString(MediaSettings.GifSizing, "resolution");
        return Ui.Group(null,
            TrimRow(),
            Ui.ChoiceRow("Size", MediaSettings.GifSizing, [("resolution", "Resolution"), ("targetSize", "File size")], "resolution", null, _ => Rebuild()),
            sizing == "targetSize"
                ? Ui.SliderRow("Target size", MediaSettings.GifTargetMb, 1, 100, 10, v => $"{v:0} MB", 1)
                : Ui.SliderRow("Width", MediaSettings.GifWidth, 160, 1600, 720, v => $"{v:0} px", 80),
            Ui.SliderRow("FPS", MediaSettings.GifFps, 6, 30, 12, v => $"{v:0}", 1),
            Ui.SliderRow("Quality", MediaSettings.GifQuality, 0.1, 1, 0.74, v => $"{v * 100:0}%", 0.02),
            Ui.ToggleRow("Loop GIF", null, MediaSettings.GifLoops, true));
    }

    private FrameworkElement ImageOptionsUi()
    {
        var o = MediaSettings.ImageOptions();
        var formats = new List<(string, string)> { ("Jpeg", "JPEG"), ("Png", "PNG"), ("Tiff", "TIFF"), ("Bmp", "BMP"), ("Gif", "GIF") };
        if (ImageProcessor.CanEncodeHeic()) formats.Add(("Heic", "HEIC"));
        var presets = Ui.Row(8,
            Ui.Text("Profile", 13.5, FontWeights.Medium),
            Ui.Button("Web", () => ApplyPreset("Jpeg", 0.72, 1600)),
            Ui.Button("Social", () => ApplyPreset("Png", 0.82, 2048)),
            Ui.Button("Docs", () => ApplyPreset("Jpeg", 0.7, 1600)));
        var rows = new List<UIElement?>
        {
            presets,
            Ui.ChoiceRow("Format", MediaSettings.ImageFormat, formats, "Jpeg", null, _ => Rebuild()),
            MediaImagePlan.UsesQuality(o.Format) ? Ui.SliderRow("Compression", MediaSettings.ImageQuality, 0.05, 1, 0.72, v => $"{v * 100:0}%", 0.01) : null,
            Ui.ChoiceRow("Resize", MediaSettings.ImageResize,
                [("None", "No change"), ("MaxDimension", "Max side"), ("Width", "Width"), ("Height", "Height"), ("Exact", "Custom")], "MaxDimension", null, _ => Rebuild()),
        };
        switch (o.Resize)
        {
            case ImageResizeKind.MaxDimension:
                rows.Add(Ui.SliderRow("Max side", MediaSettings.ImageMaxDimension, 64, 8192, 1600, v => $"{v:0} px", 64));
                break;
            case ImageResizeKind.Width:
                rows.Add(Ui.SliderRow("Width", MediaSettings.ImageWidth, 16, 8192, 1600, v => $"{v:0} px", 16));
                break;
            case ImageResizeKind.Height:
                rows.Add(Ui.SliderRow("Height", MediaSettings.ImageHeight, 16, 8192, 1200, v => $"{v:0} px", 16));
                break;
            case ImageResizeKind.Exact:
                rows.Add(Ui.SliderRow("Width", MediaSettings.ImageWidth, 16, 8192, 1600, v => $"{v:0} px", 16));
                rows.Add(Ui.SliderRow("Height", MediaSettings.ImageHeight, 16, 8192, 1200, v => $"{v:0} px", 16));
                rows.Add(Ui.ChoiceRow("Fit", MediaSettings.ImageExactMode, [("Stretch", "Stretch"), ("Fit", "Fit"), ("Fill", "Fill")], "Stretch"));
                break;
        }
        rows.Add(Ui.ChoiceRow("Watermark", MediaSettings.ImageWatermark,
            [("Off", "Off"), ("Text", "Text"), ("Logo", "Logo"), ("TextAndLogo", "Text + logo")], "Off", null, _ => Rebuild()));
        if (o.Watermark != ImageWatermarkKind.Off)
        {
            if (o.Watermark is ImageWatermarkKind.Text or ImageWatermarkKind.TextAndLogo)
                rows.Add(Ui.Split(Ui.Text("Watermark text", 13.5, FontWeights.Medium), WithWidth(Ui.TextInput(MediaSettings.ImageWatermarkText, string.Empty, "Watermark text"), 260)));
            if (o.Watermark is ImageWatermarkKind.Logo or ImageWatermarkKind.TextAndLogo)
            {
                var logo = Settings.GetString(MediaSettings.ImageWatermarkLogo, string.Empty);
                rows.Add(Ui.Split(Ui.Text("Logo", 13.5, FontWeights.Medium), Ui.Button(logo.Length == 0 ? "Choose logo" : Path.GetFileName(logo), () =>
                {
                    var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
                    if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                    {
                        Settings.Set(MediaSettings.ImageWatermarkLogo, dialog.FileName);
                        Rebuild();
                    }
                })));
            }
            rows.Add(Ui.ChoiceRow("Position", MediaSettings.ImageWatermarkPosition,
                [("TopLeft", "Top left"), ("TopRight", "Top right"), ("Center", "Center"), ("BottomLeft", "Bottom left"), ("BottomRight", "Bottom right")], "BottomRight"));
            rows.Add(Ui.SliderRow("Opacity", MediaSettings.ImageWatermarkOpacity, 0.1, 1, 0.45, v => $"{v * 100:0}%", 0.05));
            rows.Add(Ui.SliderRow("Margin", MediaSettings.ImageWatermarkMargin, 0, 400, 32, v => $"{v:0} px", 8));
            rows.Add(Ui.SliderRow("Scale", MediaSettings.ImageWatermarkScale, 0.02, 1, 0.18, v => $"{v * 100:0}%", 0.01));
        }
        rows.Add(Ui.Split(Ui.Stack(2, Ui.Text("Rename", 13.5, FontWeights.Medium),
                Ui.Caption("{name} {date} {time} {datetime} {width} {height} {ext} {index} {index:03}")),
            WithWidth(Ui.TextInput(MediaSettings.ImageRename, string.Empty, "{name}"), 220)));
        if (!MediaImagePlan.SupportsTransparency(o.Format) || o.Background != ImageBackground.Transparent)
            rows.Add(Ui.ChoiceRow("Background", MediaSettings.ImageBackground, [("Transparent", "Transparent"), ("White", "White"), ("Black", "Black")], "Transparent"));
        rows.Add(Ui.ToggleRow("Remove metadata", null, MediaSettings.ImageStripMetadata, true));
        rows.Add(Ui.ToggleRow("Keep original modified date", null, MediaSettings.ImagePreserveDate, false));
        rows.Add(Ui.ToggleRow("Save in \u201CConverted\u201D subfolder", null, MediaSettings.ImageSubfolder, false));
        return Ui.Group(null, [.. rows]);
    }

    private static FrameworkElement WithWidth(FrameworkElement e, double width)
    {
        e.Width = width;
        return e;
    }

    private void ApplyPreset(string format, double quality, int maxSide)
    {
        Settings.Set(MediaSettings.ImageFormat, format);
        Settings.Set(MediaSettings.ImageQuality, quality);
        Settings.Set(MediaSettings.ImageResize, nameof(ImageResizeKind.MaxDimension));
        Settings.Set(MediaSettings.ImageMaxDimension, (double)maxSide);
        Rebuild();
    }

    private static FrameworkElement TextOptions() => Ui.Group(null,
        Ui.ToggleRow("Remove line breaks", "Removes line breaks so copied text pastes as one paragraph.", MediaSettings.TextRemoveLineBreaks, false),
        Ui.Caption("Extracted text can be copied and saved as TXT. Recognition uses the Windows text recognition languages installed on this PC."));

    // ------------------------------------------------------------------- run

    private async Task RunAsync()
    {
        if (_files.Count == 0 || _job is not null) return;
        _job = new CancellationTokenSource();
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _cancel.Visibility = Visibility.Visible;
        _start.IsEnabled = false;
        _result.Content = null;
        var progress = new Progress<double>(v => _progress.Value = v);
        try
        {
            _result.Content = _tool switch
            {
                "video" => await RunVideoAsync(progress, _job.Token),
                "gif" => await RunGifAsync(progress, _job.Token),
                "image" => await RunImagesAsync(progress, _job.Token),
                _ => await RunTextAsync(progress, _job.Token),
            };
        }
        catch (OperationCanceledException)
        {
            _result.Content = Note(Glyphs.Info, "Cancelled.", "Orange");
        }
        catch (Exception ex)
        {
            Log.Error("Media tool failed", ex);
            _result.Content = Note(Glyphs.Warning, ex.Message, "Red");
        }
        finally
        {
            _job.Dispose();
            _job = null;
            _progress.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _start.IsEnabled = _files.Count > 0;
        }
    }

    private static FrameworkElement Note(string glyph, string text, string brush) =>
        Ui.Card(Ui.Row(8, Ui.Icon(glyph, 14, brush), Ui.Text(text, 13, brush: brush)));

    private static FrameworkElement Done(string output, long before, long after, params UIElement[] extra)
    {
        var show = Ui.Button("Show", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true }));
        var lines = Ui.Stack(4,
            Ui.Row(8, Ui.Icon(Glyphs.Check, 14, "Green"), Ui.Text("Done", 13.5, FontWeights.SemiBold, "Green")),
            Ui.Text($"Saved as {Path.GetFileName(output)}", 13),
            Ui.Caption($"{MediaImagePlan.FormatBytes(before)} to {MediaImagePlan.FormatBytes(after)}"),
            after > before ? Ui.Caption("The converted file came out larger than the original.") : null);
        var row = Ui.Row(8, [show, .. extra]);
        return Ui.Card(Ui.Split(lines, row));
    }

    private (double Start, double End) Trim(double duration)
    {
        var start = ParseSeconds(_start1.Text);
        var end = ParseSeconds(_end1.Text);
        if (end <= start || end > duration) end = duration;
        start = Math.Clamp(start, 0, Math.Max(0, duration - 0.1));
        return (start, end);
    }

    private static string OutputPath(string input, string suffix, string ext)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var name = $"{MediaImagePlan.VisibleBaseName(input)}{suffix}.{ext}";
        return Path.Combine(dir, CaptureFileNaming.UniqueFileName(name, n => File.Exists(Path.Combine(dir, n))));
    }

    private async Task<FrameworkElement> RunVideoAsync(IProgress<double> progress, CancellationToken ct)
    {
        var input = _files[0];
        var probe = await Ffmpeg.ProbeAsync(input, ct);
        if (!probe.HasVideo) throw new InvalidOperationException("This file has no video track.");
        var (start, end) = Trim(probe.Duration);
        var options = new VideoCompressOptions
        {
            Start = start,
            End = end < probe.Duration ? end : 0,
            Quality = Settings.GetDouble(MediaSettings.VideoQuality, 0.68),
            MaxDimension = (int)Settings.GetDouble(MediaSettings.VideoMaxDimension, 1280),
            Fps = Settings.GetDouble(MediaSettings.VideoFps, 30),
            KeepAudio = Settings.GetBool(MediaSettings.VideoKeepAudio, true) && probe.AudioStreams > 0,
            Hevc = Settings.GetString(MediaSettings.VideoCodec, "h264") == "hevc",
            StripMetadata = Settings.GetBool(MediaSettings.VideoStripMetadata, true),
        };
        var output = OutputPath(input, " compressed", "mp4");
        var duration = Math.Max(0.1, end - start);
        var temp = output + ".part";
        try
        {
            if (Settings.GetString(MediaSettings.VideoSizing, "resolution") == "targetSize")
            {
                var target = (long)Settings.GetDouble(MediaSettings.VideoTargetMb, 20) * 1_000_000;
                var scale = 1.0;
                for (var pass = 0; pass < 4; pass++)
                {
                    var plan = FfmpegArgs.VideoSizePlanFor(target, duration, probe.Width, probe.Height, options.Fps, options.KeepAudio, scale)
                               ?? throw new InvalidOperationException("Target size too small for this clip. Trim it or raise the limit.");
                    var args = FfmpegArgs.CompressVideo(input, temp, options, plan);
                    args.Insert(args.Count - 1, "-f");
                    args.Insert(args.Count - 1, "mp4");
                    await Ffmpeg.RunAsync(args, duration, progress, ct);
                    var size = new FileInfo(temp).Length;
                    if (FfmpegArgs.TargetRetryScale(scale, size, target) is not { } next) break;
                    scale = next;
                }
            }
            else
            {
                var args = FfmpegArgs.CompressVideo(input, temp, options);
                args.Insert(args.Count - 1, "-f");
                args.Insert(args.Count - 1, "mp4");
                await Ffmpeg.RunAsync(args, duration, progress, ct);
            }
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return Done(output, new FileInfo(input).Length, new FileInfo(output).Length);
    }

    private async Task<FrameworkElement> RunGifAsync(IProgress<double> progress, CancellationToken ct)
    {
        var input = _files[0];
        var probe = await Ffmpeg.ProbeAsync(input, ct);
        if (!probe.HasVideo) throw new InvalidOperationException("This file has no video track.");
        var (start, end) = Trim(probe.Duration);
        var options = new GifOptions
        {
            Start = start,
            End = end < probe.Duration ? end : 0,
            Width = (int)Settings.GetDouble(MediaSettings.GifWidth, 720),
            Fps = Settings.GetDouble(MediaSettings.GifFps, 12),
            Quality = Settings.GetDouble(MediaSettings.GifQuality, 0.74),
            Loop = Settings.GetBool(MediaSettings.GifLoops, true),
        };
        var output = OutputPath(input, string.Empty, "gif");
        var temp = output + ".part";
        var duration = Math.Max(0.1, end - start);
        try
        {
            if (Settings.GetString(MediaSettings.GifSizing, "resolution") == "targetSize")
            {
                var target = (long)Settings.GetDouble(MediaSettings.GifTargetMb, 10) * 1_000_000;
                options = options with { Width = Math.Clamp(probe.Width, 160, 1600), Fps = Math.Max(6, Math.Min(15, Math.Floor(300 / duration))) };
                for (var pass = 0; pass < 4; pass++)
                {
                    var args = FfmpegArgs.MakeGif(input, temp, options);
                    args.Insert(args.Count - 1, "-f");
                    args.Insert(args.Count - 1, "gif");
                    await Ffmpeg.RunAsync(args, duration, progress, ct);
                    if (FfmpegArgs.GifSizePlan(options.Width, options.Fps, new FileInfo(temp).Length, target) is not { } next) break;
                    options = options with { Width = next.Width, Fps = next.Fps };
                }
            }
            else
            {
                var args = FfmpegArgs.MakeGif(input, temp, options);
                args.Insert(args.Count - 1, "-f");
                args.Insert(args.Count - 1, "gif");
                await Ffmpeg.RunAsync(args, duration, progress, ct);
            }
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        var copy = Ui.Button("Copy", () =>
        {
            CaptureClipboard.CopyFile(output);
            Hud.Show(Glyphs.Copy, "GIF copied");
        });
        return Done(output, new FileInfo(input).Length, new FileInfo(output).Length, copy);
    }

    private async Task<FrameworkElement> RunImagesAsync(IProgress<double> progress, CancellationToken ct)
    {
        var results = await ImageProcessor.RunBatchAsync(_files.ToList(), MediaSettings.ImageOptions(), progress, ct);
        var ok = results.Where(r => r.Output is not null).ToList();
        var failed = results.Count - ok.Count;
        if (ok.Count == 1 && failed == 0) return Done(ok[0].Output!, ok[0].InputBytes, ok[0].OutputBytes);
        var summary = failed == 0 ? $"{ok.Count} images saved" : $"{ok.Count} saved, {failed} failed";
        var before = ok.Sum(r => r.InputBytes);
        var after = ok.Sum(r => r.OutputBytes);
        var lines = Ui.Stack(4,
            Ui.Row(8, Ui.Icon(failed == 0 ? Glyphs.Check : Glyphs.Warning, 14, failed == 0 ? "Green" : "Orange"), Ui.Text(summary, 13.5, FontWeights.SemiBold)),
            ok.Count > 0 ? Ui.Caption(after <= before ? $"{MediaImagePlan.FormatBytes(before - after)} saved" : $"{MediaImagePlan.FormatBytes(after - before)} larger") : null,
            failed > 0 ? Ui.Caption(string.Join(Environment.NewLine, results.Where(r => r.Error is not null).Take(3).Select(r => $"{Path.GetFileName(r.Input)}: {r.Error}"))) : null);
        var copySummary = Ui.Button("Copy summary", () => CaptureClipboard.CopyText(
            $"{summary}{Environment.NewLine}" + string.Join(Environment.NewLine, ok.Select(r => $"{Path.GetFileName(r.Input)} -> {Path.GetFileName(r.Output)}"))));
        var show = ok.Count == 0 ? null : Ui.Button("Show", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ok[0].Output}\"") { UseShellExecute = true }));
        return Ui.Card(Ui.Split(lines, Ui.Row(8, copySummary, show)));
    }

    private async Task<FrameworkElement> RunTextAsync(IProgress<double> progress, CancellationToken ct)
    {
        var texts = new List<string>();
        for (var i = 0; i < _files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var image = RasterImage.FromBitmapSource(ImageProcessor.Load(_files[i], out _));
            var text = await OcrRecognizer.RecognizeAsync(image, Settings.GetBool(MediaSettings.TextRemoveLineBreaks, false));
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(_files.Count > 1 ? $"{Path.GetFileName(_files[i])}{Environment.NewLine}{text}" : text);
            progress.Report((i + 1.0) / _files.Count);
        }
        if (texts.Count == 0) return Note(Glyphs.Info, "No text found.", "TextSecondary");
        var all = string.Join(Environment.NewLine + Environment.NewLine, texts);
        var box = new TextBox { Text = all, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var copy = Ui.Button("Copy text", () =>
        {
            CaptureClipboard.CopyText(all);
            Hud.Show(Glyphs.Copy, "Text copied");
        }, accent: true);
        var save = Ui.Button("Save as TXT", () =>
        {
            var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = MediaImagePlan.VisibleBaseName(_files[0]) + ".txt" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(dialog.FileName, all);
        });
        return Ui.Card(Ui.Stack(10, box, Ui.Row(8, copy, save)));
    }

    /// <summary>Cancels any running job (window closing, feature switched off).</summary>
    public void CancelJob() => _job?.Cancel();
}

/// <summary>The standalone Media window opened from Utilities.</summary>
public sealed class MediaWindow : Window
{
    private static MediaWindow? _current;
    private readonly MediaWorkspace _workspace = new();

    public static void Open()
    {
        PanelHost.Hide();
        if (_current is not null)
        {
            _current.Activate();
            return;
        }
        _current = new MediaWindow();
        _current.Show();
        _current.Activate();
    }

    public static void CloseCurrent() => _current?.Close();

    private MediaWindow()
    {
        Title = "Media";
        Width = 760;
        Height = 820;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowFit.ToDisplay(this);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var title = Ui.Text("Media", 22, FontWeights.SemiBold);
        var caption = Ui.Caption("Compress videos, convert and process images, make GIFs and extract text locally.", 13);
        var stack = Ui.Stack(14, Ui.Stack(4, title, caption), _workspace);
        stack.Margin = new Thickness(24);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            _workspace.CancelJob();
            _current = null;
        };
    }
}

/// <summary>Media tools feature service: nothing runs in the background; uninstalling closes the window and stops jobs.</summary>
public sealed class MediaToolsService : FeatureService
{
    public override string FeatureId => "mediaTools";

    protected override void OnStart()
    {
    }

    protected override void OnStop() => MediaWindow.CloseCurrent();
}
