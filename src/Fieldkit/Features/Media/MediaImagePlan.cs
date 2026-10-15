// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;

namespace Fieldkit.Features.Media;

public enum ImageOutputFormat { Jpeg, Png, Tiff, Bmp, Gif, Heic }

public enum ImageResizeKind { None, MaxDimension, Width, Height, Exact }

public enum ImageExactMode { Stretch, Fit, Fill }

public enum ImageWatermarkKind { Off, Text, Logo, TextAndLogo }

public enum ImageWatermarkPosition { TopLeft, TopRight, Center, BottomLeft, BottomRight }

public enum ImageBackground { Transparent, White, Black }

/// <summary>Everything the Image tool applies to each file (upstream MediaImageOptions).</summary>
public sealed record ImageOptions
{
    public ImageOutputFormat Format { get; init; } = ImageOutputFormat.Jpeg;
    public double Quality { get; init; } = 0.72;
    public ImageResizeKind Resize { get; init; } = ImageResizeKind.MaxDimension;
    public int MaxDimension { get; init; } = 1600;
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 1200;
    public ImageExactMode ExactMode { get; init; } = ImageExactMode.Stretch;
    public ImageWatermarkKind Watermark { get; init; } = ImageWatermarkKind.Off;
    public string WatermarkText { get; init; } = string.Empty;
    public string WatermarkLogoPath { get; init; } = string.Empty;
    public ImageWatermarkPosition WatermarkPosition { get; init; } = ImageWatermarkPosition.BottomRight;
    public double WatermarkOpacity { get; init; } = 0.45;
    public int WatermarkMargin { get; init; } = 32;
    public double WatermarkScale { get; init; } = 0.18;
    public string RenamePattern { get; init; } = string.Empty;
    public ImageBackground Background { get; init; } = ImageBackground.Transparent;
    public bool StripMetadata { get; init; } = true;
    public bool PreserveModificationDate { get; init; }
    public bool SaveInSubfolder { get; init; }

    public bool UsesWatermarkText => Watermark is ImageWatermarkKind.Text or ImageWatermarkKind.TextAndLogo && !string.IsNullOrWhiteSpace(WatermarkText);
    public bool UsesWatermarkLogo => Watermark is ImageWatermarkKind.Logo or ImageWatermarkKind.TextAndLogo && !string.IsNullOrWhiteSpace(WatermarkLogoPath);
}

/// <summary>How the source maps into the output: output size and the source crop to draw.</summary>
public readonly record struct ImageResizePlan(int Width, int Height, Rect SourceCrop, Rect Destination);

/// <summary>Pure planning of the Image tool (sizes, names, watermark placement).</summary>
public static class MediaImagePlan
{
    public const int MaxRenderDimension = 20_000;
    public const long MaxRenderPixels = 8_192L * 8_192L;

    public static string Extension(ImageOutputFormat f) => f switch
    {
        ImageOutputFormat.Png => "png",
        ImageOutputFormat.Tiff => "tiff",
        ImageOutputFormat.Bmp => "bmp",
        ImageOutputFormat.Gif => "gif",
        ImageOutputFormat.Heic => "heic",
        _ => "jpg",
    };

    public static bool SupportsTransparency(ImageOutputFormat f) => f is ImageOutputFormat.Png or ImageOutputFormat.Tiff or ImageOutputFormat.Gif or ImageOutputFormat.Heic;

    public static bool UsesQuality(ImageOutputFormat f) => f is ImageOutputFormat.Jpeg or ImageOutputFormat.Heic;

    /// <summary>The output size and source mapping for one image.</summary>
    public static ImageResizePlan Plan(int sourceWidth, int sourceHeight, ImageOptions o)
    {
        var sw = Math.Max(1, sourceWidth);
        var sh = Math.Max(1, sourceHeight);
        var full = new Rect(0, 0, sw, sh);
        (int W, int H) Scale(double s)
        {
            s = Math.Min(1, s); // never enlarge in the proportional modes
            return (Math.Max(1, (int)Math.Round(sw * s)), Math.Max(1, (int)Math.Round(sh * s)));
        }
        switch (o.Resize)
        {
            case ImageResizeKind.MaxDimension:
            {
                var (w, h) = Scale((double)Math.Max(1, o.MaxDimension) / Math.Max(sw, sh));
                return new ImageResizePlan(w, h, full, new Rect(0, 0, w, h));
            }
            case ImageResizeKind.Width:
            {
                var (w, h) = Scale((double)Math.Max(1, o.Width) / sw);
                return new ImageResizePlan(w, h, full, new Rect(0, 0, w, h));
            }
            case ImageResizeKind.Height:
            {
                var (w, h) = Scale((double)Math.Max(1, o.Height) / sh);
                return new ImageResizePlan(w, h, full, new Rect(0, 0, w, h));
            }
            case ImageResizeKind.Exact:
            {
                var tw = Math.Clamp(o.Width, 1, MaxRenderDimension);
                var th = Math.Clamp(o.Height, 1, MaxRenderDimension);
                switch (o.ExactMode)
                {
                    case ImageExactMode.Fit:
                    {
                        var s = Math.Min((double)tw / sw, (double)th / sh);
                        var dw = sw * s;
                        var dh = sh * s;
                        return new ImageResizePlan(tw, th, full, new Rect((tw - dw) / 2, (th - dh) / 2, dw, dh));
                    }
                    case ImageExactMode.Fill:
                    {
                        var s = Math.Max((double)tw / sw, (double)th / sh);
                        var cw = tw / s;
                        var ch = th / s;
                        return new ImageResizePlan(tw, th, new Rect((sw - cw) / 2, (sh - ch) / 2, cw, ch), new Rect(0, 0, tw, th));
                    }
                    default:
                        return new ImageResizePlan(tw, th, full, new Rect(0, 0, tw, th));
                }
            }
            default:
                return new ImageResizePlan(sw, sh, full, new Rect(0, 0, sw, sh));
        }
    }

    public static bool IsSafeSize(int width, int height) =>
        width > 0 && height > 0 && width <= MaxRenderDimension && height <= MaxRenderDimension && (long)width * height <= MaxRenderPixels;

    /// <summary>
    /// Output base name from a rename pattern: {name} {date} {time} {datetime} {width} {height}
    /// {format}/{ext} {index}/{counter} and zero-padded {index:02}…{index:06}.
    /// </summary>
    public static string OutputBaseName(string inputPath, string? pattern, int index, DateTime date, int width, int height, ImageOutputFormat format)
    {
        var name = VisibleBaseName(inputPath);
        var p = string.IsNullOrWhiteSpace(pattern) ? "{name}" : pattern.Trim();
        var i = Math.Max(1, index);
        var utc = date.ToUniversalTime();
        var output = p
            .Replace("{name}", name, StringComparison.Ordinal)
            .Replace("{datetime}", utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{date}", utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time}", utc.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{width}", Math.Max(1, width).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{height}", Math.Max(1, height).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{format}", Extension(format), StringComparison.Ordinal)
            .Replace("{ext}", Extension(format), StringComparison.Ordinal)
            .Replace("{index}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{counter}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        for (var digits = 2; digits <= 6; digits++)
        {
            var padded = i.ToString(new string('0', digits), CultureInfo.InvariantCulture);
            output = output.Replace($"{{index:0{digits}}}", padded, StringComparison.Ordinal)
                .Replace($"{{counter:0{digits}}}", padded, StringComparison.Ordinal);
        }
        var clean = SanitizeBaseName(output);
        return clean.Length == 0 ? name : clean;
    }

    public static string VisibleBaseName(string path)
    {
        var raw = Path.GetFileNameWithoutExtension(path).Trim().TrimStart('.');
        return raw.Length == 0 ? "Output" : raw;
    }

    public static string SanitizeBaseName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var clean = new string(chars).Trim().Trim('.', '-', ' ');
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>Top-left of a watermark of <paramref name="mark"/> size inside an image.</summary>
    public static Point WatermarkOrigin(Size image, Size mark, ImageWatermarkPosition position, double margin)
    {
        var m = Math.Max(0, margin);
        return position switch
        {
            ImageWatermarkPosition.TopLeft => new Point(m, m),
            ImageWatermarkPosition.TopRight => new Point(image.Width - mark.Width - m, m),
            ImageWatermarkPosition.Center => new Point((image.Width - mark.Width) / 2, (image.Height - mark.Height) / 2),
            ImageWatermarkPosition.BottomLeft => new Point(m, image.Height - mark.Height - m),
            _ => new Point(image.Width - mark.Width - m, image.Height - mark.Height - m),
        };
    }

    /// <summary>Logo width: <paramref name="scale"/> of the image width (upstream 0.18 default).</summary>
    public static Size LogoSize(Size image, Size logo, double scale)
    {
        if (logo.Width <= 0 || logo.Height <= 0) return new Size(0, 0);
        var w = image.Width * Math.Clamp(scale, 0.02, 1);
        return new Size(w, w * logo.Height / logo.Width);
    }

    /// <summary>Text height for a watermark: proportional to the short side and the scale.</summary>
    public static double WatermarkTextSize(Size image, double scale) =>
        Math.Max(8, Math.Min(image.Width, image.Height) * Math.Clamp(scale, 0.02, 1) * 0.35);

    /// <summary>"1.2 MB" style size with invariant decimals.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[u]}";
    }
}
