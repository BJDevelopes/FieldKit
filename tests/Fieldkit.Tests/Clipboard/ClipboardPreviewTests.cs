// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Features.Snippets;

namespace Fieldkit.Tests.ClipboardTools;

/// <summary>Renders the floating windows with sample data (STA) for visual review, and checks the image codec.</summary>
public class ClipboardPreviewTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "fieldkit-previews");

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static List<ClipboardEntry> Sample()
    {
        var now = DateTime.Now;
        return
        [
            new() { Text = "ssh deploy@build-01.example.internal", PinnedAt = now, CopiedAt = now.AddHours(-3) },
            new() { Text = "Meeting moved to Thursday 10:00 — room 4B.\nBring the printed agenda and the budget sheet.", CopiedAt = now.AddMinutes(-2) },
            new() { Text = "https://example.com/articles/windows-tray-utilities", CopiedAt = now.AddMinutes(-5) },
            new() { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\Users\Public\Documents\Quarterly report.xlsx"], CopiedAt = now.AddMinutes(-9) },
            new() { Kind = ClipboardEntryKind.Image, ImageFile = "missing.png", ImageWidth = 1280, ImageHeight = 720, CopiedAt = now.AddMinutes(-15) },
            new() { Text = "{\"name\":\"Fieldkit\",\"features\":[\"clipboard\",\"snippets\"],\"version\":1}", CopiedAt = now.AddMinutes(-20) },
        ];
    }

    [Fact]
    public void RendersTheHistoryWindow() => Sta(() =>
    {
        ClipboardHistoryWindow.RenderPreview(Path.Combine(Dir, "clipboard-history.png"), Sample(), preview: false);
        ClipboardHistoryWindow.RenderPreview(Path.Combine(Dir, "clipboard-history-preview.png"), Sample(), preview: true);
        ClipboardHistoryWindow.RenderPreview(Path.Combine(Dir, "clipboard-history-search.png"), Sample(), preview: false, query: "meeting");
        ClipboardHistoryWindow.RenderPreview(Path.Combine(Dir, "clipboard-history-cards.png"), Sample(), preview: false, cards: true);
        ClipboardHistoryWindow.RenderPreview(Path.Combine(Dir, "clipboard-history-empty.png"), [], preview: false);
        Assert.True(File.Exists(Path.Combine(Dir, "clipboard-history.png")));
    });

    [Fact]
    public void RendersTheSnippetMenu() => Sta(() =>
    {
        List<TextSnippet> sample =
        [
            new() { Name = "Address", Trigger = ";addr", Replacement = "Elm Street 1", Folder = "Home" },
            new() { Name = "Work mail", Trigger = ";wmail", Replacement = "work@fieldkit-test.example", Folder = "Work" },
            new() { Name = "Signature", Trigger = ";sig", Replacement = "Best regards,\nV.", Folder = "Work" },
            new() { Name = "Loose note", Trigger = ";loose", Replacement = "loose text here" },
        ];
        SnippetLibraryWindow.RenderPreview(Path.Combine(Dir, "snippet-library.png"), sample);
        SnippetLibraryWindow.RenderPreview(Path.Combine(Dir, "snippet-library-light.png"), sample, light: true);
        SnippetLibraryWindow.RenderPreview(Path.Combine(Dir, "snippet-library-noresults.png"), sample, query: "zzz");
        SnippetLibraryWindow.RenderPreview(Path.Combine(Dir, "snippet-library-empty.png"), []);
        Assert.True(File.Exists(Path.Combine(Dir, "snippet-library.png")));
    });

    [Fact]
    public void DibAndPngConvertBothWays() => Sta(() =>
    {
        var pixels = new byte[4 * 3 * 2];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 10;
            pixels[i + 1] = 20;
            pixels[i + 2] = 30;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 12);
        var png = ClipboardImageCodec.EncodePng(source);
        var dib = ClipboardImageCodec.PngToDib(png)!;
        Assert.Equal(40 + 24, dib.Length);
        Assert.Equal(3, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        var back = ClipboardImageCodec.DibToPng(dib)!;
        Assert.Equal(3, back.Width);
        Assert.Equal(2, back.Height);
        var decoded = ClipboardImageCodec.Decode(back.Png)!;
        var copy = new byte[4];
        new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), copy, 4, 0);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, copy);
    });
}
