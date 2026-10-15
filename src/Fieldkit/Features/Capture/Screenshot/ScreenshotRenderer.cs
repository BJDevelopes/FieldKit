// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Fieldkit.Core;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Draws a screenshot with its marks, background and watermark. The editor preview and the
/// exported PNG go through the same code, so what you see is what is saved.
/// </summary>
public static class ScreenshotRenderer
{
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface EmojiFace = new(new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface CounterFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static Color ToColor(AnnotationColor c, byte alpha = 255)
    {
        var (r, g, b) = AnnotationMath.Rgb(c);
        return Color.FromArgb(alpha, r, g, b);
    }

    /// <summary>The screenshot with pixelate / blur / erase marks burnt in (in mark order).</summary>
    public static RasterImage ApplyRedactions(RasterImage source, IEnumerable<Annotation> annotations)
    {
        var redactions = annotations.Where(a => a.Tool == AnnotationTool.Pixelate).ToList();
        if (redactions.Count == 0) return source;
        var copy = source.Clone();
        foreach (var a in redactions)
        {
            var r = AnnotationMath.ClampToImage(a.Bounds, copy.Width, copy.Height);
            if (r.IsEmpty) continue;
            var rect = new PixelRect((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
            switch (a.BlurStyle)
            {
                case BlurStyle.Blur:
                    PixelOps.Blur(copy.Pixels, copy.Width, copy.Height, rect, AnnotationMath.BlurRadius(copy.Width, copy.Height, a.BlurLevel));
                    break;
                case BlurStyle.Erase:
                    PixelOps.Erase(copy.Pixels, copy.Width, copy.Height, rect);
                    break;
                default:
                    PixelOps.Pixelate(copy.Pixels, copy.Width, copy.Height, rect, AnnotationMath.PixelBlockSize(copy.Width, copy.Height, a.BlurLevel));
                    break;
            }
        }
        return copy;
    }

    /// <summary>The text of a mark laid out at its size (also gives its bounds for hit testing).</summary>
    public static FormattedText LayoutText(Annotation a, double scale)
    {
        var text = string.IsNullOrEmpty(a.Text) ? " " : a.Text;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextFace,
            Math.Max(4, a.TextSize * Math.Max(1, scale)), new SolidColorBrush(ToColor(a.Color)), 1.0);
    }

    /// <summary>Draws every mark in image pixel coordinates.</summary>
    public static void DrawAnnotations(DrawingContext dc, IEnumerable<Annotation> annotations, double scale, bool shadows)
    {
        foreach (var a in annotations) DrawAnnotation(dc, a, scale, shadows);
    }

    /// <summary>
    /// The spotlight layer: one dim veil over the picture with every spotlight box cut out of it, so several
    /// spotlights add up instead of darkening each other.
    /// </summary>
    public static void DrawSpotlights(DrawingContext dc, IEnumerable<Annotation> annotations, double width, double height, double scale)
    {
        var holes = annotations.Where(a => a.Tool == AnnotationTool.Spotlight && a.Bounds.Width > 0 && a.Bounds.Height > 0).ToList();
        if (holes.Count == 0) return;
        var radius = 6 * Math.Max(1, scale);
        Geometry veil = new RectangleGeometry(new Rect(0, 0, width, height));
        foreach (var hole in holes) veil = new CombinedGeometry(GeometryCombineMode.Exclude, veil, new RectangleGeometry(hole.Bounds, radius, radius));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(SpotlightDim, 0, 0, 0)), null, veil);
    }

    /// <summary>How dark the area outside a spotlight gets (0–255).</summary>
    public const byte SpotlightDim = 150;

    public static void DrawAnnotation(DrawingContext dc, Annotation a, double scale, bool shadows)
    {
        // Burnt into the base, drawn as one layer, or never drawn.
        if (a.Tool is AnnotationTool.Pixelate or AnnotationTool.Spotlight or AnnotationTool.CopyText) return;
        var shadow = shadows || a.Shadow;
        if (shadow && a.Tool is not AnnotationTool.Highlight)
        {
            var offset = 2.5 * Math.Max(1, scale);
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushOpacity(0.35);
            DrawShape(dc, a, scale, Colors.Black);
            dc.Pop();
            dc.Pop();
        }
        DrawShape(dc, a, scale, null);
    }

    private static void DrawShape(DrawingContext dc, Annotation a, double scale, Color? overrideColor)
    {
        var color = overrideColor ?? ToColor(a.Color);
        var brush = new SolidColorBrush(color);
        var width = AnnotationMath.StrokeWidth(a.Stroke, scale);
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (a.Tool)
        {
            case AnnotationTool.Rect:
                dc.DrawRoundedRectangle(null, pen, a.Bounds, width, width);
                break;
            case AnnotationTool.Ellipse:
                dc.DrawEllipse(null, pen, new Point(a.Bounds.X + a.Bounds.Width / 2, a.Bounds.Y + a.Bounds.Height / 2), a.Bounds.Width / 2, a.Bounds.Height / 2);
                break;
            case AnnotationTool.Highlight:
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(overrideColor is null ? (byte)96 : (byte)0, color.R, color.G, color.B)), null, a.Bounds);
                break;
            case AnnotationTool.Redact:
                dc.DrawRectangle(new SolidColorBrush(overrideColor ?? Color.FromRgb(23, 23, 28)), null, a.Bounds);
                break;
            case AnnotationTool.Line when a.Points.Count >= 2:
                dc.DrawLine(pen, a.Points[0], a.Points[^1]);
                break;
            case AnnotationTool.Arrow when a.Points.Count >= 2:
                DrawArrow(dc, a, pen, brush, width);
                break;
            case AnnotationTool.Freehand when a.Points.Count >= 2:
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(a.Points[0], false, false);
                    ctx.PolyLineTo(a.Points.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                break;
            }
            case AnnotationTool.Text:
            {
                var ft = LayoutText(a, scale);
                if (overrideColor is not null) ft.SetForegroundBrush(brush);
                var origin = a.Bounds.TopLeft;
                if (overrideColor is null)
                {
                    // A thin contrasting outline keeps text readable on any background.
                    var (r, g, b) = AnnotationMath.Rgb(a.Color);
                    var light = 0.2126 * r + 0.7152 * g + 0.0722 * b > 150;
                    var outline = new Pen(new SolidColorBrush(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255)), Math.Max(1.5, ft.Height / 18));
                    dc.DrawGeometry(null, outline, ft.BuildGeometry(origin));
                }
                dc.DrawText(ft, origin);
                break;
            }
            case AnnotationTool.Sticker:
            {
                var side = Math.Max(8, Math.Min(a.Bounds.Width, a.Bounds.Height));
                var ft = new FormattedText(AnnotationMath.StickerGlyph(a.Sticker), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    EmojiFace, side * 0.8, brush, 1.0);
                if (overrideColor is not null)
                {
                    dc.DrawEllipse(brush, null, new Point(a.Bounds.X + a.Bounds.Width / 2, a.Bounds.Y + a.Bounds.Height / 2), side / 2.4, side / 2.4);
                    break;
                }
                dc.DrawText(ft, new Point(a.Bounds.X + (a.Bounds.Width - ft.Width) / 2, a.Bounds.Y + (a.Bounds.Height - ft.Height) / 2));
                break;
            }
            case AnnotationTool.Counter:
            {
                var d = a.Bounds.Width;
                var center = new Point(a.Bounds.X + d / 2, a.Bounds.Y + d / 2);
                dc.DrawEllipse(brush, overrideColor is null ? new Pen(Brushes.White, Math.Max(1.5, d / 14)) : null, center, d / 2, d / 2);
                if (overrideColor is null)
                {
                    var (r, g, b) = AnnotationMath.Rgb(a.Color);
                    var textBrush = 0.2126 * r + 0.7152 * g + 0.0722 * b > 170 ? Brushes.Black : Brushes.White;
                    var ft = new FormattedText(a.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        CounterFace, d * 0.52, textBrush, 1.0);
                    dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
                }
                break;
            }
        }
    }

    private static void DrawArrow(DrawingContext dc, Annotation a, Pen pen, Brush brush, double width)
    {
        var start = a.Points[0];
        var end = a.Points[^1];
        var (tip, left, right) = AnnotationMath.ArrowHead(start, end, width);
        var baseMid = new Point((left.X + right.X) / 2, (left.Y + right.Y) / 2);
        switch (a.ArrowStyle)
        {
            case ArrowStyle.Open:
                dc.DrawLine(pen, start, end);
                dc.DrawLine(pen, left, tip);
                dc.DrawLine(pen, right, tip);
                return;
            case ArrowStyle.Outline:
                dc.DrawLine(pen, start, baseMid);
                dc.DrawGeometry(null, pen, Triangle(tip, left, right));
                return;
            case ArrowStyle.DoubleEnded:
            {
                var (t2, l2, r2) = AnnotationMath.ArrowHead(end, start, width);
                var mid2 = new Point((l2.X + r2.X) / 2, (l2.Y + r2.Y) / 2);
                dc.DrawLine(pen, mid2, baseMid);
                dc.DrawGeometry(brush, pen, Triangle(tip, left, right));
                dc.DrawGeometry(brush, pen, Triangle(t2, l2, r2));
                return;
            }
            case ArrowStyle.Scribbly:
            {
                // A hand-drawn shaft: a gentle wave whose seed is the mark id, so it never jitters.
                var rnd = new Random(a.Id.GetHashCode());
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    var v = baseMid - start;
                    var n = new Vector(-v.Y, v.X);
                    if (n.Length > 0) n.Normalize();
                    const int steps = 6;
                    for (var i = 1; i <= steps; i++)
                    {
                        var t = (double)i / steps;
                        var wobble = i == steps ? 0 : (rnd.NextDouble() - 0.5) * width * 2.2;
                        ctx.LineTo(start + v * t + n * wobble, true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                dc.DrawGeometry(brush, pen, Triangle(tip, left, right));
                return;
            }
            default:
                dc.DrawLine(pen, start, baseMid);
                dc.DrawGeometry(brush, pen, Triangle(tip, left, right));
                return;
        }
    }

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, true, true);
            ctx.LineTo(c, true, true);
        }
        geo.Freeze();
        return geo;
    }

    // ----------------------------------------------------------- composition

    /// <summary>
    /// Draws the whole result (background, card, marks, watermark) into a canvas of
    /// <see cref="DecorationMath.Canvas"/> size. <paramref name="baseImage"/> already has redactions.
    /// </summary>
    public static void DrawComposition(DrawingContext dc, BitmapSource baseImage, int width, int height,
        IEnumerable<Annotation> annotations, double scale, bool shadows, BackdropStyle backdrop, WatermarkStyle watermark,
        Action<DrawingContext>? drawOverlay = null)
    {
        var canvas = DecorationMath.Canvas(width, height, backdrop);
        var full = new Rect(0, 0, canvas.Width, canvas.Height);
        if (backdrop.IsVisible)
        {
            DrawBackground(dc, full, backdrop, scale);
            var pad = DecorationMath.Padding(width, height, backdrop.Padding);
            var card = new Rect(pad, pad, width, height);
            var radius = DecorationMath.CornerRadius(width, height, backdrop.CornerRadius);
            if (backdrop.Shadow)
            {
                // A soft shadow from stacked translucent rounded rectangles.
                var spread = Math.Max(6, Math.Min(width, height) * 0.025);
                for (var i = 6; i >= 1; i--)
                {
                    var r = card;
                    r.Offset(0, spread * 0.35);
                    r.Inflate(spread * i / 6, spread * i / 6);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(16, 0, 0, 0)), null, r, radius + spread * i / 6, radius + spread * i / 6);
                }
            }
            dc.PushClip(new RectangleGeometry(card, radius, radius));
            dc.PushTransform(new TranslateTransform(pad, pad));
            dc.DrawImage(baseImage, new Rect(0, 0, width, height));
            DrawSpotlights(dc, annotations, width, height, scale);
            DrawAnnotations(dc, annotations, scale, shadows);
            drawOverlay?.Invoke(dc);
            dc.Pop();
            dc.Pop();
        }
        else
        {
            dc.DrawImage(baseImage, new Rect(0, 0, width, height));
            DrawSpotlights(dc, annotations, width, height, scale);
            DrawAnnotations(dc, annotations, scale, shadows);
            drawOverlay?.Invoke(dc);
        }
        if (watermark.IsVisible) DrawWatermark(dc, canvas, watermark);
    }

    /// <summary>Renders the final picture to pixels (what Copy and Save write).</summary>
    public static RasterImage Render(RasterImage source, IReadOnlyList<Annotation> annotations, bool shadows, BackdropStyle backdrop, WatermarkStyle watermark)
    {
        var redacted = ApplyRedactions(source, annotations);
        var canvas = DecorationMath.Canvas(source.Width, source.Height, backdrop);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            DrawComposition(dc, redacted.ToBitmapSource(), source.Width, source.Height, annotations, source.Scale, shadows, backdrop, watermark);
        var result = RasterImage.FromVisual(dv, (int)Math.Round(canvas.Width), (int)Math.Round(canvas.Height));
        return new RasterImage(result.Width, result.Height, result.Pixels) { Scale = source.Scale };
    }

    private static void DrawBackground(DrawingContext dc, Rect full, BackdropStyle style, double scale)
    {
        switch (style.Kind)
        {
            case BackdropKind.Solid when style.Colors.Length > 0:
                dc.DrawRectangle(new SolidColorBrush(ParseColor(style.Colors[0])), null, full);
                return;
            case BackdropKind.Gradient when style.Colors.Length > 1:
                dc.DrawRectangle(new LinearGradientBrush(ParseColor(style.Colors[0]), ParseColor(style.Colors[1]), new Point(0, 0), new Point(1, 1)), null, full);
                return;
            case BackdropKind.Image or BackdropKind.Wallpaper:
            {
                var path = style.Kind == BackdropKind.Wallpaper ? WallpaperPath() : style.ImagePath;
                var image = path is null ? null : LoadBackgroundImage(path, full, style.Blur);
                if (image is not null)
                {
                    // Cover: fill the canvas, cropping the longer side.
                    var s = Math.Max(full.Width / image.PixelWidth, full.Height / image.PixelHeight);
                    var w = image.PixelWidth * s;
                    var h = image.PixelHeight * s;
                    dc.PushClip(new RectangleGeometry(full));
                    dc.DrawImage(image, new Rect((full.Width - w) / 2, (full.Height - h) / 2, w, h));
                    dc.Pop();
                    return;
                }
                goto default;
            }
            default:
            {
                var stops = DecorationMath.PresetStops(style.Kind == BackdropKind.Preset ? style.Preset : BackdropPreset.Ocean);
                if (stops.Length < 2) stops = DecorationMath.PresetStops(BackdropPreset.Ocean);
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(stops[0].R, stops[0].G, stops[0].B), Color.FromRgb(stops[1].R, stops[1].G, stops[1].B),
                    new Point(0, 0), new Point(1, 1)), null, full);
                return;
            }
        }
    }

    private static BitmapSource? LoadBackgroundImage(string path, Rect canvas, double blur)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = (int)Math.Min(4096, Math.Max(256, canvas.Width));
            bmp.EndInit();
            bmp.Freeze();
            if (blur <= 0.001) return bmp;
            var raster = RasterImage.FromBitmapSource(bmp);
            var radius = (int)Math.Round(DecorationMath.BlurRadius(raster.Width, raster.Height, blur));
            if (radius >= 1) PixelOps.Blur(raster.Pixels, raster.Width, raster.Height, new PixelRect(0, 0, raster.Width, raster.Height), radius);
            return raster.ToBitmapSource();
        }
        catch (Exception ex)
        {
            Log.Warn($"Loading the background image failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The current desktop wallpaper file, if any.</summary>
    public static string? WallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = key?.GetValue("WallPaper") as string;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static void DrawWatermark(DrawingContext dc, Size canvas, WatermarkStyle w)
    {
        var margin = DecorationMath.WatermarkMargin(canvas);
        var size = DecorationMath.WatermarkTextSize(canvas, w.Size);
        BitmapSource? logo = null;
        if (!string.IsNullOrWhiteSpace(w.ImagePath) && File.Exists(w.ImagePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(w.ImagePath);
                bmp.EndInit();
                bmp.Freeze();
                logo = bmp;
            }
            catch (Exception ex)
            {
                Log.Warn($"Loading the watermark image failed: {ex.Message}");
            }
        }
        FormattedText? text = string.IsNullOrWhiteSpace(w.Text) ? null
            : new FormattedText(w.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextFace, size, new SolidColorBrush(ToColor(w.Color)), 1.0);
        var logoHeight = logo is null ? 0 : size * 1.6;
        var logoWidth = logo is null ? 0 : logoHeight * logo.PixelWidth / Math.Max(1, logo.PixelHeight);
        var gap = logo is not null && text is not null ? size * 0.4 : 0;
        var markWidth = logoWidth + gap + (text?.WidthIncludingTrailingWhitespace ?? 0);
        var markHeight = Math.Max(logoHeight, text?.Height ?? 0);
        var origin = DecorationMath.WatermarkOrigin(canvas, new Size(markWidth, markHeight), w.Position, margin);
        dc.PushOpacity(Math.Clamp(w.Opacity, 0.05, 1));
        dc.PushTransform(new RotateTransform(Math.Clamp(w.Rotation, -45, 45), origin.X + markWidth / 2, origin.Y + markHeight / 2));
        if (logo is not null) dc.DrawImage(logo, new Rect(origin.X, origin.Y + (markHeight - logoHeight) / 2, logoWidth, logoHeight));
        if (text is not null)
        {
            var p = new Point(origin.X + logoWidth + gap, origin.Y + (markHeight - text.Height) / 2);
            var (r, g, b) = AnnotationMath.Rgb(w.Color);
            var light = 0.2126 * r + 0.7152 * g + 0.0722 * b > 150;
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(light ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255)), Math.Max(1, size / 20)), text.BuildGeometry(p));
            dc.DrawText(text, p);
        }
        dc.Pop();
        dc.Pop();
    }

    public static Color ParseColor(string hex) =>
        ColorFormats.TryParseHex(hex, out var r, out var g, out var b) ? Color.FromRgb(r, g, b) : Colors.SteelBlue;
}
