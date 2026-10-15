// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;

namespace Fieldkit.Features.Capture;

/// <summary>The modes of the shared capture selector (upstream ScreenCaptureTool).</summary>
public enum CaptureTool { Screenshot, Recording, Text, Color }

/// <summary>Settings keys and defaults of the capture area (keys prefixed with their feature id).</summary>
public static class CaptureSettings
{
    // ---------------------------------------------------------------- screenshot
    public const string ScreenshotShortcut = "screenshot.shortcut";
    public const string ScreenshotShortcutDefault = "Ctrl+Alt+Shift+4";
    public const string FullScreenShortcut = "screenshot.fullScreenShortcut";
    public const string FullScreenShortcutDefault = "Ctrl+Alt+Shift+3";
    public const string EditLastShortcut = "screenshot.editLastShortcut";
    public const string EditLastShortcutDefault = "Ctrl+Alt+Shift+E";
    public const string ClipboardShortcut = "screenshot.clipboardShortcut";
    public const string ClipboardShortcutDefault = "Ctrl+Alt+Shift+P";
    public const string RecentShortcut = "screenshot.recentCapturesShortcut";
    public const string RecentShortcutDefault = "Ctrl+Alt+Shift+H";
    public const string ScreenshotShowMenu = "screenshot.showCaptureMenuOnShortcut";

    public const string Freeze = "screenshot.freeze";
    public const string HideOwnWindows = "screenshot.hideFieldkitWindows";
    public const string Delay = "screenshot.delay";
    public const string IncludePointer = "screenshot.includePointer";
    public const string ShowLastRegion = "screenshot.showLastRegion";
    public const string HighlightWindows = "screenshot.highlightWindows";
    public const string LoupeStartsOn = "screenshot.loupeStartsOn";
    public const string LoupeRememberZoom = "screenshot.loupeRememberZoom";
    public const string LoupeDefaultZoom = "screenshot.loupeDefaultZoom";
    public const string LoupeLastZoom = "screenshot.loupeLastZoom";
    public const string LoupeStepped = "screenshot.loupeSteppedZoom";
    public const string DefaultAction = "screenshot.defaultAction";
    public const string PreviewEnabled = "screenshot.previewEnabled";
    public const string PreviewDuration = "screenshot.previewDuration";
    public const string PreviewPosition = "screenshot.previewPosition";
    public const string PreviewFocus = "screenshot.previewTakesFocus";
    public const string OpenEditorDirectly = "screenshot.openEditorDirectly";
    public const string AutoCopy = "screenshot.copyToClipboard";
    public const string Downscale = "screenshot.downscale";
    public const string ScreenshotFolder = "screenshot.saveFolder";
    public const string Subfolder = "screenshot.saveSubfolder";
    public const string FileNamePattern = "screenshot.fileNamePattern";
    public const string FileNumberStart = "screenshot.fileNumberStart";
    public const string FileNumberNext = "screenshot.fileNumberNext";
    public const string ToolShortcuts = "screenshot.toolShortcutsEnabled";
    public const string ToolOrder = "screenshot.toolOrder";
    public const string LastTool = "screenshot.lastTool";
    public const string LastColor = "screenshot.lastColor";
    public const string LastStroke = "screenshot.lastStroke";
    public const string LastTextSize = "screenshot.lastTextSize";
    public const string LastBlurLevel = "screenshot.lastBlurLevel";
    public const string LastBlurStyle = "screenshot.lastBlurStyle";
    public const string LastArrowStyle = "screenshot.lastArrowStyle";
    public const string LastSticker = "screenshot.lastSticker";
    public const string Shadows = "screenshot.annotationShadows";
    public const string Backdrop = "screenshot.backdropStyle";
    public const string Watermark = "screenshot.watermarkStyle";
    public const string SettingsTab = "screenshot.settingsTool";

    // ------------------------------------------------------------- recording
    public const string RecorderShortcut = "screenRecorder.shortcut";
    public const string RecorderShortcutDefault = "Ctrl+Alt+Shift+5";
    public const string RecorderShowMenu = "screenRecorder.showCaptureMenuOnShortcut";
    public const string RecorderCountdown = "screenRecorder.countdown";
    public const string RecorderQuality = "screenRecorder.quality";
    public const string RecorderFrameRate = "screenRecorder.frameRate";
    public const string RecorderSystemAudio = "screenRecorder.systemAudio";
    public const string RecorderMicrophone = "screenRecorder.microphone";
    public const string RecorderFolder = "screenRecorder.saveFolder";
    public const string RecorderOpenEditor = "screenRecorder.openEditor";
    public const string RecorderAutomaticZoom = "screenRecorder.automaticZoom";
    public const string RecorderZoomAmount = "screenRecorder.zoomAmount";
    public const string RecorderGifSize = "screenRecorder.gifSize";
    public const string RecorderGifFrameRate = "screenRecorder.gifFrameRate";

    // ------------------------------------------------------------------- text
    public const string OcrShortcut = "screenOCR.shortcut";
    public const string OcrShortcutDefault = "Ctrl+Alt+Shift+T";
    public const string OcrShowMenu = "screenOCR.showCaptureMenuOnShortcut";
    public const string OcrRemoveLineBreaks = "screenOCR.removeLineBreaks";
    public const string OcrDetectQr = "screenOCR.detectQRCodes";

    // ------------------------------------------------------------------ color
    public const string ColorShortcut = "colorPicker.shortcut";
    public const string ColorShortcutDefault = "Ctrl+Alt+Shift+C";
    public const string ColorShowMenu = "colorPicker.showCaptureMenuOnShortcut";
    public const string ColorFormatKey = "colorPicker.format";
    public const string ColorBareHex = "colorPicker.bareHex";
    public const string ColorRecent = "colorPicker.recent";

    public static string FeatureId(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => "screenRecorder",
        CaptureTool.Text => "screenOCR",
        CaptureTool.Color => "colorPicker",
        _ => "screenshot",
    };

    public static string Title(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => "Screen recording",
        CaptureTool.Text => "Copy text from screen",
        CaptureTool.Color => "Color picker",
        _ => "Screenshot",
    };

    public static string ShortTitle(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => "Record",
        CaptureTool.Text => "Text",
        CaptureTool.Color => "Color",
        _ => "Screenshot",
    };

    public static (string Key, string Default) Shortcut(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => (RecorderShortcut, RecorderShortcutDefault),
        CaptureTool.Text => (OcrShortcut, OcrShortcutDefault),
        CaptureTool.Color => (ColorShortcut, ColorShortcutDefault),
        _ => (ScreenshotShortcut, ScreenshotShortcutDefault),
    };

    public static string ShowMenuKey(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => RecorderShowMenu,
        CaptureTool.Text => OcrShowMenu,
        CaptureTool.Color => ColorShowMenu,
        _ => ScreenshotShowMenu,
    };

    /// <summary>The tools whose feature is installed, in selector order (digits 1–4).</summary>
    public static List<CaptureTool> AvailableTools() =>
        Enum.GetValues<CaptureTool>().Where(t => FeatureCatalog.IsAvailable(FeatureId(t))).ToList();

    public static readonly int[] Delays = [0, 3, 5, 10];
    public static readonly int[] PreviewDurations = [1, 2, 3, 5, 10, 0];

    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static string DefaultRecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    public static string ScreenshotFolderPath
    {
        get
        {
            var custom = Settings.GetString(ScreenshotFolder, string.Empty);
            return string.IsNullOrWhiteSpace(custom) ? DefaultScreenshotFolder : custom;
        }
    }

    public static string RecordingFolderPath
    {
        get
        {
            var custom = Settings.GetString(RecorderFolder, string.Empty);
            return string.IsNullOrWhiteSpace(custom) ? DefaultRecordingFolder : custom;
        }
    }

    /// <summary>Private working folder for recent captures and recording masters (never uploaded).</summary>
    public static string PrivateFolder(string name)
    {
        var dir = AppInfo.DataPath(Path.Combine("Captures", name));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
