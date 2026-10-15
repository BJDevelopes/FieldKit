// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The combinations the person tied to single rows (port of upstream CommandBarRowShortcuts).
/// Stored as a JSON map from a row's stable key to shortcut text ("Ctrl+Alt+N"). Nothing is
/// registered unless the person asked for it.
/// </summary>
public static class CommandBarRowShortcuts
{
    public const int Limit = 64;

    public enum Issue { Invalid, Occupied, Full }

    public static Dictionary<string, Shortcut> Decode(string? raw)
    {
        var result = new Dictionary<string, Shortcut>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (stored is null) return result;
            foreach (var (key, text) in stored)
            {
                var shortcut = Shortcut.Parse(text);
                if (!shortcut.IsEmpty) result[key] = shortcut;
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    public static string Encode(Dictionary<string, Shortcut> shortcuts) =>
        JsonSerializer.Serialize(shortcuts.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value.ToString()));

    /// <summary>A bare key would take it away from every app: a modifier (or an F-key) is required.</summary>
    public static bool IsUsable(Shortcut shortcut) =>
        !shortcut.IsEmpty && (shortcut.Modifiers != Modifiers.None || shortcut.Key is >= 0x70 and <= 0x87);

    /// <summary>Why a combination cannot go on this row, or null when it can.</summary>
    public static (Issue Issue, string? Owner)? AssignmentIssue(Shortcut shortcut, string key, IReadOnlyDictionary<string, Shortcut> shortcuts)
    {
        if (!IsUsable(shortcut)) return (Issue.Invalid, null);
        var owner = KeyFor(shortcut, shortcuts);
        if (owner is not null && owner != key) return (Issue.Occupied, owner);
        return HasRoom(key, shortcuts) ? null : (Issue.Full, null);
    }

    /// <summary>The map after binding (or clearing, with null) one row. A combination already tied to
    /// another row moves: the last thing the person said wins.</summary>
    public static Dictionary<string, Shortcut> Setting(Shortcut? shortcut, string key, IReadOnlyDictionary<string, Shortcut> shortcuts)
    {
        var next = new Dictionary<string, Shortcut>(shortcuts, StringComparer.Ordinal);
        if (shortcut is null || shortcut.Value.IsEmpty)
        {
            next.Remove(key);
            return next;
        }
        foreach (var other in next.Where(p => p.Value == shortcut.Value && p.Key != key).Select(p => p.Key).ToList()) next.Remove(other);
        if (!next.ContainsKey(key) && next.Count >= Limit) return next;
        next[key] = shortcut.Value;
        return next;
    }

    public static bool HasRoom(string key, IReadOnlyDictionary<string, Shortcut> shortcuts) => shortcuts.ContainsKey(key) || shortcuts.Count < Limit;

    public static string? KeyFor(Shortcut shortcut, IReadOnlyDictionary<string, Shortcut> shortcuts) =>
        shortcuts.FirstOrDefault(p => p.Value == shortcut).Key;

    /// <summary>The stable key an app row is filed under: its AUMID / path, independent of where it is listed.</summary>
    public static string AppKey(string appId) => "app." + appId;

    /// <summary>An app's combination puts it away only when it is already in front; otherwise it comes forward.</summary>
    public static bool HidesAppInFront(bool appOwnsForegroundWindow) => appOwnsForegroundWindow;
}
