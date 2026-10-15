// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Fieldkit.Features.Island.Watch;

/// <summary>A captured frame as BGRA bytes.</summary>
public sealed record WatchFrame(int Width, int Height, byte[] Bgra)
{
    public BitmapSource ToBitmap()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Width * 4);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>GDI screen capture of a rectangle in physical pixels (Graphics.CopyFromScreen).</summary>
public static class ScreenGrabber
{
    /// <summary>Captures <paramref name="x"/>,<paramref name="y"/>,<paramref name="width"/>×<paramref name="height"/>
    /// and scales it so the longer side is at most <paramref name="maxSide"/> pixels.</summary>
    public static WatchFrame? Capture(int x, int y, int width, int height, int maxSide)
    {
        if (width < 2 || height < 2) return null;
        try
        {
            using var full = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
            var w = Math.Max(1, (int)Math.Round(width * scale));
            var h = Math.Max(1, (int)Math.Round(height * scale));
            using var small = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, w, h);
            }
            var data = small.LockBits(new Drawing.Rectangle(0, 0, w, h), Drawing.Imaging.ImageLockMode.ReadOnly,
                Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[w * h * 4];
                for (var row = 0; row < h; row++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, bytes, row * w * 4, w * 4);
                return new WatchFrame(w, h, bytes);
            }
            finally
            {
                small.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Screen capture failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>PNG bytes of a frame (kept for the expanded Watch view after restarts).</summary>
    public static byte[] ToPng(WatchFrame frame)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame.ToBitmap()));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
