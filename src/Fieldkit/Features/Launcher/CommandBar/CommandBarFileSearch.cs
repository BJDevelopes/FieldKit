// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Fieldkit.Core;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Finds files by name in the folders the person named (port of upstream CommandBarFileSearch).
/// Asks Windows Search (OLE DB provider Search.CollatorDSO through ADO, a scoped SystemIndex query
/// capped at 1000 names); when the indexer is unavailable or answers nothing it briefly lists the
/// folders instead (bounded depth, count and time) and keeps that list for this opening only.
/// Debounced; only the latest query may refresh the bar; everything is dropped when the bar closes.
/// </summary>
public sealed class CommandBarFileSearch
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private const int WalkLimit = 40_000;
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, List<string>> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherTimer? _timer;
    private int _generation;
    private string? _currentQuery;
    private List<string>? _walked;
    private string _walkedFor = string.Empty;

    /// <summary>Raised on the UI thread when results for the current query are ready.</summary>
    public event Action? ResultReady;

    public bool IsAwaiting => _timer?.IsEnabled == true || _inFlight.Count > 0;

    public void Reset()
    {
        _generation++;
        CancelPending();
        _cache.Clear();
        _inFlight.Clear();
        _walked = null;
        _walkedFor = string.Empty;
    }

    public void CancelPending()
    {
        _timer?.Stop();
        _currentQuery = null;
    }

    public List<string>? Cached(string query) => _cache.TryGetValue(query, out var paths) ? paths : null;

    public void Schedule(string query, IReadOnlyList<string> scopes, IReadOnlyList<string> patterns)
    {
        _currentQuery = query;
        if (scopes.Count == 0 || CommandBarFileSearchSupport.Sql(query, scopes) is null || _cache.ContainsKey(query) || _inFlight.Contains(query)) return;
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Debounce };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            Execute(query, scopes.ToList(), patterns.ToList());
        };
        _timer.Start();
    }

    private void Execute(string query, List<string> scopes, List<string> patterns)
    {
        var generation = _generation;
        _inFlight.Add(query);
        var walked = _walked;
        var walkedFor = _walkedFor;
        var thread = new Thread(() =>
        {
            List<string> found;
            List<string>? newWalk = null;
            try
            {
                found = QueryIndex(query, scopes);
                if (found.Count == 0)
                {
                    var key = string.Join('|', scopes);
                    if (walked is null || walkedFor != key) newWalk = walked = Walk(scopes, patterns);
                    found = walked.Where(p => CommandBarFileSearchSupport.NameMatches(Path.GetFileName(p), query)).ToList();
                }
                found = found.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                found = CommandBarFileSearchSupport.Offerable(found, patterns, IsHidden);
            }
            catch (Exception ex)
            {
                Log.Warn($"Command Bar file search failed: {ex.Message}");
                found = [];
            }
            _dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                _inFlight.Remove(query);
                if (newWalk is not null)
                {
                    _walked = newWalk;
                    _walkedFor = string.Join('|', scopes);
                }
                _cache[query] = found;
                if (CommandBarFileSearchSupport.ShouldPublish(query, _currentQuery)) ResultReady?.Invoke();
            });
        }) { IsBackground = true, Name = "Command Bar file search" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Windows Search through ADO (no package needed). Empty when the indexer is off or knows nothing.</summary>
    private static List<string> QueryIndex(string query, List<string> scopes)
    {
        var sql = CommandBarFileSearchSupport.Sql(query, scopes);
        var paths = new List<string>();
        if (sql is null) return paths;
        var connectionType = Type.GetTypeFromProgID("ADODB.Connection");
        var recordsetType = Type.GetTypeFromProgID("ADODB.Recordset");
        if (connectionType is null || recordsetType is null) return paths;
        dynamic? connection = null;
        dynamic? recordset = null;
        try
        {
            connection = Activator.CreateInstance(connectionType)!;
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            recordset = Activator.CreateInstance(recordsetType)!;
            recordset.Open(sql, connection);
            while (!(bool)recordset.EOF && paths.Count < CommandBarFileSearchSupport.CandidateLimit)
            {
                if (recordset.Fields.Item(0).Value is string path && path.Length > 0) paths.Add(path);
                recordset.MoveNext();
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Command Bar: Windows Search unavailable ({ex.Message}); listing folders instead");
        }
        finally
        {
            try { recordset?.Close(); } catch { }
            try { connection?.Close(); } catch { }
            if (recordset is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(recordset);
            if (connection is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(connection);
        }
        return paths;
    }

    /// <summary>A bounded listing of the folders (depth, count and time capped; hidden, system and ignored folders skipped).</summary>
    private static List<string> Walk(List<string> scopes, List<string> patterns)
    {
        var result = new List<string>();
        var clock = Stopwatch.StartNew();
        var ignores = patterns.Concat(CommandBarFileSearchSupport.ShippedIgnores).ToList();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 8,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var scope in scopes)
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(scope, "*", options))
                {
                    if (result.Count >= WalkLimit || clock.Elapsed > WalkBudget) return result;
                    if (CommandBarFileSearchSupport.IsIgnored(path, ignores)) continue;
                    result.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Command Bar: listing {scope} failed: {ex.Message}");
            }
        }
        return result;
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && path.Length > 3;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
