// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Windows and Taskbar: app switcher, window layout, maximize override, Taskbar Preview,
/// taskbar clicks, quit on close and the (native) fixed virtual desktop order.
/// </summary>
public sealed class WindowsModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("switcher", Parity.Partial,
            "Window previews (DWM live thumbnails), large icons and simple looks; Alt+Tab takeover via the keyboard hook, search, Q/W/M, " +
            "per-app rules, display and desktop filters. Missing: instant-selection animation and full-screen-Space options; " +
            "“All apps” without windows lists apps used since Fieldkit started; the hook cannot see keys while an elevated window is in front.");
        r.Implements("windowLayout", Parity.Partial,
            "Every placement with shortcuts, gaps, margin, side-size cycle, display moves and Restore; modifier-drag move/resize; custom edge areas " +
            "beside Windows Snap; ignored apps. Missing: the shortcut + pointer ring and move-pointer-to-next-display; Full Screen sends F11.");
        r.Implements("windowMaximizer", Parity.Partial,
            "Windows maximizes natively (no extra Space to avoid). On: maximizing fills the work area as a normal, movable window " +
            "(always or with Shift) and maximizing again puts it back; excluded apps keep the normal maximize.");
        r.Implements("dockPreview", Parity.Partial,
            "UI Automation finds the hovered taskbar button; larger DWM previews of every window across desktops with close/minimize. " +
            "Windows' own hover thumbnails still show; no pin, background opacity, creation order or keep-taskbar-visible options.");
        r.Implements("dockClick", Parity.Partial,
            "Click the active app's taskbar button to minimize all its windows (click again to restore) or cycle them. " +
            "No “hide the app” option: Windows has no app hiding.");
        r.Implements("autoQuit", Parity.Partial,
            "WinEvent show/hide/destroy tracking; apps are asked to quit by closing their hidden windows, with exceptions or an only-listed mode. " +
            "An app that ignores the request keeps running unless force quit is on.");
        r.Implements("spacesOrder", Parity.Native,
            "Windows never reorders virtual desktops by recent use (Settings › System › Multitasking).");

        r.Service("switcher", () => new SwitcherService());
        r.Service("windowLayout", () => new WindowLayoutService());
        r.Service("windowMaximizer", () => new WindowMaximizerService());
        r.Service("dockPreview", () => new TaskbarPreviewService());
        r.Service("dockClick", () => new TaskbarClickService());
        r.Service("autoQuit", () => new AutoQuitService());

        r.Control(new ControlEntry("switcher", ControlGroup.Windows, "App switcher",
            "Switch between apps and windows, including minimized windows and multiple windows from the same app.",
            Glyphs.Switcher, SwitcherService.EnabledKey, true, 10,
            [new ControlSubToggle("Show Alt+Tab with large icons", "Shows one icon per app with that app’s window previews above it.",
                SwitcherService.IconRowModeKey, false)]));
        r.Control(new ControlEntry("autoQuit", ControlGroup.Windows, "Quit on close",
            "Closing an app’s last window also quits it.", Glyphs.CloseBox, AutoQuitService.EnabledKey, false, 20));
        r.Control(new ControlEntry("windowMaximizer", ControlGroup.Windows, "Maximize windows",
            "The maximize button fills the screen as a normal window that stays movable and resizable.",
            Glyphs.Maximize, WindowMaximizerService.EnabledKey, false, 30));
        r.Control(new ControlEntry("dockPreview", ControlGroup.Windows, "Taskbar Preview",
            "Hover over an open app in the taskbar to preview and peek at its windows.",
            Glyphs.Taskbar, TaskbarPreviewService.EnabledKey, false, 40));
        r.Control(new ControlEntry("dockClick", ControlGroup.Windows, "Click the taskbar icon to minimize",
            "The active app’s windows minimize when you click its taskbar icon. Click again to bring them back.",
            GlyphTaskbarMinimize, TaskbarClickService.MinimizeKey, false, 50));
        r.Control(new ControlEntry("dockClick", ControlGroup.Windows, "Click the taskbar icon to cycle windows",
            "Click an active app’s taskbar icon to rotate through its windows, like Alt+`.",
            Glyphs.Taskbar, TaskbarClickService.CycleKey, false, 60));

        r.SettingsPage(new SettingsPageDef("switcher", SettingsCategory.WindowControls, "Switcher", Glyphs.Switcher, 10,
            ["switcher"], SwitcherPage.Build, ["alt+tab", "app switcher", "windows", "thumbnails", "search"]));
        r.SettingsPage(new SettingsPageDef("windowLayout", SettingsCategory.WindowControls, "Window layout", Glyphs.Layout, 20,
            ["windowLayout", "windowMaximizer"], WindowLayoutPage.Build,
            ["snap", "halves", "thirds", "quarters", "sixths", "tile", "drag", "resize", "maximize", "gaps", "display"]));
        r.SettingsPage(new SettingsPageDef("taskbar", SettingsCategory.WindowControls, "Taskbar", Glyphs.Taskbar, 30,
            ["dockPreview", "dockClick", "spacesOrder"], TaskbarPage.Build,
            ["dock", "preview", "thumbnails", "minimize", "cycle", "virtual desktops"]));
        r.SettingsPage(new SettingsPageDef("quitOnClose", SettingsCategory.WindowControls, "Quit on close", Glyphs.CloseBox, 35,
            ["autoQuit"], QuitOnClosePage.Build, ["auto quit", "close", "tray", "exit"]));

        r.Utility(new UtilityEntry("windowLayout", "Window layout", "Arrange the front window into screen sections",
            Glyphs.Layout, LayoutGridPopup.Open, 40));

        foreach (var action in LayoutActions.All)
        {
            var a = action;
            r.Action(new ToolAction($"windowLayout.{a.Id()}", "windowLayout", $"Layout: {a.Title()}", Glyphs.Layout,
                () => WindowLayoutEngine.Apply(a), a.ShortcutKey(), a.DefaultShortcut()));
        }
        r.Action(new ToolAction("windowLayout.grid", "windowLayout", "Window layout", Glyphs.Layout, LayoutGridPopup.Open));
    }

    // Segoe Fluent Icons "Download" (arrow into tray), read as minimize-to-taskbar.
    private const string GlyphTaskbarMinimize = "\uE896";
}
