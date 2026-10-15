// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>What a slice does. Raw values (see <see cref="RadialRaw"/>) are persisted and match upstream.</summary>
public enum RadialItemKind { App, File, Url, Shortcut, Tool, QuickToggle, WindowLayout, Media, Submenu }

/// <summary>Accent colors available for radial menu profiles (upstream <c>RadialMenuColor</c>).</summary>
public enum RadialMenuColor { Accent, Blue, Purple, Pink, Red, Orange, Yellow, Green, Mint, Cyan, Indigo, Graphite }

/// <summary>Media keys a slice can press. Now Playing opens a metadata card instead.</summary>
public enum RadialMediaKey { PlayPause, PreviousTrack, NextTrack, NowPlaying }

/// <summary>How the summoning shortcut or mouse button owns a session (upstream <c>RadialMenuActivationMode</c>).</summary>
public enum RadialActivationMode
{
    /// <summary>Release over a slice runs it; release near the center leaves the wheel open.</summary>
    PressOrHold,
    /// <summary>A press opens a sticky wheel; releasing the summoner has no effect.</summary>
    Press,
    /// <summary>The wheel exists only while the summoner is down.</summary>
    Hold,
}

public enum RadialReleaseAction { StayOpen, Dismiss, Select }

/// <summary>The extra mouse buttons Windows reports to a low-level hook.</summary>
public enum RadialMouseButton { Off, Middle, Back, Forward }

/// <summary>Curated starter sets for new profiles.</summary>
public enum RadialMenuPreset { General, Media, Tools, WindowLayout, QuickToggles, Blank }

/// <summary>Persisted raw values, identical to upstream so settings stay portable.</summary>
public static class RadialRaw
{
    public static string Kind(RadialItemKind kind) => kind switch
    {
        RadialItemKind.App => "app",
        RadialItemKind.File => "file",
        RadialItemKind.Url => "url",
        RadialItemKind.Shortcut => "shortcut",
        RadialItemKind.Tool => "tool",
        RadialItemKind.QuickToggle => "quickToggle",
        RadialItemKind.WindowLayout => "windowLayout",
        RadialItemKind.Media => "media",
        _ => "submenu",
    };

    public static RadialItemKind? ParseKind(string? raw) => raw switch
    {
        "app" => RadialItemKind.App,
        "file" => RadialItemKind.File,
        "url" => RadialItemKind.Url,
        "shortcut" => RadialItemKind.Shortcut,
        "tool" => RadialItemKind.Tool,
        "quickToggle" => RadialItemKind.QuickToggle,
        "windowLayout" => RadialItemKind.WindowLayout,
        "media" => RadialItemKind.Media,
        "submenu" => RadialItemKind.Submenu,
        _ => null,
    };

    public static string Color(RadialMenuColor color) => color.ToString().ToLowerInvariant();

    public static RadialMenuColor ParseColor(string? raw) =>
        Enum.GetValues<RadialMenuColor>().FirstOrDefault(c => Color(c) == raw);

    public static string Media(RadialMediaKey key) => key switch
    {
        RadialMediaKey.PlayPause => "playPause",
        RadialMediaKey.PreviousTrack => "previousTrack",
        RadialMediaKey.NextTrack => "nextTrack",
        _ => "nowPlaying",
    };

    public static RadialMediaKey? ParseMedia(string? raw) => raw switch
    {
        "playPause" => RadialMediaKey.PlayPause,
        "previousTrack" => RadialMediaKey.PreviousTrack,
        "nextTrack" => RadialMediaKey.NextTrack,
        "nowPlaying" => RadialMediaKey.NowPlaying,
        _ => null,
    };

    public static string Activation(RadialActivationMode mode) => mode switch
    {
        RadialActivationMode.Press => "press",
        RadialActivationMode.Hold => "hold",
        _ => "pressOrHold",
    };

    /// <summary>Unknown or missing values fall back to the adaptive gesture, like upstream.</summary>
    public static RadialActivationMode ParseActivation(string? raw) => raw switch
    {
        "press" => RadialActivationMode.Press,
        "hold" => RadialActivationMode.Hold,
        _ => RadialActivationMode.PressOrHold,
    };

    /// <summary>"off", "back", "forward" (upstream) and "middle" (Windows only).</summary>
    public static string MouseButton(RadialMouseButton button) => button switch
    {
        RadialMouseButton.Middle => "middle",
        RadialMouseButton.Back => "back",
        RadialMouseButton.Forward => "forward",
        _ => "off",
    };

    /// <summary>Upstream also stores "button:N" for buttons 5 and up; Windows cannot see them, so they read as off.</summary>
    public static RadialMouseButton ParseMouseButton(string? raw) => raw switch
    {
        "middle" => RadialMouseButton.Middle,
        "back" or "button:3" => RadialMouseButton.Back,
        "forward" or "button:4" => RadialMouseButton.Forward,
        _ => RadialMouseButton.Off,
    };

    public static string Preset(RadialMenuPreset preset) => preset switch
    {
        RadialMenuPreset.General => "general",
        RadialMenuPreset.Media => "media",
        RadialMenuPreset.Tools => "tools",
        RadialMenuPreset.WindowLayout => "windowLayout",
        RadialMenuPreset.QuickToggles => "quickToggles",
        _ => "blank",
    };

    public static RadialMenuPreset? ParsePreset(string? raw) =>
        Enum.GetValues<RadialMenuPreset>().Select(p => (RadialMenuPreset?)p).FirstOrDefault(p => Preset(p!.Value) == raw);
}

/// <summary>
/// One action on the wheel. <see cref="Payload"/> carries the target: an app or file path, a
/// link, a tool / quick toggle / window layout / media identifier, or a shortcut ("Ctrl+Shift+T").
/// Submenus keep their actions in <see cref="Children"/>.
/// </summary>
public sealed class RadialMenuItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RadialItemKind Kind { get; set; } = RadialItemKind.App;
    /// <summary>Custom label; empty derives one from the target.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Upstream SF Symbol name picked in the editor (mapped to a Fluent glyph); empty = automatic.</summary>
    public string SymbolName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    /// <summary>PNG bytes of a downloaded website icon (at most 64 KB).</summary>
    public byte[]? CustomIconData { get; set; }
    public List<RadialMenuItem> Children { get; set; } = [];

    public RadialMenuItem() { }

    public RadialMenuItem(RadialItemKind kind, string payload = "")
    {
        Kind = kind;
        Payload = payload;
    }

    public RadialMediaKey? MediaKey => Kind == RadialItemKind.Media ? RadialRaw.ParseMedia(Payload) : null;

    /// <summary>App and file slices draw their real icon unless the user picked a symbol.</summary>
    public bool UsesFileIcon => Kind is RadialItemKind.App or RadialItemKind.File && SymbolName.Length == 0;

    public RadialMenuItem Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        SymbolName = SymbolName,
        Payload = Payload,
        CustomIconData = CustomIconData?.ToArray(),
        Children = Children.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>A complete wheel: its items, color, shortcut and mouse button.</summary>
public sealed class RadialMenuProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public RadialMenuColor Color { get; set; } = RadialMenuColor.Accent;
    /// <summary>"Ctrl+Alt+Space"; empty = none. The first profile's shortcut lives in <c>radialMenu.shortcut</c>.</summary>
    public string Shortcut { get; set; } = string.Empty;
    public string MouseButton { get; set; } = "off";
    /// <summary>Windows addition: keys that must be held with the mouse button ("Ctrl", "Ctrl+Shift"; empty = none).</summary>
    public string MouseModifiers { get; set; } = string.Empty;
    public List<RadialMenuItem> Items { get; set; } = [];
    /// <summary>The starter set this wheel was created from (raw value), so Reset knows what to restore.</summary>
    public string? Preset { get; set; }
    /// <summary>Upstream four-finger tap; kept so settings round-trip, unused on Windows.</summary>
    public bool TrackpadTap { get; set; }

    public string DisplayName => Name.Length == 0 ? RadialStrings.PresetGeneral : Name;

    public RadialMouseButton Button => RadialRaw.ParseMouseButton(MouseButton);

    public Modifiers ButtonModifiers => RadialMenuSupport.ParseModifiers(MouseModifiers);

    /// <summary>The copy Duplicate adds: triggers each open one wheel, so the copy starts without them.</summary>
    public RadialMenuProfile Duplicate(string name)
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = name;
        copy.Shortcut = string.Empty;
        copy.MouseButton = "off";
        copy.MouseModifiers = string.Empty;
        copy.TrackpadTap = false;
        return copy;
    }

    public RadialMenuProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Color = Color,
        Shortcut = Shortcut,
        MouseButton = MouseButton,
        MouseModifiers = MouseModifiers,
        Items = Items.Select(i => i.Clone()).ToList(),
        Preset = Preset,
        TrackpadTap = TrackpadTap,
    };
}

/// <summary>Starter sets (upstream <c>RadialMenuProfilePreset</c>).</summary>
public static class RadialPresets
{
    public static RadialMenuColor DefaultColor(RadialMenuPreset preset) => preset switch
    {
        RadialMenuPreset.Media => RadialMenuColor.Purple,
        RadialMenuPreset.Tools => RadialMenuColor.Cyan,
        RadialMenuPreset.WindowLayout => RadialMenuColor.Orange,
        RadialMenuPreset.QuickToggles => RadialMenuColor.Mint,
        RadialMenuPreset.Blank => RadialMenuColor.Graphite,
        _ => RadialMenuColor.Accent,
    };

    /// <summary>The wheel a fresh install starts with: media around the top, tools and Downloads below.</summary>
    public static List<RadialMenuItem> StarterItems() =>
    [
        new(RadialItemKind.Media, "playPause"),
        new(RadialItemKind.Media, "nextTrack"),
        new(RadialItemKind.Tool, "screenshot"),
        new(RadialItemKind.File, "~\\Downloads"),
        new(RadialItemKind.Tool, "colorPicker"),
        new(RadialItemKind.Media, "previousTrack"),
    ];

    public static List<RadialMenuItem> MakeItems(RadialMenuPreset preset) => preset switch
    {
        RadialMenuPreset.General => StarterItems(),
        RadialMenuPreset.Media =>
        [
            new(RadialItemKind.Media, "playPause"),
            new(RadialItemKind.Media, "nextTrack"),
            new(RadialItemKind.Media, "nowPlaying"),
            new(RadialItemKind.Media, "previousTrack"),
        ],
        RadialMenuPreset.Tools =>
        [
            new(RadialItemKind.Tool, "screenshot"),
            new(RadialItemKind.Tool, "colorPicker"),
            new(RadialItemKind.Tool, "screenOCR"),
            new(RadialItemKind.Tool, "screenRecorder"),
            new(RadialItemKind.Tool, "micMute"),
            new(RadialItemKind.Tool, "scratchpad"),
        ],
        RadialMenuPreset.WindowLayout =>
        [
            new(RadialItemKind.WindowLayout, "maximize"),
            new(RadialItemKind.WindowLayout, "rightHalf"),
            new(RadialItemKind.WindowLayout, "bottomHalf"),
            new(RadialItemKind.WindowLayout, "leftHalf"),
            new(RadialItemKind.WindowLayout, "topHalf"),
        ],
        RadialMenuPreset.QuickToggles =>
        [
            new(RadialItemKind.QuickToggle, "darkMode"),
            new(RadialItemKind.QuickToggle, "desktopIcons"),
            new(RadialItemKind.QuickToggle, "hiddenFiles"),
            new(RadialItemKind.QuickToggle, "lockScreen"),
            new(RadialItemKind.QuickToggle, "emptyTrash"),
        ],
        _ => [],
    };

    public static RadialMenuProfile Create(RadialMenuPreset preset, string? name = null, RadialMenuColor? color = null,
        string shortcut = "", string mouseButton = "off") => new()
    {
        Name = name ?? string.Empty,
        Color = color ?? DefaultColor(preset),
        Shortcut = shortcut,
        MouseButton = mouseButton,
        Items = MakeItems(preset),
        Preset = RadialRaw.Preset(preset),
    };
}

/// <summary>Profile colors per theme. Light values are upstream's; dark ones are the system palette.</summary>
public static class RadialColors
{
    public static Color Resolve(RadialMenuColor color, bool dark, Color accent) => color switch
    {
        RadialMenuColor.Accent => accent,
        RadialMenuColor.Blue => dark ? Rgb(0x0A, 0x84, 0xFF) : Rgb(0x00, 0x7A, 0xFF),
        RadialMenuColor.Purple => dark ? Rgb(0xBF, 0x5A, 0xF2) : Rgb(0x94, 0x33, 0xD9),
        RadialMenuColor.Pink => dark ? Rgb(0xFF, 0x37, 0x5F) : Rgb(0xE0, 0x29, 0x73),
        RadialMenuColor.Red => dark ? Rgb(0xFF, 0x45, 0x3A) : Rgb(0xD9, 0x2E, 0x2E),
        RadialMenuColor.Orange => dark ? Rgb(0xFF, 0x9F, 0x0A) : Rgb(0xF2, 0x73, 0x00),
        RadialMenuColor.Yellow => dark ? Rgb(0xFF, 0xD6, 0x0A) : Rgb(0xD9, 0xA6, 0x00),
        RadialMenuColor.Green => dark ? Rgb(0x30, 0xD1, 0x58) : Rgb(0x2E, 0xA6, 0x40),
        RadialMenuColor.Mint => dark ? Rgb(0x63, 0xE6, 0xE2) : Rgb(0x00, 0xAD, 0x99),
        RadialMenuColor.Cyan => dark ? Rgb(0x64, 0xD2, 0xFF) : Rgb(0x26, 0xA6, 0xD9),
        RadialMenuColor.Indigo => dark ? Rgb(0x5E, 0x5C, 0xE6) : Rgb(0x59, 0x59, 0xD9),
        RadialMenuColor.Graphite => dark ? Rgb(0xA6, 0xA6, 0xA6) : Rgb(0x66, 0x66, 0x66),
        _ => accent,
    };

    private static Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);
}
