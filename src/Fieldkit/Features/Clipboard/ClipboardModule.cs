// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>Clipboard history (with auto clear), paste as plain text and Clean URL.</summary>
public sealed class ClipboardModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(ClipboardHistory.FeatureId, Parity.Partial,
            "Text, images and files with search, pins, preview/edit, Ctrl+1…9, multi-select, ignored apps, privacy formats and auto clear. " +
            "Missing: the notification-area \"latest copy\" preview, the \"Show in panel\" switch, and pasting a mixed text+image selection as rich text.");
        r.Implements(PastePlainService.Id, Parity.Full,
            "Shortcut pastes the plain text and restores the original clipboard (the temporary text is hidden from Windows clipboard history).");
        r.Implements(UrlCleanerService.Id, Parity.Full,
            "Upstream rules (global, per-site, user additions and switched-off built-ins); automatic on copy via the clipboard listener, or manually with Clean URL.");

        // The core Keyboard shortcuts page has no per-action defaults yet: seed them once.

        r.Service(ClipboardHistory.FeatureId, () => new ClipboardHistoryService());
        r.Service(ClipboardHistory.FeatureId, () => new ClipboardAutoClearService());
        r.Service(PastePlainService.Id, () => new PastePlainService());
        r.Service(UrlCleanerService.Id, () => new UrlCleanerService());

        r.SettingsPage(new SettingsPageDef("clipboard", SettingsCategory.Files, "Clipboard", Glyphs.Clipboard, 0,
            [ClipboardHistory.FeatureId, PastePlainService.Id], ClipboardPage.Build,
            ["history", "copy", "paste", "plain text", "auto clear", "pin", "password", "sensitive", "limit"]));
        r.SettingsPage(new SettingsPageDef("cleanUrl", SettingsCategory.Utilities, "Clean URL", Glyphs.Link, 10,
            [UrlCleanerService.Id], CleanUrlViews.BuildPage, ["tracking", "utm", "link", "parameters", "fbclid"]));

        r.Utility(new UtilityEntry(ClipboardHistory.FeatureId, "Clipboard",
            "Stores copied text so you can reuse it later. Everything stays local and can be cleared anytime.",
            Glyphs.Clipboard, ClipboardHistoryWindow.Open, Order: 50, ShortcutKey: ClipboardHistory.ShortcutKey));
        r.Utility(new UtilityEntry(UrlCleanerService.Id, "Clean URL", "Removes tracking parameters from copied links.",
            Glyphs.Link, CleanUrlViews.OpenTool, Order: 70));

        r.Action(new ToolAction("clipboardHistory.open", ClipboardHistory.FeatureId, "Clipboard history", Glyphs.Clipboard,
            ClipboardHistoryWindow.Toggle, ClipboardHistory.ShortcutKey, ClipboardHistory.DefaultShortcut));
        r.Action(new ToolAction("pastePlain.paste", PastePlainService.Id, "Paste as plain text", Glyphs.Document,
            () => FeatureRuntime.Service<PastePlainService>()?.Perform(), PastePlainService.ShortcutKey, PastePlainService.DefaultShortcut));
        r.Action(new ToolAction("urlCleaner.open", UrlCleanerService.Id, "Clean URL", Glyphs.Link, CleanUrlViews.OpenTool));
    }
}
