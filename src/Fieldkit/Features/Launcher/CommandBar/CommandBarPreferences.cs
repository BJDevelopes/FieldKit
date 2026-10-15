// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// A kind of result the bar can offer. Storage ids (see <see cref="CommandBarPreferences.StorageId"/>)
/// never change. Upstream's menu commands, clipboard items, snippets, selection, uninstall and kill
/// process sources are not offered as row sources on Windows (their tools are reached through actions).
/// </summary>
public enum CommandBarSource
{
    /// <summary>What Fieldkit itself can do. Always on.</summary>
    Actions,
    Apps,
    Windows,
    QuitApps,
    SettingsPages,
    WindowsSettings,
    Emoji,
    Folders,
    Answers,
    Calculator,
    Links,
    /// <summary>Files found by name in the folders the person named. Last: the one source that has to go and look.</summary>
    Files,
}

/// <summary>Everything the person decides about the bar (port of upstream CommandBarPreferences). Pure.</summary>
public static class CommandBarPreferences
{
    public const string EmojiBrowserRowId = "emoji.browse";

    /// <summary>Rows that open another category: actions that carry the prefix of what they open.</summary>
    public static readonly HashSet<string> ActionBrowseRowIds = [EmojiBrowserRowId];

    public static IReadOnlyList<CommandBarSource> AllSources { get; } = Enum.GetValues<CommandBarSource>();

    public static string StorageId(CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => "actions",
        CommandBarSource.Apps => "apps",
        CommandBarSource.Windows => "windows",
        CommandBarSource.QuitApps => "quitApps",
        CommandBarSource.SettingsPages => "settingsPages",
        CommandBarSource.WindowsSettings => "windowsSettings",
        CommandBarSource.Emoji => "emoji",
        CommandBarSource.Folders => "folders",
        CommandBarSource.Answers => "answers",
        CommandBarSource.Calculator => "calculator",
        CommandBarSource.Links => "links",
        _ => "files",
    };

    public static bool IsAlwaysOn(CommandBarSource source) => source == CommandBarSource.Actions;

    /// <summary>The row id prefix a source owns.</summary>
    public static string? IdPrefix(CommandBarSource source) => source switch
    {
        CommandBarSource.Apps => "app.",
        CommandBarSource.Windows => "window.",
        CommandBarSource.QuitApps => "quit.",
        CommandBarSource.SettingsPages => "settings.",
        CommandBarSource.WindowsSettings => "winsettings.",
        CommandBarSource.Emoji => "emoji.",
        CommandBarSource.Folders => "folder.",
        CommandBarSource.Answers => "answer.",
        CommandBarSource.Links => "link.",
        CommandBarSource.Files => "file.",
        _ => null,
    };

    public static string Glyph(CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => CommandBarGlyphs.Wand,
        CommandBarSource.Apps => CommandBarGlyphs.Apps,
        CommandBarSource.Windows => CommandBarGlyphs.Window,
        CommandBarSource.QuitApps => CommandBarGlyphs.QuitApp,
        CommandBarSource.SettingsPages => Glyphs.Settings,
        CommandBarSource.WindowsSettings => CommandBarGlyphs.WindowsSettings,
        CommandBarSource.Emoji => Glyphs.Smile,
        CommandBarSource.Folders => Glyphs.Folder,
        CommandBarSource.Answers => Glyphs.Info,
        CommandBarSource.Calculator => Glyphs.Calculator,
        CommandBarSource.Links => CommandBarGlyphs.Bookmark,
        _ => CommandBarGlyphs.FileSearch,
    };

    /// <summary>The settings toggle label ("What the bar searches").</summary>
    public static string Title(CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => CommandBarStrings.SourceActions,
        CommandBarSource.Apps => CommandBarStrings.SourceApps,
        CommandBarSource.Windows => CommandBarStrings.SourceWindows,
        CommandBarSource.QuitApps => CommandBarStrings.SourceQuitApps,
        CommandBarSource.SettingsPages => CommandBarStrings.SourceSettingsPages,
        CommandBarSource.WindowsSettings => CommandBarStrings.SourceWindowsSettings,
        CommandBarSource.Emoji => CommandBarStrings.SourceEmoji,
        CommandBarSource.Folders => CommandBarStrings.SourceFolders,
        CommandBarSource.Answers => CommandBarStrings.SourceAnswers,
        CommandBarSource.Calculator => CommandBarStrings.SourceCalculator,
        CommandBarSource.Links => CommandBarStrings.LinksTitle,
        _ => CommandBarStrings.SourceFiles,
    };

    // ------------------------------------------------------------- sources

    public static HashSet<CommandBarSource> DisabledSources(string? raw)
    {
        var set = new HashSet<CommandBarSource>();
        if (string.IsNullOrEmpty(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var source = AllSources.FirstOrDefault(s => StorageId(s) == part, CommandBarSource.Actions);
            if (StorageId(source) == part && !IsAlwaysOn(source)) set.Add(source);
        }
        return set;
    }

    /// <summary>Sorted so the same set always writes the same string.</summary>
    public static string StorageValue(IEnumerable<CommandBarSource> sources) =>
        string.Join(',', sources.Where(s => !IsAlwaysOn(s)).Select(StorageId).Distinct().OrderBy(s => s, StringComparer.Ordinal));

    public static bool IsEnabled(CommandBarSource source, IReadOnlySet<CommandBarSource> disabled) =>
        IsAlwaysOn(source) || !disabled.Contains(source);

    /// <summary>The source a row belongs to, by its id; rows without a prefix are actions.</summary>
    public static CommandBarSource SourceOf(string rowId)
    {
        foreach (var source in AllSources)
        {
            if (IdPrefix(source) is { } prefix && rowId.StartsWith(prefix, StringComparison.Ordinal)) return source;
        }
        return CommandBarSource.Actions;
    }

    public static bool IsActionRow(string id) => SourceOf(id) == CommandBarSource.Actions || ActionBrowseRowIds.Contains(id);

    public static bool IsActionRow(string id, IReadOnlySet<CommandBarSource> disabled) =>
        IsActionRow(id) && IsEnabled(SourceOf(id), disabled);

    /// <summary>What a kind of row is worth before a letter of it is read.</summary>
    public static int RankBias(CommandBarSource source) => source switch
    {
        CommandBarSource.Files or CommandBarSource.SettingsPages => -40,
        CommandBarSource.Apps => 80,
        _ => 0,
    };

    // ------------------------------------------------------------- emoji

    public static string EmojiRowId(string identity) => "emoji." + identity;

    public static string? EmojiIdentity(string rowId) =>
        rowId.StartsWith("emoji.", StringComparison.Ordinal) && rowId.Length > 6 && rowId != EmojiBrowserRowId ? rowId[6..] : null;

    // ------------------------------------------------------------- aliases

    public static Dictionary<string, string> DecodeAliases(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (map is null) return result;
            foreach (var (k, v) in map)
            {
                var clean = v?.Trim() ?? string.Empty;
                if (clean.Length > 0) result[k] = clean;
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    public static string EncodeAliases(Dictionary<string, string> aliases) => JsonSerializer.Serialize(aliases);

    /// <summary>Only rows whose id survives may carry a name (not windows or found files).</summary>
    public static bool AcceptsAlias(string rowId) => SourceOf(rowId) is not (CommandBarSource.Windows or CommandBarSource.Files);

    public static Dictionary<string, string> SettingAlias(string alias, string rowId, Dictionary<string, string> aliases)
    {
        var next = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        var clean = alias.Trim();
        if (clean.Length == 0) next.Remove(rowId);
        else next[rowId] = clean.Length > 60 ? clean[..60] : clean;
        return next;
    }

    public const int AliasExact = 2400;
    public const int AliasPrefix = 1100;

    /// <summary>How well what was typed names this alias (exact word or prefix of a word).</summary>
    public static int? AliasHit(string alias, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return null;
        int? best = null;
        foreach (var word in CommandBarSearch.Normalize(alias).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == normalizedQuery) return AliasExact;
            if (word.StartsWith(normalizedQuery, StringComparison.Ordinal)) best = AliasPrefix;
        }
        return best;
    }

    /// <summary>Another row already answering to one of these words (never steal a name silently).</summary>
    public static string? RowUsingAlias(string alias, Dictionary<string, string> aliases, string excluding)
    {
        var wanted = CommandBarSearch.Normalize(alias).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return null;
        foreach (var (key, value) in aliases)
        {
            if (key == excluding) continue;
            if (CommandBarSearch.Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(wanted.Contains)) return key;
        }
        return null;
    }

    // ------------------------------------------------------------- pins

    /// <summary>A pin only means something on a row that can lead the empty bar.</summary>
    public static bool AcceptsPin(string rowId) =>
        SourceOf(rowId) is not (CommandBarSource.QuitApps or CommandBarSource.Emoji or CommandBarSource.Files);

    public static List<string> DecodePins(string? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (raw ?? string.Empty).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && seen.Add(s)).ToList();
    }

    public static string EncodePins(IEnumerable<string> pins) => string.Join('\n', pins);

    /// <summary>Pinning toggles; new pins go last; at most 30.</summary>
    public static List<string> TogglingPin(string rowId, IReadOnlyList<string> pins)
    {
        var next = pins.ToList();
        if (next.Remove(rowId)) return next;
        next.Add(rowId);
        if (next.Count > 30) next.RemoveRange(0, next.Count - 30);
        return next;
    }

    /// <summary>A pin breaks ties between equally good matches; it never jumps a better one.</summary>
    public const int PinTieBreak = 60;

    public static List<string> LeadingPins(IEnumerable<string> pins, IReadOnlySet<string> available) => pins.Where(available.Contains).ToList();

    /// <summary>Settings lists pins still present, plus apps/folders that are gone (so they can be removed);
    /// pins of uninstalled features are dropped.</summary>
    public static List<string> ListedPins(IEnumerable<string> pins, IReadOnlySet<string> present) =>
        pins.Where(p => present.Contains(p) || !IsHubOwned(p)).ToList();

    private static bool IsHubOwned(string rowId) => SourceOf(rowId) is CommandBarSource.Actions or CommandBarSource.SettingsPages;

    // ------------------------------------------------------------- hidden

    public static HashSet<string> DecodeHidden(string? raw) =>
        (raw ?? string.Empty).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);

    public static string EncodeHidden(IEnumerable<string> hidden) => string.Join('\n', hidden.OrderBy(s => s, StringComparer.Ordinal));

    public static HashSet<string> TogglingHidden(string key, IReadOnlySet<string> hidden)
    {
        var next = hidden.ToHashSet(StringComparer.Ordinal);
        if (!next.Remove(key)) next.Add(key);
        return next;
    }

    // ------------------------------------------------------------- position

    /// <summary>How far the bar was dragged from its default spot ("x,y" in device-independent pixels).</summary>
    public static Vector DecodePositionOffset(string? raw)
    {
        var parts = (raw ?? string.Empty).Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(y))
            return new Vector(0, 0);
        return new Vector(x, y);
    }

    public static string EncodePositionOffset(Vector offset)
    {
        var x = (int)Math.Round(offset.X);
        var y = (int)Math.Round(offset.Y);
        return x == 0 && y == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{x},{y}");
    }

    /// <summary>Where the bar opens: centred, its top at 28% of the work area, moved by the person's offset,
    /// kept 16 px inside the screen (height is the tallest the bar can grow).</summary>
    public static Point ClampedOrigin(Size size, Rect workArea, Vector offset)
    {
        const double margin = 16;
        var wantedX = workArea.Left + (workArea.Width - size.Width) / 2 + offset.X;
        var wantedY = workArea.Top + workArea.Height * 0.28 + offset.Y;
        var minX = workArea.Left + margin;
        var maxX = Math.Max(minX, workArea.Right - size.Width - margin);
        var minY = workArea.Top + margin;
        var maxY = Math.Max(minY, workArea.Bottom - size.Height - margin);
        return new Point(Math.Clamp(wantedX, minX, maxX), Math.Clamp(wantedY, minY, maxY));
    }
}
