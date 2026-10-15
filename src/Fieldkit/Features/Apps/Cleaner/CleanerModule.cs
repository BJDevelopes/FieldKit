// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner.Messaging;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>
/// The Cleaner: leftovers of uninstalled apps, orphaned startup items, caches, temp files, logs and
/// crash dumps, developer caches, forgotten screenshots, iPhone backups and the Recycle Bin, reviewed
/// item by item and moved to the Recycle Bin; an optional automatic cleanup of the safe groups; and
/// Messaging downloads (upstream WhatsApp downloads) with its own cleanup and organizer.
/// </summary>
public sealed class CleanerModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("cleaner", Parity.Partial,
            "Review list, Recycle Bin moves, schedule and Messaging downloads (WhatsApp/Telegram) work like upstream. Mapped to Windows " +
            "AppData/Temp/WER/browser caches; leftovers are matched by folder name against installed apps (no bundle ids on Windows), " +
            "so they also must sit untouched 30 days. Orphaned startup items cover HKCU Run and the Startup folder only. Messaging " +
            "downloads are attributed by Zone.Identifier and the Telegram Desktop folder; organizer undo cannot restore discarded duplicates from the Recycle Bin.");

        r.Service("cleaner", () => new CleanerShellService());
        r.Service("cleaner", () => new CleanerScheduleService());
        r.Service("cleaner", () => new MessagingService());

        r.Utility(new UtilityEntry("cleaner", S.Name, S.PanelCaption, Glyphs.Broom, OpenCleaner, 40, CleanerKeys.Shortcut));
        r.SettingsPage(new SettingsPageDef("cleaner", SettingsCategory.Utilities, S.Name, Glyphs.Broom, 40, ["cleaner"],
            CleanerSettingsPage.Build,
            ["junk", "cache", "caches", "temp", "logs", "leftovers", "recycle bin", "whatsapp", "telegram", "downloads", "schedule", "clean"]));

        r.Action(new ToolAction("cleaner.open", "cleaner", "Open Cleaner", Glyphs.Broom, OpenCleaner, CleanerKeys.Shortcut));
        r.Action(new ToolAction("cleaner.messaging", "cleaner", S.MsgTitle, CleanerGlyphs.Download, MessagingWindow.Open));
    }

    public static void OpenCleaner() => CleanerWindow.Open();
}
