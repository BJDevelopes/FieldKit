// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>Settings keys of the Command Bar (all prefixed with the feature id).</summary>
public static class CommandBarKeys
{
    public const string FeatureId = "commandBar";

    /// <summary>The global shortcut that opens the bar. Empty (the default, like upstream) means off.</summary>
    public const string Shortcut = "commandBar.shortcut";
    /// <summary>The last shortcut used, restored when the switch is turned back on.</summary>
    public const string LastShortcut = "commandBar.lastShortcut";
    /// <summary>Default when the person first turns the shortcut on. Upstream's Option-Space maps to
    /// Alt+Space, which Windows owns (window menu, PowerToys Run), so Ctrl+Alt+Space.</summary>
    public const string DefaultShortcut = "Ctrl+Alt+Space";

    public const string CompactMode = "commandBar.compactMode";
    public const string EmojiSkinTone = "commandBar.emojiSkinTone";
    public const string DisabledSources = "commandBar.disabledSources";
    public const string Aliases = "commandBar.aliases";
    public const string Pins = "commandBar.pins";
    public const string Hidden = "commandBar.hidden";
    public const string Links = "commandBar.links";
    public const string RowShortcuts = "commandBar.rowShortcuts";
    public const string FileScopes = "commandBar.fileScopes";
    public const string FileIgnores = "commandBar.fileIgnores";
    public const string Usage = "commandBar.usage";
    public const string QueryHabits = "commandBar.queryHabits";
    public const string QueryHabitKey = "commandBar.queryHabitKey";
    public const string Position = "commandBar.position";

    public static bool ShortcutEnabled => !Interop.Shortcut.FromSetting(Shortcut, string.Empty).IsEmpty;

    /// <summary>Turns the global shortcut on (restoring the last one) or off (remembering it).</summary>
    public static void SetShortcutEnabled(bool on)
    {
        if (on)
        {
            if (ShortcutEnabled) return;
            var last = Settings.GetString(LastShortcut, DefaultShortcut);
            Settings.Set(Shortcut, Interop.Shortcut.Parse(last).IsEmpty ? DefaultShortcut : last);
        }
        else
        {
            var current = Settings.GetString(Shortcut, string.Empty);
            if (current.Length > 0) Settings.Set(LastShortcut, current);
            Settings.Set(Shortcut, string.Empty);
        }
    }

    /// <summary>The installation key behind the query-habit digests (created on first use).</summary>
    public static byte[] HabitKey()
    {
        try
        {
            var raw = Settings.GetString(QueryHabitKey, string.Empty);
            if (raw.Length > 0)
            {
                var key = Convert.FromBase64String(raw);
                if (key.Length == 32) return key;
            }
        }
        catch (FormatException)
        {
        }
        var fresh = CommandBarQueryHabits.NewKey();
        Settings.Remove(QueryHabits);
        Settings.Set(QueryHabitKey, Convert.ToBase64String(fresh));
        return fresh;
    }

    public static string ShortcutDisplay => Interop.Shortcut.FromSetting(Shortcut, string.Empty).ToString();
}

/// <summary>Everything the person decided, read once per opening so typing never pays for it.</summary>
public sealed class CommandBarPrefs
{
    public HashSet<CommandBarSource> Disabled { get; init; } = [];
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
    public List<string> Pins { get; init; } = [];
    public HashSet<string> Hidden { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, CommandBarUse> Usage { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, CommandBarUse>> Habits { get; set; } = new(StringComparer.Ordinal);
    public byte[]? HabitKey { get; init; }
    public Dictionary<string, Shortcut> RowShortcuts { get; init; } = new(StringComparer.Ordinal);
    public List<CommandBarLink> Links { get; init; } = [];
    public List<string> FileScopes { get; init; } = [];
    public List<string> FileIgnores { get; init; } = [];
    public bool Compact { get; init; }
    public CommandBarEmoji.SkinTone SkinTone { get; init; }

    /// <summary>Whether learning is written back to settings (off for previews).</summary>
    public bool Persist { get; init; }

    public static CommandBarPrefs Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var savedScopes = CommandBarFileSearchSupport.DecodeList(Settings.GetString(CommandBarKeys.FileScopes, string.Empty));
        var children = System.IO.Directory.Exists(home)
            ? System.IO.Directory.EnumerateDirectories(home).Select(System.IO.Path.GetFileName).OfType<string>()
            : [];
        return new CommandBarPrefs
        {
            Disabled = CommandBarPreferences.DisabledSources(Settings.GetString(CommandBarKeys.DisabledSources, string.Empty)),
            Aliases = CommandBarPreferences.DecodeAliases(Settings.GetString(CommandBarKeys.Aliases, string.Empty)),
            Pins = CommandBarPreferences.DecodePins(Settings.GetString(CommandBarKeys.Pins, string.Empty)),
            Hidden = CommandBarPreferences.DecodeHidden(Settings.GetString(CommandBarKeys.Hidden, string.Empty)),
            Usage = CommandBarUsage.Decode(Settings.GetString(CommandBarKeys.Usage, string.Empty)),
            Habits = CommandBarQueryHabits.Decode(Settings.GetString(CommandBarKeys.QueryHabits, string.Empty)),
            HabitKey = CommandBarKeys.HabitKey(),
            RowShortcuts = CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty)),
            Links = CommandBarLinks.Decode(Settings.GetString(CommandBarKeys.Links, string.Empty)),
            FileScopes = savedScopes.Count == 0 ? [] : CommandBarFileSearchSupport.ResolvedScopes(savedScopes, home, children, System.IO.Directory.Exists),
            FileIgnores = CommandBarFileSearchSupport.DecodeList(Settings.GetString(CommandBarKeys.FileIgnores, string.Empty)),
            Compact = Settings.GetBool(CommandBarKeys.CompactMode, false),
            SkinTone = CommandBarEmoji.ParseTone(Settings.GetString(CommandBarKeys.EmojiSkinTone, string.Empty)),
            Persist = true,
        };
    }
}
