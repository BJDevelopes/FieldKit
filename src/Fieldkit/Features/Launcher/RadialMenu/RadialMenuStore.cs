// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Fieldkit.Core;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Settings of the radial menu. Profiles are one JSON setting (<c>radialMenu.profiles</c>); the first
/// profile's shortcut lives in <c>radialMenu.shortcut</c> so Settings → Keyboard shortcuts edits it.
/// </summary>
internal static class RadialMenuStore
{
    public const string EnabledKey = "radialMenu.enabled";
    public const string ProfilesKey = "radialMenu.profiles";
    public const string ShortcutKey = "radialMenu.shortcut";
    public const string ActivationModeKey = "radialMenu.activationMode";
    public const string AtPointerKey = "radialMenu.atPointer";
    /// <summary>Legacy single-wheel keys migrated into the first profile.</summary>
    public const string LegacyItemsKey = "radialMenu.items";
    public const string LegacyMouseButtonKey = "radialMenu.mouseButton";

    private static string PrimaryShortcut => Settings.GetString(ShortcutKey, RadialMenuSupport.DefaultShortcut);

    public static List<RadialMenuProfile> Load()
    {
        var profiles = RadialMenuSupport.DecodeProfiles(ReadJson(ProfilesKey), ReadJson(LegacyItemsKey),
            PrimaryShortcut, Settings.Has(LegacyMouseButtonKey) ? Settings.GetString(LegacyMouseButtonKey) : null);
        return WithPrimaryShortcut(profiles, PrimaryShortcut);
    }

    /// <summary>The first wheel answers to the shortcut stored in its own setting.</summary>
    public static List<RadialMenuProfile> WithPrimaryShortcut(List<RadialMenuProfile> profiles, string primary)
    {
        if (profiles.Count > 0) profiles[0].Shortcut = Interop.Shortcut.Parse(primary).ToString();
        return profiles;
    }

    public static void Save(IReadOnlyList<RadialMenuProfile> profiles)
    {
        var sanitized = RadialMenuSupport.SanitizedProfiles(profiles);
        Settings.Set(ShortcutKey, sanitized[0].Shortcut);
        Settings.Set(ProfilesKey, JsonNode.Parse(RadialMenuSupport.EncodeProfiles(sanitized)));
    }

    /// <summary>The setting as JSON text (stored structured; a string holding JSON is accepted too).</summary>
    private static string? ReadJson(string key)
    {
        if (!Settings.Has(key)) return null;
        var node = Settings.Get<JsonNode?>(key, null);
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };
    }
}
