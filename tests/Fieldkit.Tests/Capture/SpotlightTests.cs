// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using Fieldkit.Features.Capture;

namespace Fieldkit.Tests.Capture;

public class SpotlightTests
{
    private static string Out(string name) => Path.Combine(CaptureWpfHost.PreviewDir, name);

    private static int Brightness((byte R, byte G, byte B) p) => p.R + p.G + p.B;

    [Fact]
    public void OutsideTheSpotlightIsDimmedAndInsideIsUntouched()
    {
        CaptureWpfHost.Run(() =>
        {
            var sample = PreviewRendering.SampleScreen(400, 300);
            var marks = new List<Annotation> { new() { Tool = AnnotationTool.Spotlight, Bounds = new Rect(100, 100, 120, 80) } };
            var none = new BackdropStyle { Kind = BackdropKind.None };
            var plain = ScreenshotRenderer.Render(sample, [], false, none, new WatermarkStyle());
            var lit = ScreenshotRenderer.Render(sample, marks, false, none, new WatermarkStyle());
            Assert.Equal(plain.PixelAt(160, 140), lit.PixelAt(160, 140)); // inside
            Assert.True(Brightness(lit.PixelAt(20, 20)) < Brightness(plain.PixelAt(20, 20)) * 0.6 + 1); // outside
            File.WriteAllBytes(Out("spotlight.png"), lit.EncodePng());
        });
    }

    [Fact]
    public void SeveralSpotlightsDoNotDarkenEachOther()
    {
        CaptureWpfHost.Run(() =>
        {
            var sample = PreviewRendering.SampleScreen(400, 300);
            var none = new BackdropStyle { Kind = BackdropKind.None };
            var one = ScreenshotRenderer.Render(sample, [new() { Tool = AnnotationTool.Spotlight, Bounds = new Rect(20, 20, 60, 60) }], false, none, new WatermarkStyle());
            var two = ScreenshotRenderer.Render(sample,
            [
                new() { Tool = AnnotationTool.Spotlight, Bounds = new Rect(20, 20, 60, 60) },
                new() { Tool = AnnotationTool.Spotlight, Bounds = new Rect(200, 150, 60, 60) },
            ], false, none, new WatermarkStyle());
            Assert.Equal(one.PixelAt(350, 30), two.PixelAt(350, 30)); // dimmed once, not twice
            Assert.Equal(one.PixelAt(50, 50), two.PixelAt(50, 50));
        });
    }

    [Fact]
    public void CopyTextAndSpotlightAreDraggedBoxesWithoutColour()
    {
        Assert.True(AnnotationMath.DragsRect(AnnotationTool.Spotlight));
        Assert.True(AnnotationMath.DragsRect(AnnotationTool.CopyText));
        Assert.Contains(AnnotationTool.Spotlight, AnnotationMath.OrderedTools(AnnotationMath.DefaultToolOrder));
        // A saved order from before keeps its tools first and gains the new ones at the end.
        var order = AnnotationMath.OrderedTools("select,arrow,crop");
        Assert.Equal([AnnotationTool.Spotlight, AnnotationTool.CopyText], order[^2..]);
    }

    [Fact]
    public async Task CopyTextReadsOnlyTheDraggedBox()
    {
        if (OcrRecognizer.AvailableLanguages().Count == 0) return; // no OCR language on this machine (some CI images)
        RasterImage? box = null;
        CaptureWpfHost.Run(() =>
        {
            // The sample draws "Line N: The quick brown fox…" from y = 56 + 44 + 26·(N−1) on a 900×560 screen.
            var sample = PreviewRendering.SampleScreen(900, 560);
            box = ScreenshotRenderer.ApplyRedactions(sample, []).Crop(new PixelRect(80, 122, 380, 24));
        });
        // Awaited off the UI thread: blocking on WinRT OCR there would wait on itself.
        var text = await Task.Run(() => OcrRecognizer.RecognizeAsync(box!, removeLineBreaks: false)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("Line 2", text);
        Assert.Contains("quick brown", text);
        Assert.DoesNotContain("Line 1", text);
        Assert.DoesNotContain("Line 3", text);
    }

    [Fact]
    public void EditorPreviewWithASpotlight()
    {
        CaptureWpfHost.Run(() => ScreenshotEditorWindow.RenderPreview(Out("editor-spotlight.png"), PreviewRendering.SampleScreen(900, 560), spotlight: true));
        Assert.True(new FileInfo(Out("editor-spotlight.png")).Length > 10_000);
    }
}
