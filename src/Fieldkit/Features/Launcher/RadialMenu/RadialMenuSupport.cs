// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Pure rules of the radial menu (upstream <c>RadialMenuSupport</c>): what may sit on a wheel,
/// how profiles are (de)serialized and migrated, which wheel owns which trigger, and how a
/// session reacts to its summoner being released. No settings, windows or hooks here.
/// </summary>
public static class RadialMenuSupport
{
    public const int MaxItemsPerWheel = 12;
    /// <summary>Root plus one submenu level. Deeper nesting turns the wheel into a maze.</summary>
    public const int MaxDepth = 2;
    public const int MaxNameLength = 60;
    public const int MaxStoredIconBytes = 65536;
    /// <summary>The shortcut of a fresh install (upstream ⌃⌥⌘Space; Win+Space belongs to Windows).</summary>
    public const string DefaultShortcut = "Ctrl+Alt+Win+Space"; // upstream ⌃⌥⌘Space

    // ------------------------------------------------------------------ items

    /// <summary>Whether the target can run for this kind. The editor blocks saving what fails here,
    /// and <see cref="Sanitized"/> drops it, so the two never disagree.</summary>
    public static bool IsValidPayload(RadialMenuItem item) => item.Kind switch
    {
        RadialItemKind.App or RadialItemKind.File => item.Payload.Length > 0,
        RadialItemKind.Url => NormalizedUrl(item.Payload) is not null,
        RadialItemKind.Shortcut => !Shortcut.Parse(item.Payload).IsEmpty,
        RadialItemKind.Tool or RadialItemKind.QuickToggle or RadialItemKind.WindowLayout => item.Payload.Length > 0,
        RadialItemKind.Media => item.MediaKey is not null,
        _ => true,
    };

    /// <summary>
    /// Drops what cannot run (empty targets, unknown media keys, unparseable shortcuts, submenus
    /// past the depth cap, duplicate ids) and clamps counts, so the wheel never renders a dead slice.
    /// </summary>
    public static List<RadialMenuItem> Sanitized(IEnumerable<RadialMenuItem> items, int depth = 0)
    {
        if (depth >= MaxDepth) return [];
        var seen = new HashSet<Guid>();
        var result = new List<RadialMenuItem>();
        foreach (var source in items)
        {
            if (result.Count >= MaxItemsPerWheel || !seen.Add(source.Id)) continue;
            var item = source.Clone();
            item.Name = Truncate(item.Name.Trim(), MaxNameLength);
            item.Payload = item.Payload.Trim();
            if (!IsValidPayload(item)) continue;
            if (item.Kind == RadialItemKind.Url && NormalizedUrl(item.Payload) is { } normalized) item.Payload = normalized;
            if (item.Kind == RadialItemKind.Shortcut) item.Payload = Shortcut.Parse(item.Payload).ToString();
            if (item.CustomIconData is { } data && !IsAcceptableIcon(data)) item.CustomIconData = null;
            if (item.Kind == RadialItemKind.Submenu)
            {
                if (depth + 1 >= MaxDepth) continue;
                item.Children = Sanitized(item.Children, depth + 1);
            }
            else
            {
                item.Children = [];
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>A stored icon must be small and look like an image file.</summary>
    public static bool IsAcceptableIcon(byte[] data)
    {
        if (data.Length is 0 or > MaxStoredIconBytes) return false;
        ReadOnlySpan<byte> d = data;
        return d.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })   // PNG
               || d.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })      // JPEG
               || d.StartsWith("GIF8"u8)
               || d.StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 }) // ICO
               || d.StartsWith("BM"u8);
    }

    /// <summary>Wheels show only what can run today: slices whose tool is gone disappear, and
    /// so do submenus left empty.</summary>
    public static List<RadialMenuItem> AvailableItems(IEnumerable<RadialMenuItem> items, Func<RadialMenuItem, bool> isRunnable)
    {
        var result = new List<RadialMenuItem>();
        foreach (var source in items)
        {
            if (source.Kind == RadialItemKind.Submenu)
            {
                var children = AvailableItems(source.Children, isRunnable);
                if (children.Count == 0) continue;
                var copy = source.Clone();
                copy.Children = children;
                result.Add(copy);
            }
            else if (isRunnable(source))
            {
                result.Add(source);
            }
        }
        return result;
    }

    public static bool ContainsNowPlaying(IEnumerable<RadialMenuItem> items) =>
        items.Any(i => i.MediaKey == RadialMediaKey.NowPlaying || (i.Kind == RadialItemKind.Submenu && ContainsNowPlaying(i.Children)));

    public static bool UsesWindowLayout(IEnumerable<RadialMenuItem> items) =>
        items.Any(i => i.Kind == RadialItemKind.WindowLayout || (i.Kind == RadialItemKind.Submenu && UsesWindowLayout(i.Children)));

    /// <summary>Expands "~" (the user profile) and %VARIABLES% of a stored path.</summary>
    public static string ExpandPath(string payload)
    {
        var path = payload.Trim();
        if (path == "~") path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }

    // -------------------------------------------------------------------- URLs

    /// <summary>
    /// Accepts "example.com/page" style input by assuming https, keeps explicit schemes
    /// (mailto:, app links) as typed, and rejects anything that cannot become a loadable URL.
    /// </summary>
    public static string? NormalizedUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        if (HasExplicitScheme(trimmed))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme is "http" or "https") return uri.Host.Length > 0 ? trimmed : null;
            return trimmed;
        }
        var candidate = "https://" + trimmed;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var https) && https.Host.Length > 0 && !https.Host.StartsWith('.')
            ? candidate
            : null;
    }

    /// <summary>A leading URL scheme, minding that "example.com:8080" is a host and port while
    /// "tel:5551234" is a scheme.</summary>
    public static bool HasExplicitScheme(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return true;
        if (value.Length == 0 || !char.IsLetter(value[0])) return false;
        var colon = value.IndexOf(':');
        if (colon < 0) return false;
        var prefix = value[..colon];
        if (!prefix.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.')) return false;
        if (colon + 1 >= value.Length) return false;
        if (!char.IsDigit(value[colon + 1])) return true;
        return !prefix.Contains('.') && !prefix.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- profiles

    public static Modifiers ParseModifiers(string? text)
    {
        var mods = Modifiers.None;
        if (string.IsNullOrWhiteSpace(text)) return mods;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Modifiers.Ctrl,
                "alt" => Modifiers.Alt,
                "shift" => Modifiers.Shift,
                "win" or "windows" => Modifiers.Win,
                _ => Modifiers.None,
            };
        }
        return mods;
    }

    public static string ModifiersText(Modifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Modifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>
    /// Trims names, drops invalid shortcuts, keeps each (button, keys) trigger on the first wheel
    /// that has it, and never returns an empty list.
    /// </summary>
    public static List<RadialMenuProfile> SanitizedProfiles(IEnumerable<RadialMenuProfile> profiles, string defaultShortcut = DefaultShortcut)
    {
        var seenIds = new HashSet<Guid>();
        var seenButtons = new HashSet<(RadialMouseButton, Modifiers)>();
        var result = new List<RadialMenuProfile>();
        foreach (var source in profiles)
        {
            if (!seenIds.Add(source.Id)) continue;
            var profile = source.Clone();
            profile.Name = Truncate(profile.Name.Trim(), MaxNameLength);
            profile.Items = Sanitized(profile.Items);
            profile.Shortcut = Shortcut.Parse(profile.Shortcut.Trim()).ToString();
            var button = RadialRaw.ParseMouseButton(profile.MouseButton);
            var mods = ParseModifiers(profile.MouseModifiers);
            profile.MouseButton = RadialRaw.MouseButton(button);
            profile.MouseModifiers = ModifiersText(mods);
            if (button != RadialMouseButton.Off && !seenButtons.Add((button, mods)))
            {
                profile.MouseButton = RadialRaw.MouseButton(RadialMouseButton.Off);
                profile.MouseModifiers = string.Empty;
            }
            result.Add(profile);
        }
        if (result.Count == 0) result.Add(RadialPresets.Create(RadialMenuPreset.General, shortcut: defaultShortcut));
        return result;
    }

    /// <summary>
    /// Decodes the stored profiles. When none were saved yet, migrates the single wheel that came
    /// before profiles (legacy items + shortcut + mouse button), or seeds the starter wheel.
    /// </summary>
    public static List<RadialMenuProfile> DecodeProfiles(string? json, string? legacyItemsJson = null,
        string? legacyShortcut = null, string? legacyMouseButton = null)
    {
        if (json is not null && ParseProfiles(json) is { } decoded)
        {
            var sanitized = SanitizedProfiles(decoded);
            if (sanitized.Count > 0) return sanitized;
        }
        var items = DecodeItems(legacyItemsJson);
        var initial = new RadialMenuProfile
        {
            Shortcut = legacyShortcut ?? DefaultShortcut,
            MouseButton = legacyMouseButton ?? "off",
            Items = items,
            Preset = RadialRaw.Preset(RadialMenuPreset.General),
        };
        return SanitizedProfiles([initial]);
    }

    /// <summary>A missing blob is a fresh install (starter wheel); a present one, even empty, is the user's.</summary>
    public static List<RadialMenuItem> DecodeItems(string? json)
    {
        if (json is null) return RadialPresets.StarterItems();
        try
        {
            return JsonNode.Parse(json) is JsonArray array ? Sanitized(ReadItems(array)) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string EncodeProfiles(IEnumerable<RadialMenuProfile> profiles)
    {
        var array = new JsonArray();
        foreach (var p in SanitizedProfiles(profiles))
        {
            var obj = new JsonObject
            {
                ["id"] = p.Id.ToString("D").ToUpperInvariant(),
                ["name"] = p.Name,
                ["color"] = RadialRaw.Color(p.Color),
                ["shortcut"] = p.Shortcut,
                ["mouseButton"] = p.MouseButton,
                ["items"] = WriteItems(p.Items),
                ["trackpadTap"] = p.TrackpadTap,
            };
            if (p.MouseModifiers.Length > 0) obj["mouseModifiers"] = p.MouseModifiers;
            if (p.Preset is not null) obj["preset"] = p.Preset;
            array.Add(obj);
        }
        return array.ToJsonString();
    }

    public static string EncodeItems(IEnumerable<RadialMenuItem> items) => WriteItems(Sanitized(items)).ToJsonString();

    /// <summary>Null when the text is not a JSON array at all (corrupt blob).</summary>
    public static List<RadialMenuProfile>? ParseProfiles(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return null;
            var result = new List<RadialMenuProfile>();
            foreach (var node in array)
            {
                if (node is not JsonObject obj) continue;
                try
                {
                    result.Add(new RadialMenuProfile
                    {
                        Id = ReadGuid(obj["id"]),
                        Name = ReadString(obj["name"]),
                        Color = RadialRaw.ParseColor(ReadString(obj["color"], "accent")),
                        Shortcut = ReadString(obj["shortcut"]),
                        MouseButton = ReadString(obj["mouseButton"], "off"),
                        MouseModifiers = ReadString(obj["mouseModifiers"]),
                        Items = obj["items"] is JsonArray items ? ReadItems(items) : [],
                        Preset = obj["preset"] is JsonValue preset && preset.TryGetValue<string>(out var raw) ? raw : null,
                        TrackpadTap = obj["trackpadTap"] is JsonValue tap && tap.TryGetValue<bool>(out var on) && on,
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    // A profile written by a newer version fails alone.
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lossy: an item of an unknown kind fails alone instead of losing the whole wheel.</summary>
    private static List<RadialMenuItem> ReadItems(JsonArray array)
    {
        var result = new List<RadialMenuItem>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;
            var kind = RadialRaw.ParseKind(ReadString(obj["kind"]));
            if (kind is null) continue;
            byte[]? icon = null;
            if (obj["customIconData"] is JsonValue iconValue && iconValue.TryGetValue<string>(out var b64))
            {
                try { icon = Convert.FromBase64String(b64); }
                catch (FormatException) { icon = null; }
            }
            result.Add(new RadialMenuItem
            {
                Id = ReadGuid(obj["id"]),
                Kind = kind.Value,
                Name = ReadString(obj["name"]),
                SymbolName = ReadString(obj["symbolName"]),
                Payload = ReadString(obj["payload"]),
                CustomIconData = icon,
                Children = obj["children"] is JsonArray children ? ReadItems(children) : [],
            });
        }
        return result;
    }

    private static JsonArray WriteItems(IEnumerable<RadialMenuItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var obj = new JsonObject
            {
                ["id"] = item.Id.ToString("D").ToUpperInvariant(),
                ["kind"] = RadialRaw.Kind(item.Kind),
                ["name"] = item.Name,
                ["symbolName"] = item.SymbolName,
                ["payload"] = item.Payload,
                ["children"] = WriteItems(item.Children),
            };
            if (item.CustomIconData is { Length: > 0 } icon) obj["customIconData"] = Convert.ToBase64String(icon);
            array.Add(obj);
        }
        return array;
    }

    private static string ReadString(JsonNode? node, string fallback = "") =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static Guid ReadGuid(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : Guid.NewGuid();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    // ---------------------------------------------------------------- triggers

    /// <summary>Whether anything opens this wheel (the profile picked in Settings is only the one being edited).</summary>
    public static bool HasTrigger(RadialMenuProfile profile) =>
        !Shortcut.Parse(profile.Shortcut).IsEmpty || profile.Button != RadialMouseButton.Off;

    /// <summary>The other wheel that already opens on a combination; Settings refuses a second one.</summary>
    public static RadialMenuProfile? ProfileUsing(Shortcut shortcut, IEnumerable<RadialMenuProfile> profiles, Guid excluding) =>
        shortcut.IsEmpty ? null : profiles.FirstOrDefault(p => p.Id != excluding && Shortcut.Parse(p.Shortcut) == shortcut);

    /// <summary>Gives one wheel a mouse button (+ keys); a trigger opens only one wheel, so the others lose it.</summary>
    public static List<RadialMenuProfile> AssigningMouseButton(RadialMouseButton button, Modifiers mods, Guid profileId,
        IEnumerable<RadialMenuProfile> profiles)
    {
        var result = new List<RadialMenuProfile>();
        foreach (var source in profiles)
        {
            var profile = source.Clone();
            if (profile.Id == profileId)
            {
                profile.MouseButton = RadialRaw.MouseButton(button);
                profile.MouseModifiers = button == RadialMouseButton.Off ? string.Empty : ModifiersText(mods);
            }
            else if (button != RadialMouseButton.Off && profile.Button == button && profile.ButtonModifiers == mods)
            {
                profile.MouseButton = RadialRaw.MouseButton(RadialMouseButton.Off);
                profile.MouseModifiers = string.Empty;
            }
            result.Add(profile);
        }
        return result;
    }

    /// <summary>The mouse triggers the wheels answer to, first wheel wins.</summary>
    public static List<RadialMouseTrigger> MouseTriggers(IEnumerable<RadialMenuProfile> profiles)
    {
        var result = new List<RadialMouseTrigger>();
        foreach (var p in profiles)
        {
            if (p.Button == RadialMouseButton.Off) continue;
            var trigger = new RadialMouseTrigger(p.Button, p.ButtonModifiers, p.Id);
            if (result.Any(t => t.Button == trigger.Button && t.Modifiers == trigger.Modifiers)) continue;
            result.Add(trigger);
        }
        return result;
    }

    /// <summary>The wheel a press of <paramref name="button"/> with <paramref name="held"/> keys opens, if any.</summary>
    public static RadialMouseTrigger? MatchMouseTrigger(IReadOnlyList<RadialMouseTrigger> triggers, RadialMouseButton button, Modifiers held)
    {
        foreach (var t in triggers)
        {
            if (t.Button == button && t.Modifiers == held) return t;
        }
        return null;
    }

    /// <summary>Maps a low-level mouse event to the wheel's button vocabulary.</summary>
    public static RadialMouseButton ButtonOf(MouseAction action, int xButton) => action switch
    {
        MouseAction.MiddleDown or MouseAction.MiddleUp => RadialMouseButton.Middle,
        MouseAction.XDown or MouseAction.XUp when xButton == 1 => RadialMouseButton.Back,
        MouseAction.XDown or MouseAction.XUp when xButton == 2 => RadialMouseButton.Forward,
        _ => RadialMouseButton.Off,
    };

    // ------------------------------------------------------------- activation

    /// <summary>Whether a new session starts in its hold phase (owning the summoner until release).</summary>
    public static bool StartsHeld(RadialActivationMode mode, bool requestedHold, bool hasHeldButton, bool shortcutHasModifiers) =>
        mode != RadialActivationMode.Press && (hasHeldButton || (requestedHold && shortcutHasModifiers));

    /// <summary>What releasing the summoner does.</summary>
    public static RadialReleaseAction ReleaseAction(RadialActivationMode mode, bool hasSelection) =>
        hasSelection ? RadialReleaseAction.Select
        : mode == RadialActivationMode.Hold ? RadialReleaseAction.Dismiss
        : RadialReleaseAction.StayOpen;

    /// <summary>Arrow-key browsing: steps the highlight around the wheel, wrapping.</summary>
    public static int? RotateHighlight(int? current, int delta, int count)
    {
        if (count <= 0) return null;
        var start = current ?? (delta > 0 ? -1 : 0);
        return ((start + delta) % count + count) % count;
    }

    /// <summary>Digits 1–9 pick a slice directly.</summary>
    public static int? DigitIndex(int virtualKey, int count)
    {
        int digit;
        if (virtualKey is >= 0x31 and <= 0x39) digit = virtualKey - 0x30;
        else if (virtualKey is >= 0x61 and <= 0x69) digit = virtualKey - 0x60;
        else return null;
        return digit <= count ? digit - 1 : null;
    }

    // ---------------------------------------------------------------- presets

    /// <summary>The name a new profile gets ("General 3" for the general set, the preset title otherwise).</summary>
    public static string NewProfileName(RadialMenuPreset preset, int existingCount) =>
        preset == RadialMenuPreset.General ? $"{RadialStrings.PresetGeneral} {existingCount + 1}" : RadialStrings.PresetTitle(preset);

    /// <summary>The starter set a wheel came from: stored, or matched by its (untouched) name.</summary>
    public static RadialMenuPreset? ResolvedPreset(RadialMenuProfile profile)
    {
        if (RadialRaw.ParsePreset(profile.Preset) is { } stored) return stored;
        foreach (var preset in Enum.GetValues<RadialMenuPreset>())
        {
            if (string.Equals(RadialStrings.PresetTitle(preset), profile.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(RadialRaw.Preset(preset), profile.Name, StringComparison.OrdinalIgnoreCase))
                return preset;
        }
        return null;
    }
}

/// <summary>A mouse button (plus held keys) that opens a given wheel.</summary>
public readonly record struct RadialMouseTrigger(RadialMouseButton Button, Modifiers Modifiers, Guid ProfileId);
