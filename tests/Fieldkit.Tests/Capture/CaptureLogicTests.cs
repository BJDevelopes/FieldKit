// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using Fieldkit.Features.Capture;

namespace Fieldkit.Tests.Capture;

public class ColorFormatTests
{
    [Theory]
    [InlineData(ColorFormat.Hex, false, "#1A2B3C")]
    [InlineData(ColorFormat.Hex, true, "1A2B3C")]
    [InlineData(ColorFormat.Rgb, false, "rgb(26, 43, 60)")]
    [InlineData(ColorFormat.Wpf, false, "Color.FromRgb(26, 43, 60)")]
    public void FormatsComponents(ColorFormat format, bool bare, string expected)
    {
        Assert.Equal(expected, ColorFormats.Format(0x1A, 0x2B, 0x3C, format, bare));
    }

    [Fact]
    public void HslMatchesKnownColours()
    {
        Assert.Equal("hsl(0, 100%, 50%)", ColorFormats.Format(255, 0, 0, ColorFormat.Hsl));
        Assert.Equal("hsl(120, 100%, 25%)", ColorFormats.Format(0, 128, 0, ColorFormat.Hsl));
        Assert.Equal("hsl(0, 0%, 100%)", ColorFormats.Format(255, 255, 255, ColorFormat.Hsl));
        Assert.Equal("hsl(210, 39%, 18%)", ColorFormats.Format(0x1C, 0x2E, 0x40, ColorFormat.Hsl));
    }

    [Fact]
    public void AlphaForms()
    {
        Assert.Equal("#FF000080", ColorFormats.Format(255, 0, 0, ColorFormat.Hex, alpha: 0.5));
        Assert.Equal("rgba(255, 0, 0, 0.5)", ColorFormats.Format(255, 0, 0, ColorFormat.Rgb, alpha: 0.5));
    }

    [Fact]
    public void BareHexOnlyAffectsHex()
    {
        Assert.Equal("rgb(1, 2, 3)", ColorFormats.Format(1, 2, 3, ColorFormat.Rgb, bareHex: true));
    }

    [Fact]
    public void ParseFallsBackToHex()
    {
        Assert.Equal(ColorFormat.Hex, ColorFormats.Parse("swiftui"));
        Assert.Equal(ColorFormat.Wpf, ColorFormats.Parse("WPF"));
    }

    [Fact]
    public void RecentColoursDeduplicateAndCap()
    {
        var list = new List<string>();
        for (var i = 0; i < 20; i++) list = ColorFormats.PushRecent(list, $"#0000{i:X2}");
        Assert.Equal(12, list.Count);
        list = ColorFormats.PushRecent(list, "#000013");
        Assert.Equal("#000013", list[0]);
        Assert.Single(list, c => c == "#000013");
        Assert.True(ColorFormats.TryParseHex(list[0], out _, out _, out var b));
        Assert.Equal(0x13, b);
    }
}

public class CaptureGeometryTests
{
    [Fact]
    public void DragRectIsNormalised()
    {
        Assert.Equal(new PixelRect(10, 20, 30, 40), PixelRect.FromPoints(40, 60, 10, 20));
        Assert.False(CaptureGeometry.IsDrag(5, 5, 7, 6));
        Assert.True(CaptureGeometry.IsDrag(5, 5, 9, 5));
    }

    [Fact]
    public void IntersectAndEven()
    {
        var a = new PixelRect(0, 0, 100, 100);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(new PixelRect(50, 50, 200, 200)));
        Assert.True(a.Intersect(new PixelRect(200, 0, 5, 5)).IsEmpty);
        Assert.Equal(new PixelRect(1, 1, 98, 100), new PixelRect(1, 1, 99, 101).EvenSize());
    }

    [Fact]
    public void FrontMostWindowWins()
    {
        var windows = new List<PickableWindow>
        {
            new(1, new PixelRect(0, 0, 100, 100), "front"),
            new(2, new PixelRect(0, 0, 500, 500), "back"),
        };
        Assert.Equal("front", CaptureGeometry.WindowAt(50, 50, windows)?.Title);
        Assert.Equal("back", CaptureGeometry.WindowAt(300, 300, windows)?.Title);
        Assert.Null(CaptureGeometry.WindowAt(900, 900, windows));
    }

    [Theory]
    [InlineData(1.0, 13)]
    [InlineData(0.5, 27)]
    [InlineData(4.3333, 3)]
    [InlineData(2.0, 7)]
    public void LoupeSampleSideIsOdd(double zoom, int expected)
    {
        var side = CaptureGeometry.LoupeSampleSide(zoom);
        Assert.Equal(expected, side);
        Assert.Equal(1, side % 2);
    }

    [Fact]
    public void SteppedZoomMovesOneGridLevel()
    {
        var z = 1.0;
        var side = CaptureGeometry.LoupeSampleSide(z);
        z = CaptureGeometry.LoupeSteppedZoom(z, 120);
        Assert.Equal(side - 2, CaptureGeometry.LoupeSampleSide(z));
        z = CaptureGeometry.LoupeSteppedZoom(z, -120);
        Assert.Equal(side, CaptureGeometry.LoupeSampleSide(z));
        Assert.Equal(CaptureGeometry.LoupeMinZoom, CaptureGeometry.LoupeSteppedZoom(CaptureGeometry.LoupeMinZoom, -120));
    }

    [Fact]
    public void FastZoomStaysInRange()
    {
        var z = 1.0;
        for (var i = 0; i < 50; i++) z = CaptureGeometry.LoupeFastZoom(z, 120);
        Assert.Equal(CaptureGeometry.LoupeMaxZoom, z, 6);
        for (var i = 0; i < 50; i++) z = CaptureGeometry.LoupeFastZoom(z, -120);
        Assert.Equal(CaptureGeometry.LoupeMinZoom, z, 6);
    }

    [Fact]
    public void LoupeSampleRectStaysInside()
    {
        Assert.Equal(new PixelRect(0, 0, 13, 13), CaptureGeometry.LoupeSampleRect(2, 3, 1920, 1080, 13));
        Assert.Equal(new PixelRect(1907, 1067, 13, 13), CaptureGeometry.LoupeSampleRect(1919, 1079, 1920, 1080, 13));
        Assert.Equal(new PixelRect(94, 44, 13, 13), CaptureGeometry.LoupeSampleRect(100, 50, 1920, 1080, 13));
    }

    [Fact]
    public void LoupeFlipsAwayFromEdges()
    {
        var (x, y) = CaptureGeometry.LoupeOrigin(100, 400, 132, 162, 1920, 1080);
        Assert.Equal(116, x);
        Assert.Equal(400 - 162 - 16, y);
        (x, y) = CaptureGeometry.LoupeOrigin(1900, 20, 132, 162, 1920, 1080);
        Assert.Equal(1900 - 132 - 16, x);
        Assert.Equal(36, y);
    }

    [Fact]
    public void DownscaleOnlyOnScaledDisplays()
    {
        Assert.Equal((800, 600), CaptureGeometry.DownscaledSize(800, 600, 1));
        Assert.Equal((400, 300), CaptureGeometry.DownscaledSize(800, 600, 2));
        Assert.Equal((640, 480), CaptureGeometry.DownscaledSize(960, 720, 1.5));
    }
}

public class CaptureFileNamingTests
{
    private static readonly DateTime Date = new(2024, 3, 7, 9, 5, 2);

    [Fact]
    public void DefaultNameIsColonFree()
    {
        Assert.Equal("Screenshot 2024-03-07 at 09.05.02.png", CaptureFileNaming.DefaultFileName("Screenshot", Date));
    }

    [Fact]
    public void SubfolderTokens()
    {
        Assert.Equal("24-03", CaptureFileNaming.ExpandSubfolder("%y-%mo", Date));
        Assert.Equal($"2024{Path.DirectorySeparatorChar}March", CaptureFileNaming.ExpandSubfolder("%year/%month", Date));
        Assert.Equal("x", CaptureFileNaming.ExpandSubfolder("../x/./", Date));
        Assert.Equal(string.Empty, CaptureFileNaming.ExpandSubfolder("  ", Date));
    }

    [Fact]
    public void FileNamePatternNumbers()
    {
        Assert.Equal("Shot 007 07-09-05-02", CaptureFileNaming.ExpandFileNamePattern("Shot %### %d-%h-%mi-%s", Date, 7));
        Assert.Equal("a-b", CaptureFileNaming.ExpandFileNamePattern("a/b", Date, 1));
        Assert.True(CaptureFileNaming.PatternUsesNumber("x%#"));
        Assert.Equal("Shot 12.png", CaptureFileNaming.FileName("Screenshot", "Shot %#", Date, 12, "png"));
        Assert.Equal("Screenshot 2024-03-07 at 09.05.02.jpg", CaptureFileNaming.FileName("Screenshot", "", Date, 1, "jpg"));
    }

    [Fact]
    public void UniqueNameAddsCounter()
    {
        var taken = new HashSet<string> { "a.png", "a 2.png" };
        Assert.Equal("a 3.png", CaptureFileNaming.UniqueFileName("a.png", taken.Contains));
        Assert.Equal("b.png", CaptureFileNaming.UniqueFileName("b.png", taken.Contains));
    }
}

public class AnnotationTests
{
    [Fact]
    public void ToolOrderSanitises()
    {
        var order = AnnotationMath.OrderedTools("crop,bogus,arrow,crop");
        Assert.Equal(AnnotationTool.Crop, order[0]);
        Assert.Equal(AnnotationTool.Arrow, order[1]);
        Assert.Equal(Enum.GetValues<AnnotationTool>().Length, order.Count);
        Assert.Equal(AnnotationTool.Crop, AnnotationMath.ToolForDigit(1, "crop,arrow", true));
        Assert.Null(AnnotationMath.ToolForDigit(1, "crop,arrow", false));
        Assert.Equal(2, AnnotationMath.DigitForTool(AnnotationTool.Arrow, "crop,arrow", true));
        Assert.Equal(AnnotationTool.Select, AnnotationMath.ToolForDigit(1, AnnotationMath.DefaultToolOrder, true));
    }

    [Fact]
    public void CounterNumbersIncrease()
    {
        var list = new List<Annotation>
        {
            new() { Tool = AnnotationTool.Counter, Number = 1 },
            new() { Tool = AnnotationTool.Counter, Number = 4 },
            new() { Tool = AnnotationTool.Rect },
        };
        Assert.Equal(5, AnnotationMath.NextCounterNumber(list));
        Assert.Equal(1, AnnotationMath.NextCounterNumber([]));
    }

    [Fact]
    public void TextSizeSteps()
    {
        Assert.Equal(24, AnnotationMath.SteppedTextSize(19, true));
        Assert.Equal(16, AnnotationMath.SteppedTextSize(19, false));
        Assert.Null(AnnotationMath.SteppedTextSize(96, true));
        Assert.Equal(19, AnnotationMath.SanitizedTextSize(0));
        Assert.Equal(96, AnnotationMath.SanitizedTextSize(500));
    }

    [Fact]
    public void PixelBlockFollowsLevel()
    {
        Assert.Equal(19, AnnotationMath.PixelBlockSize(1920, 1080));
        Assert.Equal(10, AnnotationMath.PixelBlockSize(200, 200));
        Assert.True(AnnotationMath.PixelBlockSize(1920, 1080, 5) > AnnotationMath.PixelBlockSize(1920, 1080, 1));
    }

    [Fact]
    public void ArrowHeadIsPerpendicular()
    {
        var (tip, left, right) = AnnotationMath.ArrowHead(new Point(0, 0), new Point(100, 0), 4);
        Assert.Equal(new Point(100, 0), tip);
        Assert.Equal(left.X, right.X, 6);
        Assert.Equal(-left.Y, right.Y, 6);
        Assert.True(left.X < 100);
    }

    [Fact]
    public void HitTestingPicksTopMost()
    {
        var line = new Annotation { Tool = AnnotationTool.Line, Points = [new Point(0, 0), new Point(100, 100)] };
        var rect = new Annotation { Tool = AnnotationTool.Rect, Bounds = new Rect(40, 40, 20, 20) };
        var list = new List<Annotation> { line, rect };
        Assert.Same(rect, AnnotationMath.TopMostAt(list, new Point(50, 50), 3));
        Assert.Same(line, AnnotationMath.TopMostAt(list, new Point(10, 11), 3));
        Assert.Null(AnnotationMath.TopMostAt(list, new Point(90, 10), 3));
    }

    [Fact]
    public void TranslateMovesPointsAndBounds()
    {
        var a = new Annotation { Tool = AnnotationTool.Arrow, Bounds = new Rect(1, 1, 2, 2), Points = [new Point(0, 0), new Point(5, 5)] };
        a.Translate(10, -1);
        Assert.Equal(new Point(10, -1), a.Points[0]);
        Assert.Equal(11, a.Bounds.X);
    }

    [Fact]
    public void ClampToImage()
    {
        Assert.Equal(new Rect(0, 0, 10, 10), AnnotationMath.ClampToImage(new Rect(-5, -5, 15, 15), 100, 100));
        Assert.True(AnnotationMath.ClampToImage(new Rect(200, 200, 5, 5), 100, 100).IsEmpty);
    }
}

public class DecorationTests
{
    [Fact]
    public void PaddingHasFloorAndGrows()
    {
        Assert.Equal(24, DecorationMath.Padding(100, 100, 0));
        Assert.Equal(Math.Round(1080 * (0.035 + 0.14)), DecorationMath.Padding(1920, 1080, 1));
        var canvas = DecorationMath.Canvas(100, 50, new BackdropStyle { Kind = BackdropKind.Preset });
        Assert.Equal(148, canvas.Width);
        Assert.Equal(new Size(100, 50), DecorationMath.Canvas(100, 50, new BackdropStyle()));
    }

    [Fact]
    public void WatermarkPositions()
    {
        var canvas = new Size(200, 100);
        var mark = new Size(20, 10);
        Assert.Equal(new Point(5, 5), DecorationMath.WatermarkOrigin(canvas, mark, WatermarkPosition.TopLeft, 5));
        Assert.Equal(new Point(175, 85), DecorationMath.WatermarkOrigin(canvas, mark, WatermarkPosition.BottomRight, 5));
        Assert.Equal(new Point(90, 45), DecorationMath.WatermarkOrigin(canvas, mark, WatermarkPosition.Center, 5));
    }

    [Fact]
    public void StylesRoundTripThroughJson()
    {
        var b = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = ["#010203", "#040506"], Padding = 0.2 };
        Assert.Equal(b.Kind, BackdropStyle.Parse(b.ToJson()).Kind);
        Assert.Equal(b.Colors, BackdropStyle.Parse(b.ToJson()).Colors);
        var w = new WatermarkStyle { Enabled = true, Text = "(c) me", Position = WatermarkPosition.Top };
        Assert.Equal(w, WatermarkStyle.Parse(w.ToJson()) with { });
        Assert.Equal(BackdropKind.None, BackdropStyle.Parse("not json").Kind);
    }
}

public class PixelOpsTests
{
    private static byte[] Gradient(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                data[i] = (byte)(x * 255 / (w - 1));
                data[i + 1] = (byte)(y * 255 / (h - 1));
                data[i + 2] = (byte)((x + y) % 256);
                data[i + 3] = 255;
            }
        return data;
    }

    [Fact]
    public void PixelateMakesUniformBlocksInsideOnly()
    {
        var img = Gradient(40, 40);
        var outside = img[(0 * 40 + 39) * 4];
        PixelOps.Pixelate(img, 40, 40, new PixelRect(10, 10, 20, 20), 10);
        // Every pixel of one block shares a colour.
        var first = img[(10 * 40 + 10) * 4];
        for (var y = 10; y < 20; y++)
            for (var x = 10; x < 20; x++)
                Assert.Equal(first, img[(y * 40 + x) * 4]);
        Assert.Equal(outside, img[(0 * 40 + 39) * 4]);
    }

    [Fact]
    public void BlurSmoothsAnEdge()
    {
        var w = 30;
        var img = new byte[w * w * 4];
        for (var y = 0; y < w; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(x < 15 ? 0 : 255);
                var i = (y * w + x) * 4;
                img[i] = img[i + 1] = img[i + 2] = v;
                img[i + 3] = 255;
            }
        PixelOps.Blur(img, w, w, new PixelRect(0, 0, w, w), 3);
        var mid = img[(15 * w + 14) * 4];
        Assert.InRange(mid, 40, 215);
        Assert.True(img[(15 * w + 0) * 4] < 10);
    }

    [Fact]
    public void EraseBlendsEdges()
    {
        var w = 20;
        var img = new byte[w * w * 4];
        for (var i = 0; i < img.Length; i++) img[i] = 200;
        // Dark "text" in the middle.
        PixelOps.Fill(img, w, w, new PixelRect(8, 8, 4, 4), 0, 0, 0);
        PixelOps.Erase(img, w, w, new PixelRect(6, 6, 8, 8));
        Assert.Equal(200, img[(10 * w + 10) * 4]);
    }

    [Fact]
    public void CropCopiesRows()
    {
        var img = Gradient(10, 10);
        var c = PixelOps.Crop(img, 10, new PixelRect(2, 3, 4, 5));
        Assert.Equal(4 * 5 * 4, c.Length);
        Assert.Equal(img[(3 * 10 + 2) * 4], c[0]);
        Assert.Equal(img[(7 * 10 + 5) * 4 + 1], c[(4 * 4 + 3) * 4 + 1]);
    }
}

public class ScrollStitcherTests
{
    private const int W = 120;

    private static byte[] Page(int height, int seed)
    {
        var rnd = new Random(seed);
        var data = new byte[W * height * 4];
        rnd.NextBytes(data);
        // Smooth horizontally a bit so rows look like content, keep rows distinct.
        for (var i = 3; i < data.Length; i += 4) data[i] = 255;
        return data;
    }

    private static byte[] Solid(int height, byte value)
    {
        var data = new byte[W * height * 4];
        for (var i = 0; i < data.Length; i++) data[i] = (i % 4 == 3) ? (byte)255 : value;
        return data;
    }

    private static byte[] View(byte[] header, byte[] page, int offset, int bodyRows, byte[] footer)
    {
        var stride = W * 4;
        var h = header.Length / stride + bodyRows + footer.Length / stride;
        var data = new byte[h * stride];
        Buffer.BlockCopy(header, 0, data, 0, header.Length);
        Buffer.BlockCopy(page, offset * stride, data, header.Length, bodyRows * stride);
        Buffer.BlockCopy(footer, 0, data, header.Length + bodyRows * stride, footer.Length);
        return data;
    }

    [Fact]
    public void StitchesWithStickyHeaderAndFooter()
    {
        var stride = W * 4;
        var page = Page(900, 7);
        var header = Solid(20, 40);
        var footer = Solid(15, 220);
        const int body = 200;
        var offsets = new[] { 0, 90, 200, 333, 460, 600, 700 };
        var frames = offsets.Select(o => View(header, page, o, body, footer)).ToList();
        var h = 20 + body + 15;
        var matches = new List<ScrollMatch>();
        for (var i = 1; i < frames.Count; i++)
        {
            var m = ScrollStitcher.Match(ScrollStitcher.ToGray(frames[i - 1], W, h), ScrollStitcher.ToGray(frames[i], W, h), W, h);
            Assert.Equal(ScrollStep.Advanced, m.Step);
            Assert.Equal(offsets[i] - offsets[i - 1], m.Advance);
            matches.Add(m);
        }
        var (bgra, height) = ScrollStitcher.Stitch(frames, matches, W, h);
        Assert.Equal(20 + 700 + body + 15, height);
        // Header, then the page from the top, then the footer once.
        Assert.Equal(header.AsSpan(0, stride).ToArray(), bgra.AsSpan(0, stride).ToArray());
        for (var row = 0; row < 700 + body; row += 37)
            Assert.Equal(page.AsSpan(row * stride, stride).ToArray(), bgra.AsSpan((20 + row) * stride, stride).ToArray());
        Assert.Equal(footer.AsSpan(0, stride).ToArray(), bgra.AsSpan((height - 1) * stride, stride).ToArray());
    }

    [Fact]
    public void IdenticalViewsMeanTheEnd()
    {
        var page = Page(300, 3);
        var g = ScrollStitcher.ToGray(page, W, 300);
        Assert.Equal(ScrollStep.End, ScrollStitcher.Match(g, g, W, 300).Step);
    }

    [Fact]
    public void FlatContentIsNotGuessed()
    {
        var a = Solid(200, 255);
        var b = Solid(200, 255);
        // Two blank pages that differ only by one stripe at different heights: ambiguous.
        for (var x = 0; x < W * 4; x++)
        {
            a[50 * W * 4 + x] = 0;
            b[150 * W * 4 + x] = 0;
        }
        var m = ScrollStitcher.Match(ScrollStitcher.ToGray(a, W, 200), ScrollStitcher.ToGray(b, W, 200), W, 200);
        Assert.NotEqual(ScrollStep.Advanced, m.Step);
    }
}

public class RecordingEditTests
{
    [Fact]
    public void KeptSegmentsApplyTrimAndCuts()
    {
        var edit = RecordingEdit.ForDuration(10);
        edit.TrimStart = 1;
        edit.TrimEnd = 9;
        edit.Cuts = [new TimeRange(3, 4), new TimeRange(3.5, 5), new TimeRange(8.5, 12)];
        var kept = RecordingEditMath.KeptSegments(edit);
        Assert.Equal([new TimeRange(1, 3), new TimeRange(5, 8.5)], kept);
        Assert.Equal(5.5, RecordingEditMath.ExportDuration(edit), 6);
        edit.Speed = 2;
        Assert.Equal(2.75, RecordingEditMath.ExportDuration(edit), 6);
        Assert.Null(RecordingEditMath.SourceToExport(edit, 4));
        Assert.Equal(1.5, RecordingEditMath.SourceToExport(edit, 6)!.Value, 6);
    }

    [Fact]
    public void TinyCutsAreDropped()
    {
        Assert.Empty(RecordingEditMath.NormalizedCuts([new TimeRange(1, 1.05)], 10));
    }

    [Fact]
    public void AutomaticZoomsMergeBursts()
    {
        var clicks = new[]
        {
            new RecordedClick(1, 0.2, 0.2),
            new RecordedClick(2, 0.4, 0.4),
            new RecordedClick(9, 0.9, 0.9),
            new RecordedClick(19.5, 0.5, 0.5), // the stop click
        };
        var zooms = RecordingEditMath.AutomaticZooms(clicks, 20, 2);
        Assert.Equal(2, zooms.Count);
        Assert.Equal(0.7, zooms[0].Start, 6);
        Assert.Equal(4.5, zooms[0].End, 6);
        Assert.Equal(0.3, zooms[0].FocusX, 6);
        Assert.Equal(2, zooms[0].Amount);
    }

    [Fact]
    public void ZoomProgressEases()
    {
        var z = new List<ZoomSegment> { new(1, 3, 0.5, 0.5, 2) };
        Assert.Equal(0, RecordingEditMath.ZoomProgress(0.5, z));
        Assert.Equal(1, RecordingEditMath.ZoomProgress(2, z));
        var mid = RecordingEditMath.ZoomProgress(1 + RecordingEditMath.ZoomRampIn / 2, z);
        Assert.InRange(mid, 0.4, 0.6);
        Assert.Equal(0, RecordingEditMath.ZoomProgress(3 + RecordingEditMath.ZoomRampOut + 0.1, z));
    }

    [Fact]
    public void ViewportStaysInside()
    {
        var (x, y, w, h) = RecordingEditMath.Viewport(2, 0.95, 0.02);
        Assert.Equal(0.5, w, 6);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void OverlappingZoomsAreShifted()
    {
        var zooms = RecordingEditMath.NormalizedZooms([new ZoomSegment(1, 4, 0.5, 0.5, 2), new ZoomSegment(3, 6, 0.5, 0.5, 9)], 10);
        Assert.Equal(4, zooms[1].Start);
        Assert.Equal(RecordingEditMath.MaximumZoomAmount, zooms[1].Amount);
    }

    [Fact]
    public void SpeedIsBounded()
    {
        Assert.Equal(4, RecordingEditMath.SanitizedSpeed(10));
        Assert.Equal(0.25, RecordingEditMath.SanitizedSpeed(0.01));
        Assert.Equal(1, RecordingEditMath.SanitizedSpeed(double.NaN));
        Assert.Equal(60, RecordingEditMath.GifFrameCount(5, 12));
    }

    [Fact]
    public void BitrateFollowsQuality()
    {
        Assert.True(RecordingEditMath.VideoBitrate(1920, 1080, 60, "high") > RecordingEditMath.VideoBitrate(1920, 1080, 60, "balanced"));
        Assert.True(RecordingEditMath.VideoBitrate(1920, 1080, 60, "small") < RecordingEditMath.VideoBitrate(1920, 1080, 60, "balanced"));
    }
}

public class RecentCaptureTests
{
    [Fact]
    public void HistoryKeepsTwelveWithinTheSizeBudget()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(i => new RecentCapture(Guid.NewGuid(), i % 5 == 0 ? RecentCaptureKind.Recording : RecentCaptureKind.Screenshot, DateTime.Now, $"{i}.png", null))
            .ToList();
        var kept = RecentCaptures.Capped(entries, _ => 1000);
        Assert.Equal(RecentCaptures.Limit, kept.Count);
        // Huge screenshots: only the newest one is kept, recordings (no private copy) stay.
        var big = RecentCaptures.Capped(entries, _ => RecentCaptures.MaximumBytes);
        Assert.Single(big, e => e.Kind == RecentCaptureKind.Screenshot);
        Assert.Equal(entries.Take(RecentCaptures.Limit).Count(e => e.Kind == RecentCaptureKind.Recording), big.Count(e => e.Kind == RecentCaptureKind.Recording));
    }
}

public class DownscaleTests
{
    [Fact]
    public void BoxFilterAverages()
    {
        var src = new byte[4 * 2 * 4];
        for (var i = 0; i < 8; i++) src[i * 4] = (byte)(i % 2 == 0 ? 0 : 200);
        var dst = PixelOps.Downscale(src, 4, 2, 2, 1);
        Assert.Equal(2 * 4, dst.Length);
        Assert.Equal(100, dst[0]);
        Assert.Equal(100, dst[4]);
    }
}

public class OcrTextTests
{
    [Fact]
    public void JoinsLines()
    {
        Assert.Equal($"a{Environment.NewLine}b", OcrText.Join(["a", " ", "b "], false));
        Assert.Equal("Hello world example", OcrText.Join(["Hello world", "example"], true));
        Assert.Equal("hyphenated word", OcrText.Join(["hyphen-", "ated word"], true));
        Assert.Equal("\u65E5\u672C\u8A9E\u30C6\u30AD\u30B9\u30C8", OcrText.Join(["\u65E5\u672C\u8A9E", "\u30C6\u30AD\u30B9\u30C8"], true));
    }

    [Fact]
    public void TidiesCjkSpaces()
    {
        Assert.Equal("\u65E5\u672C\u8A9E abc", OcrText.TidyCjkSpacing("\u65E5 \u672C \u8A9E abc"));
    }

    [Fact]
    public void LinksAndScale()
    {
        Assert.True(OcrText.IsOpenableLink("https://example.com/x"));
        Assert.False(OcrText.IsOpenableLink("javascript:alert(1)"));
        Assert.False(OcrText.IsOpenableLink("hello"));
        Assert.Equal(3, OcrText.RecognitionScale(300, 40, 2600));
        Assert.Equal(1, OcrText.RecognitionScale(1600, 900, 2600));
        Assert.Equal(0.5, OcrText.RecognitionScale(5200, 1000, 2600), 6);
    }
}
