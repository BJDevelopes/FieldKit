// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Capture;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>A quick toggle this area provides, with its visibility rule.</summary>
public sealed record QuickToggleSpec(string Id, string Title, string Caption, string Glyph, Func<bool>? IsOn, Action Toggle, string? RequiresFeature = null);

/// <summary>
/// The Quick toggles this area contributes, in upstream order, and the user's choice of
/// which are shown and in what order. The defs in <see cref="ModuleRegistry.QuickToggles"/>
/// are rebuilt from these whenever the choice (or feature availability) changes, because
/// the shared tile grid draws whatever is registered.
/// </summary>
public static class QuickTogglesCatalog
{
    public const string OrderKey = "quickToggles.order";
    public const string HiddenKey = "quickToggles.hidden";
    public const string ExcludedDrivesKey = "quickToggles.excludedDrives";

    public const string DarkModeGlyph = "\uE7A1";
    public const string ScreenSaverGlyph = "\uE91B";

    public static IReadOnlyList<QuickToggleSpec> All { get; } =
    [
        new("darkMode", "Dark mode", "Changes the appearance of the whole system.", DarkModeGlyph,
            QuickToggleActions.IsDarkMode, QuickToggleActions.ToggleDarkMode),
        new("keepAwake", "Keep awake", "Keeps the PC awake until you turn it off.", Glyphs.Moon,
            () => KeepAwakeService.Current?.IsActive == true, EnergyModule.ToggleKeepAwake, KeepAwakeService.Id),
        new("mute", "Mute sound", "Mutes or unmutes the speakers.", Glyphs.Mute,
            QuickToggleActions.IsMuted, QuickToggleActions.ToggleMute),
        new("screenshot", "Screenshot", "Drag over what to capture, then annotate, copy or pin it.", Glyphs.Camera,
            null, () => CaptureEntry.Start(CaptureTool.Screenshot, false), "screenshot"),
        new("emptyRecycleBin", "Empty Recycle Bin", "Removes everything from the Recycle Bin.", Glyphs.Delete,
            null, QuickToggleActions.EmptyRecycleBin),
        new("ejectDrives", "Eject all drives", "Safely ejects every external drive.", Glyphs.Usb,
            null, QuickToggleActions.EjectAll),
        new("hiddenFiles", "Show hidden files", "Explorer windows refresh to apply it.", Glyphs.Eye,
            QuickToggleActions.HiddenFilesShown, QuickToggleActions.ToggleHiddenFiles),
        new("desktopIcons", "Hide desktop icons", "Hides or shows the icons on the desktop.", Glyphs.Display,
            QuickToggleActions.DesktopIconsHidden, QuickToggleActions.ToggleDesktopIcons),
        new("lockScreen", "Lock the screen", "Asks for the password to come back.", Glyphs.Lock,
            null, QuickToggleActions.LockScreen),
        new("displayOff", "Turn off the display", "The PC keeps running with the display off.", Glyphs.Power,
            null, QuickToggleActions.DisplayOff),
        new("screenSaver", "Start the screen saver", "Starts right away, on every display.", ScreenSaverGlyph,
            null, QuickToggleActions.StartScreenSaver),
    ];

    public static IReadOnlyList<string> DefaultOrder { get; } = All.Select(t => t.Id).ToList();

    public static List<string> Order() => QuickToolsLogic.SanitizedOrder(Settings.GetString(OrderKey, string.Empty), DefaultOrder);

    public static HashSet<string> Hidden() => QuickToolsLogic.ParseSet(Settings.GetString(HiddenKey, string.Empty));

    public static bool IsAvailable(QuickToggleSpec spec) => spec.RequiresFeature is null || FeatureCatalog.IsAvailable(spec.RequiresFeature);

    /// <summary>The toggles to show, in the user's order.</summary>
    public static List<QuickToggleSpec> Visible()
    {
        var hidden = Hidden();
        var byId = All.ToDictionary(t => t.Id);
        return Order().Where(id => !hidden.Contains(id)).Select(id => byId[id]).Where(IsAvailable).ToList();
    }

    public static void SetHidden(string id, bool hidden)
    {
        var set = Hidden();
        if (hidden) set.Add(id);
        else set.Remove(id);
        Settings.Set(HiddenKey, QuickToolsLogic.JoinedSet(set));
    }

    public static void Move(string id, int delta)
    {
        var order = Order();
        var i = order.IndexOf(id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        Settings.Set(OrderKey, QuickToolsLogic.Joined(order));
    }

    public static void Reset()
    {
        Settings.Remove(OrderKey);
        Settings.Remove(HiddenKey);
    }

    private static ModuleRegistry? _registry;

    /// <summary>Keeps the registry's quick toggles in step with the user's choice.</summary>
    public static void Attach(ModuleRegistry registry)
    {
        _registry = registry;
        Apply();
        Settings.Changed += key =>
        {
            if (key is OrderKey or HiddenKey) Ui.OnUi(Apply);
        };
        FeatureRuntime.AvailabilityChanged += Apply;
    }

    private static void Apply()
    {
        if (_registry is null) return;
        var ids = new HashSet<string>(DefaultOrder, StringComparer.Ordinal);
        _registry.QuickToggles.RemoveAll(t => ids.Contains(t.Id));
        var order = 10;
        foreach (var spec in Visible())
        {
            _registry.QuickToggles.Add(new QuickToggleDef(spec.Id, spec.Title, spec.Glyph, spec.IsOn, spec.Toggle, order));
            order += 10;
        }
    }
}
