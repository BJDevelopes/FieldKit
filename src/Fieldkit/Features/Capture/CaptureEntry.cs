// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// One entry point for screenshot, recording, text and color (upstream ScreenCaptureService): the
/// tool's own button or shortcut only picks the initial mode of the shared selector.
/// </summary>
public static class CaptureEntry
{
    private static DispatcherTimer? _countdown;

    public static void Start(CaptureTool tool, bool fromShortcut)
    {
        if (!FeatureCatalog.IsAvailable(CaptureSettings.FeatureId(tool))) return;

        // The record shortcut stops a running recording instead of opening the selector.
        if (tool == CaptureTool.Recording && RecorderController.StopIfActive()) return;
        // A running recording blocks the menu (picking Record from it would stop the take); a
        // shortcut that skips the menu may still run its own tool on top of it.
        if (RecorderController.IsActive)
        {
            var menu = !fromShortcut || Settings.GetBool(CaptureSettings.ShowMenuKey(tool), true);
            if (menu) return;
        }
        if (_countdown is not null)
        {
            CancelCountdown();
            return;
        }
        if (CaptureSelector.IsActive) return;

        var delay = tool == CaptureTool.Screenshot ? Settings.GetInt(CaptureSettings.Delay, 0) : 0;
        if (!CaptureSettings.Delays.Contains(delay)) delay = 0;
        if (delay == 0)
        {
            CaptureSelector.Begin(tool, fromShortcut, Route);
            return;
        }
        PanelHost.Hide();
        var remaining = delay;
        Hud.ShowCountdown(remaining);
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                Hud.ShowCountdown(remaining);
                return;
            }
            CancelCountdown();
            Hud.Hide();
            CaptureSelector.Begin(tool, fromShortcut, Route);
        };
        _countdown.Start();
    }

    /// <summary>Opens the selector on Screenshot with scrolling capture switched on.</summary>
    public static void StartScrolling()
    {
        if (!FeatureCatalog.IsAvailable("screenshot") || CaptureSelector.IsActive || RecorderController.IsActive) return;
        CaptureSelector.Begin(CaptureTool.Screenshot, fromShortcut: false, Route, scrolling: true);
    }

    /// <summary>Captures the whole monitor under the pointer straight away ("Capture the whole screen").</summary>
    public static void CaptureFullScreen()
    {
        if (!FeatureCatalog.IsAvailable("screenshot") || CaptureSelector.IsActive) return;
        PanelHost.Hide();
        // Let the panel leave the screen before photographing it.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Fieldkit.Interop.Native.GetCursorPos(out var p);
                var mon = Fieldkit.Interop.WindowHelper.GetMonitors()
                    .Select(m => PixelRect.FromLtrb(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom))
                    .FirstOrDefault(r => r.Contains(p.X, p.Y));
                if (mon.IsEmpty) mon = ScreenGrabber.VirtualScreen();
                var image = ScreenGrabber.Capture(mon, Settings.GetBool(CaptureSettings.IncludePointer, false),
                    Settings.GetBool(CaptureSettings.HideOwnWindows, true));
                ScreenshotFlow.Receive(image);
            }
            catch (Exception ex)
            {
                Log.Error("Full-screen capture failed", ex);
                Hud.Show(Glyphs.Warning, "The screen could not be captured");
            }
        };
        timer.Start();
    }

    public static void CancelCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    private static void Route(CaptureTool tool, CaptureOutcome outcome)
    {
        if (!FeatureCatalog.IsAvailable(CaptureSettings.FeatureId(tool))) return;
        switch (tool, outcome)
        {
            case (CaptureTool.Screenshot, CaptureOutcome.Image image):
                ScreenshotFlow.Receive(image.Picture);
                break;
            case (CaptureTool.Screenshot, CaptureOutcome.Scrolling scrolling):
                ScrollingCapture.Start(scrolling.ScreenRect);
                break;
            case (CaptureTool.Text, CaptureOutcome.Image image):
                ScreenTextService.Recognize(image.Picture);
                break;
            case (CaptureTool.Recording, CaptureOutcome.Region region):
                RecorderController.Record(region);
                break;
            case (CaptureTool.Color, CaptureOutcome.ColorSample color):
                ColorPickerService.Receive(color.R, color.G, color.B);
                break;
            default:
                Hud.Show(Glyphs.Warning, tool == CaptureTool.Recording ? "The screen could not be recorded" : "The screen could not be captured");
                break;
        }
    }
}
