// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Input.Acceleration;
using Fieldkit.Features.Input.Buttons;
using Fieldkit.Features.Input.Debounce;
using Fieldkit.Features.Input.Focus;
using Fieldkit.Features.Input.Pages;
using Fieldkit.Features.Input.QuitProtection;
using Fieldkit.Features.Input.Scrolling;
using Fieldkit.Features.Input.SuperKey;
using Fieldkit.UI;

namespace Fieldkit.Features.Input;

/// <summary>Mouse and keyboard: wheel, pointer, buttons, debounce, super key and quit protection.</summary>
public sealed class InputModule : IFeatureModule
{
    private static readonly string[] MouseFeatures =
    [
        "scrollInverter", "scrollHorizontal", "focusFollowsMouse", "smoothScroll", "linearScroll", "mouseAcceleration",
        "mouseNavigation", "mouseButtonShortcuts", "mouseClickDebounce", "middleClick",
    ];

    public void Register(ModuleRegistry r)
    {
        r.Implements("scrollInverter", Parity.Full,
            "Vertical and horizontal wheel inversion per axis, with its own app exceptions. Precision touchpads keep their own setting.");
        r.Implements("scrollHorizontal", Parity.Partial,
            "Shift, Alt, Ctrl or Win turns the vertical wheel sideways; Windows cannot strip the held key from the wheel event, so the app still sees it.");
        r.Implements("focusFollowsMouse", Parity.Partial,
            "Hover delay, raise and wait-for-stop options and app exceptions; without raising, the previous Z-order is restored right after activation, so some apps flash forward briefly.");
        r.Implements("smoothScroll", Parity.Partial,
            "Eased high-resolution wheel deltas with speed, response and coast; older apps that treat every wheel message as a full notch need to be added to Apps to leave alone.");
        r.Implements("linearScroll", Parity.Full,
            "Accelerated or batched wheel events count as one notch, scaled to the chosen lines per step.");
        r.Implements("mouseAcceleration", Parity.Full,
            "Turns off Enhance pointer precision for the session and restores the saved value when switched off, uninstalled, on exit or after a crash.");
        r.Implements("mouseNavigation", Parity.Native,
            "Windows maps the side buttons to Back and Forward natively; they can be remapped under Mouse button shortcuts.");
        r.Implements("mouseButtonShortcuts", Parity.Partial,
            "Middle, Back and Forward buttons and tilt-wheel directions take shortcuts, and a held button drags between virtual desktops. Buttons beyond the fifth are invisible to Windows' mouse hook, and dragging down shows the desktop (Windows has no App Exposé).");
        r.Implements("middleClick", Parity.Native,
            "Precision touchpads offer a three-finger tap as middle click in Touchpad settings.");
        r.Implements("mouseClickDebounce", Parity.Full, "Left, right and middle bounce filter with exact press/release ownership.");
        r.Implements("keyboardDebounce", Parity.Full, "Global window plus per-key overrides; auto-repeat is never filtered.");
        r.Implements("superKey", Parity.Full,
            "Caps Lock or a right-side key holds the chosen modifiers, with a tap action and app exceptions. Windows reserves a few Ctrl+Alt+Shift+Win letters for Office.");
        r.Implements("quitWindowProtection", Parity.Full,
            "Alt+F4 / Ctrl+Q and Ctrl+W / Ctrl+F4 need a hold, a double press or an extra modifier, per app, with the confirmation pill.");

        r.Service("scrollInverter", () => new ScrollInverterService());
        r.Service("scrollHorizontal", () => new ScrollHorizontalService());
        r.Service("linearScroll", () => new LinearScrollService());
        r.Service("smoothScroll", () => new SmoothScrollService());
        r.Service("focusFollowsMouse", () => new FocusFollowsMouseService());
        r.Service("mouseAcceleration", () => new MouseAccelerationService());
        r.Service("mouseButtonShortcuts", () => new MouseButtonService());
        r.Service("mouseClickDebounce", () => new MouseClickDebounceService());
        r.Service("keyboardDebounce", () => new KeyboardDebounceService());
        r.Service("superKey", () => new SuperKeyService());
        r.Service("quitWindowProtection", () => new QuitProtectionService());

        // Controls tab, MOUSE AND KEYBOARD group, in upstream's order.
        var g = ControlGroup.MouseKeyboard;
        r.Control(new ControlEntry("scrollInverter", g, "Invert mouse scrolling", "Reverses the mouse wheel direction.",
            Glyphs.Mouse, "scrollInverter.vertical", false, 10,
            [new ControlSubToggle("Horizontal too", "Also reverses the tilt wheel and sideways scrolling.", "scrollInverter.horizontal", false)]));
        r.Control(new ControlEntry("linearScroll", g, "Linear scrolling",
            "Every notch of the mouse wheel scrolls the same distance, no matter how fast it spins.",
            Glyphs.ArrowsUpDown, "linearScroll.enabled", false, 20));
        r.Control(new ControlEntry("focusFollowsMouse", g, "Focus follows mouse", "Focuses the window under the pointer.",
            Glyphs.Pointer, "focusFollowsMouse.enabled", false, 30));
        r.Control(new ControlEntry("mouseAcceleration", g, "Disable mouse acceleration",
            "Removes pointer acceleration. Your previous setting returns when this is turned off or Fieldkit quits.",
            Glyphs.Pointer, "mouseAcceleration.enabled", false, 40));
        r.Control(new ControlEntry("keyboardDebounce", g, "Debounce", "Filters very fast duplicate key presses.",
            Glyphs.Keyboard, "keyboardDebounce.enabled", false, 60));
        r.Control(new ControlEntry("mouseButtonShortcuts", g, "Mouse button shortcuts",
            "Extra buttons and side-wheel directions press key combinations you choose.",
            Glyphs.Mouse, "mouseButtonShortcuts.enabled", false, 100));
        r.Control(new ControlEntry("superKey", g, "Super key", "Turns one key into the modifier combination you choose.",
            Glyphs.Keyboard, "superKey.enabled", false, 110));
        r.Control(new ControlEntry("mouseClickDebounce", g, "Extra click filter",
            "Ignores rapid extra clicks from worn mouse buttons without slowing normal clicks.",
            MousePage.ClickGlyph, "mouseClickDebounce.enabled", false, 120));

        r.SettingsPage(new SettingsPageDef("mouse", SettingsCategory.WindowControls, "Mouse & touchpad", Glyphs.Mouse, 0, MouseFeatures,
            MousePage.Build,
            ["scroll", "wheel", "invert", "natural", "smooth", "linear", "acceleration", "pointer precision", "focus", "hover",
             "side buttons", "back", "forward", "middle click", "touchpad", "debounce", "click", "virtual desktop", "task view"]));
        r.SettingsPage(new SettingsPageDef("keyboard", SettingsCategory.WindowControls, "Keyboard", Glyphs.Keyboard, 5,
            ["superKey", "keyboardDebounce"], KeyboardPage.Build,
            ["super key", "hyper", "caps lock", "modifier", "debounce", "chatter", "duplicate keys"]));
        r.SettingsPage(new SettingsPageDef("quitProtection", SettingsCategory.WindowControls, "Quit protection", Glyphs.Shield, 40,
            ["quitWindowProtection"], QuitProtectionPage.Build,
            ["alt+f4", "ctrl+q", "ctrl+w", "close", "quit", "accidental", "hold to confirm"]));

        // Command Bar / quick panel entries (upstream has no global shortcuts for these features).
        r.Action(new ToolAction("smoothScroll.toggle", "smoothScroll", "Toggle smooth scrolling", Glyphs.Mouse, () => Flip("smoothScroll.enabled")));
        r.Action(new ToolAction("focusFollowsMouse.toggle", "focusFollowsMouse", "Toggle focus follows mouse", Glyphs.Pointer, () => Flip("focusFollowsMouse.enabled")));
        r.Action(new ToolAction("superKey.toggle", "superKey", "Toggle super key", Glyphs.Keyboard, () => Flip("superKey.enabled")));
    }

    private static void Flip(string key) => Settings.Set(key, !Settings.GetBool(key));
}
