// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Radial menu: a shortcut or mouse button opens a wheel of apps, files, links, shortcuts, media
/// keys, quick toggles, window layouts and every other installed tool around the pointer.
/// </summary>
public sealed class RadialMenuModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(RadialCatalog.FeatureId, Parity.Partial,
            "Wheels, profiles, submenus, hold/press/release gestures, keyboard browsing, per-profile shortcut and mouse button, " +
            "Now Playing (system media session), website icons and the visual editor match upstream. Missing: the four-finger " +
            "trackpad tap (Windows owns touchpad gestures); mouse triggers are limited to middle/back/forward (optionally with keys); " +
            "the wheel draws a flat translucent disc instead of the macOS glass material; tool slices run other features' registered actions.");
        r.Service(RadialCatalog.FeatureId, () => new RadialMenuService());
        r.SettingsPage(new SettingsPageDef("radialMenu", SettingsCategory.Utilities, RadialStrings.PageTitle, Glyphs.Radial, 90,
            [RadialCatalog.FeatureId], RadialMenuPage.Build, ["wheel", "pie", "profiles", "launcher", "mouse button", "now playing"]));
        r.Control(new ControlEntry(RadialCatalog.FeatureId, ControlGroup.MouseKeyboard, RadialStrings.PageTitle, RadialStrings.PanelCaption,
            Glyphs.Radial, RadialMenuStore.EnabledKey, false, Order: 80));
        r.Action(new ToolAction("radialMenu.open", RadialCatalog.FeatureId, "Open the radial menu", Glyphs.Radial, OpenPrimary,
            RadialMenuStore.ShortcutKey, RadialMenuSupport.DefaultShortcut));
    }

    /// <summary>From the Command Bar or quick panel: the first wheel, as a sticky session.</summary>
    private static void OpenPrimary()
    {
        var profiles = RadialMenuStore.Load();
        RadialMenuService.Preview(profiles[0]);
    }
}
