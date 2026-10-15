// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fieldkit.Features.Capture;

/// <summary>A BGRA image in memory (pixels are opaque unless a feature made them transparent).</summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer does not match the size.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    /// <summary>Display scale the pixels came from (1 = 96 dpi). Used for "Save at 100% scale".</summary>
    public double Scale { get; init; } = 1;

    public RasterImage Crop(PixelRect rect)
    {
        var r = rect.Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) throw new ArgumentException("Crop is outside the image.");
        return new RasterImage(r.Width, r.Height, PixelOps.Crop(Pixels, Width, r)) { Scale = Scale };
    }

    public RasterImage Clone() => new(Width, Height, (byte[])Pixels.Clone()) { Scale = Scale };

    public (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var i = (y * Width + x) * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    public BitmapSource ToBitmapSource()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Stride);
        bmp.Freeze();
        return bmp;
    }

    public static RasterImage FromBitmapSource(BitmapSource source, double scale = 1)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var w = bgra.PixelWidth;
        var h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return new RasterImage(w, h, pixels) { Scale = scale };
    }

    /// <summary>Renders a visual at its pixel size into a new image.</summary>
    public static RasterImage FromVisual(Visual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return FromBitmapSource(rtb);
    }

    public byte[] EncodePng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource()));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static RasterImage DecodeFile(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return FromBitmapSource(decoder.Frames[0]);
    }

    /// <summary>A copy resized with high-quality filtering.</summary>
    public RasterImage Resized(int width, int height)
    {
        if (width == Width && height == Height) return Clone();
        var scaled = new TransformedBitmap(ToBitmapSource(), new ScaleTransform((double)width / Width, (double)height / Height));
        var result = FromBitmapSource(scaled);
        if (result.Width == width && result.Height == height) return result;
        // TransformedBitmap rounds; draw into an exact canvas when it is off by one.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawImage(scaled, new Rect(0, 0, width, height));
        return FromVisual(dv, width, height);
    }
}
