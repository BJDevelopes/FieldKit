// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Screenshot, screen recording, copy text from screen, color picker and camera preview: the tools
/// that start from the screen, sharing one selector (upstream ScreenCaptureService).
/// </summary>
public sealed class CaptureModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("screenshot", Parity.Partial,
            "Area/window/screen capture on a frozen per-monitor overlay with mode bar, loupe, last-area repeat and delay; " +
            "editor with arrows (5 styles), shapes, pen, highlighter, text, stickers, numbered steps, pixelate/blur/erase, solid blocks, crop, " +
            "background and watermark; copy, save, pin, Windows share sheet, open with, recent captures. Missing: upstream's expiring upload links " +
            "(no hosting service: captures are never uploaded), Add to Shelf, editor zoom/resize handles, the blur \"text only\" option and saved " +
            "background/watermark presets; a picked window is cropped from the frozen screen (overlapping windows show), and scrolling capture " +
            "only joins vertical scrolling.");
        r.Implements("screenRecorder", Parity.Partial,
            "Windows.Graphics.Capture (monitor, window or area) to H.264 MP4 via the Media Foundation sink writer, PC sound (WASAPI loopback) and " +
            "microphone as separate AAC tracks, countdown, pause/resume/stop/delete controls kept out of the picture, then an editor with trim, " +
            "cuts, automatic zooms from clicks, manual zooms, privacy blurs, per-track volume/removal and export speed, saved as MP4 or GIF or copied. " +
            "Zoom, blur, volume, speed and GIF exports need a local ffmpeg (offered via winget); without it only trims and cuts export. Missing: " +
            "upstream's pointer smoothing/redrawing, click rings, looks/backgrounds/shapes, text and image overlays, typing-aware zooms, presets and " +
            "expiring share links; the yellow capture border Windows draws is not removed; capture was not exercised with real hardware in development.");
        r.Implements("screenOCR", Parity.Full,
            "Area from the shared selector, offline Windows.Media.Ocr in the user's languages, optional line-break removal, QR codes read with ZXing.Net " +
            "and offered to copy or open.");
        r.Implements("colorPicker", Parity.Full,
            "Selector Color mode with the pixel loupe (wheel zoom, arrow-key nudge); copies HEX (optionally without #), RGB, HSL or WPF code " +
            "(SwiftUI becomes WPF); recent colours on the settings page.");
        r.Implements("cameraPreview", Parity.Full,
            "Floating always-on-top mirror via MediaCapture frame reader with camera picker, closes on Esc or a click elsewhere; adds mirror, round " +
            "shape and size options. Not exercised with a real camera in development.");

        r.Service("screenshot", () => new ScreenshotService());
        r.Service("screenRecorder", () => new ScreenRecorderService());
        r.Service("screenOCR", () => new ScreenTextService());
        r.Service("colorPicker", () => new ColorPickerService());
        r.Service("cameraPreview", () => new CameraPreviewService());

        r.Utility(new UtilityEntry("screenshot", "Screen capture", "Capture an area, window or the whole screen",
            Glyphs.Camera, () => CaptureEntry.Start(CaptureTool.Screenshot, false), 95, CaptureSettings.ScreenshotShortcut));
        r.Utility(new UtilityEntry("screenRecorder", "Screen recording", "Record an area, window or the whole screen",
            Glyphs.Record, RecorderController.Toggle, 96, CaptureSettings.RecorderShortcut));
        r.Utility(new UtilityEntry("screenshot", "Recent captures", "Screenshots and recordings you took recently, ready to restore or open.",
            Glyphs.Clock, RecentCapturesWindow.Open, 97, CaptureSettings.RecentShortcut));
        r.Utility(new UtilityEntry("screenOCR", "Copy text from screen", "Select an area of the screen and the recognized text is copied, ready to paste.",
            Glyphs.TextScan, ScreenTextService.Capture, 140, CaptureSettings.OcrShortcut));
        r.Utility(new UtilityEntry("colorPicker", "Color picker", "Grab the color of any pixel on screen and copy it in your favorite format.",
            Glyphs.Eyedropper, ColorPickerService.Pick, 145, CaptureSettings.ColorShortcut));
        r.Utility(new UtilityEntry("cameraPreview", "Camera preview", "Check how you look before a call",
            Glyphs.Webcam, CameraPreviewWindow.Open, 150, CameraSettings.Shortcut));

        r.SettingsPage(new SettingsPageDef("screenCapture", SettingsCategory.Utilities, "Screen capture", Glyphs.Camera, 5,
            ["screenshot", "screenRecorder", "screenOCR", "colorPicker"], ScreenCapturePage.Build,
            ["screenshot", "recording", "record", "video", "ocr", "text", "qr", "color", "colour", "picker", "eyedropper", "loupe", "magnifier",
             "annotate", "pin", "scrolling", "gif", "print screen"]));
        r.SettingsPage(new SettingsPageDef("camera", SettingsCategory.Utilities, "Camera preview", Glyphs.Webcam, 120,
            ["cameraPreview"], CameraPage.Build, ["webcam", "mirror", "call"]));

        r.Action(new ToolAction("screenshot.capture", "screenshot", "Screenshot", Glyphs.Camera,
            () => CaptureEntry.Start(CaptureTool.Screenshot, false), CaptureSettings.ScreenshotShortcut));
        r.Action(new ToolAction("screenshot.fullScreen", "screenshot", "Capture the whole screen", Glyphs.Maximize,
            CaptureEntry.CaptureFullScreen, CaptureSettings.FullScreenShortcut));
        r.Action(new ToolAction("screenshot.scrolling", "screenshot", "Scrolling capture", SelectorChrome.StackGlyph, CaptureEntry.StartScrolling));
        r.Action(new ToolAction("screenshot.editLast", "screenshot", "Edit latest screenshot", Glyphs.Edit,
            ScreenshotFlow.EditLatest, CaptureSettings.EditLastShortcut));
        r.Action(new ToolAction("screenshot.editClipboard", "screenshot", "Edit clipboard image", Glyphs.Clipboard,
            ScreenshotFlow.EditClipboard, CaptureSettings.ClipboardShortcut));
        r.Action(new ToolAction("screenshot.recent", "screenshot", "Recent captures", Glyphs.Clock,
            RecentCapturesWindow.Open, CaptureSettings.RecentShortcut));
        r.Action(new ToolAction("screenshot.closePins", "screenshot", "Close all pins", Glyphs.Unpin, PinWindow.CloseAll));
        r.Action(new ToolAction("screenRecorder.toggle", "screenRecorder", "Screen recording", Glyphs.Record,
            RecorderController.Toggle, CaptureSettings.RecorderShortcut));
        r.Action(new ToolAction("screenOCR.capture", "screenOCR", "Copy text from screen", Glyphs.TextScan,
            ScreenTextService.Capture, CaptureSettings.OcrShortcut));
        r.Action(new ToolAction("colorPicker.pick", "colorPicker", "Color picker", Glyphs.Eyedropper,
            ColorPickerService.Pick, CaptureSettings.ColorShortcut));
        r.Action(new ToolAction("cameraPreview.toggle", "cameraPreview", "Camera preview", Glyphs.Webcam,
            CameraPreviewWindow.Toggle, CameraSettings.Shortcut));
    }
}
