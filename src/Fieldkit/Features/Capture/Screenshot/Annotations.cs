// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Fieldkit.Features.Capture;

/// <summary>Editor tools. Declaration order is the default rail order and digit mapping (upstream).</summary>
public enum AnnotationTool
{
    Select, Arrow, Pixelate, Crop, Text, Sticker, Rect, Highlight, Freehand, Line, Ellipse, Counter, Redact,
}

public enum AnnotationColor { Red, Orange, Yellow, Green, Blue, Purple, Black, White }

public enum StrokeSize { Small, Medium, Large }

public enum ArrowStyle { Filled, Outline, Open, DoubleEnded, Scribbly }

/// <summary>What a blur area does to what is under it.</summary>
public enum BlurStyle { Pixelate, Blur, Erase }

public enum Sticker { Check, Cross, Star, Heart, ThumbsUp, ThumbsDown, Smile, Laugh, Party, Fire, Warning, Eyes }

/// <summary>
/// One mark on a screenshot, in image pixel coordinates. Rect-like tools use <see cref="Bounds"/>;
/// arrows and lines use the first and last <see cref="Points"/>; the pen keeps every point.
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationTool Tool { get; init; }
    public Rect Bounds { get; set; }
    public List<Point> Points { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public AnnotationColor Color { get; set; } = AnnotationColor.Red;
    public StrokeSize Stroke { get; set; } = StrokeSize.Medium;
    public int TextSize { get; set; } = AnnotationMath.DefaultTextSize;
    public int BlurLevel { get; set; } = AnnotationMath.DefaultBlurLevel;
    public BlurStyle BlurStyle { get; set; } = BlurStyle.Pixelate;
    public ArrowStyle ArrowStyle { get; set; } = ArrowStyle.Filled;
    public Sticker Sticker { get; set; } = Sticker.Check;
    public int Number { get; set; }
    public bool Shadow { get; set; }

    public Annotation Clone() => new()
    {
        Id = Id,
        Tool = Tool,
        Bounds = Bounds,
        Points = [.. Points],
        Text = Text,
        Color = Color,
        Stroke = Stroke,
        TextSize = TextSize,
        BlurLevel = BlurLevel,
        BlurStyle = BlurStyle,
        ArrowStyle = ArrowStyle,
        Sticker = Sticker,
        Number = Number,
        Shadow = Shadow,
    };

    /// <summary>Moves the mark by an offset in image pixels.</summary>
    public void Translate(double dx, double dy)
    {
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
        for (var i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }
}

/// <summary>Pure maths of the annotation model (sizes, colours, hit testing, geometry).</summary>
public static class AnnotationMath
{
    public const int DefaultTextSize = 19;
    public static readonly int[] TextSizes = [10, 12, 14, 16, 19, 24, 28, 36, 48, 64, 72, 96];
    public const int DefaultBlurLevel = 3;
    public const int ShortcutLimit = 9;

    /// <summary>Tools that create their mark by dragging a rectangle.</summary>
    public static bool DragsRect(AnnotationTool tool) =>
        tool is AnnotationTool.Rect or AnnotationTool.Ellipse or AnnotationTool.Highlight or AnnotationTool.Pixelate or AnnotationTool.Redact;

    /// <summary>sRGB components (upstream ColorID), shared by drawing and export.</summary>
    public static (byte R, byte G, byte B) Rgb(AnnotationColor color) => color switch
    {
        AnnotationColor.Red => (237, 66, 54),
        AnnotationColor.Orange => (255, 148, 0),
        AnnotationColor.Yellow => (255, 204, 0),
        AnnotationColor.Green => (51, 199, 89),
        AnnotationColor.Blue => (10, 133, 255),
        AnnotationColor.Purple => (176, 82, 222),
        AnnotationColor.Black => (23, 23, 28),
        _ => (255, 255, 255),
    };

    /// <summary>Line width in image pixels; scales with the capture's display scale so marks keep their weight.</summary>
    public static double StrokeWidth(StrokeSize stroke, double scale = 1) => (stroke switch
    {
        StrokeSize.Small => 2,
        StrokeSize.Large => 7,
        _ => 4,
    }) * Math.Max(1, scale);

    public static int SanitizedTextSize(int size) =>
        size == 0 ? DefaultTextSize : Math.Clamp(size, TextSizes[0], TextSizes[^1]);

    /// <summary>The preset one step away, or null at either end.</summary>
    public static int? SteppedTextSize(int size, bool up) =>
        up ? TextSizes.Where(s => s > size).Cast<int?>().FirstOrDefault()
           : TextSizes.Where(s => s < size).Cast<int?>().LastOrDefault();

    public static int SanitizedBlurLevel(int level) => Math.Clamp(level, 1, 5);

    /// <summary>Mosaic block relative to level 3 (upstream BlurStrength.blockFactor).</summary>
    public static double BlockFactor(int level) => SanitizedBlurLevel(level) switch
    {
        1 => 0.4,
        2 => 0.65,
        4 => 1.5,
        5 => 2.2,
        _ => 1,
    };

    /// <summary>Mosaic block side in pixels for an image (upstream pixelBlockSize).</summary>
    public static int PixelBlockSize(double imageWidth, double imageHeight, int level = DefaultBlurLevel)
    {
        var baseSize = Math.Max(10, (int)(Math.Min(imageWidth, imageHeight) / 55));
        return Math.Max(2, (int)Math.Round(baseSize * BlockFactor(level)));
    }

    /// <summary>Soft blur radius in pixels for a level.</summary>
    public static int BlurRadius(double imageWidth, double imageHeight, int level = DefaultBlurLevel) =>
        Math.Max(2, PixelBlockSize(imageWidth, imageHeight, level) / 2);

    public static Rect NormalizedRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Clamps a rectangle to the image; empty when it falls outside.</summary>
    public static Rect ClampToImage(Rect r, double width, double height)
    {
        var x = Math.Clamp(r.X, 0, width);
        var y = Math.Clamp(r.Y, 0, height);
        var right = Math.Clamp(r.Right, 0, width);
        var bottom = Math.Clamp(r.Bottom, 0, height);
        return right <= x || bottom <= y ? Rect.Empty : new Rect(x, y, right - x, bottom - y);
    }

    /// <summary>Whether a finished gesture is a tap (no mark), using upstream's 7-unit tolerance.</summary>
    public static bool IsTap(Point start, Point end, double viewScale = 1) =>
        (end - start).Length * viewScale < 7;

    /// <summary>The next step number: one more than the highest so far.</summary>
    public static int NextCounterNumber(IEnumerable<Annotation> annotations) =>
        annotations.Where(a => a.Tool == AnnotationTool.Counter).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Counter badge diameter for a text size.</summary>
    public static double CounterDiameter(int textSize, double scale = 1) => Math.Max(22, textSize * 1.55) * Math.Max(1, scale);

    /// <summary>
    /// Arrow head triangle for a shaft from <paramref name="start"/> to <paramref name="end"/>:
    /// returns (tip, left, right) where the base is perpendicular to the shaft.
    /// </summary>
    public static (Point Tip, Point Left, Point Right) ArrowHead(Point start, Point end, double strokeWidth)
    {
        var v = end - start;
        var length = v.Length;
        if (length < 0.001) return (end, end, end);
        v /= length;
        var headLength = Math.Min(length * 0.6, Math.Max(12, strokeWidth * 4.2));
        var headWidth = headLength * 0.62;
        var basePoint = end - v * headLength;
        var normal = new Vector(-v.Y, v.X);
        return (end, basePoint + normal * headWidth, basePoint - normal * headWidth);
    }

    /// <summary>Distance from a point to a segment.</summary>
    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        var t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Whether a click at <paramref name="p"/> picks the mark (tolerance in image pixels).</summary>
    public static bool HitTest(Annotation a, Point p, double tolerance)
    {
        switch (a.Tool)
        {
            case AnnotationTool.Arrow or AnnotationTool.Line when a.Points.Count >= 2:
                return DistanceToSegment(p, a.Points[0], a.Points[^1]) <= tolerance + StrokeWidth(a.Stroke);
            case AnnotationTool.Freehand when a.Points.Count >= 2:
                for (var i = 1; i < a.Points.Count; i++)
                    if (DistanceToSegment(p, a.Points[i - 1], a.Points[i]) <= tolerance + StrokeWidth(a.Stroke)) return true;
                return false;
            case AnnotationTool.Rect or AnnotationTool.Ellipse:
            {
                // Outline shapes are picked near their edge or inside (generous, like upstream).
                var inflated = a.Bounds;
                inflated.Inflate(tolerance, tolerance);
                return inflated.Contains(p);
            }
            default:
            {
                var r = a.Bounds;
                r.Inflate(tolerance, tolerance);
                return r.Contains(p);
            }
        }
    }

    /// <summary>The top-most mark under a point.</summary>
    public static Annotation? TopMostAt(IReadOnlyList<Annotation> annotations, Point p, double tolerance)
    {
        for (var i = annotations.Count - 1; i >= 0; i--)
            if (HitTest(annotations[i], p, tolerance)) return annotations[i];
        return null;
    }

    /// <summary>Bounds of a mark including its points (for selection chrome).</summary>
    public static Rect Extent(Annotation a)
    {
        if (a.Points.Count == 0) return a.Bounds;
        var minX = a.Points.Min(p => p.X);
        var minY = a.Points.Min(p => p.Y);
        var maxX = a.Points.Max(p => p.X);
        var maxY = a.Points.Max(p => p.Y);
        var r = new Rect(minX, minY, maxX - minX, maxY - minY);
        return a.Bounds.IsEmpty || a.Bounds.Width == 0 ? r : Rect.Union(r, a.Bounds);
    }

    // ------------------------------------------------------------- tool order

    public static string DefaultToolOrder => string.Join(",", Enum.GetNames<AnnotationTool>().Select(Camel));

    /// <summary>Saved ids first, then tools added later in canonical order; junk and duplicates dropped.</summary>
    public static List<AnnotationTool> OrderedTools(string? raw)
    {
        var seen = new HashSet<AnnotationTool>();
        var result = new List<AnnotationTool>();
        foreach (var id in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AnnotationTool>(id, ignoreCase: true, out var tool) && Enum.IsDefined(tool) && seen.Add(tool))
                result.Add(tool);
        }
        foreach (var tool in Enum.GetValues<AnnotationTool>())
            if (seen.Add(tool)) result.Add(tool);
        return result;
    }

    /// <summary>The tool a digit 1–9 selects, when tool shortcuts are on.</summary>
    public static AnnotationTool? ToolForDigit(int digit, string? orderRaw, bool enabled)
    {
        if (!enabled || digit < 1 || digit > ShortcutLimit) return null;
        var order = OrderedTools(orderRaw);
        return digit - 1 < order.Count ? order[digit - 1] : null;
    }

    public static int? DigitForTool(AnnotationTool tool, string? orderRaw, bool enabled)
    {
        if (!enabled) return null;
        var index = OrderedTools(orderRaw).IndexOf(tool);
        return index >= 0 && index < ShortcutLimit ? index + 1 : null;
    }

    public static string StickerGlyph(Sticker s) => s switch
    {
        Sticker.Check => "\u2705",
        Sticker.Cross => "\u274C",
        Sticker.Star => "\u2B50",
        Sticker.Heart => "\u2764\uFE0F",
        Sticker.ThumbsUp => "\U0001F44D",
        Sticker.ThumbsDown => "\U0001F44E",
        Sticker.Smile => "\U0001F600",
        Sticker.Laugh => "\U0001F602",
        Sticker.Party => "\U0001F389",
        Sticker.Fire => "\U0001F525",
        Sticker.Warning => "\u26A0\uFE0F",
        _ => "\U0001F440",
    };

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
