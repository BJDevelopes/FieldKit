// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>
/// Installer helper (upstream <c>diskImageInstaller</c>): install from a disk image or ZIP in Downloads,
/// then eject and move the download to the Recycle Bin. Nothing runs until the tool is opened.
/// </summary>
public sealed class InstallerModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("diskImageInstaller", Parity.Partial,
            "You choose the image or archive yourself (Windows gives no event when an image is opened). " +
            "Upstream copies an .app; here the image's own installer runs and is waited for, so it is only as " +
            "unattended as that installer's UI allows. ZIPs without an installer are copied as portable apps. " +
            "VHD/VHDX mounting needs administrator rights.");
        r.SettingsPage(new SettingsPageDef("installer", SettingsCategory.Files, InstallerText.Title, Glyphs.Disc, 40,
            ["diskImageInstaller"], InstallerPage.Build,
            ["iso", "img", "vhd", "vhdx", "zip", "disk image", "archive", "mount", "eject", "setup", "msi", "install", "downloads", "portable", "recycle bin"]));
        r.Utility(new UtilityEntry("diskImageInstaller", InstallerText.Title,
            "Install from a disk image or archive in Downloads and clean up afterwards.", Glyphs.Disc, InstallerWindow.Open, Order: 60));
        r.Action(new ToolAction("diskImageInstaller.open", "diskImageInstaller", InstallerText.Title, Glyphs.Disc, InstallerWindow.Open));
    }
}
