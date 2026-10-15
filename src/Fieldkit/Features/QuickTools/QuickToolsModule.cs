// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>Quick tools: Quick toggles, Quick panel, Cleaning Mode, Scratchpad, Kill Process and Port Manager.</summary>
public sealed class QuickToolsModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("quickToggles", Parity.Partial,
            "Dark mode, keep awake, mute, empty Recycle Bin (with size/count confirmation), eject all drives (with exclusions), hidden " +
            "files, desktop icons, lock, display off and screen saver, with show/hide and order in Settings. Missing: keyboard backlight " +
            "(no Windows API) and drag-to-reorder in the panel; tile titles are fixed (the shared tile shows on/off state instead).");
        r.Implements("quickLauncher", Parity.Partial,
            "3-column tile grid of every installed tool action, 1–9 and arrow keys, Esc layers, edit mode with hide, add back and drag " +
            "to reorder, Quick toggles hosted inline. Missing: the inline options cards (keep awake, mic, clipboard, colour picker).");
        r.Implements("cleaningMode", Parity.Full,
            "Swallows every key and the scroll wheel behind a blackout or corner indicator; Escape ×5 or Unlock ends it, fails open on " +
            "session lock, plus a maximum duration. Ctrl+Alt+Del stays available (Windows).");
        r.Implements("scratchpad", Parity.Full,
            "Up to 12 tabs, 0.8 s autosave with verified writes, retention, Markdown marks and preview, find, copy, clear, save as .txt/.md.");
        r.Implements("killProcess", Parity.Partial,
            "All processes with CPU/memory, grouping, sorting, filter, Kill (closes windows, then offers Force Kill), Force Kill, Kill All, " +
            "Kill Process Tree, Restart, identity checks. Missing: Command Bar listing and elevation for processes owned by other users.");
        r.Implements("portManager", Parity.Full,
            "Listening TCP (and optionally UDP) ports via GetExtendedTcpTable/UdpTable with owner, all-interfaces badge, filter, copy, " +
            "open in browser and Kill/Force Kill. Opens as a small window instead of inside the panel.");

        QuickTogglesCatalog.Attach(r);

        r.Service("quickLauncher", () => new HotkeyService("quickLauncher", QuickPanelItems.ShortcutKey, QuickPanelItems.DefaultShortcut, QuickPanelWindow.ToggleInstance, QuickPanelWindow.CloseInstance));
        r.Service("scratchpad", () => new HotkeyService("scratchpad", ScratchpadWindow.ShortcutKey, string.Empty, ScratchpadWindow.Toggle, ScratchpadWindow.CloseInstance));
        r.Service("cleaningMode", () => new CleaningModeService());
        r.Service("portManager", () => new HotkeyService("portManager", null, string.Empty, () => { }, PortManagerWindow.CloseInstance));

        r.Utility(new UtilityEntry("quickLauncher", "Quick panel", "A floating panel with your favorite tools, summoned by a shortcut from anywhere.",
            Glyphs.Grid, QuickPanelWindow.ToggleInstance, 0, QuickPanelItems.ShortcutKey));
        r.Utility(new UtilityEntry("scratchpad", "Scratchpad", "Quick notes in separate tabs", Glyphs.Note, ScratchpadWindow.Open, 60, ScratchpadWindow.ShortcutKey));
        r.Utility(new UtilityEntry("cleaningMode", "Cleaning Mode", "Locks the keyboard so you can clean safely.", Glyphs.Keyboard, CleaningMode.Start, 70));
        r.Utility(new UtilityEntry("killProcess", "Kill Process", "Search running processes and force quit, restart, or kill process trees.",
            ProcessesUi.KillGlyph, () => PanelHost.OpenSettings("processes"), 110));
        r.Utility(new UtilityEntry("portManager", "Port Manager", "Your listening ports", ProcessesUi.NetworkGlyph, PortManagerWindow.Open, 111));

        r.SettingsPage(new SettingsPageDef("quickTools", SettingsCategory.Utilities, "Quick tools", Glyphs.Toggles, 0,
            ["quickLauncher", "quickToggles"], QuickToolsPages.BuildQuickTools,
            ["quick panel", "launcher", "toggles", "dark mode", "recycle bin", "eject", "hidden files", "desktop icons", "lock", "screen saver"]));
        r.SettingsPage(new SettingsPageDef("scratchpad", SettingsCategory.Utilities, "Scratchpad", Glyphs.Note, 60, ["scratchpad"],
            QuickToolsPages.BuildScratchpad, ["notes", "markdown", "tabs"]));
        r.SettingsPage(new SettingsPageDef("cleaningMode", SettingsCategory.Utilities, "Cleaning Mode", Glyphs.Keyboard, 70, ["cleaningMode"],
            QuickToolsPages.BuildCleaningMode, ["clean", "keyboard lock", "keep screen visible"]));
        r.SettingsPage(new SettingsPageDef("processes", SettingsCategory.Utilities, "Processes and ports", ProcessesUi.KillGlyph, 110,
            ["killProcess", "portManager"], ProcessesUi.BuildPage,
            ["force quit", "process", "cpu", "memory", "kill", "port", "ports", "listening", "socket", "pid", "kill port"]));

        r.Action(new ToolAction("quickLauncher.open", "quickLauncher", "Quick panel", Glyphs.Grid, QuickPanelWindow.ToggleInstance,
            QuickPanelItems.ShortcutKey, QuickPanelItems.DefaultShortcut));
        r.Action(new ToolAction("cleaningMode.start", "cleaningMode", "Cleaning Mode", Glyphs.Keyboard, CleaningMode.Start));
        r.Action(new ToolAction("scratchpad.open", "scratchpad", "Scratchpad", Glyphs.Note, ScratchpadWindow.Toggle,
            ShortcutKey: ScratchpadWindow.ShortcutKey));
        r.Action(new ToolAction("killProcess.open", "killProcess", "Kill Process", ProcessesUi.KillGlyph, () => PanelHost.OpenSettings("processes")));
        r.Action(new ToolAction("portManager.open", "portManager", "Port Manager", ProcessesUi.NetworkGlyph, PortManagerWindow.Open));
        foreach (var t in QuickTogglesCatalog.All.Where(t => t.RequiresFeature is null))
            r.Action(new ToolAction("quickToggles." + t.Id, "quickToggles", t.Title, t.Glyph, t.Toggle));
    }
}

/// <summary>Binds one global shortcut while the feature is installed, and closes its window when it is removed.</summary>
/// <param name="defaultShortcut">Used until the user records one; empty = off until recorded.</param>
public sealed class HotkeyService(string featureId, string? shortcutKey, string defaultShortcut, Action onShortcut, Action onStop) : FeatureService
{
    private IDisposable? _binding;

    public override string FeatureId => featureId;

    protected override void OnStart()
    {
        if (shortcutKey is not null) _binding = Hotkeys.Bind(shortcutKey, defaultShortcut, onShortcut);
    }

    protected override void OnStop()
    {
        _binding?.Dispose();
        _binding = null;
        onStop();
    }
}
