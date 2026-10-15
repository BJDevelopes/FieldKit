// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Agents;

/// <summary>
/// Everything read so far, one record per response, plus the turns agents are working on
/// (port of upstream <c>AgentUsageStore</c>). Not thread-safe: the usage service confines it to one thread.
/// </summary>
public sealed class AgentUsageStore
{
    /// <summary>A turn that ended longer ago than this is history found late, not news.</summary>
    public static readonly TimeSpan LateEnd = TimeSpan.FromMinutes(5);
    /// <summary>Records older than this are dropped.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(91);

    public static TimeSpan ResumeWindow(AgentProvider p) => p == AgentProvider.Claude ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);

    private readonly List<AgentUsageRecord> _records = [];
    private readonly List<AgentBillable> _billables = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentLiveSession> _turns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentLiveSession> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _settled = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentProvider, AgentLimits> _limits = [];
    private DateTimeOffset _codexPlanObserved = DateTimeOffset.MinValue;

    public IReadOnlyList<AgentUsageRecord> Records => _records;
    public IReadOnlyDictionary<AgentProvider, AgentLimits> Limits => _limits;
    public IReadOnlyCollection<AgentLiveSession> Live => _turns.Values;
    public string? CodexPlan { get; private set; }

    /// <summary>Off while the logs are first read, so history never replays as news.</summary>
    public bool ReportsTransitions { get; set; }

    /// <summary>Applies one file's entries and returns the events they caused.</summary>
    /// <param name="parent">The log whose turn a subagent's responses count toward.</param>
    public List<AgentUsageEvent> Apply(IEnumerable<AgentLogEntry> entries, string file, AgentProvider provider, bool tracksTurns,
        string? parent, DateTimeOffset modified, DateTimeOffset now)
    {
        var events = new List<AgentUsageEvent>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case AgentLogEntry.Usage u:
                {
                    var turn = provider == AgentProvider.Copilot ? null : tracksTurns ? file : parent;
                    Add(u.Record, u.Billable, u.Key, turn, subagent: !tracksTurns);
                    break;
                }
                case AgentLogEntry.UsageModel um:
                    if (_index.TryGetValue(um.Key, out var pos) && _records[pos].Model.Length == 0)
                        _records[pos] = _records[pos] with { Model = um.Model };
                    break;
                case AgentLogEntry.Limits l:
                    UpdateLimits(l.Reading);
                    break;
                case AgentLogEntry.Plan p:
                    if (_codexPlanObserved <= p.ObservedAt)
                    {
                        CodexPlan = p.Name;
                        _codexPlanObserved = p.ObservedAt;
                    }
                    break;
                case AgentLogEntry.TurnBegan b:
                {
                    if (!tracksTurns) break;
                    _settled.Remove(file);
                    var existing = _turns.GetValueOrDefault(file) ?? _waiting.GetValueOrDefault(file);
                    if (existing is not null && Math.Abs((existing.Started - b.Date).TotalSeconds) < 1) break;
                    _waiting.Remove(file);
                    var last = _turns.TryGetValue(file, out var t) && t.LastActivity > b.Date ? t.LastActivity : b.Date;
                    _turns[file] = new AgentLiveSession(file, provider, b.Date, last, string.Empty, string.Empty, default, 0);
                    break;
                }
                case AgentLogEntry.TurnContext c:
                    if (!tracksTurns) break;
                    if (_turns.TryGetValue(file, out var tc)) _turns[file] = WithContext(tc, c);
                    else if (_waiting.TryGetValue(file, out var wc)) _waiting[file] = WithContext(wc, c);
                    break;
                case AgentLogEntry.TurnActive a:
                {
                    if (!tracksTurns) break;
                    _settled.Remove(file);
                    var moment = a.Date ?? modified;
                    if (_turns.TryGetValue(file, out var ta) || _waiting.Remove(file, out ta))
                        _turns[file] = ta with { LastActivity = Max(ta.LastActivity, moment) };
                    else
                        _turns[file] = new AgentLiveSession(file, provider, moment, moment, string.Empty, string.Empty, default, 0);
                    break;
                }
                case AgentLogEntry.TurnSettled s:
                {
                    if (!tracksTurns) break;
                    if (!_turns.TryGetValue(file, out var ts) && !_waiting.Remove(file, out ts)) break;
                    _turns[file] = ts with { LastActivity = Max(ts.LastActivity, s.Date) };
                    _settled[file] = s.Date;
                    break;
                }
                case AgentLogEntry.TurnEnded e:
                {
                    if (!tracksTurns) break;
                    _settled.Remove(file);
                    _waiting.Remove(file, out var quiet);
                    var turn = _turns.Remove(file, out var tt) ? tt : quiet;
                    if (turn is null || !e.Completed || !ReportsTransitions) break;
                    var end = e.Date ?? modified;
                    if (now - end > LateEnd) break;
                    var duration = e.Duration ?? end - turn.Started;
                    if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                    events.Add(new AgentUsageEvent.Finished(provider, duration, turn.Cost, turn.Tokens.Total, turn.Project));
                    break;
                }
                case AgentLogEntry.Reset:
                    Forget(file.Split('#')[0]);
                    break;
            }
        }
        return events;
    }

    private static AgentLiveSession WithContext(AgentLiveSession s, AgentLogEntry.TurnContext c) => s with
    {
        Model = c.Model.Length > 0 ? c.Model : s.Model,
        Project = c.Project.Length > 0 ? c.Project : s.Project,
    };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>A log removed while its turn ran ends that turn without a notice. True when one was showing.</summary>
    public bool Forget(string file)
    {
        _waiting.Remove(file);
        foreach (var k in _settled.Keys.Where(k => k == file || k.StartsWith(file + "#", StringComparison.Ordinal)).ToList()) _settled.Remove(k);
        var removed = _turns.Remove(file);
        foreach (var k in _turns.Keys.Where(k => k.StartsWith(file + "#", StringComparison.Ordinal)).ToList()) { _turns.Remove(k); removed = true; }
        foreach (var k in _waiting.Keys.Where(k => k.StartsWith(file + "#", StringComparison.Ordinal)).ToList()) _waiting.Remove(k);
        return removed;
    }

    private void Add(AgentUsageRecord record, AgentBillable billable, string key, string? turnFile, bool subagent)
    {
        var delta = record.Tokens;
        var extra = record.Cost ?? 0;
        if (_index.TryGetValue(key, out var position))
        {
            var old = _records[position];
            var merged = old.Tokens.Max(record.Tokens);
            if (merged == old.Tokens)
            {
                if (record.Provider == AgentProvider.OpenCode)
                {
                    var (newCost, reported) = OpenCodeCost(record, old, null);
                    if (newCost != old.Cost || reported != old.ReportedCost)
                    {
                        var diff = (newCost ?? 0) - (old.Cost ?? 0);
                        _records[position] = old with { Cost = newCost, ReportedCost = reported };
                        if (turnFile is not null && TurnFor(turnFile) is { } turn && record.Date >= turn.Started.AddSeconds(-1))
                        {
                            _waiting.Remove(turnFile);
                            _turns[turnFile] = turn with { Cost = turn.Cost + diff };
                        }
                    }
                }
                return;
            }
            var combined = _billables[position].Merge(billable) with { Tokens = merged };
            var priced = AgentPricing.Cost(combined, old.Model);
            double? cost;
            var isReported = false;
            if (record.Provider == AgentProvider.OpenCode) (cost, isReported) = OpenCodeCost(record, old, priced.Cost);
            else cost = priced.Cost;
            delta = merged.Minus(old.Tokens);
            extra = (cost ?? 0) - (old.Cost ?? 0);
            _billables[position] = combined;
            _records[position] = old with { Tokens = merged, Cost = cost, Savings = priced.Savings, ReportedCost = isReported };
        }
        else
        {
            _index[key] = _records.Count;
            _records.Add(record);
            _billables.Add(billable);
        }
        if (turnFile is null) return;
        if (TurnFor(turnFile) is not { } live || record.Date < live.Started.AddSeconds(-1)) return;
        _waiting.Remove(turnFile);
        _turns[turnFile] = live with
        {
            Tokens = live.Tokens + delta,
            Cost = live.Cost + extra,
            LastActivity = Max(live.LastActivity, record.Date),
            Model = !subagent && record.Model.Length > 0 ? record.Model : live.Model,
            Project = !subagent && record.Project.Length > 0 ? record.Project : live.Project,
        };
    }

    private AgentLiveSession? TurnFor(string file) => _turns.GetValueOrDefault(file) ?? _waiting.GetValueOrDefault(file);

    private static (double? Cost, bool Reported) OpenCodeCost(AgentUsageRecord record, AgentUsageRecord old, double? priced)
    {
        if (record.ReportedCost) return (record.Cost, true);
        if (old.ReportedCost) return (old.Cost, true);
        return (priced ?? record.Cost ?? old.Cost, false);
    }

    /// <summary>Keeps the newer of two readings of an account.</summary>
    public void UpdateLimits(AgentLimits reading)
    {
        if (!_limits.TryGetValue(reading.Provider, out var current) || current.ObservedAt <= reading.ObservedAt)
            _limits[reading.Provider] = reading;
    }

    public void SetLimits(AgentLimits reading) => _limits[reading.Provider] = reading;

    public void ClearLimits(AgentProvider provider) => _limits.Remove(provider);

    /// <summary>Quiet turns stop showing as working and wait aside for work to resume.</summary>
    public void CloseIdleTurns(DateTimeOffset now, TimeSpan idle)
    {
        foreach (var (file, turn) in _turns.Where(p => now - p.Value.LastActivity >= idle).ToList())
        {
            _turns.Remove(file);
            _waiting[file] = turn;
        }
        foreach (var file in _waiting.Where(p => now - p.Value.LastActivity >= ResumeWindow(p.Value.Provider)).Select(p => p.Key).ToList())
            _waiting.Remove(file);
    }

    /// <summary>A settled turn with nothing after it for 30 s stopped there (no notice). True when one was showing.</summary>
    public bool CloseSettledTurns(DateTimeOffset now)
    {
        var removed = false;
        foreach (var (file, date) in _settled.Where(p => (now - p.Value).TotalSeconds >= AgentLogParser.OpenCodeSettle).ToList())
        {
            _settled.Remove(file);
            _waiting.Remove(file);
            if (_turns.Remove(file)) removed = true;
        }
        return removed;
    }

    /// <summary>Ends Claude turns whose process is gone (no notice). True when one was showing.</summary>
    public bool CloseTurns(Func<AgentLiveSession, bool> ended)
    {
        var removed = false;
        foreach (var (file, turn) in _turns.Where(p => ended(p.Value)).ToList())
        {
            _turns.Remove(file);
            removed = true;
        }
        return removed;
    }

    public void DropRecords(DateTimeOffset before)
    {
        if (_records.All(r => r.Date >= before)) return;
        var keep = new List<int>();
        for (var i = 0; i < _records.Count; i++) if (_records[i].Date >= before) keep.Add(i);
        var remap = new Dictionary<int, int>();
        for (var n = 0; n < keep.Count; n++) remap[keep[n]] = n;
        var records = keep.Select(i => _records[i]).ToList();
        var billables = keep.Select(i => _billables[i]).ToList();
        foreach (var k in _index.Keys.ToList())
        {
            if (remap.TryGetValue(_index[k], out var n)) _index[k] = n;
            else _index.Remove(k);
        }
        _records.Clear();
        _records.AddRange(records);
        _billables.Clear();
        _billables.AddRange(billables);
    }
}
