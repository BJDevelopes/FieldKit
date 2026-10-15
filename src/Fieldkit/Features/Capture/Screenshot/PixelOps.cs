// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Capture;

/// <summary>
/// In-place pixel operations on 32-bit BGRA buffers (stride = width * 4) used by the redaction
/// tools: mosaic, soft blur and erase. Pure so they are unit tested on synthetic images.
/// </summary>
public static class PixelOps
{
    /// <summary>Replaces each block in <paramref name="region"/> by its average colour.</summary>
    public static void Pixelate(byte[] bgra, int width, int height, PixelRect region, int block)
    {
        var r = region.Intersect(new PixelRect(0, 0, width, height));
        if (r.IsEmpty || block < 1) return;
        // Blocks are aligned to the image grid so neighbouring areas share block edges.
        for (var by = r.Y - r.Y % block; by < r.Bottom; by += block)
        {
            for (var bx = r.X - r.X % block; bx < r.Right; bx += block)
            {
                var cell = new PixelRect(bx, by, block, block).Intersect(r);
                if (cell.IsEmpty) continue;
                long sb = 0, sg = 0, sr = 0, n = 0;
                for (var y = cell.Y; y < cell.Bottom; y++)
                {
                    var row = y * width * 4;
                    for (var x = cell.X; x < cell.Right; x++)
                    {
                        var i = row + x * 4;
                        sb += bgra[i];
                        sg += bgra[i + 1];
                        sr += bgra[i + 2];
                        n++;
                    }
                }
                var b = (byte)(sb / n);
                var g = (byte)(sg / n);
                var rr = (byte)(sr / n);
                for (var y = cell.Y; y < cell.Bottom; y++)
                {
                    var row = y * width * 4;
                    for (var x = cell.X; x < cell.Right; x++)
                    {
                        var i = row + x * 4;
                        bgra[i] = b;
                        bgra[i + 1] = g;
                        bgra[i + 2] = rr;
                        bgra[i + 3] = 255;
                    }
                }
            }
        }
    }

    /// <summary>Three box-blur passes (close to a Gaussian) inside <paramref name="region"/>.</summary>
    public static void Blur(byte[] bgra, int width, int height, PixelRect region, int radius)
    {
        var r = region.Intersect(new PixelRect(0, 0, width, height));
        if (r.IsEmpty || radius < 1) return;
        var w = r.Width;
        var h = r.Height;
        var buf = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = ((r.Y + y) * width + r.X + x) * 4;
                var j = (y * w + x) * 3;
                buf[j] = bgra[i];
                buf[j + 1] = bgra[i + 1];
                buf[j + 2] = bgra[i + 2];
            }
        }
        var tmp = new float[buf.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(buf, tmp, w, h, radius);
            BoxVertical(tmp, buf, w, h, radius);
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = ((r.Y + y) * width + r.X + x) * 4;
                var j = (y * w + x) * 3;
                bgra[i] = ClampByte(buf[j]);
                bgra[i + 1] = ClampByte(buf[j + 1]);
                bgra[i + 2] = ClampByte(buf[j + 2]);
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Erase: fills the region with a smooth blend of the colours just outside its edges, so a
    /// patch of text on a plain background disappears instead of turning into a visible block.
    /// </summary>
    public static void Erase(byte[] bgra, int width, int height, PixelRect region)
    {
        var r = region.Intersect(new PixelRect(0, 0, width, height));
        if (r.IsEmpty) return;
        // Edge colours sampled one pixel outside (or on the edge when at the image border).
        var top = new float[r.Width * 3];
        var bottom = new float[r.Width * 3];
        var left = new float[r.Height * 3];
        var right = new float[r.Height * 3];
        var ty = Math.Max(0, r.Y - 1);
        var byy = Math.Min(height - 1, r.Bottom);
        var lx = Math.Max(0, r.X - 1);
        var rx = Math.Min(width - 1, r.Right);
        for (var x = 0; x < r.Width; x++)
        {
            Read(bgra, width, r.X + x, ty, top, x);
            Read(bgra, width, r.X + x, byy, bottom, x);
        }
        for (var y = 0; y < r.Height; y++)
        {
            Read(bgra, width, lx, r.Y + y, left, y);
            Read(bgra, width, rx, r.Y + y, right, y);
        }
        for (var y = 0; y < r.Height; y++)
        {
            var v = r.Height == 1 ? 0.5f : (float)y / (r.Height - 1);
            for (var x = 0; x < r.Width; x++)
            {
                var u = r.Width == 1 ? 0.5f : (float)x / (r.Width - 1);
                var i = ((r.Y + y) * width + r.X + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var vertical = top[x * 3 + c] * (1 - v) + bottom[x * 3 + c] * v;
                    var horizontal = left[y * 3 + c] * (1 - u) + right[y * 3 + c] * u;
                    bgra[i + c] = ClampByte((vertical + horizontal) / 2);
                }
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>Fills a region with one opaque colour (solid block redaction).</summary>
    public static void Fill(byte[] bgra, int width, int height, PixelRect region, byte r, byte g, byte b)
    {
        var rect = region.Intersect(new PixelRect(0, 0, width, height));
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            for (var x = rect.X; x < rect.Right; x++)
            {
                var i = (y * width + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>Average luminance (0–255) of a region, used to pick readable overlay colours.</summary>
    public static double AverageLuminance(byte[] bgra, int width, int height, PixelRect region)
    {
        var r = region.Intersect(new PixelRect(0, 0, width, height));
        if (r.IsEmpty) return 0;
        double sum = 0;
        for (var y = r.Y; y < r.Bottom; y++)
        {
            for (var x = r.X; x < r.Right; x++)
            {
                var i = (y * width + x) * 4;
                sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
            }
        }
        return sum / r.Area;
    }

    /// <summary>Copies a sub-rectangle of a BGRA buffer.</summary>
    public static byte[] Crop(byte[] bgra, int width, PixelRect region)
    {
        var result = new byte[region.Width * region.Height * 4];
        for (var y = 0; y < region.Height; y++)
            Buffer.BlockCopy(bgra, ((region.Y + y) * width + region.X) * 4, result, y * region.Width * 4, region.Width * 4);
        return result;
    }

    /// <summary>Shrinks an image by averaging the source pixels under each output pixel (box filter).</summary>
    public static byte[] Downscale(byte[] bgra, int width, int height, int outWidth, int outHeight)
    {
        var result = new byte[outWidth * outHeight * 4];
        for (var oy = 0; oy < outHeight; oy++)
        {
            var y0 = oy * height / outHeight;
            var y1 = Math.Max(y0 + 1, (oy + 1) * height / outHeight);
            for (var ox = 0; ox < outWidth; ox++)
            {
                var x0 = ox * width / outWidth;
                var x1 = Math.Max(x0 + 1, (ox + 1) * width / outWidth);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var y = y0; y < y1; y++)
                {
                    var i = (y * width + x0) * 4;
                    for (var x = x0; x < x1; x++, i += 4)
                    {
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        a += bgra[i + 3];
                        n++;
                    }
                }
                var o = (oy * outWidth + ox) * 4;
                result[o] = (byte)(b / n);
                result[o + 1] = (byte)(g / n);
                result[o + 2] = (byte)(r / n);
                result[o + 3] = (byte)(a / n);
            }
        }
        return result;
    }

    /// <summary>Forces every alpha byte to 255 (GDI leaves it undefined).</summary>
    public static void MakeOpaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }

    private static void Read(byte[] bgra, int width, int x, int y, float[] into, int index)
    {
        var i = (y * width + x) * 4;
        into[index * 3] = bgra[i];
        into[index * 3 + 1] = bgra[i + 1];
        into[index * 3 + 2] = bgra[i + 2];
    }

    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int radius)
    {
        var span = radius * 2 + 1;
        for (var y = 0; y < h; y++)
        {
            for (var c = 0; c < 3; c++)
            {
                float sum = 0;
                for (var k = -radius; k <= radius; k++) sum += src[(y * w + Math.Clamp(k, 0, w - 1)) * 3 + c];
                for (var x = 0; x < w; x++)
                {
                    dst[(y * w + x) * 3 + c] = sum / span;
                    var add = Math.Min(w - 1, x + radius + 1);
                    var remove = Math.Max(0, x - radius);
                    sum += src[(y * w + add) * 3 + c] - src[(y * w + remove) * 3 + c];
                }
            }
        }
    }

    private static void BoxVertical(float[] src, float[] dst, int w, int h, int radius)
    {
        var span = radius * 2 + 1;
        for (var x = 0; x < w; x++)
        {
            for (var c = 0; c < 3; c++)
            {
                float sum = 0;
                for (var k = -radius; k <= radius; k++) sum += src[(Math.Clamp(k, 0, h - 1) * w + x) * 3 + c];
                for (var y = 0; y < h; y++)
                {
                    dst[(y * w + x) * 3 + c] = sum / span;
                    var add = Math.Min(h - 1, y + radius + 1);
                    var remove = Math.Max(0, y - radius);
                    sum += src[(add * w + x) * 3 + c] - src[(remove * w + x) * 3 + c];
                }
            }
        }
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
