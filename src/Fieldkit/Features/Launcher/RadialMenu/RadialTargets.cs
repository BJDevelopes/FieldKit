// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Resolves tool, quick toggle and window layout slices against what other modules registered
/// (<see cref="ToolAction"/>s and <see cref="QuickToggleDef"/>s), and names slices. Payloads may be a
/// registered id ("screenshot.capture") or an upstream raw value ("screenshot", "leftHalf",
/// "emptyTrash"), so wheels imported from upstream and the starter sets keep working.
/// </summary>
public sealed class RadialCatalog(IReadOnlyList<ToolAction> actions, IReadOnlyList<QuickToggleDef> toggles, Func<string, bool> isAvailable)
{
    public const string FeatureId = "radialMenu";
    private const string LayoutFeature = "windowLayout";
    private const string TogglesFeature = "quickToggles";

    /// <summary>The live registry (only installed features count).</summary>
    public static RadialCatalog Current =>
        new(FeatureRuntime.Registry.Actions, FeatureRuntime.Registry.QuickToggles, FeatureCatalog.IsAvailable);

    /// <summary>Upstream quick toggle raw values and the Windows names they may be registered under.</summary>
    private static readonly Dictionary<string, string[]> ToggleAliases = new(StringComparer.Ordinal)
    {
        ["darkmode"] = ["darkmode", "appearance", "theme", "darktheme"],
        ["emptytrash"] = ["emptytrash", "emptyrecyclebin", "recyclebin"],
        ["ejectdisks"] = ["ejectdisks", "eject", "ejectdrives", "ejectall"],
        ["hiddenfiles"] = ["hiddenfiles", "showhiddenfiles", "hidden"],
        ["desktopicons"] = ["desktopicons", "hidedesktopicons", "desktop"],
        ["lockscreen"] = ["lockscreen", "lock"],
        ["displayoff"] = ["displayoff", "turnoffdisplay", "screenoff", "displaysoff"],
        ["screensaver"] = ["screensaver"],
    };

    /// <summary>Tools a slice may start: every installed feature's actions except the wheel's own
    /// and window layouts (which have their own kind, like upstream).</summary>
    public IEnumerable<ToolAction> ToolChoices =>
        actions.Where(a => a.FeatureId is not FeatureId and not LayoutFeature && isAvailable(a.FeatureId));

    public IEnumerable<ToolAction> LayoutChoices =>
        isAvailable(LayoutFeature) ? actions.Where(a => a.FeatureId == LayoutFeature) : [];

    public IEnumerable<QuickToggleDef> ToggleChoices => isAvailable(TogglesFeature) ? toggles : [];

    public ToolAction? Tool(string payload)
    {
        var choices = ToolChoices.ToList();
        return choices.FirstOrDefault(a => a.Id == payload)
               ?? choices.FirstOrDefault(a => a.FeatureId == payload);
    }

    public ToolAction? Layout(string payload)
    {
        var choices = LayoutChoices.ToList();
        var wanted = Normalize(payload);
        return choices.FirstOrDefault(a => a.Id == payload)
               ?? choices.FirstOrDefault(a => Normalize(LastSegment(a.Id)) == wanted);
    }

    public QuickToggleDef? Toggle(string payload)
    {
        var choices = ToggleChoices.ToList();
        var exact = choices.FirstOrDefault(t => t.Id == payload);
        if (exact is not null) return exact;
        var wanted = Normalize(LastSegment(payload));
        var names = ToggleAliases.TryGetValue(wanted, out var aliases) ? aliases : [wanted];
        return choices.FirstOrDefault(t => names.Contains(Normalize(LastSegment(t.Id))));
    }

    /// <summary>Whether a (non-submenu) slice can run right now.</summary>
    public bool IsRunnable(RadialMenuItem item) => item.Kind switch
    {
        RadialItemKind.Tool => Tool(item.Payload) is not null,
        RadialItemKind.QuickToggle => Toggle(item.Payload) is not null,
        RadialItemKind.WindowLayout => Layout(item.Payload) is not null,
        _ => true,
    };

    /// <summary>A custom name wins; everything else derives from the target.</summary>
    public string DisplayName(RadialMenuItem item, RadialNowPlayingState? nowPlaying = null)
    {
        if (item.Name.Length > 0) return item.Name;
        return item.Kind switch
        {
            RadialItemKind.App or RadialItemKind.File => FileDisplayName(item.Payload),
            RadialItemKind.Url => UrlDisplayName(item.Payload),
            RadialItemKind.Shortcut => Shortcut.Parse(item.Payload) is { IsEmpty: false } s ? s.ToString() : RadialStrings.KindShortcut,
            RadialItemKind.Tool => Tool(item.Payload)?.Title
                                   ?? (FeatureCatalog.TryGet(item.Payload, out var f) ? f.Title : RadialStrings.KindTool),
            RadialItemKind.QuickToggle => Toggle(item.Payload)?.Title ?? RadialStrings.KindQuickToggle,
            RadialItemKind.WindowLayout => Layout(item.Payload)?.Title ?? RadialStrings.KindWindowLayout,
            RadialItemKind.Media => MediaName(item.MediaKey, nowPlaying),
            _ => RadialStrings.KindSubmenu,
        };
    }

    private static string MediaName(RadialMediaKey? key, RadialNowPlayingState? state) => key switch
    {
        RadialMediaKey.NowPlaying => state switch
        {
            { Kind: RadialNowPlayingKind.Playing, Snapshot: { } snapshot } => snapshot.RadialLabel ?? RadialStrings.MediaNowPlaying,
            { Kind: RadialNowPlayingKind.NothingPlaying } => RadialStrings.MediaNothingPlaying,
            _ => RadialStrings.MediaNowPlaying,
        },
        { } k => RadialStrings.MediaTitle(k),
        null => RadialStrings.KindMedia,
    };

    /// <summary>"Notepad" for "C:\…\notepad.exe" or a .lnk, the folder name for folders, the app
    /// name for a Start menu entry ("shell:AppsFolder\…!App").</summary>
    public static string FileDisplayName(string payload)
    {
        var p = payload.Trim().TrimEnd('\\', '/');
        if (p.Length == 0) return RadialStrings.KindFile;
        if (p.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            var id = p["shell:AppsFolder\\".Length..];
            var bang = id.LastIndexOf('!');
            var name = bang >= 0 ? id[(bang + 1)..] : id;
            if (bang >= 0 && (name.Length == 0 || name.Equals("App", StringComparison.OrdinalIgnoreCase)))
                name = id[..bang].Split('_')[0];
            return LastSegment(name);
        }
        if (p == "~") return "Home";
        if (p.Length == 2 && p[1] == ':') return p + "\\";
        var file = Path.GetFileName(p);
        if (file.Length == 0) return p;
        var ext = Path.GetExtension(file);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(file)
            : file;
    }

    public static string UrlDisplayName(string payload)
    {
        var normalized = RadialMenuSupport.NormalizedUrl(payload) ?? payload;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : payload;
    }

    private static string LastSegment(string id)
    {
        var dot = id.LastIndexOf('.');
        return dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
