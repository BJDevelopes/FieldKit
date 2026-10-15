// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Wallpaper;

/// <summary>Wallpaper: pick a still from Windows' own pictures or yours, per display, and rotate.</summary>
public sealed class WallpaperModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        const string id = WallpaperKeys.FeatureId;
        r.Implements(id, Parity.Partial,
            "Gallery of Windows stills and your pictures / folders with filter, paging and remove, like upstream. " +
            "\"Show on all Spaces\" becomes \"Show on all displays\": IDesktopWallpaper is per monitor, not per " +
            "virtual desktop. No OneDrive download progress (cloud-only files keep a placeholder thumbnail). " +
            "Adds picture position, automatic changes, next / previous / shuffle.");

        r.Service(id, () => new WallpaperService());
        r.Service(id, () => new WallpaperRotationService());

        r.PanelSection(new PanelSectionDef(id, WallpaperText.PageTitle, Glyphs.Wallpaper, 110, [id],
            WallpaperSection.Build, WallpaperSection.HeaderAccessory));
        r.SettingsPage(new SettingsPageDef(id, SettingsCategory.Utilities, WallpaperText.PageTitle, Glyphs.Wallpaper, 100, [id],
            WallpaperPage.Build, ["background", "desktop", "picture", "slideshow", "rotate", "shuffle", "display", "monitor"]));

        r.Action(new ToolAction("wallpaper.next", id, WallpaperText.NextWallpaper, Glyphs.Next,
            () => _ = WallpaperController.NextAsync(), WallpaperKeys.NextShortcut));
        r.Action(new ToolAction("wallpaper.previous", id, WallpaperText.PreviousWallpaper, Glyphs.Previous,
            () => _ = WallpaperController.PreviousAsync(), WallpaperKeys.PreviousShortcut));
        r.Action(new ToolAction("wallpaper.shuffle", id, WallpaperText.ShuffleWallpaper, WallpaperSection.ShuffleGlyph,
            () => _ = WallpaperController.ShuffleAsync(), WallpaperKeys.ShuffleShortcut));
        r.Action(new ToolAction("wallpaper.toggleRotation", id, "Change wallpaper automatically", Glyphs.Refresh,
            () => Settings.Set(WallpaperKeys.Rotate, !Settings.GetBool(WallpaperKeys.Rotate, false))));
        r.Action(new ToolAction("wallpaper.addFolder", id, "Add wallpaper folder", Glyphs.Folder, WallpaperController.AddFolder));
        r.Action(new ToolAction("wallpaper.openSettings", id, WallpaperText.OpenSystemSettings, Glyphs.Settings,
            WallpaperController.OpenSystemSettings));
    }
}
