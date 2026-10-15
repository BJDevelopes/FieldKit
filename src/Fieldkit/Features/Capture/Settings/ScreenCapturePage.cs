// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Media;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Settings → Screen capture (upstream ScreenCaptureSettings): one page for every tool that starts
/// from the screen. The tool picker at the top switches the options shown below it.
/// </summary>
public static class ScreenCapturePage
{
    public static FrameworkElement Build()
    {
        var tools = CaptureSettings.AvailableTools();
        if (tools.Count == 0) return Ui.Page("Screen capture", null, Ui.Caption("No screen capture tool is installed."));
        var body = new ContentControl();
        var top = new ContentControl();
        void Show()
        {
            var raw = Settings.GetString(CaptureSettings.SettingsTab, "Screenshot");
            var tool = Enum.TryParse<CaptureTool>(raw, out var t) && tools.Contains(t) ? t : tools[0];
            top.Content = TopSection(tools, tool, Show);
            body.Content = tool switch
            {
                CaptureTool.Recording => RecordingSettings(),
                CaptureTool.Text => TextSettings(),
                CaptureTool.Color => ColorSettings(),
                _ => ScreenshotSettings(),
            };
        }
        Show();
        return Ui.Page("Screen capture", "Screenshots, recordings, text and colours start from the same selector. A tool's shortcut opens it already on that tool.", top, body);
    }

    private static FrameworkElement TopSection(List<CaptureTool> tools, CaptureTool tool, Action refresh)
    {
        FrameworkElement? picker = null;
        if (tools.Count > 1)
        {
            var panel = new WrapPanel();
            foreach (var t in tools)
            {
                var tt = t;
                var rb = new RadioButton
                {
                    Content = Ui.Row(6, Ui.Icon(SelectorChrome.ToolGlyph(t), 13, "TextPrimary"), Ui.Text(CaptureSettings.Title(t), 13, wrap: false)),
                    GroupName = "captureTools",
                    IsChecked = t == tool,
                    Padding = new Thickness(12, 5, 12, 5),
                };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
                rb.Checked += (_, _) =>
                {
                    Settings.Set(CaptureSettings.SettingsTab, tt.ToString());
                    refresh();
                };
                panel.Children.Add(rb);
            }
            var border = new Border { Child = panel, CornerRadius = new CornerRadius(9), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
            border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            picker = border;
        }
        var (key, suggested) = CaptureSettings.Shortcut(tool);
        var recent = tools.Contains(CaptureTool.Screenshot) || tools.Contains(CaptureTool.Recording)
            ? CaptureUi.ShortcutToggle("Recent captures shortcut", CaptureSettings.RecentShortcut, CaptureSettings.RecentShortcutDefault,
                "Opens the screenshots and recordings you took recently.")
            : null;
        return Ui.Group("Screen capture",
            picker,
            CaptureUi.ShortcutToggle($"{CaptureSettings.Title(tool)} shortcut", key, suggested),
            Ui.ToggleRow("Show capture menu when using keyboard shortcut", "Off: the shortcut opens the selector without the mode bar. Digits 1\u20134 still switch tools.",
                CaptureSettings.ShowMenuKey(tool), true),
            recent);
    }

    // ------------------------------------------------------------- screenshot

    private static FrameworkElement ScreenshotSettings()
    {
        var actions = Ui.Stack(8,
            CaptureUi.ActionButtons(
                ("Capture now", Glyphs.Camera, () => CaptureEntry.Start(CaptureTool.Screenshot, false)),
                ("Scrolling capture", SelectorChrome.StackGlyph, CaptureEntry.StartScrolling)),
            Ui.Caption("Capture an area, window or the whole screen. Choose an area, scroll it, then press Enter or Done to join a scrolling capture."));
        var screenshot = Ui.Group("Screenshot",
            actions,
            CaptureUi.ShortcutToggle("Capture the whole screen", CaptureSettings.FullScreenShortcut, CaptureSettings.FullScreenShortcutDefault),
            CaptureUi.ShortcutToggle("Edit latest screenshot", CaptureSettings.EditLastShortcut, CaptureSettings.EditLastShortcutDefault),
            CaptureUi.ShortcutToggle("Edit clipboard image", CaptureSettings.ClipboardShortcut, CaptureSettings.ClipboardShortcutDefault));

        var zooms = new[] { 0.5, 1, 2, 4 };
        var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var currentZoom = Settings.GetDouble(CaptureSettings.LoupeDefaultZoom, 1);
        foreach (var z in zooms)
        {
            var zz = z;
            var rb = new RadioButton { Content = $"{z.ToString("0.#", CultureInfo.CurrentCulture)}\u00D7", GroupName = "loupeZoom", IsChecked = Math.Abs(z - currentZoom) < 0.01, Padding = new Thickness(12, 4, 12, 4) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) => Settings.Set(CaptureSettings.LoupeDefaultZoom, zz);
            zoomPanel.Children.Add(rb);
        }
        var zoomHost = new Border { Child = zoomPanel, CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
        zoomHost.SetResourceReference(Border.BackgroundProperty, "ControlBackground");

        var selection = Ui.Group("Selection",
            Ui.ToggleRow("Freeze the screen while selecting", "The picture stops while you choose the area, so nothing moves away or changes during the selection.", CaptureSettings.Freeze, true),
            Ui.ToggleRow("Hide Fieldkit windows", "Keeps Fieldkit's own windows out of screenshots.", CaptureSettings.HideOwnWindows, true),
            CaptureUi.SegmentedIntRow("Delay", null, CaptureSettings.Delay, [(0, "Off"), (3, "3 s"), (5, "5 s"), (10, "10 s")], 0),
            Ui.ToggleRow("Include the pointer", null, CaptureSettings.IncludePointer, false),
            Ui.ToggleRow("Show the last capture outline", "R repeats the last area.", CaptureSettings.ShowLastRegion, true),
            Ui.ToggleRow("Highlight the window under the pointer", "Turn off the blue window highlight while choosing an area. You can still click to capture a window.", CaptureSettings.HighlightWindows, true),
            Ui.ToggleRow("Start selection with the magnifier on", "Z toggles the loupe; C copies the colour under it.", CaptureSettings.LoupeStartsOn, false),
            Ui.ToggleRow("Remember the magnifier\u2019s last zoom", null, CaptureSettings.LoupeRememberZoom, false),
            Ui.Split(Ui.Text("Default magnifier zoom", 13.5, FontWeights.Medium), zoomHost),
            Ui.Split(Ui.Stack(2, Ui.Text("Wheel zoom", 13.5, FontWeights.Medium), Ui.Caption("Hold Alt to temporarily use the other mode.")),
                CaptureUi.SegmentedBool(CaptureSettings.LoupeStepped, "Fast", "Step by step", false)));

        var after = Ui.Group("After capture",
            Ui.ChoiceRow("Default action", CaptureSettings.DefaultAction, ScreenshotFlow.DefaultActions, string.Empty, "Runs automatically right after capture."),
            Ui.ToggleRow("Show confirmation preview", "After a successful automatic action, keep the preview available for editing or discarding.", CaptureSettings.PreviewEnabled, true),
            CaptureUi.SegmentedIntRow("Confirmation duration", null, CaptureSettings.PreviewDuration,
                [(1, "1 s"), (2, "2 s"), (3, "3 s"), (5, "5 s"), (10, "10 s"), (0, "Until dismissed")], 3),
            Ui.ChoiceRow("Preview position", CaptureSettings.PreviewPosition, QuickPreviewWindow.Positions, "automatic"),
            Ui.ToggleRow("Focus the preview automatically", "Shortcuts work the moment the preview appears, but the keyboard leaves the app you were using until it closes.", CaptureSettings.PreviewFocus, true),
            Ui.ToggleRow("Open the editor right after capturing", "The capture skips the floating preview and opens ready to annotate.", CaptureSettings.OpenEditorDirectly, false),
            Ui.ToggleRow("Copy to the clipboard automatically", "Every capture goes to the clipboard as soon as it is taken, ready to paste. Saving a file stays a separate choice.", CaptureSettings.AutoCopy, false),
            Ui.ToggleRow("Save at 100% size", "Captures from scaled displays (125%, 150%, 200%\u2026) are saved at their 100% pixel size, which makes smaller files.", CaptureSettings.Downscale, false));

        var number = Ui.Caption($"Next: {ScreenshotFlow.NextNumber()}");
        var numberRow = Ui.Split(Ui.Stack(2, Ui.Text("Starts at", 13.5, FontWeights.Medium), number),
            Ui.Row(8, WithWidth(NumberBox(CaptureSettings.FileNumberStart, 1, () => number.Text = $"Next: {ScreenshotFlow.NextNumber()}"), 90),
                Ui.Button("Reset", () =>
                {
                    Settings.Set(CaptureSettings.FileNumberNext, Settings.GetInt(CaptureSettings.FileNumberStart, 1));
                    number.Text = $"Next: {ScreenshotFlow.NextNumber()}";
                })));
        var saving = Ui.Group("Saving",
            CaptureUi.FolderRow("Save to", CaptureSettings.ScreenshotFolder, () => CaptureSettings.ScreenshotFolderPath, "Pictures\\Screenshots"),
            Ui.Stack(6, Ui.Text("Subfolder pattern", 13.5, FontWeights.Medium), Ui.TextInput(CaptureSettings.Subfolder, string.Empty, "%y-%mo"),
                Ui.Caption("Optional. Creates dated subfolders inside the folder above using %y, %year, %mo, %month, %d, %h, %mi and %s, for example %y-%mo for 24-03.")),
            Ui.Stack(6, Ui.Text("File name", 13.5, FontWeights.Medium), Ui.TextInput(CaptureSettings.FileNamePattern, string.Empty, "Screenshot %###"),
                Ui.Caption("Optional. Overrides the default name using the same %y, %year, %mo, %month, %d, %h, %mi and %s tokens, plus %# for an auto-incrementing number (%## pads to 2 digits, %### to 3, and so on).")),
            numberRow);

        var tools = Ui.Group("Editor tools",
            Ui.ToggleRow("Use tool shortcuts", "Digits 1\u20139 pick the first nine tools below. Move a tool up or down to change its digit.", CaptureSettings.ToolShortcuts, true),
            ToolOrderList());

        var links = Ui.Card(Ui.Stack(4,
            Ui.Row(8, Ui.Icon(Glyphs.Shield, 14), Ui.Text("Temporary links", 13.5, FontWeights.SemiBold)),
            Ui.Caption("Fieldkit never uploads captures. Share opens the Windows share sheet with the file instead (Nearby sharing, Mail, Teams and other apps).")));

        return Ui.Stack(20, screenshot, selection, after, saving, tools, links);
    }

    private static FrameworkElement WithWidth(FrameworkElement e, double w)
    {
        e.Width = w;
        return e;
    }

    private static TextBox NumberBox(string key, int fallback, Action changed)
    {
        var box = new TextBox { Text = Settings.GetInt(key, fallback).ToString(CultureInfo.CurrentCulture) };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v is >= 0 and <= 999_999)
            {
                Settings.Set(key, v);
                changed();
            }
        };
        return box;
    }

    private static FrameworkElement ToolOrderList()
    {
        var host = new ContentControl();
        void Build()
        {
            var raw = Settings.GetString(CaptureSettings.ToolOrder, AnnotationMath.DefaultToolOrder);
            var order = AnnotationMath.OrderedTools(raw);
            var grid = new WrapPanel();
            for (var i = 0; i < order.Count; i++)
            {
                var index = i;
                var tool = order[i];
                var digit = i < AnnotationMath.ShortcutLimit ? (i + 1).ToString(CultureInfo.InvariantCulture) : "\u2013";
                var badge = Ui.Badge(digit);
                badge.MinWidth = 22;
                var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => Move(order, index, -1), 10);
                var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => Move(order, index, +1), 10);
                up.IsEnabled = i > 0;
                down.IsEnabled = i < order.Count - 1;
                var cell = Ui.Row(6, badge, Ui.Icon(ScreenshotEditorWindow.ToolGlyph(tool), 14, "TextPrimary"),
                    WithWidth(Ui.Text(ScreenshotEditorWindow.ToolTitle(tool), 12.5, wrap: false), 90), up, down);
                cell.Margin = new Thickness(0, 2, 18, 2);
                grid.Children.Add(cell);
            }
            host.Content = grid;
        }
        void Move(List<AnnotationTool> order, int index, int delta)
        {
            var j = index + delta;
            if (j < 0 || j >= order.Count) return;
            (order[index], order[j]) = (order[j], order[index]);
            Settings.Set(CaptureSettings.ToolOrder, string.Join(",", order.Select(t => char.ToLowerInvariant(t.ToString()[0]) + t.ToString()[1..])));
            Build();
        }
        Build();
        return host;
    }

    // -------------------------------------------------------------- recording

    private static FrameworkElement RecordingSettings()
    {
        var recording = RecorderController.IsActive;
        var record = Ui.Stack(8,
            CaptureUi.ActionButtons((recording ? "Stop recording" : "Record now", Glyphs.Record, RecorderController.Toggle)),
            Ui.Caption("Record an area, window or the whole screen. Choose what to record in the selector; the controls stay out of the picture."));
        var main = Ui.Group("Screen recording",
            record,
            CaptureUi.SegmentedIntRow("Countdown", null, CaptureSettings.RecorderCountdown, [(0, "Off"), (3, "3 s"), (5, "5 s"), (10, "10 s")], 3),
            Ui.ToggleRow("Record the sound of the PC", "Everything you hear goes into the recording, on its own track, so you can silence it later.", CaptureSettings.RecorderSystemAudio, true),
            Ui.ToggleRow("Record the microphone", "Your voice goes into its own track and stays adjustable in the editor.", CaptureSettings.RecorderMicrophone, false),
            Ui.ToggleRow("Open the editor after recording", "The recording opens ready to trim, mute and save. Turn this off to get the file straight away.", CaptureSettings.RecorderOpenEditor, true),
            Ui.ToggleRow("Add zooms automatically", "Turn this off to start new recordings without zooms. You can still add them in the editor.", CaptureSettings.RecorderAutomaticZoom, true));
        var more = Ui.Group("More options",
            CaptureUi.SegmentedRow("Quality", "Balanced fits most uses. High keeps every detail and makes bigger files.", CaptureSettings.RecorderQuality,
                [("small", "Small file"), ("balanced", "Balanced"), ("high", "High")], "balanced"),
            CaptureUi.SegmentedIntRow("Frames per second", null, CaptureSettings.RecorderFrameRate, [(30, "30 fps"), (60, "60 fps")], 60),
            CaptureUi.SegmentedRow("GIF size", null, CaptureSettings.RecorderGifSize, [("small", "Small"), ("medium", "Medium"), ("large", "Large")], "medium"),
            CaptureUi.SegmentedIntRow("GIF smoothness", null, CaptureSettings.RecorderGifFrameRate, [(8, "8 fps"), (12, "12 fps"), (15, "15 fps")], 12),
            CaptureUi.FolderRow("Save to", CaptureSettings.RecorderFolder, () => CaptureSettings.RecordingFolderPath, "Videos\\Captures"));
        FrameworkElement ffmpeg = Ffmpeg.Locate() is { } path
            ? Ui.Card(Ui.Stack(4, Ui.Row(8, Ui.Icon(Glyphs.Check, 14, "Green"), Ui.Text("ffmpeg found", 13.5, FontWeights.SemiBold)),
                Ui.Caption($"Zooms, blurs, volume, export speed and GIFs are rendered with {path}.")))
            : Ui.Stack(8, Ffmpeg.MissingCard(), Ui.Caption("Recording, trimming and cutting work without ffmpeg."));
        var links = Ui.Card(Ui.Stack(4,
            Ui.Row(8, Ui.Icon(Glyphs.Shield, 14), Ui.Text("Temporary links", 13.5, FontWeights.SemiBold)),
            Ui.Caption("Recordings stay on this PC. Copy puts the file on the clipboard for chats and mail; nothing is uploaded.")));
        return Ui.Stack(20, main, more, ffmpeg, links);
    }

    // ------------------------------------------------------------------- text

    private static FrameworkElement TextSettings()
    {
        string languages;
        try { languages = string.Join(", ", OcrRecognizer.AvailableLanguages()); }
        catch { languages = string.Empty; }
        var langRow = Ui.Stack(6,
            Ui.Text("Recognition languages", 13.5, FontWeights.Medium),
            Ui.Caption(languages.Length == 0 ? "No text recognition language is installed." : languages),
            Ui.Button("Language settings", () => Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true })));
        return Ui.Group("Copy text from screen",
            Ui.Stack(8,
                CaptureUi.ActionButtons(("Copy text from screen", Glyphs.TextScan, () => ScreenTextService.Capture())),
                Ui.Caption("Select an area of the screen and the recognized text is copied, ready to paste.")),
            Ui.ToggleRow("Remove line breaks", "Removes line breaks so copied text pastes as one paragraph. Hold Shift as you finish the selection to do the opposite once.", CaptureSettings.OcrRemoveLineBreaks, false),
            Ui.ToggleRow("Read QR codes", "If the area has a QR code, its content is shown to copy or open.", CaptureSettings.OcrDetectQr, true),
            langRow);
    }

    // ------------------------------------------------------------------ color

    private static FrameworkElement ColorSettings()
    {
        var bare = Ui.ToggleRow("Copy without the # prefix", null, CaptureSettings.ColorBareHex, false);
        void SyncBare() => bare.Visibility = Settings.GetString(CaptureSettings.ColorFormatKey, "hex") == "hex" ? Visibility.Visible : Visibility.Collapsed;
        SyncBare();
        var recentHost = new ContentControl();
        void BuildRecent()
        {
            var recent = ColorPickerService.Recent;
            if (recent.Count == 0)
            {
                recentHost.Content = Ui.Caption("Colours you pick appear here. Click one to copy it again.");
                return;
            }
            var panel = new WrapPanel();
            foreach (var hex in recent)
            {
                if (!ColorFormats.TryParseHex(hex, out var r, out var g, out var b)) continue;
                var value = ColorPickerService.Formatted(r, g, b);
                var swatch = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    BorderThickness = new Thickness(1),
                    ToolTip = value,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                swatch.MouseLeftButtonUp += (_, _) =>
                {
                    CaptureClipboard.CopyText(value);
                    Hud.Show(Glyphs.Eyedropper, value, Color.FromRgb(r, g, b));
                };
                panel.Children.Add(swatch);
            }
            recentHost.Content = Ui.Stack(6, panel, Ui.Button("Clear", ColorPickerService.ClearRecent));
        }
        BuildRecent();
        Ui.Track(recentHost, () => Settings.Observe(BuildRecent, CaptureSettings.ColorRecent, CaptureSettings.ColorFormatKey, CaptureSettings.ColorBareHex));
        return Ui.Group("Color picker",
            Ui.Stack(8,
                CaptureUi.ActionButtons(("Pick color", Glyphs.Eyedropper, ColorPickerService.Pick)),
                Ui.Caption("Grab the color of any pixel on screen and copy it in your favorite format. Arrow keys move one pixel; the wheel zooms the loupe.")),
            CaptureUi.SegmentedRow("Copied format", null, CaptureSettings.ColorFormatKey, ColorFormats.Options, "hex", _ => SyncBare()),
            bare,
            Ui.Stack(6, Ui.Text("Recent colours", 13.5, FontWeights.Medium), recentHost));
    }
}
