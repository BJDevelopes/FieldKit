// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Agents;

/// <summary>
/// Follows Claude Code, Codex, OpenCode and GitHub Copilot from the logs they keep in the user profile
/// (port of upstream <c>AgentUsageService</c>). Everything runs on one background thread; prompts, replies and
/// files are never kept and nothing leaves the PC (the bundled price list is used; upstream's daily price-list
/// download and Codex's account server are not used).
/// </summary>
public sealed class AgentUsageService : IslandExtensionService
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    /// <summary>
    /// A working agent writes its log many times a second and every write wakes the reader; passes (and the
    /// summary rebuild after them) are spaced at least this far apart so following live work stays cheap.
    /// </summary>
    private static readonly TimeSpan MinimumPass = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan IdleTurn = TimeSpan.FromSeconds(AgentLogParser.IdleTurn);

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentLogCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, AgentLimitWindow> _lastWindows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private AgentUsageStore _store = new();
    private CancellationTokenSource? _cts;
    private AutoResetEvent? _wake;
    private Thread? _thread;
    private DateTime _budgetDay = DateTime.MinValue;
    private DateTime _lastRootCheck = DateTime.MinValue;
    private int _linesRead;

    public override string FeatureId => "notchAgents";
    protected override string EnableKey => IslandKeys.AgentsEnabled;
    public override bool EnabledByDefault => false;

    /// <summary>Latest summary, or null while the logs are first read ("Reading usage…").</summary>
    public AgentUsageSnapshot? Snapshot { get; private set; }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool ProviderOn(AgentProvider p) => Settings.GetBool(IslandKeys.AgentsProvider(p), true);

    /// <summary>Whether a provider's log folder exists ("Found on this PC").</summary>
    public static bool Found(AgentProvider p) => AgentLogReader.Roots(Home).Any(r => r.Provider == p && Directory.Exists(r.Path));

    protected override void OnStart()
    {
        IslandHub.Agents = this;
        // A reader from a previous run must be gone before its store is replaced.
        if (_thread is { IsAlive: true } previous && !previous.Join(TimeSpan.FromSeconds(5)))
            Log.Warn("The previous agent usage reader is still finishing");
        _store = new AgentUsageStore();
        _cursors.Clear();
        _cts = new CancellationTokenSource();
        _wake = new AutoResetEvent(false);
        var token = _cts.Token;
        var wake = _wake;
        _thread = new Thread(() => Run(token, wake)) { IsBackground = true, Name = "Fieldkit agent usage", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _wake?.Set();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _cts = null;
        Snapshot = null;
        if (IslandHub.Agents == this) IslandHub.Agents = null;
        IslandHub.RaiseChanged();
    }

    private void Run(CancellationToken token, AutoResetEvent wake)
    {
        try
        {
            var horizon = DateTime.UtcNow - AgentUsageStore.Horizon;
            lock (_gate)
            {
                foreach (var (path, provider) in AgentLogReader.Discover(EnabledRoots(), horizon))
                {
                    if (token.IsCancellationRequested) return;
                    Read(Cursor(path, provider), DateTimeOffset.UtcNow);
                }
                CloseEndedClaudeTurns();
                // History read in one go leaves every turn that never logged its end looking live; retire the idle ones
                // now rather than at the first 30 s tick, so the island never opens on months-old "working" timers.
                var now = DateTimeOffset.UtcNow;
                _store.CloseIdleTurns(now, IdleTurn);
                _store.CloseSettledTurns(now);
                _store.ReportsTransitions = true;
            }
            Ui.OnUi(WatchRoots);
            Publish(firstPublish: true);
            var lastTick = DateTime.UtcNow;
            var lastPass = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                wake.WaitOne(FastPoll);
                var sinceLast = DateTime.UtcNow - lastPass;
                if (sinceLast < MinimumPass) token.WaitHandle.WaitOne(MinimumPass - sinceLast);
                if (token.IsCancellationRequested) break;
                lastPass = DateTime.UtcNow;
                var now = DateTimeOffset.UtcNow;
                var events = new List<AgentUsageEvent>();
                bool broad;
                lock (_gate)
                {
                    _linesRead = 0;
                    broad = DateTime.UtcNow - lastTick >= Tick;
                    if (broad)
                    {
                        lastTick = DateTime.UtcNow;
                        if (DateTime.UtcNow - _lastRootCheck > TimeSpan.FromMinutes(5))
                        {
                            _lastRootCheck = DateTime.UtcNow;
                            foreach (var (path, provider) in AgentLogReader.Discover(EnabledRoots(), DateTime.UtcNow.AddHours(-24)))
                                _dirty.Add(path + "|" + provider);
                            Ui.OnUi(WatchRoots);
                        }
                        _store.CloseIdleTurns(now, IdleTurn);
                        _store.CloseSettledTurns(now);
                        _store.DropRecords(now - AgentUsageStore.Horizon);
                        CloseEndedClaudeTurns();
                    }
                    var due = _cursors.Values.Where(c =>
                        DateTime.UtcNow - c.Modified < TimeSpan.FromMinutes(30) || _store.Live.Any(l => l.Id == c.Path) || broad && DateTime.UtcNow - c.Modified < TimeSpan.FromHours(24)).ToList();
                    foreach (var key in _dirty.ToList())
                    {
                        var parts = key.Split('|');
                        if (parts.Length == 2 && Enum.TryParse<AgentProvider>(parts[1], out var p) && File.Exists(parts[0]))
                            due.Add(Cursor(parts[0], p));
                    }
                    _dirty.Clear();
                    foreach (var cursor in due.Distinct())
                    {
                        var modified = AgentLogReader.Modified(cursor.Path);
                        long size;
                        try { size = cursor.Provider == AgentProvider.OpenCode ? -1 : new FileInfo(cursor.Path).Length; }
                        catch { continue; }
                        if (cursor.Provider != AgentProvider.OpenCode && size == cursor.Size && modified == cursor.Modified) continue;
                        if (cursor.Provider == AgentProvider.OpenCode && modified == cursor.Modified && cursor.OpenRows.Count == 0) continue;
                        events.AddRange(Read(cursor, now));
                    }
                }
                Handle(events);
                // Summaries are rebuilt only when something was read, and on the 30 s tick (idle turns, limits, plans).
                if (_linesRead > 0 || broad || events.Count > 0) Publish(firstPublish: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Agent usage reader stopped", ex);
        }
    }

    private IEnumerable<AgentLogRoot> EnabledRoots() => AgentLogReader.Roots(Home).Where(r => ProviderOn(r.Provider));

    private AgentLogCursor Cursor(string path, AgentProvider provider)
    {
        if (!_cursors.TryGetValue(path, out var c)) _cursors[path] = c = new AgentLogCursor(path, provider);
        return c;
    }

    private List<AgentUsageEvent> Read(AgentLogCursor cursor, DateTimeOffset now)
    {
        var events = new List<AgentUsageEvent>();
        var tracks = cursor.TracksTurns;
        var parent = cursor.Provider == AgentProvider.Claude ? AgentLogReader.ClaudeParent(cursor.Path) : null;
        if (cursor.Provider == AgentProvider.OpenCode)
        {
            var rows = OpenCodeReader.ReadAppended(cursor, now - AgentUsageStore.Horizon);
            cursor.Modified = AgentLogReader.Modified(cursor.Path);
            _linesRead += rows.Count;
            foreach (var row in rows) Handle(row);
        }
        else
        {
            // Lines are parsed as they are read: a log can be hundreds of MB and must never sit in memory whole.
            _linesRead += AgentLogReader.ReadAppended(cursor, Handle);
        }
        return events;

        void Handle(string line)
        {
            if (line.Length == 0) return;
            var entries = AgentLogParser.Parse(cursor.Provider, line, cursor.State, now);
            if (entries.Count == 0) return;
            // Set by the reader before the first line.
            var modified = new DateTimeOffset(cursor.Modified == default ? DateTime.UtcNow : cursor.Modified, TimeSpan.Zero);
            var file = cursor.Path;
            var parentFile = parent;
            if (cursor.Provider == AgentProvider.OpenCode)
            {
                // One database holds every session: each tracks its own turn.
                var sub = cursor.State.ParentSession.Length > 0;
                file = cursor.Path + "#" + (sub ? cursor.State.ParentSession : cursor.State.Session);
                if (sub)
                {
                    parentFile = file;
                    events.AddRange(_store.Apply(entries, file, cursor.Provider, false, parentFile, modified, now));
                    return;
                }
            }
            events.AddRange(_store.Apply(entries, file, cursor.Provider, tracks, parentFile, modified, now));
        }
    }

    /// <summary>Claude Code records running sessions in ~/.claude/sessions/&lt;pid&gt;.json: a turn whose process is gone is over.</summary>
    private void CloseEndedClaudeTurns()
    {
        var folder = Path.Combine(Home, ".claude", "sessions");
        if (!Directory.Exists(folder)) return;
        var running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("sessionId", out var s) || s.ValueKind != JsonValueKind.String) continue;
                    if (!root.TryGetProperty("pid", out var p) || p.ValueKind != JsonValueKind.Number) continue;
                    running[s.GetString()!] = ProcessAlive(p.GetInt32());
                }
                catch { }
            }
        }
        catch { return; }
        _store.CloseTurns(t => t.Provider == AgentProvider.Claude &&
                               running.TryGetValue(Path.GetFileNameWithoutExtension(t.Id), out var alive) && !alive);
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void WatchRoots()
    {
        if (_cts is null) return;
        foreach (var root in EnabledRoots())
        {
            if (!Directory.Exists(root.Path) || _watchers.Any(w => string.Equals(w.Path, root.Path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var w = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = root.Provider is AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Copilot,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };
                var provider = root.Provider;
                void OnChange(object? s, FileSystemEventArgs e)
                {
                    var path = e.FullPath;
                    if (provider == AgentProvider.OpenCode)
                    {
                        if (!Path.GetFileName(path).StartsWith("opencode.db", StringComparison.OrdinalIgnoreCase)) return;
                        path = Path.Combine(Path.GetDirectoryName(path)!, "opencode.db");
                    }
                    else if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
                    if (provider == AgentProvider.Copilot && !Path.GetFileName(path).Equals("events.jsonl", StringComparison.OrdinalIgnoreCase)) return;
                    lock (_gate) _dirty.Add(path + "|" + provider);
                    _wake?.Set();
                }
                w.Changed += OnChange;
                w.Created += OnChange;
                w.Renamed += (s, e) => OnChange(s, e);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Warn($"Watching {root.Path} failed: {ex.Message}");
            }
        }
    }

    private void Publish(bool firstPublish)
    {
        AgentUsageSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var plans = new Dictionary<AgentProvider, AgentPlan>();
            if (ClaudeAccount.ReadPlan(Home) is { } info && AgentPricing.ClaudePlan(info.OrganizationType, info.RateLimitTier) is { } claudePlan)
                plans[AgentProvider.Claude] = claudePlan;
            if (AgentPricing.CodexPlan(_store.CodexPlan) is { } codexPlan) plans[AgentProvider.Codex] = codexPlan;
            UpdateClaudeAppLimits(now);
            var enabled = AgentProviders.All.Where(ProviderOn).ToHashSet();
            var records = _store.Records.Where(r => enabled.Contains(r.Provider)).ToList();
            var limits = _store.Limits.Where(l => enabled.Contains(l.Key)).ToDictionary(l => l.Key, l => l.Value);
            var live = _store.Live.Where(l => enabled.Contains(l.Provider)).ToList();
            snapshot = AgentSummary.Build(records, limits, live, plans, now);
        }
        var events = firstPublish ? [] : LimitEvents(snapshot);
        Ui.OnUi(() =>
        {
            if (_cts is null) return;
            var hadLive = Snapshot?.Live.Count ?? 0;
            Snapshot = snapshot;
            if (hadLive == 0 && snapshot.Live.Count > 0 && !firstPublish) IslandHub.React(MascotEvent.AgentStarted);
            IslandHub.RaiseChanged();
        });
        Handle(events);
        if (firstPublish) _budgetDay = BudgetPassed(snapshot) ? DateTime.Today : DateTime.MinValue;
        else CheckBudget(snapshot);
    }

    private void UpdateClaudeAppLimits(DateTimeOffset now)
    {
        if (!ProviderOn(AgentProvider.Claude)) return;
        foreach (var file in ClaudeAccount.LimitFiles())
        {
            if (!File.Exists(file)) continue;
            try
            {
                var org = ClaudeAccount.ReadPlan(Home)?.OrganizationUuid;
                var block = AgentSummary.CurrentBlock(_store.Records.Where(r => r.Provider == AgentProvider.Claude && r.Date >= now.AddHours(-24)).ToList(), now);
                var limits = ClaudeAccount.Limits(ClaudeAccount.ParseSamples(File.ReadAllText(file)), org, now, block?.Start);
                if (limits is not null) _store.SetLimits(limits);
                else if (_store.Limits.TryGetValue(AgentProvider.Claude, out var old) && old.Source == AgentLimitSource.ClaudeApp) _store.ClearLimits(AgentProvider.Claude);
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading Claude's plan limits failed: {ex.Message}");
            }
            return;
        }
    }

    private List<AgentUsageEvent> LimitEvents(AgentUsageSnapshot snapshot)
    {
        var events = new List<AgentUsageEvent>();
        var threshold = Settings.GetDouble(IslandKeys.AgentsLimitThreshold, IslandDefaults.AgentsLimitThreshold);
        foreach (var (provider, limits) in snapshot.Limits)
        {
            foreach (var w in limits.Windows)
            {
                var key = provider + ":" + w.Id;
                _lastWindows.TryGetValue(key, out var before);
                var current = w.Current(snapshot.Now);
                if (AgentSummary.CrossedThreshold(before, current, threshold))
                {
                    events.Add(new AgentUsageEvent.LimitWarning(provider, current));
                    _warned.Add(key);
                }
                else if (_warned.Contains(key) && before is not null && current.UsedPercent < threshold &&
                         (before.ResetsAt != current.ResetsAt || current.UsedPercent < before.UsedPercent))
                {
                    events.Add(new AgentUsageEvent.LimitReset(provider, current));
                    _warned.Remove(key);
                }
                _lastWindows[key] = current;
            }
        }
        return events;
    }

    private static bool BudgetPassed(AgentUsageSnapshot s)
    {
        var budget = Settings.GetDouble(IslandKeys.AgentsDailyBudget, 0);
        return budget > 0 && s.Totals[AgentPeriod.Today].Cost >= budget;
    }

    private void CheckBudget(AgentUsageSnapshot s)
    {
        if (_budgetDay == DateTime.Today || !BudgetPassed(s)) return;
        _budgetDay = DateTime.Today;
        Handle([new AgentUsageEvent.BudgetReached(s.Totals[AgentPeriod.Today].Cost, Settings.GetDouble(IslandKeys.AgentsDailyBudget, 0))]);
    }

    private static void Handle(List<AgentUsageEvent> events)
    {
        if (events.Count == 0) return;
        Ui.OnUi(() =>
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case AgentUsageEvent.Finished f:
                        if (!ProviderOn(f.Provider) || !Settings.GetBool(IslandKeys.AgentsFinishAlert, true)) break;
                        if (f.Duration.TotalSeconds < Math.Clamp(Settings.GetDouble(IslandKeys.AgentsFinishMinimum, IslandDefaults.AgentsFinishMinimum), 0, 3600)) break;
                        var detail = AgentFormat.Duration(f.Duration.TotalSeconds) + (f.Cost > 0 ? " · " + AgentFormat.Cost(f.Cost) : string.Empty);
                        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Agents, $"{f.Provider.DisplayName()} finished", detail, AgentGlyphs.For(f.Provider),
                            AgentGlyphs.TintKey(f.Provider), OpenSection: "agents"));
                        IslandHub.React(MascotEvent.AgentFinished);
                        break;
                    case AgentUsageEvent.LimitWarning w:
                        if (!Settings.GetBool(IslandKeys.AgentsLimitAlert, true)) break;
                        var showUsed = Settings.GetString(IslandKeys.AgentsLimitDisplay, "remaining") == "used";
                        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Agents, $"{w.Provider.DisplayName()} · {w.Window.Label}",
                            showUsed ? $"{AgentFormat.Percent(w.Window.UsedFraction)} used" : $"{AgentFormat.Percent(w.Window.RemainingFraction)} left",
                            Glyphs.Warning, "Orange", OpenSection: "agents"));
                        break;
                    case AgentUsageEvent.LimitReset r:
                        if (!Settings.GetBool(IslandKeys.AgentsLimitAlert, true)) break;
                        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Agents, $"{r.Provider.DisplayName()} · {r.Window.Label}", "Limit renewed",
                            Glyphs.Refresh, "Green", OpenSection: "agents"));
                        IslandHub.React(MascotEvent.AgentLimitRenewed);
                        break;
                    case AgentUsageEvent.BudgetReached b:
                        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Agents, "Daily budget", AgentFormat.Cost(b.Spent), AgentGlyphs.Dollar, "Orange", OpenSection: "agents"));
                        break;
                }
            }
        });
    }
}

/// <summary>Provider marks drawn with icon-font glyphs, tinted like upstream.</summary>
public static class AgentGlyphs
{
    public const string Sparkle = "\uEA38";   // Claude (upstream "sparkle")
    public const string Code = "\uE943";      // Codex ("</>")
    public const string Terminal = "\uE756";  // OpenCode
    public const string Infinity = "\uE99A";  // Copilot: the icon font has no infinity sign; a bot head stands in
    public const string Dollar = "\uE8C7";

    public static string For(AgentProvider p) => p switch
    {
        AgentProvider.Claude => Sparkle,
        AgentProvider.Codex => Code,
        AgentProvider.OpenCode => Terminal,
        _ => Infinity,
    };

    /// <summary>A theme brush key close to each provider's tint.</summary>
    public static string TintKey(AgentProvider p) => p switch
    {
        AgentProvider.Claude => "Orange",
        AgentProvider.Codex => "Accent",
        AgentProvider.OpenCode => "Green",
        _ => "Mint",
    };
}
