// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>A tile of the Quick panel.</summary>
public sealed record QuickPanelItem(string Id, string FeatureId, string Title, string Glyph, ToolAction? Action, Func<bool>? IsActive = null)
{
    public bool IsTogglesTile => Id == QuickPanelItems.TogglesId;
}

/// <summary>Which tools the Quick panel offers, which are shown, and in what order.</summary>
public static class QuickPanelItems
{
    public const string TogglesId = "toggles";
    public const string OrderKey = "quickLauncher.order";
    public const string HiddenKey = "quickLauncher.hidden";
    public const string AddedKey = "quickLauncher.added";
    public const string ShortcutKey = "quickLauncher.shortcut";
    /// <summary>Upstream ⌃⌘V maps to Ctrl+Win+V, which Windows 11 uses for sound output; Ctrl+Alt+letter is AltGr typing on many layouts, so Ctrl+Alt+Win+P.</summary>
    public const string DefaultShortcut = "Ctrl+Alt+Win+P";

    /// <summary>Upstream tile order, by feature.</summary>
    private static readonly string[] FeaturePriority =
    [
        "keepAwake", "cleaner", "quickToggles", "micMute", "screenOCR", "colorPicker", "clipboardHistory", "windowLayout",
        "cleaningMode", "homebrew", "mediaTools", "urlCleaner", "uninstaller", "screenshot", "screenRecorder", "cameraPreview", "scratchpad",
    ];

    /// <summary>Every tile that could be shown (installed features only), in default order, with "primary" flags.</summary>
    public static List<(QuickPanelItem Item, bool Primary)> Candidates()
    {
        var list = new List<(QuickPanelItem, bool)>();
        if (FeatureCatalog.IsAvailable("quickToggles"))
            list.Add((new QuickPanelItem(TogglesId, "quickToggles", "Quick toggles", Glyphs.Toggles, null), true));
        var seenFeatures = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in FeatureRuntime.Registry.Actions)
        {
            if (action.FeatureId is "quickLauncher" or "quickToggles" || !FeatureCatalog.IsAvailable(action.FeatureId) || !seenIds.Add(action.Id)) continue;
            var primary = seenFeatures.Add(action.FeatureId);
            Func<bool>? active = action.Id == "keepAwake.toggle" ? () => KeepAwakeService.Current?.IsActive == true : null;
            list.Add((new QuickPanelItem("action:" + action.Id, action.FeatureId, action.Title, action.Glyph, action, active), primary));
        }
        int Rank(string feature)
        {
            var i = Array.IndexOf(FeaturePriority, feature);
            return i < 0 ? FeaturePriority.Length : i;
        }
        return list.Select((x, i) => (x, i)).OrderBy(p => Rank(p.x.Item1.FeatureId)).ThenBy(p => p.i).Select(p => p.x).ToList();
    }

    public static List<string> Order(IReadOnlyList<string> defaults) =>
        QuickToolsLogic.SanitizedOrder(Settings.GetString(OrderKey, string.Empty), defaults);

    /// <summary>(shown tiles in order, hidden tiles).</summary>
    public static (List<QuickPanelItem> Shown, List<QuickPanelItem> Hidden) Resolve()
    {
        var candidates = Candidates();
        var byId = candidates.ToDictionary(c => c.Item.Id, c => c);
        var order = Order(candidates.Select(c => c.Item.Id).ToList());
        var hidden = QuickToolsLogic.ParseSet(Settings.GetString(HiddenKey, string.Empty));
        var added = QuickToolsLogic.ParseSet(Settings.GetString(AddedKey, string.Empty));
        var shown = new List<QuickPanelItem>();
        var off = new List<QuickPanelItem>();
        foreach (var id in order)
        {
            var (item, primary) = byId[id];
            if (!hidden.Contains(id) && (primary || added.Contains(id))) shown.Add(item);
            else off.Add(item);
        }
        return (shown, off);
    }

    public static void Hide(string id)
    {
        var hidden = QuickToolsLogic.ParseSet(Settings.GetString(HiddenKey, string.Empty));
        var added = QuickToolsLogic.ParseSet(Settings.GetString(AddedKey, string.Empty));
        hidden.Add(id);
        added.Remove(id);
        Settings.Set(HiddenKey, QuickToolsLogic.JoinedSet(hidden));
        Settings.Set(AddedKey, QuickToolsLogic.JoinedSet(added));
    }

    public static void Show(string id)
    {
        var hidden = QuickToolsLogic.ParseSet(Settings.GetString(HiddenKey, string.Empty));
        var added = QuickToolsLogic.ParseSet(Settings.GetString(AddedKey, string.Empty));
        hidden.Remove(id);
        added.Add(id);
        Settings.Set(HiddenKey, QuickToolsLogic.JoinedSet(hidden));
        Settings.Set(AddedKey, QuickToolsLogic.JoinedSet(added));
    }

    public static void MoveBefore(string id, string? beforeId)
    {
        var defaults = Candidates().Select(c => c.Item.Id).ToList();
        Settings.Set(OrderKey, QuickToolsLogic.Joined(QuickToolsLogic.Moved(Order(defaults), id, beforeId)));
    }

    public static void Reset()
    {
        Settings.Remove(OrderKey);
        Settings.Remove(HiddenKey);
        Settings.Remove(AddedKey);
    }
}
