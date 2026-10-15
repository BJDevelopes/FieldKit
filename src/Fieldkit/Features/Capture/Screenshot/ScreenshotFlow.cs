// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>What happens to a screenshot after capture: default action, preview, save, copy, edit.</summary>
public static class ScreenshotFlow
{
    public const string FilePrefix = "Screenshot";

    public static readonly IReadOnlyList<(string Value, string Label)> DefaultActions =
    [
        ("", "Ask each time"),
        ("save", "Save"),
        ("saveAndCopy", "Save & Copy"),
        ("copy", "Copy"),
        ("edit", "Edit"),
    ];

    /// <summary>Entry point for every new screenshot (selector, full screen, scrolling, clipboard).</summary>
    public static void Receive(RasterImage image, bool fromCapture = true)
    {
        if (!FeatureCatalog.IsAvailable("screenshot")) return;
        if (fromCapture && Settings.GetBool(CaptureSettings.Downscale, false) && image.Scale > 1.01)
        {
            var (w, h) = CaptureGeometry.DownscaledSize(image.Width, image.Height, image.Scale);
            image = image.Resized(w, h);
        }
        RecentCaptures.AddScreenshot(image);
        var copied = Settings.GetBool(CaptureSettings.AutoCopy, false) && CaptureClipboard.CopyImage(image);

        if (Settings.GetBool(CaptureSettings.OpenEditorDirectly, false))
        {
            ScreenshotEditorWindow.Open(image);
            return;
        }
        var action = Settings.GetString(CaptureSettings.DefaultAction, string.Empty);
        switch (action)
        {
            case "edit":
                ScreenshotEditorWindow.Open(image);
                return;
            case "save" or "saveAndCopy" or "copy":
            {
                string? saved = null;
                if (action != "copy") saved = Save(image);
                if (action != "save" && !copied) copied = CaptureClipboard.CopyImage(image);
                ShowResultHud(saved, copied);
                if (Settings.GetBool(CaptureSettings.PreviewEnabled, true))
                    QuickPreviewWindow.Show(image, savedPath: saved, autoDismissSeconds: Settings.GetInt(CaptureSettings.PreviewDuration, 3));
                return;
            }
            default:
                if (copied) Hud.Show(Glyphs.Copy, "Screenshot copied");
                QuickPreviewWindow.Show(image, savedPath: null, autoDismissSeconds: null);
                return;
        }
    }

    public static void ShowResultHud(string? saved, bool copied)
    {
        if (saved is not null)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(saved)) ?? saved;
            Hud.Show(Glyphs.Check, copied ? $"Saved to {folder} and copied" : $"Saved to {folder}");
        }
        else if (copied)
        {
            Hud.Show(Glyphs.Copy, "Screenshot copied");
        }
    }

    /// <summary>The folder a new screenshot goes to (folder + dated subfolder).</summary>
    public static string TargetFolder(DateTime now)
    {
        var folder = CaptureSettings.ScreenshotFolderPath;
        var sub = CaptureFileNaming.ExpandSubfolder(Settings.GetString(CaptureSettings.Subfolder, string.Empty), now);
        return sub.Length == 0 ? folder : Path.Combine(folder, sub);
    }

    /// <summary>Saves a PNG with the configured folder and name pattern; returns the path or null.</summary>
    public static string? Save(RasterImage image)
    {
        try
        {
            var now = DateTime.Now;
            var folder = TargetFolder(now);
            Directory.CreateDirectory(folder);
            var pattern = Settings.GetString(CaptureSettings.FileNamePattern, string.Empty);
            var number = NextNumber();
            var name = CaptureFileNaming.FileName(FilePrefix, pattern, now, number, "png");
            name = CaptureFileNaming.UniqueFileName(name, n => File.Exists(Path.Combine(folder, n)));
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, image.EncodePng());
            if (CaptureFileNaming.PatternUsesNumber(pattern)) Settings.Set(CaptureSettings.FileNumberNext, number + 1);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the screenshot failed", ex);
            Hud.Show(Glyphs.Warning, "The screenshot could not be saved");
            return null;
        }
    }

    public static int NextNumber()
    {
        var start = Settings.GetInt(CaptureSettings.FileNumberStart, 1);
        return Math.Max(start, Settings.GetInt(CaptureSettings.FileNumberNext, start));
    }

    /// <summary>"Save As\u2026": PNG or JPEG wherever the person chooses.</summary>
    public static string? SaveAs(RasterImage image, System.Windows.Window? owner)
    {
        var now = DateTime.Now;
        var dialog = new SaveFileDialog
        {
            Title = "Save As\u2026",
            FileName = CaptureFileNaming.FileName(FilePrefix, Settings.GetString(CaptureSettings.FileNamePattern, string.Empty), now, NextNumber(), "png"),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            InitialDirectory = Directory.Exists(TargetFolder(now)) ? TargetFolder(now) : CaptureSettings.ScreenshotFolderPath,
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true) return null;
        try
        {
            BitmapEncoder encoder = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image.ToBitmapSource()));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            return dialog.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("Save As failed", ex);
            Hud.Show(Glyphs.Warning, "The screenshot could not be saved");
            return null;
        }
    }

    /// <summary>A temporary PNG for drag-out, Share and "Open with" (lives in the private cache).</summary>
    public static string TemporaryFile(RasterImage image)
    {
        var folder = CaptureSettings.PrivateFolder("Shared");
        // Older temporary files are removed once they are a day old.
        foreach (var old in Directory.EnumerateFiles(folder).Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)))
        {
            try { File.Delete(old); } catch { /* in use */ }
        }
        var path = Path.Combine(folder, CaptureFileNaming.DefaultFileName(FilePrefix, DateTime.Now));
        path = Path.Combine(folder, CaptureFileNaming.UniqueFileName(Path.GetFileName(path), n => File.Exists(Path.Combine(folder, n))));
        File.WriteAllBytes(path, image.EncodePng());
        return path;
    }

    /// <summary>Opens the image with the app the person picks (Windows "Open with" dialog).</summary>
    public static void OpenWith(RasterImage image)
    {
        var path = TemporaryFile(image);
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Open with failed", ex);
        }
    }

    public static void EditLatest()
    {
        var latest = RecentCaptures.LatestScreenshot;
        if (latest?.ImageFile is null)
        {
            Hud.Show(Glyphs.Info, "Take a screenshot first");
            return;
        }
        try { ScreenshotEditorWindow.Open(RasterImage.DecodeFile(latest.ImageFile)); }
        catch (Exception ex) { Log.Error("Opening the latest screenshot failed", ex); }
    }

    public static void EditClipboard()
    {
        var image = CaptureClipboard.ReadImage();
        if (image is null)
        {
            Hud.Show(Glyphs.Info, "Copy an image first");
            return;
        }
        ScreenshotEditorWindow.Open(image);
    }
}
