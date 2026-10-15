// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Media;

/// <summary>Media: compress and trim videos, make GIFs, batch-process images and extract text, all locally.</summary>
public sealed class MediaModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("mediaTools", Parity.Partial,
            "Video compression (Low/Medium/High, resolution or target file size, FPS, H.264/HEVC, audio, metadata) and trimming, GIFs (size, FPS, " +
            "quality, loop, target size) through a local ffmpeg offered via winget; batch image convert/resize/compress/watermark/rename with WIC " +
            "(JPEG, PNG, TIFF, BMP, GIF, HEIC when the HEIF extension is installed); text extraction with Windows OCR. Missing: PDF output, saved " +
            "image profiles (the Web/Social/Docs presets are there) and upstream's Accurate/Fast OCR and language-correction switches.");
        r.Service("mediaTools", () => new MediaToolsService());
        r.Utility(new UtilityEntry("mediaTools", "Media", "Compress videos and images, make GIFs and extract text locally.",
            Glyphs.Photo, MediaWindow.Open, 40));
        r.SettingsPage(new SettingsPageDef("media", SettingsCategory.Files, "Media", Glyphs.Photo, 50, ["mediaTools"], BuildPage,
            ["video", "compress", "gif", "image", "convert", "resize", "watermark", "ocr", "ffmpeg", "heic", "jpeg", "png"]));
        r.Action(new ToolAction("mediaTools.open", "mediaTools", "Media", Glyphs.Photo, MediaWindow.Open));
    }

    private static FrameworkElement BuildPage() =>
        Ui.Page("Media", "Compress videos, convert and process images, make GIFs and extract text locally.",
            Ui.Row(8, Ui.Button("Open in a window", MediaWindow.Open, glyph: Glyphs.Photo)),
            new MediaWorkspace());
}
