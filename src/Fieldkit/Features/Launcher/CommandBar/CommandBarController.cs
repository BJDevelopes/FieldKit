// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>What the field is asking for right now.</summary>
public enum CommandBarMode
{
    Search,
    /// <summary>A command waiting for its number ("volume" → 0 to 100).</summary>
    Argument,
    /// <summary>A destructive command waiting for Enter.</summary>
    Confirm,
    /// <summary>The list of things that can be done to one row (Ctrl+K).</summary>
    Actions,
    /// <summary>The field is the name a row answers to.</summary>
    Naming,
    /// <summary>Waiting for the combination one row should answer to.</summary>
    CapturingShortcut,
}

/// <summary>One line of the actions list.</summary>
public sealed record CommandBarRowAction(string Id, string Title, string Glyph, Action Run, bool IsDestructive = false);

/// <summary>What the controller needs from whoever shows it (the floating window, or nothing for previews).</summary>
public interface ICommandBarHost
{
    /// <summary>Closes the bar.</summary>
    void Hide();

    /// <summary>The window that had focus before the bar opened (for typing at the caret).</summary>
    IntPtr PreviousWindow { get; }

    /// <summary>Opens a Fieldkit settings page.</summary>
    void OpenSettings(string pageId);

    /// <summary>Called when a row's own shortcut changed so global hotkeys are re-registered.</summary>
    void RowShortcutsChanged();

    /// <summary>Suspends (true) or restores (false) the bar's own global hotkeys while a combination is recorded.</summary>
    void SuspendHotkeys(bool suspend);
}

/// <summary>
/// The bar's state machine and ranking pipeline (port of upstream CommandBarService's search, browse,
/// run, actions, naming and shortcut-capture logic). Pure UI-thread object without a window: the view
/// renders it and the service feeds it rows.
/// </summary>
public sealed class CommandBarController
{
    private static readonly CommandBarSource[] ChipOrder =
    [
        CommandBarSource.Actions, CommandBarSource.Apps, CommandBarSource.Windows, CommandBarSource.SettingsPages,
        CommandBarSource.WindowsSettings, CommandBarSource.Emoji, CommandBarSource.Folders, CommandBarSource.Links,
    ];



    private static readonly (string Prefix, int Limit)[] KindLimits =
    [
        ("app.", 5), ("window.", 4), ("quit.", 3), ("emoji.", 6), ("settings.", 4), ("winsettings.", 4), ("file.", 4), ("toggle.", 5),
    ];

    private const int BrowseGroupLimit = 12;
    private const int ResultLimit = 12;

    private readonly ICommandBarHost _host;
    private readonly CommandBarQueryMemory _memory = new();
    private int _memoryStep;
    private string? _lastRankedQuery;
    private string? _selectedId;
    private string _savedQuery = string.Empty;
    private string? _queryBeforeCompletion;
    private string? _completedQuery;
    private bool _peeking;
    private Dictionary<string, CommandBarEntry> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, (string Title, string Keywords)> _folded = new(StringComparer.Ordinal);

    public CommandBarController(ICommandBarHost host, CommandBarPrefs? prefs = null)
    {
        _host = host;
        Prefs = prefs ?? new CommandBarPrefs();
        ScriptRunner.ResultReady += Refresh;
        FileSearch.ResultReady += Refresh;
    }

    // ------------------------------------------------------------------ data the service provides

    public CommandBarPrefs Prefs { get; private set; }
    public List<CommandBarEntry> Catalog { get; private set; } = [];
    public List<CommandBarEntry> AppEntries { get; private set; } = [];
    public List<CommandBarEntry> WindowEntries { get; private set; } = [];
    public List<CommandBarEntry> QuitEntries { get; private set; } = [];
    public List<CommandBarEntry> WindowsSettingsEntries { get; private set; } = [];
    public List<CommandBarEntry> EmojiEntries { get; private set; } = [];
    public bool AppsLoading { get; set; }

    /// <summary>Builds a file row (the catalog knows how to open and reveal it).</summary>
    public Func<string, CommandBarEntry>? FileEntryFactory { get; set; }

    /// <summary>Builds a script's answer row (null output: still running).</summary>
    public Func<CommandBarLink, string?, CommandBarEntry>? ScriptAnswerFactory { get; set; }

    /// <summary>Ids worth discovering before any habit forms (the catalog picks them).</summary>
    public List<string> Curated { get; set; } = [];

    /// <summary>Builds the calculator / conversion / colour / date answer for a query.</summary>
    public Func<string, CommandBarEntry?>? AnswerFactory { get; set; }

    /// <summary>Builds the "open this address" row.</summary>
    public Func<string, CommandBarEntry?>? OpenUrlFactory { get; set; }

    public CommandBarScriptRunner ScriptRunner { get; } = new();
    public CommandBarFileSearch FileSearch { get; } = new();

    // ------------------------------------------------------------------ state the view reads

    public string Query { get; private set; } = string.Empty;
    public CommandBarMode Mode { get; private set; } = CommandBarMode.Search;
    public string? ModeEntryId { get; private set; }
    public List<CommandBarEntry> Rows { get; private set; } = [];
    public Dictionary<int, string> SectionTitles { get; private set; } = [];
    public int SelectedIndex { get; private set; }
    public CommandBarSource? ActiveCategory { get; private set; }
    public List<CommandBarSource> CategoryChips { get; private set; } = [];
    public bool IsShowingSuggestions { get; private set; }
    public bool IsCompactHome { get; private set; }
    public int ActionIndex { get; private set; }
    public string? AliasWarning { get; private set; }
    public bool ControlHeld { get; private set; }
    public bool AwaitsAnswers => FileSearch.IsAwaiting || ScriptRunner.IsAwaiting;

    /// <summary>What was typed when the last row ran (closing the bar wipes the field).</summary>
    public string QueryWhenRun { get; private set; } = string.Empty;

    /// <summary>Raised whenever anything the view shows changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the controller replaced the field's text (prefill, Tab, naming).</summary>
    public event Action<string>? QueryReplaced;

    public CommandBarEntry? SelectedEntry => SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    public CommandBarEntry? Entry(string? id) =>
        id is null ? null : _byId.TryGetValue(id, out var e) ? e : Rows.FirstOrDefault(r => r.Id == id);

    // ------------------------------------------------------------------ feeding

    /// <summary>Replaces the data the bar searches and rebuilds the index.</summary>
    public void SetData(CommandBarPrefs prefs, List<CommandBarEntry>? catalog = null, List<CommandBarEntry>? apps = null,
        List<CommandBarEntry>? windows = null, List<CommandBarEntry>? quit = null, List<CommandBarEntry>? windowsSettings = null,
        List<CommandBarEntry>? emoji = null)
    {
        Prefs = prefs;
        if (catalog is not null) Catalog = catalog;
        if (apps is not null) AppEntries = apps;
        if (windows is not null) WindowEntries = windows;
        if (quit is not null) QuitEntries = quit;
        if (windowsSettings is not null) WindowsSettingsEntries = windowsSettings;
        if (emoji is not null) EmojiEntries = emoji;
        Index();
        Refresh();
    }

    private void Index()
    {
        var byId = new Dictionary<string, CommandBarEntry>(StringComparer.Ordinal);
        var folded = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var e in Catalog.Concat(AppEntries).Concat(WindowEntries).Concat(QuitEntries).Concat(WindowsSettingsEntries).Concat(EmojiEntries))
        {
            if (!byId.TryAdd(e.Id, e)) continue;
            folded[e.Id] = (CommandBarSearch.Normalize(e.MatchTitle ?? e.Title), CommandBarSearch.Normalize(e.Keywords));
        }
        _byId = byId;
        _folded = folded;
    }

    /// <summary>Back to a fresh, empty bar (called when it closes).</summary>
    public void Reset()
    {
        Query = string.Empty;
        Mode = CommandBarMode.Search;
        ModeEntryId = null;
        ActiveCategory = null;
        _peeking = false;
        _selectedId = null;
        _lastRankedQuery = null;
        _queryBeforeCompletion = null;
        _completedQuery = null;
        AliasWarning = null;
        ControlHeld = false;
        ScriptRunner.Reset();
        FileSearch.Reset();
        Rows = [];
        SectionTitles = [];
        QueryReplaced?.Invoke(string.Empty);
    }

    /// <summary>Drops everything the bar holds (the feature was switched off).</summary>
    public void Release()
    {
        Reset();
        Catalog = [];
        AppEntries = [];
        WindowEntries = [];
        QuitEntries = [];
        WindowsSettingsEntries = [];
        EmojiEntries = [];
        _byId = new(StringComparer.Ordinal);
        _folded = new(StringComparer.Ordinal);
        _memory.Clear();
    }

    // ------------------------------------------------------------------ typing

    public void SetQuery(string text)
    {
        if (text == Query) return;
        Query = text;
        if (Mode == CommandBarMode.Confirm)
        {
            // Typing takes a confirmation down: a destructive Enter must not stay armed.
            Mode = CommandBarMode.Search;
            ModeEntryId = null;
        }
        if (Mode == CommandBarMode.Naming) AliasWarning = null;
        if (Mode is CommandBarMode.Search)
        {
            if (!(_queryBeforeCompletion is not null && text == _completedQuery))
            {
                _queryBeforeCompletion = null;
                _completedQuery = null;
            }
        }
        Refresh();
    }

    private void ReplaceQuery(string text)
    {
        Query = text;
        QueryReplaced?.Invoke(text);
    }

    /// <summary>Puts text in the field (a saved search with nothing to search for yet).</summary>
    public void Prefill(string text)
    {
        Mode = CommandBarMode.Search;
        ModeEntryId = null;
        ReplaceQuery(text);
        Refresh();
    }

    public void SetControlHeld(bool held)
    {
        if (held == ControlHeld) return;
        ControlHeld = held;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the pipeline

    public void Refresh()
    {
        if (Mode == CommandBarMode.Search)
        {
            var trimmed = Query.Trim();
            if (trimmed.Length == 0)
            {
                var browse = !Prefs.Compact || ActiveCategory is not null || _peeking;
                CategoryChips = browse ? ChipOrder.Where(s => CommandBarPreferences.IsEnabled(s, Prefs.Disabled) && CategoryHasContent(s)).ToList() : [];
                if (ActiveCategory is { } category)
                {
                    var content = Uniqued(CategoryContent(category));
                    var byId = content.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    var usage = Prefs.Usage.Where(p => byId.TryGetValue(p.Key, out var e) && e.CountsUsage)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    Rows = CommandBarUsage.CategoryIds(usage, content.Select(e => e.Id).ToList()).Select(id => byId[id]).ToList();
                    SectionTitles = Rows.Count == 0 ? [] : new Dictionary<int, string> { [0] = CategoryHeading(category, Rows.Count) };
                }
                else if (browse)
                {
                    (Rows, SectionTitles) = SuggestionRows();
                }
                else
                {
                    Rows = [];
                    SectionTitles = [];
                }
                IsShowingSuggestions = Rows.Count > 0;
            }
            else
            {
                Rows = Uniqued(SearchRows(trimmed));
                SectionTitles = ActiveCategory is { } c ? new Dictionary<int, string> { [0] = CategoryHeading(c, Rows.Count) } : [];
                IsShowingSuggestions = false;
            }
            // Typing always lands on the best match; a background load never moves the selection.
            var queryChanged = trimmed != _lastRankedQuery;
            _lastRankedQuery = trimmed;
            if (queryChanged) SelectedIndex = 0;
            else if (_selectedId is not null && Rows.FindIndex(r => r.Id == _selectedId) is var keep and >= 0) SelectedIndex = keep;
            else SelectedIndex = 0;
            _selectedId = SelectedIndex < Rows.Count ? Rows[SelectedIndex].Id : null;
            IsCompactHome = Prefs.Compact && ActiveCategory is null && !_peeking && trimmed.Length == 0;
        }
        else
        {
            IsCompactHome = false;
        }
        Changed?.Invoke();
    }

    private static List<CommandBarEntry> Uniqued(List<CommandBarEntry> rows)
    {
        var keep = CommandBarSearch.FirstOccurrences(rows.Select(r => r.Id).ToList());
        return keep.Count == rows.Count ? rows : keep.Select(i => rows[i]).ToList();
    }

    private bool Allowed(CommandBarEntry e) => CommandBarPreferences.IsEnabled(CommandBarPreferences.SourceOf(e.Id), Prefs.Disabled);

    private bool CategoryHasContent(CommandBarSource source) => source switch
    {
        CommandBarSource.Apps or CommandBarSource.WindowsSettings => true,
        CommandBarSource.Windows => WindowEntries.Count > 0,
        CommandBarSource.Emoji => EmojiEntries.Any(e => !Prefs.Hidden.Contains(e.StableKey)),
        CommandBarSource.Actions => Catalog.Any(e => CommandBarPreferences.IsActionRow(e.Id, Prefs.Disabled) && !Prefs.Hidden.Contains(e.StableKey)),
        CommandBarSource.SettingsPages or CommandBarSource.Folders or CommandBarSource.Links =>
            Catalog.Any(e => CommandBarPreferences.SourceOf(e.Id) == source && !Prefs.Hidden.Contains(e.StableKey)),
        _ => false,
    };

    /// <summary>Every row of one kind (the drill-in): no caps, no guesses.</summary>
    private List<CommandBarEntry> CategoryContent(CommandBarSource source)
    {
        IEnumerable<CommandBarEntry> rows = source switch
        {
            CommandBarSource.Actions => Catalog.Where(e => CommandBarPreferences.IsActionRow(e.Id, Prefs.Disabled)),
            CommandBarSource.Apps => AppEntries,
            CommandBarSource.WindowsSettings => WindowsSettingsEntries,
            CommandBarSource.Windows => WindowEntries,
            CommandBarSource.Emoji => EmojiEntries,
            CommandBarSource.SettingsPages or CommandBarSource.Folders or CommandBarSource.Links =>
                Catalog.Where(e => CommandBarPreferences.SourceOf(e.Id) == source),
            _ => [],
        };
        return rows.Where(e => !Prefs.Hidden.Contains(e.StableKey)).ToList();
    }

    public static string CategoryTitle(CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => CommandBarStrings.SourceActions,
        CommandBarSource.Apps => CommandBarStrings.SourceApps,
        CommandBarSource.Windows => CommandBarStrings.SourceWindows,
        CommandBarSource.SettingsPages => CommandBarStrings.SourceSettingsPages,
        CommandBarSource.WindowsSettings => "Windows Settings",
        CommandBarSource.Emoji => CommandBarStrings.SourceEmoji,
        CommandBarSource.Folders => CommandBarStrings.SourceFolders,
        CommandBarSource.Links => CommandBarStrings.LinksTitle,
        _ => CommandBarPreferences.Title(source),
    };

    private static string CategoryHeading(CommandBarSource source, int count) => CategoryTitle(source) + " · " + count;

    /// <summary>The empty bar: pins, then suggestions (habit, then curated), then everything by area, capped per group.</summary>
    private (List<CommandBarEntry>, Dictionary<int, string>) SuggestionRows()
    {
        var rows = new List<CommandBarEntry>();
        var titles = new Dictionary<int, string>();
        var offerable = Catalog.Concat(AppEntries).Concat(WindowsSettingsEntries).Concat(WindowEntries)
            .Where(e => !Prefs.Hidden.Contains(e.StableKey) && Allowed(e)).ToList();
        var byKey = new Dictionary<string, CommandBarEntry>(StringComparer.Ordinal);
        foreach (var e in offerable) byKey.TryAdd(e.StableKey, e);
        var pinned = CommandBarPreferences.LeadingPins(Prefs.Pins, byKey.Keys.ToHashSet()).Select(k => byKey[k]).ToList();
        if (pinned.Count > 0)
        {
            titles[rows.Count] = CommandBarStrings.PinnedTitle;
            rows.AddRange(pinned);
        }
        var pinnedIds = pinned.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ids = CommandBarUsage.SuggestionIds(Prefs.Usage, offerable.Select(e => e.Id).Where(id => !pinnedIds.Contains(id)),
            Curated, Math.Max(0, 7 - pinned.Count));
        var suggestions = ids.Select(id => _byId.TryGetValue(id, out var e) ? e : null).OfType<CommandBarEntry>().ToList();
        if (suggestions.Count > 0)
        {
            titles[rows.Count] = CommandBarStrings.SuggestionsLabel;
            rows.AddRange(suggestions);
        }
        // Then everything else the app can do, grouped by where it lives (apps and windows stay out:
        // hundreds of them would bury the commands, and they are what search is for).
        var shown = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        var groups = new Dictionary<string, List<CommandBarEntry>>(StringComparer.Ordinal);
        foreach (var e in Catalog)
        {
            if (shown.Contains(e.Id) || Prefs.Hidden.Contains(e.StableKey) || !Allowed(e)) continue;
            shown.Add(e.Id);
            var group = BrowseGroup(e);
            if (!groups.TryGetValue(group, out var members))
            {
                groups[group] = members = [];
                order.Add(group);
            }
            members.Add(e.Subtitle == group ? e with { Subtitle = string.Empty } : e);
        }
        foreach (var group in order)
        {
            var members = groups[group];
            if (members.Count == 0) continue;
            titles[rows.Count] = group;
            rows.AddRange(members.Take(BrowseGroupLimit));
        }
        return (rows, titles);
    }

    private static string BrowseGroup(CommandBarEntry e) => CommandBarPreferences.SourceOf(e.Id) switch
    {
        CommandBarSource.Answers => CommandBarStrings.KindAnswer,
        CommandBarSource.Links => CommandBarStrings.KindLink,
        CommandBarSource.Folders => CommandBarStrings.KindFolder,
        _ => e.Subtitle.Length == 0 ? CommandBarStrings.EverythingTitle : e.Subtitle,
    };

    private string RankingTitle(CommandBarEntry e, string query)
    {
        if (!e.TakesArgument) return _folded.TryGetValue(e.Id, out var f) ? f.Title : CommandBarSearch.Normalize(e.MatchTitle ?? e.Title);
        return CommandBarSearch.Normalize(CommandBarLinks.RankingTitle(e.Title, query));
    }

    private string FoldedKeywords(CommandBarEntry e) => _folded.TryGetValue(e.Id, out var f) ? f.Keywords : CommandBarSearch.Normalize(e.Keywords);

    private bool NumericCommandMatches(string text) =>
        text.Length > 0 && Catalog.Any(e => e.NumericRange is not null && CommandBarSearch.Matches(e.Title, e.Keywords, text));

    private List<CommandBarEntry> SearchRows(string trimmed)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        // Inside a category: only that category, ranked, no answers or caps.
        if (ActiveCategory is { } category)
        {
            FileSearch.CancelPending();
            var categoryPool = CategoryContent(category);
            var habit = CommandBarQueryHabits.Prepare(trimmed, Prefs.HabitKey);
            var candidates = categoryPool.Select((e, i) => new CommandBarCandidate(i, RankingTitle(e, trimmed), FoldedKeywords(e),
                e.CountsUsage ? CommandBarQueryHabits.Boost(e.Id, habit, Prefs.Habits, now) : 0)).ToList();
            return CommandBarSearch.RankedIndexes(candidates, trimmed).Take(40).Select(i => categoryPool[i]).ToList();
        }

        // Emoji only join a search when a leading colon asks for them.
        if (CommandBarSearch.EmojiQuery(trimmed) is { } emojiQuery)
        {
            FileSearch.CancelPending();
            if (!CommandBarPreferences.IsEnabled(CommandBarSource.Emoji, Prefs.Disabled)) return [];
            var emojiPool = CategoryContent(CommandBarSource.Emoji);
            if (emojiQuery.Length == 0) return emojiPool.Take(40).ToList();
            var habit = CommandBarQueryHabits.Prepare(emojiQuery, Prefs.HabitKey);
            var candidates = emojiPool.Select((e, i) => new CommandBarCandidate(i, RankingTitle(e, emojiQuery), FoldedKeywords(e),
                CommandBarQueryHabits.Boost(e.Id, habit, Prefs.Habits, now))).ToList();
            return CommandBarSearch.RankedIndexes(candidates, emojiQuery).Take(40).Select(i => emojiPool[i]).ToList();
        }

        // A sum is answered, not searched: the result leads.
        var answer = CommandBarPreferences.IsEnabled(CommandBarSource.Calculator, Prefs.Disabled) ? AnswerFactory?.Invoke(trimmed) : null;
        var openUrl = OpenUrlFactory?.Invoke(trimmed);

        // A saved script answers once it has run (on Enter).
        var scriptMatch = CommandBarPreferences.IsEnabled(CommandBarSource.Links, Prefs.Disabled)
            ? CommandBarLinks.MatchingScriptLink(Prefs.Links, trimmed)
            : null;
        CommandBarEntry? scriptAnswer = null;
        if (scriptMatch is { } sm && !Prefs.Hidden.Contains("link." + sm.Link.Id))
        {
            if (ScriptRunner.Cached(sm.Link.Id, sm.Argument) is { } output) scriptAnswer = ScriptAnswerFactory?.Invoke(sm.Link, output);
            else if (ScriptRunner.IsRunning(sm.Link.Id, sm.Argument)) scriptAnswer = ScriptAnswerFactory?.Invoke(sm.Link, null);
        }

        // Files, once the person named a folder to look in.
        var fileRows = new List<CommandBarEntry>();
        if (CommandBarPreferences.IsEnabled(CommandBarSource.Files, Prefs.Disabled) && Prefs.FileScopes.Count > 0)
        {
            if (FileSearch.Cached(trimmed) is { } paths)
            {
                FileSearch.CancelPending();
                if (FileEntryFactory is not null) fileRows.AddRange(paths.Select(FileEntryFactory));
            }
            else
            {
                FileSearch.Schedule(trimmed, Prefs.FileScopes, Prefs.FileIgnores);
            }
        }
        else
        {
            FileSearch.CancelPending();
        }

        // "volume 40" is a command with a value; "code 1234" is a search.
        var split = CommandBarSearch.SplitTrailingNumber(trimmed);
        var effective = split.Number is not null && NumericCommandMatches(split.Text) ? split.Text : trimmed;

        var pool2 = Catalog.Concat(AppEntries).Concat(WindowsSettingsEntries).Concat(WindowEntries).ToList();
        if (scriptMatch is { } match)
        {
            var winner = "link." + match.Link.Id;
            var matching = CommandBarLinks.MatchingScriptLinks(Prefs.Links, trimmed).Select(l => "link." + l.Id).ToHashSet(StringComparer.Ordinal);
            pool2.RemoveAll(e => matching.Contains(e.Id) && (scriptAnswer is not null || e.Id != winner));
        }
        // Quitting is a rare, heavy verb: those rows join only when asked for.
        if (CommandBarSearch.MatchesVerb(trimmed, CommandBarStrings.QuitFormat)) pool2.AddRange(QuitEntries);
        pool2.AddRange(fileRows);

        var pool = new List<CommandBarEntry>(pool2.Count);
        var sources = new List<CommandBarSource>(pool2.Count);
        foreach (var e in pool2)
        {
            if (Prefs.Hidden.Contains(e.StableKey)) continue;
            var source = CommandBarPreferences.SourceOf(e.Id);
            if (!CommandBarPreferences.IsEnabled(source, Prefs.Disabled)) continue;
            pool.Add(e);
            sources.Add(source);
        }

        var foldedQuery = CommandBarSearch.Normalize(effective);
        var habitQuery = CommandBarQueryHabits.Prepare(effective, Prefs.HabitKey);
        var pins = Prefs.Pins.ToHashSet(StringComparer.Ordinal);
        var candidatesAll = new List<CommandBarCandidate>(pool.Count);
        for (var i = 0; i < pool.Count; i++)
        {
            var e = pool[i];
            var aliasBoost = Prefs.Aliases.TryGetValue(e.StableKey, out var alias) ? CommandBarPreferences.AliasHit(alias, foldedQuery) ?? 0 : 0;
            var habitPriority = e.CountsUsage ? CommandBarQueryHabits.Boost(e.Id, habitQuery, Prefs.Habits, now) : 0;
            var boost = (e.CountsUsage ? CommandBarUsage.Boost(Prefs.Usage.GetValueOrDefault(e.Id), now) : 0)
                        + (e.IsActive ? 20 : 0)
                        + aliasBoost
                        + (e.CountsUsage ? _memory.BoostNormalized(foldedQuery, e.Id) : 0)
                        + CommandBarPreferences.RankBias(sources[i])
                        + (pins.Contains(e.StableKey) ? CommandBarPreferences.PinTieBreak : 0);
            // An alias makes the row match even when no word of its title does.
            var title = RankingTitle(e, effective);
            var keywords = FoldedKeywords(e);
            if (aliasBoost > 0) keywords = keywords.Length == 0 ? CommandBarSearch.Normalize(alias) : keywords + " " + CommandBarSearch.Normalize(alias);
            candidatesAll.Add(new CommandBarCandidate(i, title, keywords, Math.Max(aliasBoost, habitPriority), boost));
        }
        var ranked = CommandBarSearch.RankedIndexes(candidatesAll, effective);

        // A fact about this PC only shows when asked for by name.
        var firstToken = foldedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var colorPreview = answer?.Id == "color.preview" ? answer : null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<CommandBarEntry>();
        if (answer is not null && colorPreview is null) result.Add(answer);
        if (openUrl is not null) result.Add(openUrl);
        if (scriptAnswer is not null) result.Add(scriptAnswer);
        foreach (var index in ranked)
        {
            var e = pool[index];
            if (e.Id.StartsWith("answer.", StringComparison.Ordinal)
                && (firstToken.Length < 3 || !CommandBarSearch.Normalize(e.Title).StartsWith(firstToken, StringComparison.Ordinal)))
                continue;
            var kind = KindLimits.FirstOrDefault(k => e.Id.StartsWith(k.Prefix, StringComparison.Ordinal));
            if (kind.Prefix is not null)
            {
                var used = counts.GetValueOrDefault(kind.Prefix);
                if (used >= kind.Limit) continue;
                counts[kind.Prefix] = used + 1;
            }
            result.Add(e);
            if (result.Count >= ResultLimit) break;
        }
        if (colorPreview is not null)
        {
            result.Insert(CommandBarSearch.ColorPreviewIndex(result.Select(r => r.Title), trimmed), colorPreview);
            if (result.Count > ResultLimit) result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    // ------------------------------------------------------------------ navigation

    public void MoveSelection(int delta)
    {
        if (Mode == CommandBarMode.Actions)
        {
            var count = ActionRows().Count;
            if (count == 0) return;
            ActionIndex = ((ActionIndex + delta) % count + count) % count;
            Changed?.Invoke();
            return;
        }
        if (Rows.Count == 0) return;
        Select(((SelectedIndex + delta) % Rows.Count + Rows.Count) % Rows.Count);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Rows.Count || index == SelectedIndex && _selectedId == Rows[index].Id) return;
        SelectedIndex = index;
        _selectedId = Rows[index].Id;
        Changed?.Invoke();
    }

    /// <summary>Down on a collapsed (compact) field shows the list for the rest of this opening.</summary>
    public bool PeekHome()
    {
        if (Mode != CommandBarMode.Search || !IsCompactHome || _peeking) return false;
        _peeking = true;
        Refresh();
        return true;
    }

    /// <summary>Walks the chips with Left/Right, only while the field is empty.</summary>
    public bool MoveCategory(int delta)
    {
        if (Mode != CommandBarMode.Search || Query.Trim().Length > 0 || CategoryChips.Count == 0) return false;
        var stops = new List<CommandBarSource?> { null };
        stops.AddRange(CategoryChips.Select(c => (CommandBarSource?)c));
        var current = Math.Max(0, stops.IndexOf(ActiveCategory));
        SetCategory(stops[((current + delta) % stops.Count + stops.Count) % stops.Count]);
        return true;
    }

    public void SetCategory(CommandBarSource? source)
    {
        if (ActiveCategory == source) return;
        ActiveCategory = source;
        _selectedId = null;
        _lastRankedQuery = null;
        Refresh();
    }

    /// <summary>Back to the unfiltered bar (the empty state's way out).</summary>
    public void GoHome()
    {
        if (Query.Length > 0) ReplaceQuery(string.Empty);
        ActiveCategory = null;
        _lastRankedQuery = null;
        Refresh();
    }

    /// <summary>Tab: reuse a calculator answer, or complete the selected title.</summary>
    public void CompleteSelection()
    {
        if (Mode != CommandBarMode.Search || SelectedEntry is not { } entry) return;
        if (entry.Id == "math.result" && CommandBarMath.Evaluate(Query) is { } result)
        {
            ReplaceQuery(CommandBarMath.ReusableExpression(result));
            Refresh();
            return;
        }
        if (entry.IsAnswer) return;
        _queryBeforeCompletion ??= Query;
        var completion = CommandBarSearch.EmojiQuery(Query) is not null ? ":" + (entry.MatchTitle ?? entry.Title) : entry.MatchTitle ?? entry.Title;
        _completedQuery = completion;
        ReplaceQuery(completion);
        Refresh();
    }

    // ------------------------------------------------------------------ running

    public void RunSelected()
    {
        switch (Mode)
        {
            case CommandBarMode.Actions:
            {
                var actions = ActionRows();
                if (ActionIndex >= 0 && ActionIndex < actions.Count) actions[ActionIndex].Run();
                return;
            }
            case CommandBarMode.Naming:
                CommitName();
                return;
            case CommandBarMode.CapturingShortcut:
                return;
            case CommandBarMode.Confirm:
                if (Entry(ModeEntryId) is { } confirmed) Finish(confirmed, null);
                return;
            case CommandBarMode.Argument:
            {
                if (Entry(ModeEntryId) is not { NumericRange: { } range } entry
                    || CommandBarSearch.ArgumentValue(Query, range.Min, range.Max) is not { } value)
                {
                    CommandBarSystem.Beep();
                    return;
                }
                Finish(entry, value);
                return;
            }
            default:
                if (SelectedEntry is { } selected) Run(selected);
                else if (Query.Length > 0) GoHome();
                return;
        }
    }

    /// <summary>Ctrl+1…9 run by position.</summary>
    public void RunAt(int index)
    {
        if (Mode == CommandBarMode.Search && index >= 0 && index < Rows.Count) Run(Rows[index]);
    }

    public void Run(CommandBarEntry entry)
    {
        if (entry.NeedsSetupPage is { } page)
        {
            _host.Hide();
            _host.OpenSettings(page);
            return;
        }
        if (entry.ConfirmationPrompt is not null)
        {
            _savedQuery = Query;
            Mode = CommandBarMode.Confirm;
            ModeEntryId = entry.Id;
            Changed?.Invoke();
            return;
        }
        if (entry.NumericRange is { } range)
        {
            var split = CommandBarSearch.SplitTrailingNumber(Query.Trim());
            if (split.Number is { } typed)
            {
                Finish(entry, Math.Clamp(typed, range.Min, range.Max));
            }
            else if (entry.NumericIsOptional)
            {
                Finish(entry, null);
            }
            else
            {
                _savedQuery = Query;
                Mode = CommandBarMode.Argument;
                ModeEntryId = entry.Id;
                ReplaceQuery(string.Empty);
                Changed?.Invoke();
            }
            return;
        }
        Finish(entry, null);
    }

    private void Finish(CommandBarEntry entry, int? value)
    {
        QueryWhenRun = Mode == CommandBarMode.Argument ? _savedQuery : Query;
        RecordUsage(entry);
        if (!entry.KeepsBarOpen) _host.Hide();
        else if (Mode != CommandBarMode.Search)
        {
            Mode = CommandBarMode.Search;
            ModeEntryId = null;
        }
        try
        {
            entry.Run(value);
        }
        catch (Exception ex)
        {
            Log.Error($"Command Bar row {entry.Id} failed", ex);
        }
    }

    private void RecordUsage(CommandBarEntry entry)
    {
        if (!entry.CountsUsage) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var current = Mode is CommandBarMode.Argument or CommandBarMode.Actions ? _savedQuery : Query;
        var typed = (_queryBeforeCompletion ?? current).Trim();
        _memory.Record(typed, entry.Id, ++_memoryStep);
        Prefs.Usage = CommandBarUsage.Recording(Prefs.Usage, entry.Id, now);
        if (typed.Length > 0 && Prefs.HabitKey is not null)
            Prefs.Habits = CommandBarQueryHabits.Recording(Prefs.Habits, CommandBarQueryHabits.Prepare(typed, Prefs.HabitKey), entry.Id, now);
        if (!Prefs.Persist) return;
        Settings.Set(CommandBarKeys.Usage, CommandBarUsage.Encode(Prefs.Usage));
        if (typed.Length > 0) Settings.Set(CommandBarKeys.QueryHabits, CommandBarQueryHabits.Encode(Prefs.Habits));
    }

    /// <summary>Esc: leave a mode, leave a category, clear the field, then close.</summary>
    public void StepBack()
    {
        switch (Mode)
        {
            case CommandBarMode.Actions:
                LeaveActions();
                return;
            case CommandBarMode.Naming:
                AliasWarning = null;
                Mode = CommandBarMode.Actions;
                ReplaceQuery(string.Empty);
                Changed?.Invoke();
                return;
            case CommandBarMode.CapturingShortcut:
                _host.SuspendHotkeys(false);
                AliasWarning = null;
                Mode = CommandBarMode.Actions;
                Changed?.Invoke();
                return;
            case CommandBarMode.Argument or CommandBarMode.Confirm:
                Mode = CommandBarMode.Search;
                ModeEntryId = null;
                ReplaceQuery(_savedQuery);
                Refresh();
                return;
        }
        // A query typed by mistake clears first; then Esc leaves the category, then puts a peeked
        // list away, and only from home does it close.
        if (Query.Length > 0)
        {
            ReplaceQuery(string.Empty);
            Refresh();
        }
        else if (ActiveCategory is not null)
        {
            SetCategory(null);
        }
        else if (_peeking)
        {
            _peeking = false;
            Refresh();
        }
        else
        {
            _host.Hide();
        }
    }

    // ------------------------------------------------------------------ the actions list (Ctrl+K)

    public bool CanOpenActions => Mode == CommandBarMode.Search && SelectedEntry is { IsAnswer: false } e && _byId.ContainsKey(e.Id) && ActionsFor(e).Count > 0;

    public void OpenActions()
    {
        if (!CanOpenActions || SelectedEntry is not { } entry) return;
        _savedQuery = Query;
        Mode = CommandBarMode.Actions;
        ModeEntryId = entry.Id;
        ActionIndex = 0;
        Changed?.Invoke();
    }

    private void LeaveActions()
    {
        if (Mode == CommandBarMode.CapturingShortcut) _host.SuspendHotkeys(false);
        Mode = CommandBarMode.Search;
        ModeEntryId = null;
        AliasWarning = null;
        ReplaceQuery(_savedQuery);
        Refresh();
    }

    public List<CommandBarRowAction> ActionRows() => Mode == CommandBarMode.Actions && Entry(ModeEntryId) is { } e ? ActionsFor(e) : [];

    /// <summary>Extra actions a row offers beyond the common ones (app quit/restart, emoji tones).</summary>
    public Func<CommandBarEntry, List<CommandBarRowAction>>? ExtraActions { get; set; }

    private List<CommandBarRowAction> ActionsFor(CommandBarEntry entry)
    {
        var actions = new List<CommandBarRowAction>();
        if (ExtraActions?.Invoke(entry) is { } extra) actions.AddRange(extra);
        if (entry.RevealPath is { } path)
            actions.Add(new CommandBarRowAction("reveal", CommandBarStrings.ActionRevealInExplorer, CommandBarGlyphs.Reveal, () =>
            {
                _host.Hide();
                CommandBarSystem.Reveal(path);
            }));
        if (CommandBarPreferences.AcceptsPin(entry.Id))
        {
            var pinned = IsPinned(entry);
            actions.Add(new CommandBarRowAction("pin", pinned ? CommandBarStrings.ActionUnpin : CommandBarStrings.ActionPin,
                pinned ? CommandBarGlyphs.Unpin : CommandBarGlyphs.Pin, () =>
                {
                    TogglePin(entry);
                    LeaveActions();
                }));
        }
        if (CommandBarPreferences.AcceptsAlias(entry.Id))
        {
            var named = Prefs.Aliases.ContainsKey(entry.StableKey);
            actions.Add(new CommandBarRowAction("name", named ? CommandBarStrings.ActionRename : CommandBarStrings.ActionName,
                CommandBarGlyphs.Rename, () => BeginNaming(entry)));
            var bound = Prefs.RowShortcuts.ContainsKey(entry.StableKey);
            actions.Add(new CommandBarRowAction("shortcut", bound ? CommandBarStrings.ActionShortcutChange : CommandBarStrings.ActionShortcut,
                CommandBarGlyphs.Keyboard, () => BeginCapturingShortcut(entry)));
            if (bound)
                actions.Add(new CommandBarRowAction("shortcutClear", CommandBarStrings.ActionShortcutRemove, CommandBarGlyphs.Keyboard, () =>
                {
                    SetRowShortcut(null, entry);
                    LeaveActions();
                }));
        }
        actions.Add(new CommandBarRowAction("hide", CommandBarStrings.ActionHide, CommandBarGlyphs.Hide, () =>
        {
            ToggleHidden(entry);
            LeaveActions();
        }));
        if (entry.CountsUsage)
            actions.Add(new CommandBarRowAction("forget", CommandBarStrings.ActionForget, CommandBarGlyphs.Forget, () =>
            {
                ResetRanking(entry);
                LeaveActions();
            }));
        return actions;
    }

    public void RunAction(CommandBarRowAction action) => action.Run();

    /// <summary>Closes the bar.</summary>
    public void Close() => _host.Hide();

    public bool IsPinned(CommandBarEntry entry) => Prefs.Pins.Contains(entry.StableKey);

    public void TogglePin(CommandBarEntry entry)
    {
        if (!CommandBarPreferences.AcceptsPin(entry.Id)) return;
        var next = CommandBarPreferences.TogglingPin(entry.StableKey, Prefs.Pins);
        Prefs.Pins.Clear();
        Prefs.Pins.AddRange(next);
        if (Prefs.Persist) Settings.Set(CommandBarKeys.Pins, CommandBarPreferences.EncodePins(next));
        Refresh();
    }

    public void ToggleHidden(CommandBarEntry entry)
    {
        var next = CommandBarPreferences.TogglingHidden(entry.StableKey, Prefs.Hidden);
        Prefs.Hidden.Clear();
        Prefs.Hidden.UnionWith(next);
        if (Prefs.Persist) Settings.Set(CommandBarKeys.Hidden, CommandBarPreferences.EncodeHidden(next));
    }

    public void ResetRanking(CommandBarEntry entry)
    {
        _memory.Forget(entry.Id);
        Prefs.Usage.Remove(entry.Id);
        Prefs.Habits = CommandBarQueryHabits.Removing(entry.Id, Prefs.Habits);
        if (!Prefs.Persist) return;
        Settings.Set(CommandBarKeys.Usage, CommandBarUsage.Encode(Prefs.Usage));
        Settings.Set(CommandBarKeys.QueryHabits, CommandBarQueryHabits.Encode(Prefs.Habits));
    }

    /// <summary>"Forget what I use most": usage, habits and this session's memory.</summary>
    public void ForgetLearning()
    {
        _memory.Clear();
        Prefs.Usage = new(StringComparer.Ordinal);
        Prefs.Habits = new(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ naming

    private void BeginNaming(CommandBarEntry entry)
    {
        Mode = CommandBarMode.Naming;
        ModeEntryId = entry.Id;
        AliasWarning = null;
        ReplaceQuery(Prefs.Aliases.GetValueOrDefault(entry.StableKey) ?? string.Empty);
        Changed?.Invoke();
    }

    private void CommitName()
    {
        if (Entry(ModeEntryId) is not { } entry) return;
        var typed = Query.Trim();
        if (typed.Length > 0 && CommandBarPreferences.RowUsingAlias(typed, Prefs.Aliases, entry.StableKey) is { } owner)
        {
            AliasWarning = string.Format(CommandBarStrings.AliasTakenFormat, TitleFor(owner));
            Changed?.Invoke();
            return;
        }
        SetAlias(typed, entry.StableKey);
        AliasWarning = null;
        LeaveActions();
    }

    public void SetAlias(string alias, string stableKey)
    {
        var next = CommandBarPreferences.SettingAlias(alias, stableKey, Prefs.Aliases);
        Prefs.Aliases.Clear();
        foreach (var (k, v) in next) Prefs.Aliases[k] = v;
        if (Prefs.Persist) Settings.Set(CommandBarKeys.Aliases, CommandBarPreferences.EncodeAliases(next));
    }

    /// <summary>A readable title for a stable key (falls back to the key itself).</summary>
    public string TitleFor(string stableKey) =>
        _byId.Values.FirstOrDefault(e => e.StableKey == stableKey)?.Title ?? stableKey;

    // ------------------------------------------------------------------ row shortcuts

    private void BeginCapturingShortcut(CommandBarEntry entry)
    {
        _host.SuspendHotkeys(true);
        Mode = CommandBarMode.CapturingShortcut;
        ModeEntryId = entry.Id;
        AliasWarning = null;
        Changed?.Invoke();
    }

    /// <summary>One key press while a combination is recorded: Esc goes back, Delete clears, anything else binds.</summary>
    public void HandleCaptureKey(int vk, Modifiers modifiers)
    {
        if (Mode != CommandBarMode.CapturingShortcut || Entry(ModeEntryId) is not { } entry) return;
        if (vk == 0x1B)
        {
            StepBack();
            return;
        }
        if (vk is 0x08 or 0x2E && modifiers == Modifiers.None)
        {
            SetRowShortcut(null, entry);
            StepBack();
            return;
        }
        if (Shortcut.IsModifierKey(vk)) return;
        var shortcut = new Shortcut(modifiers, vk);
        if (!CommandBarRowShortcuts.IsUsable(shortcut))
        {
            CommandBarSystem.Beep();
            return;
        }
        if (SetRowShortcut(shortcut, entry) is { } message)
        {
            AliasWarning = message;
            Changed?.Invoke();
            return;
        }
        StepBack();
    }

    /// <summary>Binds (or clears) a row's own shortcut; returns why not, or null.</summary>
    public string? SetRowShortcut(Shortcut? shortcut, CommandBarEntry entry) => SetRowShortcut(shortcut, entry.StableKey);

    public string? SetRowShortcut(Shortcut? shortcut, string stableKey)
    {
        if (shortcut is { } s)
        {
            var issue = CommandBarRowShortcuts.AssignmentIssue(s, stableKey, Prefs.RowShortcuts);
            if (issue?.Issue == CommandBarRowShortcuts.Issue.Invalid) return CommandBarStrings.ShortcutInvalid;
            if (issue?.Issue == CommandBarRowShortcuts.Issue.Full) return string.Format(CommandBarStrings.RowShortcutsLimitFormat, CommandBarRowShortcuts.Limit);
        }
        var next = CommandBarRowShortcuts.Setting(shortcut, stableKey, Prefs.RowShortcuts);
        Prefs.RowShortcuts.Clear();
        foreach (var (k, v) in next) Prefs.RowShortcuts[k] = v;
        if (Prefs.Persist) Settings.Set(CommandBarKeys.RowShortcuts, CommandBarRowShortcuts.Encode(next));
        _host.RowShortcutsChanged();
        return null;
    }

    public string? RowShortcutText(CommandBarEntry entry) =>
        Prefs.RowShortcuts.TryGetValue(entry.StableKey, out var s) ? s.ToString() : null;

    /// <summary>Character positions of the title that the query literally matched.</summary>
    public HashSet<int> HighlightOffsets(CommandBarEntry entry)
    {
        if (entry.IsAnswer || Mode != CommandBarMode.Search) return [];
        var query = CommandBarSearch.EmojiQuery(Query) ?? Query;
        return query.Trim().Length == 0 ? [] : CommandBarSearch.HighlightOffsets(entry.Title, query);
    }
}
