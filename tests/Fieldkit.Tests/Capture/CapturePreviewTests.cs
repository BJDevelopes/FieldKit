// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Capture;
using ZXing;
using ZXing.Common;

namespace Fieldkit.Tests.Capture;

/// <summary>
/// Renders the windows --render-ui cannot reach (selector, editor, recorder controls and editor,
/// camera mirror) from synthetic pictures, and checks OCR and QR reading on generated images.
/// No screen capture, camera or microphone is used.
/// </summary>
public class CapturePreviewTests
{
    private static string Out(string name) => Path.Combine(CaptureWpfHost.PreviewDir, name);

    private static void AssertPng(string path)
    {
        Assert.True(File.Exists(path), path);
        Assert.True(new FileInfo(path).Length > 2000, path);
    }

    [Fact]
    public void SelectorPreviews()
    {
        CaptureWpfHost.Run(() =>
        {
            var sample = PreviewRendering.SampleScreen(1280, 720);
            CaptureSelector.RenderPreview(Out("selector-hover.png"), sample);
            CaptureSelector.RenderPreview(Out("selector-drag.png"), sample, CaptureTool.Screenshot, dragging: true);
            CaptureSelector.RenderPreview(Out("selector-color.png"), sample, CaptureTool.Color);
            CaptureSelector.RenderPreview(Out("selector-record.png"), sample, CaptureTool.Recording);
        });
        AssertPng(Out("selector-hover.png"));
        AssertPng(Out("selector-drag.png"));
        AssertPng(Out("selector-color.png"));
        AssertPng(Out("selector-record.png"));
    }

    [Fact]
    public void EditorPreview()
    {
        CaptureWpfHost.Run(() => ScreenshotEditorWindow.RenderPreview(Out("editor.png"), PreviewRendering.SampleScreen(900, 560)));
        AssertPng(Out("editor.png"));
    }

    [Fact]
    public void RenderedScreenshotHasBackgroundAndMarks()
    {
        CaptureWpfHost.Run(() =>
        {
            var sample = PreviewRendering.SampleScreen(400, 300);
            var marks = new List<Annotation>
            {
                new() { Tool = AnnotationTool.Redact, Bounds = new Rect(10, 10, 50, 40) },
                new() { Tool = AnnotationTool.Pixelate, Bounds = new Rect(100, 100, 80, 60) },
            };
            var backdrop = new BackdropStyle { Kind = BackdropKind.Preset, Preset = BackdropPreset.Sunset, Padding = 0 };
            var result = ScreenshotRenderer.Render(sample, marks, false, backdrop, new WatermarkStyle { Enabled = true, Text = "Fieldkit" });
            var pad = (int)DecorationMath.Padding(400, 300, 0);
            Assert.Equal(400 + pad * 2, result.Width);
            // The solid block is drawn inside the card.
            var (r, g, b) = result.PixelAt(pad + 30, pad + 30);
            Assert.True(r < 40 && g < 40 && b < 40);
            File.WriteAllBytes(Out("rendered.png"), result.EncodePng());
        });
    }

    [Fact]
    public void RecorderPreviews()
    {
        CaptureWpfHost.Run(() =>
        {
            RecorderControlsWindow.RenderPreview(Out("recorder-controls.png"));
            RecorderEditorWindow.RenderPreview(Out("recorder-editor.png"));
        });
        AssertPng(Out("recorder-controls.png"));
        AssertPng(Out("recorder-editor.png"));
    }

    [Fact]
    public void CameraPreview()
    {
        CaptureWpfHost.Run(() => CameraPreviewWindow.RenderPreview(Out("camera.png"), PreviewRendering.SampleScreen(640, 480)));
        AssertPng(Out("camera.png"));
    }

    [Fact]
    public void SettingsPagesForEveryTool()
    {
        CaptureWpfHost.Run(() =>
        {
            foreach (var tool in Enum.GetValues<CaptureTool>())
            {
                Settings.Set(CaptureSettings.SettingsTab, tool.ToString());
                var page = ScreenCapturePage.Build();
                var content = (FrameworkElement)((System.Windows.Controls.ScrollViewer)page).Content;
                ((System.Windows.Controls.ScrollViewer)page).Content = null;
                content.Width = 820;
                var host = new System.Windows.Controls.Border { Child = content };
                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBackground");
                PreviewRendering.SaveElement(host, Out($"settings-screenCapture-{tool}.png"));
            }
            Settings.Remove(CaptureSettings.SettingsTab);
        });
        foreach (var tool in Enum.GetValues<CaptureTool>()) AssertPng(Out($"settings-screenCapture-{tool}.png"));
    }

    [Fact]
    public void ReadsQrCodes()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 260, Height = 260, Margin = 2 },
        };
        var data = writer.Write("https://example.com/fieldkit?q=1");
        var image = new RasterImage(data.Width, data.Height, data.Pixels);
        Assert.Equal("https://example.com/fieldkit?q=1", OcrRecognizer.DecodeQr(image));
        // A plain picture has none.
        var blank = new RasterImage(50, 50, Enumerable.Repeat((byte)255, 50 * 50 * 4).ToArray());
        Assert.Null(OcrRecognizer.DecodeQr(blank));
    }

    [Fact]
    public async Task RecognisesRenderedText()
    {
        var text = await CaptureWpfHost.RunAsync(async () =>
        {
            if (OcrRecognizer.CreateEngine() is null) return "SKIPPED";
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 700, 140));
                var ft = new FormattedText("Fieldkit reads this line", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 34, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(20, 30));
                var ft2 = new FormattedText("and this second one", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 34, Brushes.Black, 1.0);
                dc.DrawText(ft2, new Point(20, 80));
            }
            var image = RasterImage.FromVisual(dv, 700, 140);
            return await OcrRecognizer.RecognizeAsync(image, removeLineBreaks: true);
        });
        if (text == "SKIPPED") return; // no OCR language installed on this machine
        Assert.Contains("Fieldkit reads this line", text);
        Assert.Contains("second one", text);
        Assert.DoesNotContain("\n", text);
    }
}
