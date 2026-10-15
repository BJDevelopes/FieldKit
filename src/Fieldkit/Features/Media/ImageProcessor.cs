// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;

namespace Fieldkit.Features.Media;

/// <summary>Outcome of one converted image.</summary>
public sealed record ImageResult(string Input, string? Output, long InputBytes, long OutputBytes, string? Error);

/// <summary>
/// Converts, resizes, watermarks and compresses images locally with WIC (the codecs Windows has:
/// JPEG, PNG, TIFF, BMP, GIF, plus HEIC/WebP decoding and HEIC encoding when their extensions are
/// installed). Must run on an STA thread (WPF imaging); <see cref="RunBatchAsync"/> provides one.
/// </summary>
public static class ImageProcessor
{
    public static readonly string[] InputExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp", ".jxr", ".ico", ".avif"];

    public static bool IsImage(string path) => InputExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Whether Windows can encode HEIC here (HEIF Image Extensions installed).</summary>
    public static bool CanEncodeHeic()
    {
        try
        {
            return Windows.Graphics.Imaging.BitmapEncoder.GetEncoderInformationEnumerator()
                .Any(e => e.CodecId == Windows.Graphics.Imaging.BitmapEncoder.HeifEncoderId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Processes files on a private STA thread, reporting progress (0…1).</summary>
    public static Task<List<ImageResult>> RunBatchAsync(IReadOnlyList<string> files, ImageOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<ImageResult>>();
        var thread = new Thread(() =>
        {
            var results = new List<ImageResult>();
            try
            {
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < files.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    results.Add(Process(files[i], options, i + 1, reserved));
                    progress?.Report((i + 1.0) / files.Count);
                }
                tcs.SetResult(results);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Fieldkit images" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static ImageResult Process(string input, ImageOptions o, int index, ISet<string>? reserved = null)
    {
        long inBytes = 0;
        try
        {
            inBytes = new FileInfo(input).Length;
            var source = Load(input, out var metadata);
            var plan = MediaImagePlan.Plan(source.PixelWidth, source.PixelHeight, o);
            if (!MediaImagePlan.IsSafeSize(plan.Width, plan.Height))
                return new ImageResult(input, null, inBytes, 0, "These dimensions are too large to process safely. Choose a smaller size.");
            var rendered = Render(source, plan, o);
            var folder = Path.GetDirectoryName(Path.GetFullPath(input))!;
            if (o.SaveInSubfolder) folder = Path.Combine(folder, "Converted");
            Directory.CreateDirectory(folder);
            var baseName = MediaImagePlan.OutputBaseName(input, o.RenamePattern, index, DateTime.Now, plan.Width, plan.Height, o.Format);
            var ext = MediaImagePlan.Extension(o.Format);
            var output = Path.Combine(folder, $"{baseName}.{ext}");
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                output = Path.Combine(folder, $"{baseName} converted.{ext}");
            output = Unique(output, reserved);
            reserved?.Add(output);
            Encode(rendered, output, o, o.StripMetadata ? null : metadata);
            if (o.PreserveModificationDate) File.SetLastWriteTime(output, File.GetLastWriteTime(input));
            return new ImageResult(input, output, inBytes, new FileInfo(output).Length, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Converting {input} failed: {ex.Message}");
            return new ImageResult(input, null, inBytes, 0, ex.Message);
        }
    }

    private static string Unique(string path, ISet<string>? reserved)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Capture.CaptureFileNaming.UniqueFileName(Path.GetFileName(path),
            n => File.Exists(Path.Combine(dir, n)) || reserved?.Contains(Path.Combine(dir, n)) == true);
        return Path.Combine(dir, name);
    }

    /// <summary>Decodes the first frame, upright (EXIF orientation applied).</summary>
    public static BitmapSource Load(string path, out BitmapMetadata? metadata)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        metadata = frame.Metadata as BitmapMetadata;
        var orientation = 1;
        try
        {
            if (metadata?.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
        }
        catch
        {
            // No orientation for this format.
        }
        BitmapSource result = frame;
        var transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => (Transform)new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (transform is not null) result = new TransformedBitmap(frame, transform);
        result.Freeze();
        return result;
    }

    private static BitmapSource Render(BitmapSource source, ImageResizePlan plan, ImageOptions o)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var transparentOk = MediaImagePlan.SupportsTransparency(o.Format) && o.Background == ImageBackground.Transparent;
            if (!transparentOk)
            {
                var fill = o.Background == ImageBackground.Black ? Brushes.Black : Brushes.White;
                dc.DrawRectangle(fill, null, new Rect(0, 0, plan.Width, plan.Height));
            }
            var crop = plan.SourceCrop;
            BitmapSource image = source;
            if (crop.Width < source.PixelWidth - 0.5 || crop.Height < source.PixelHeight - 0.5)
            {
                image = new CroppedBitmap(source, new Int32Rect((int)Math.Round(crop.X), (int)Math.Round(crop.Y),
                    Math.Max(1, (int)Math.Round(crop.Width)), Math.Max(1, (int)Math.Round(crop.Height))));
            }
            // Pixels, not DIPs: images with odd DPI metadata must not change size.
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            dc.DrawImage(image, plan.Destination);
            DrawWatermark(dc, new Size(plan.Width, plan.Height), o);
        }
        var rtb = new RenderTargetBitmap(plan.Width, plan.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawWatermark(DrawingContext dc, Size image, ImageOptions o)
    {
        if (!o.UsesWatermarkText && !o.UsesWatermarkLogo) return;
        var opacity = Math.Clamp(o.WatermarkOpacity, 0.05, 1);
        BitmapSource? logo = null;
        if (o.UsesWatermarkLogo && File.Exists(o.WatermarkLogoPath))
        {
            try { logo = Load(o.WatermarkLogoPath, out _); }
            catch (Exception ex) { Log.Warn($"Watermark logo failed: {ex.Message}"); }
        }
        FormattedText? text = null;
        if (o.UsesWatermarkText)
        {
            text = new FormattedText(o.WatermarkText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                MediaImagePlan.WatermarkTextSize(image, o.WatermarkScale), Brushes.White, 1.0);
        }
        var logoSize = logo is null ? new Size(0, 0) : MediaImagePlan.LogoSize(image, new Size(logo.PixelWidth, logo.PixelHeight), o.WatermarkScale);
        var gap = logo is not null && text is not null ? (text.Height * 0.4) : 0;
        var markWidth = logoSize.Width + gap + (text?.WidthIncludingTrailingWhitespace ?? 0);
        var markHeight = Math.Max(logoSize.Height, text?.Height ?? 0);
        var origin = MediaImagePlan.WatermarkOrigin(image, new Size(markWidth, markHeight), o.WatermarkPosition, o.WatermarkMargin);
        dc.PushOpacity(opacity);
        if (logo is not null) dc.DrawImage(logo, new Rect(origin.X, origin.Y + (markHeight - logoSize.Height) / 2, logoSize.Width, logoSize.Height));
        if (text is not null)
        {
            var p = new Point(origin.X + logoSize.Width + gap, origin.Y + (markHeight - text.Height) / 2);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), Math.Max(1, text.Height / 22)), text.BuildGeometry(p));
            dc.DrawText(text, p);
        }
        dc.Pop();
    }

    private static void Encode(BitmapSource image, string output, ImageOptions o, BitmapMetadata? metadata)
    {
        if (o.Format == ImageOutputFormat.Heic)
        {
            EncodeHeic(image, output, o.Quality).GetAwaiter().GetResult();
            return;
        }
        BitmapEncoder encoder = o.Format switch
        {
            ImageOutputFormat.Png => new PngBitmapEncoder(),
            ImageOutputFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageOutputFormat.Bmp => new BmpBitmapEncoder(),
            ImageOutputFormat.Gif => new GifBitmapEncoder(),
            _ => new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(o.Quality * 100), 1, 100) },
        };
        BitmapSource frame = o.Format is ImageOutputFormat.Jpeg or ImageOutputFormat.Bmp
            ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)
            : image;
        BitmapFrame bitmapFrame;
        try
        {
            bitmapFrame = metadata is not null && o.Format == ImageOutputFormat.Jpeg
                ? BitmapFrame.Create(frame, null, (BitmapMetadata)metadata.Clone(), null)
                : BitmapFrame.Create(frame);
        }
        catch
        {
            bitmapFrame = BitmapFrame.Create(frame);
        }
        encoder.Frames.Add(bitmapFrame);
        var temp = output + ".part";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, output, overwrite: true);
    }

    private static async Task EncodeHeic(BitmapSource image, string output, double quality)
    {
        var w = image.PixelWidth;
        var h = image.PixelHeight;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var props = new Windows.Graphics.Imaging.BitmapPropertySet
        {
            { "ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue((float)Math.Clamp(quality, 0.05, 1), Windows.Foundation.PropertyType.Single) },
        };
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.HeifEncoderId, memory, props);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)w, (uint)h, 96, 96, pixels);
        await encoder.FlushAsync();
        memory.Seek(0);
        var bytes = new byte[memory.Size];
        await memory.ReadAsync(bytes.AsBuffer(), (uint)memory.Size, Windows.Storage.Streams.InputStreamOptions.None);
        await File.WriteAllBytesAsync(output, bytes);
    }
}
