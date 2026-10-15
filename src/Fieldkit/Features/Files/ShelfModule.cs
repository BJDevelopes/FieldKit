// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>The shelf: a floating spot to park files, pictures, links and text while dragging.</summary>
public sealed class ShelfModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("shelf", Parity.Partial,
            "Shortcut, shake while dragging, File Explorer selection, drops of files, pictures, links and text, " +
            "drag-out, pins, After use rules, app exceptions, Share and persistence work. Missing: the " +
            "notification-area drop zone, edge peek and Dynamic Island routing; multi-item drops become separate " +
            "tiles instead of a pile; virtual files (e.g. Outlook attachments) are not accepted; moved files are " +
            "not followed; drag-out always copies. Windows has no drag pasteboard, so any left-button drag counts " +
            "as a shake unless a window is being moved or resized.");
        r.Service("shelf", () => new ShelfService());
        r.SettingsPage(new SettingsPageDef("shelf", SettingsCategory.Files, "Shelf", Glyphs.Tray, 20, ["shelf"], ShelfPage.Build,
            ["drag", "drop", "shake", "park", "temporary", "files", "dropover", "yoink", "explorer"]));
        r.Control(new ControlEntry("shelf", ControlGroup.Files, "Shelf",
            "A floating spot to gather files, images and text, then drag them anywhere later.",
            Glyphs.Tray, "shelf.enabled", false, Order: 20));
        r.Action(new ToolAction("shelf.open", "shelf", "Open shelf", Glyphs.Tray, Toggle, "shelf.shortcut", ShelfService.ShortcutDefault));
    }

    private static void Toggle()
    {
        if (FeatureRuntime.Service<ShelfService>() is { IsRunning: true } service) service.Toggle();
        else Notifier.Show("Shelf", "Turn on the shelf in Settings → Shelf first.");
    }
}
