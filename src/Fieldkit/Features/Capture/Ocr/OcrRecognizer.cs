// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;
using ZXing.Common;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Offline text and QR recognition: Windows.Media.Ocr with the user's languages, and ZXing.Net
/// for QR codes. Images never leave the PC.
/// </summary>
public static class OcrRecognizer
{
    /// <summary>The engine for the user's languages, or any installed OCR language as a fallback.</summary>
    public static OcrEngine? CreateEngine(string? languageTag = null)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var lang = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(lang)) return OcrEngine.TryCreateFromLanguage(lang);
            }
            catch (ArgumentException)
            {
                // Not a language tag; fall through to the profile languages.
            }
        }
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null) return engine;
        var any = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        return any is null ? null : OcrEngine.TryCreateFromLanguage(any);
    }

    public static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName).ToList();

    /// <summary>Recognises the text of an image. Call from the UI thread (resizing uses WPF imaging).</summary>
    public static async Task<string> RecognizeAsync(RasterImage image, bool removeLineBreaks, string? languageTag = null)
    {
        var engine = CreateEngine(languageTag) ?? throw new InvalidOperationException(
            "No text recognition language is installed. Add one in Settings > Time & language > Language & region.");
        var scale = OcrText.RecognitionScale(image.Width, image.Height, (int)OcrEngine.MaxImageDimension);
        var input = Math.Abs(scale - 1) < 0.001
            ? image
            : image.Resized(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(input.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return OcrText.Join(result.Lines.Select(l => OcrText.TidyCjkSpacing(l.Text)), removeLineBreaks);
    }

    /// <summary>Decodes a QR code in the image, or null.</summary>
    public static string? DecodeQr(RasterImage image)
    {
        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    TryInverted = true,
                    PossibleFormats = [BarcodeFormat.QR_CODE],
                },
            };
            var source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            return reader.Decode(source)?.Text;
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"QR decoding failed: {ex.Message}");
            return null;
        }
    }
}
