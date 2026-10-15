// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Capture;

/// <summary>An axis-aligned rectangle in physical pixels (right/bottom exclusive).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect Empty => default;

    /// <summary>The rectangle spanned by two drag points, whatever the drag direction.</summary>
    public static PixelRect FromPoints(int x0, int y0, int x1, int y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public PixelRect Intersect(PixelRect other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? Empty : FromLtrb(l, t, r, b);
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLtrb(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>Shrinks to even width and height (video encoders need them).</summary>
    public PixelRect EvenSize() => this with { Width = Width & ~1, Height = Height & ~1 };

    public override string ToString() => $"{X},{Y} {Width}\u00D7{Height}";
}

/// <summary>A window the selector can pick, front-most first.</summary>
public sealed record PickableWindow(IntPtr Handle, PixelRect Bounds, string Title);

/// <summary>
/// Pure geometry of the capture selector: drags, window picking and the pixel loupe
/// (ported from upstream ScreenshotSupport so the same numbers drive both platforms).
/// </summary>
public static class CaptureGeometry
{
    /// <summary>Movement below this many pixels is a click, not a drag.</summary>
    public const int DragThreshold = 4;

    public static bool IsDrag(int x0, int y0, int x1, int y1) =>
        Math.Abs(x1 - x0) >= DragThreshold || Math.Abs(y1 - y0) >= DragThreshold;

    /// <summary>The front-most window whose bounds contain the point.</summary>
    public static PickableWindow? WindowAt(int x, int y, IReadOnlyList<PickableWindow> windows) =>
        windows.FirstOrDefault(w => w.Bounds.Contains(x, y));

    /// <summary>Dim over the frozen picture (upstream 0.22 frozen, 0.18 live).</summary>
    public static double DimAlpha(bool frozen) => frozen ? 0.22 : 0.18;

    // ------------------------------------------------------------------ loupe

    public const double LoupeBaseSampleSide = 13;
    public const double LoupeMinSampleSide = 3;
    public const double LoupeMinZoom = 0.5;
    public static double LoupeMaxZoom => LoupeBaseSampleSide / LoupeMinSampleSide;
    public static readonly double[] LoupeDefaultZooms = [0.5, 1, 2, 4];

    /// <summary>The magnifier square on screen, in device-independent pixels.</summary>
    public const double LoupeFrameSide = 132;

    public static double ClampZoom(double zoom) =>
        double.IsFinite(zoom) ? Math.Clamp(zoom, LoupeMinZoom, LoupeMaxZoom) : 1;

    public static double LoupeInitialZoom(bool rememberLast, double defaultZoom, double lastZoom) =>
        ClampZoom(rememberLast ? lastZoom : defaultZoom);

    /// <summary>Stepped mode is the default unless flipped; holding Alt uses the other mode.</summary>
    public static bool UsesSteppedZoom(bool steppedByDefault, bool altPressed) => steppedByDefault != altPressed;

    /// <summary>Sampled source pixels per side: always odd and at least 3, so the target pixel is a real middle cell.</summary>
    public static int LoupeSampleSide(double zoom)
    {
        var raw = LoupeBaseSampleSide / ClampZoom(zoom);
        var odd = 2 * Math.Floor(raw / 2) + 1;
        return (int)Math.Max(LoupeMinSampleSide, odd);
    }

    /// <summary>Fast mode: one 15% step per wheel notch (three for a full notch from a classic wheel).</summary>
    public static double LoupeFastZoom(double zoom, double wheelDelta)
    {
        if (!double.IsFinite(wheelDelta) || wheelDelta == 0) return ClampZoom(zoom);
        var steps = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) / 40));
        var factor = wheelDelta > 0 ? 1.15 : 1 / 1.15;
        var result = zoom;
        for (var i = 0; i < steps; i++) result = ClampZoom(result * factor);
        return result;
    }

    /// <summary>Stepped mode: exactly one drawable grid level per notch.</summary>
    public static double LoupeSteppedZoom(double zoom, double wheelDelta)
    {
        var clamped = ClampZoom(zoom);
        if (!double.IsFinite(wheelDelta) || wheelDelta == 0) return clamped;
        var current = LoupeSampleSide(clamped);
        var widest = LoupeSampleSide(LoupeMinZoom);
        var target = wheelDelta > 0 ? Math.Max((int)LoupeMinSampleSide, current - 2) : Math.Min(widest, current + 2);
        if (target == current) return clamped;
        if (target == widest) return LoupeMinZoom;
        return ClampZoom(LoupeBaseSampleSide / target);
    }

    /// <summary>The source pixels shown in the loupe around a pixel, clamped inside the image.</summary>
    public static PixelRect LoupeSampleRect(int px, int py, int imageWidth, int imageHeight, int side)
    {
        side = Math.Max(1, Math.Min(side, Math.Min(imageWidth, imageHeight)));
        var half = side / 2;
        var x = Math.Clamp(px - half, 0, Math.Max(0, imageWidth - side));
        var y = Math.Clamp(py - half, 0, Math.Max(0, imageHeight - side));
        return new PixelRect(x, y, side, side);
    }

    /// <summary>The pixel grid only earns ink once a cell is at least 6 units big.</summary>
    public static bool LoupeGridVisible(double frameSide, int sampleSide) => sampleSide > 0 && frameSide / sampleSide >= 6;

    /// <summary>
    /// Where the loupe block (frame + readout) goes near the pointer: above-right by default,
    /// flipped left/down near the edges, always inside <paramref name="boundsWidth"/>×<paramref name="boundsHeight"/>.
    /// </summary>
    public static (double X, double Y) LoupeOrigin(double pointX, double pointY, double width, double height,
        double boundsWidth, double boundsHeight, double gap = 16, double inset = 8)
    {
        var x = pointX + gap;
        var y = pointY - height - gap;
        if (x + width > boundsWidth - inset) x = pointX - width - gap;
        if (y < inset) y = pointY + gap;
        x = Math.Clamp(x, inset, Math.Max(inset, boundsWidth - width - inset));
        y = Math.Clamp(y, inset, Math.Max(inset, boundsHeight - height - inset));
        return (x, y);
    }

    /// <summary>Arrow-key nudge in physical pixels (Shift moves ten).</summary>
    public static int NudgeStep(bool fast) => fast ? 10 : 1;

    // --------------------------------------------------------------- scaling

    /// <summary>The size a capture is saved at with "Save at 100% scale" (scaled displays only).</summary>
    public static (int Width, int Height) DownscaledSize(int width, int height, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 1.01) return (width, height);
        return (Math.Max(1, (int)Math.Round(width / scale)), Math.Max(1, (int)Math.Round(height / scale)));
    }
}
