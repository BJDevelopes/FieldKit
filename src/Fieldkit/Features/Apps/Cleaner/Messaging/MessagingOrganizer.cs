// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner.Messaging;

/// <summary>A file the organizer filed away, identified by its SHA-256 digest.</summary>
public sealed record OrganizerRecord(string Digest, string DestinationPath, string OriginalName, long Size, DateTime OrganizedAt);

/// <summary>One step of an organization pass, kept so the pass can be undone.</summary>
/// <param name="Kind">"move" (CurrentPath can be moved back to RestorePath) or "trash" (went to the Recycle Bin).</param>
public sealed record UndoAction(string Kind, string CurrentPath, string? RestorePath);

public sealed record UndoTransaction(Guid Id, List<UndoAction> Actions, List<OrganizerRecord> RecordsBefore,
    List<OrganizerRecord> RecordsAfter, DateTime CreatedAt);

public sealed record OrganizerSettings(string Destination, int DelayMinutes, IReadOnlySet<MessagingCategory> Categories,
    OrganizerLayout Layout, DuplicateAction Duplicate);

/// <summary>A file the organizer may move.</summary>
public sealed record OrganizerSource(string Path, long Size, DateTime DownloadedAt, DateTime ModifiedAt, MessagingCategory Category);

public sealed record OrganizerRunResult(int Moved, int Duplicates, int Failed, List<OrganizerRecord> Records, UndoTransaction? Undo, DateTime? NextEligible);

/// <summary>File operations of the organizer, behind a seam so tests use fixture folders only.</summary>
public interface IOrganizerFileOps
{
    bool Exists(string path);
    bool IsLocked(string path);
    string? Hash(string path);
    bool Move(string source, string destination);
    bool Recycle(string path, long size);
}

public sealed class SystemOrganizerFileOps : IOrganizerFileOps
{
    public static readonly SystemOrganizerFileOps Instance = new();

    public bool Exists(string path) => File.Exists(path);

    public bool IsLocked(string path) => CleanerScanner.IsLocked(path);

    public string? Hash(string path)
    {
        // Hashing a cloud-only file would download it; unknown means "not a duplicate", which is safe.
        if (CloudFiles.IsCloudOnly(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A move on one volume is a rename. Across volumes the copy is verified by digest
    /// before the source goes to the Recycle Bin (never deleted).</summary>
    public bool Move(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Move(source, destination, overwrite: false);
                return true;
            }
            File.Copy(source, destination, overwrite: false);
            if (Hash(source) is { } a && a == Hash(destination) && Recycle(source, new FileInfo(source).Length)) return true;
            try { File.Delete(destination); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Organizer: moving {source} failed: {ex.Message}");
            return false;
        }
    }

    public bool Recycle(string path, long size) => SystemCleanerDeleter.Instance.Recycle(path, size, allowProtected: false);
}

/// <summary>
/// Experimental organizer for confirmed messaging downloads (upstream WhatsAppDownloadOrganizer):
/// watches the top level of Downloads and the Telegram Desktop folder, waits until a file is
/// stable, then moves it to the configured folder. File bytes are read only to compute a local
/// SHA-256 digest for exact duplicate detection.
/// </summary>
public sealed class MessagingOrganizer
{
    public static MessagingOrganizer Shared { get; } = new();

    private MessagingOrganizer() { }

    public enum Phase { Idle, Waiting, Organizing, Undoing, Done, Failed }

    public Phase State { get; private set; } = Phase.Idle;
    public (int Moved, int Duplicates, int Failed) LastResult { get; private set; }
    public bool IsBusy => State is Phase.Organizing or Phase.Undoing;
    public bool CanUndo => ValidUndoTransactions(DateTime.Now).Count > 0;

    public IOrganizerFileOps Ops { get; set; } = SystemOrganizerFileOps.Instance;

    public event Action? Changed;

    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _timer;
    private DateTime? _nextCheck;

    private static string RecordsFile => AppInfo.DataPath("cleaner-messaging-organizer.json");
    private static string UndoFile => AppInfo.DataPath("cleaner-messaging-organizer-undo.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---------------------------------------------------------------- settings

    public static string DefaultDestination(MessagingPaths paths) => Path.Combine(paths.Downloads, "Messaging");

    public static string Destination(MessagingPaths paths)
    {
        var configured = Settings.GetString(MessagingKeys.OrganizerDestination, string.Empty);
        return ResolveDestination(configured, paths.Downloads);
    }

    /// <summary>The configured folder, or Downloads\Messaging when unset or when it is Downloads itself.</summary>
    public static string ResolveDestination(string configured, string downloads)
    {
        var fallback = Path.Combine(downloads, "Messaging");
        if (string.IsNullOrWhiteSpace(configured)) return fallback;
        try
        {
            var full = Path.GetFullPath(configured).TrimEnd('\\');
            return string.Equals(full, Path.GetFullPath(downloads).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? fallback : full;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Sets the destination (null = default). False when it is Downloads itself or missing.</summary>
    public static bool SetDestination(string? folder, MessagingPaths paths)
    {
        if (folder is null)
        {
            Settings.Set(MessagingKeys.OrganizerDestination, string.Empty);
            return true;
        }
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        if (string.Equals(full, Path.GetFullPath(paths.Downloads).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (!Directory.Exists(full)) return false;
        Settings.Set(MessagingKeys.OrganizerDestination, full);
        return true;
    }

    public static OrganizerSettings CurrentSettings(MessagingPaths paths) => new(
        Destination(paths),
        MessagingRules.SanitizedOrganizerDelayMinutes(Settings.GetInt(MessagingKeys.OrganizerDelay, 5)),
        MessagingRules.DecodeCategories(Settings.GetString(MessagingKeys.OrganizerCategories, MessagingKeys.AllCategories)),
        MessagingRules.ParseLayout(Settings.GetString(MessagingKeys.OrganizerLayout, "flat")),
        MessagingRules.ParseDuplicateAction(Settings.GetString(MessagingKeys.OrganizerDuplicateAction, "trashNew")));

    // ----------------------------------------------------------------- monitor

    /// <summary>Starts watching (organizer on) or stops everything (off). Called by the service.</summary>
    public void Sync(bool active)
    {
        StopMonitoring();
        if (!active)
        {
            if (!IsBusy) State = Phase.Idle;
            Raise();
            return;
        }
        var paths = MessagingPaths.Current();
        foreach (var folder in new[] { paths.Downloads, paths.TelegramFolder })
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
                watcher.Created += (_, _) => UiThread.Post(() => ScheduleAfter(TimeSpan.FromSeconds(2)));
                watcher.Renamed += (_, _) => UiThread.Post(() => ScheduleAfter(TimeSpan.FromSeconds(2)));
                watcher.Changed += (_, _) => UiThread.Post(() => ScheduleAfter(TimeSpan.FromSeconds(2)));
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warn($"Organizer: watching {folder} failed: {ex.Message}");
            }
        }
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            if (_nextCheck is { } next && DateTime.Now >= next) _ = RunAsync(manual: false);
        };
        _timer.Start();
        ScheduleAfter(TimeSpan.FromSeconds(2));
    }

    private void StopMonitoring()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _timer?.Stop();
        _timer = null;
        _nextCheck = null;
    }

    private void ScheduleAfter(TimeSpan delay)
    {
        if (_timer is null) return;
        var at = DateTime.Now + delay;
        if (_nextCheck is { } existing && existing <= at) return;
        _nextCheck = at;
        if (!IsBusy && State != Phase.Done) State = Phase.Waiting;
    }

    // --------------------------------------------------------------------- run

    public async Task RunAsync(bool manual)
    {
        _nextCheck = null;
        if (IsBusy)
        {
            ScheduleAfter(TimeSpan.FromMinutes(1));
            return;
        }
        var manager = MessagingDownloads.Shared;
        if (manager.IsBusy || (!manual && manager.ReviewVisible))
        {
            ScheduleAfter(TimeSpan.FromMinutes(1));
            return;
        }
        State = Phase.Organizing;
        Raise();
        var paths = MessagingPaths.Current();
        var settings = CurrentSettings(paths);
        var snapshot = MessagingSnapshot.FromSettings();
        var ops = Ops;
        try
        {
            var result = await Task.Run(() =>
            {
                var sources = Sources(paths, snapshot);
                var records = LoadRecords();
                return RunCore(sources, settings, records, DateTime.Now, ops);
            }).ConfigureAwait(true);
            SaveRecords(result.Records);
            if (result.Undo is not null)
            {
                var undo = ValidUndoTransactions(DateTime.Now);
                undo.Add(result.Undo);
                SaveUndo(undo);
            }
            LastResult = (result.Moved, result.Duplicates, result.Failed);
            if (manual || result.Moved + result.Duplicates + result.Failed > 0)
            {
                Settings.Set(MessagingKeys.OrganizerLastRun, CleanerSchedule.ToStamp(DateTime.Now));
                Settings.Set(MessagingKeys.OrganizerLastMoved, result.Moved);
                Settings.Set(MessagingKeys.OrganizerLastDuplicates, result.Duplicates);
                Settings.Set(MessagingKeys.OrganizerLastFailed, result.Failed);
                if (Settings.GetBool(MessagingKeys.Notify, true) && result.Moved + result.Duplicates + result.Failed > 0)
                    Notifier.Show(S.OrgNotificationTitle, string.Format(S.OrgNotificationFormat, result.Moved, result.Duplicates, result.Failed));
            }
            State = Phase.Done;
            if (result.NextEligible is { } next) ScheduleAfter(next > DateTime.Now ? next - DateTime.Now + TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));
        }
        catch (Exception ex)
        {
            Log.Error("Organizer run failed", ex);
            State = Phase.Failed;
        }
        Raise();
    }

    /// <summary>Attributed files at the top of Downloads and the Telegram Desktop folder (never
    /// WhatsApp's own media store: moving files out of an app's private data would break it).</summary>
    public static List<OrganizerSource> Sources(MessagingPaths paths, MessagingSnapshot snapshot)
    {
        var (found, _) = MessagingDownloads.ScanCore(paths, snapshot, new HashSet<string>(), [], DateTime.Now,
            MessagingDownloads.ReadZoneIdentifier, whatsAppRunning: true);
        return found.Where(c => c.Source is MessagingSource.Downloads or MessagingSource.TelegramFolder)
            .Select(c => new OrganizerSource(c.Path, c.Size, c.DownloadedAt, c.ModifiedAt, c.Category))
            .ToList();
    }

    /// <summary>One organization pass. Pure apart from <paramref name="ops"/>.</summary>
    public static OrganizerRunResult RunCore(IEnumerable<OrganizerSource> sources, OrganizerSettings settings, List<OrganizerRecord> records,
        DateTime now, IOrganizerFileOps ops)
    {
        var before = records.Where(r => ops.Exists(r.DestinationPath)).ToList();
        var current = new List<OrganizerRecord>(before);
        var actions = new List<UndoAction>();
        int moved = 0, duplicates = 0, failed = 0;
        DateTime? nextEligible = null;

        foreach (var source in sources.OrderBy(s => s.DownloadedAt))
        {
            if (!settings.Categories.Contains(source.Category)) continue;
            if (ProtectedPaths.IsInside(source.Path, settings.Destination)) continue;
            if (!MessagingRules.IsStableForOrganization(source.DownloadedAt, source.ModifiedAt, now, settings.DelayMinutes))
            {
                var latest = source.DownloadedAt > source.ModifiedAt ? source.DownloadedAt : source.ModifiedAt;
                var eligible = latest.AddMinutes(MessagingRules.SanitizedOrganizerDelayMinutes(settings.DelayMinutes));
                if (nextEligible is null || eligible < nextEligible) nextEligible = eligible;
                continue;
            }
            if (ops.IsLocked(source.Path))
            {
                var retry = now.AddMinutes(1);
                if (nextEligible is null || retry < nextEligible) nextEligible = retry;
                continue;
            }
            var digest = ops.Hash(source.Path);
            if (digest is null)
            {
                failed++;
                continue;
            }
            // The organized copy is re-hashed before another copy is discarded.
            var existing = current.FirstOrDefault(r => r.Digest == digest && r.Size == source.Size && ops.Exists(r.DestinationPath)
                                                       && ops.Hash(r.DestinationPath) == digest);
            if (existing is not null)
            {
                switch (settings.Duplicate)
                {
                    case DuplicateAction.TrashNew:
                        if (ops.Recycle(source.Path, source.Size))
                        {
                            duplicates++;
                            actions.Add(new UndoAction("trash", source.Path, null));
                        }
                        else failed++;
                        continue;
                    case DuplicateAction.ReplaceExisting:
                        if (!ops.Recycle(existing.DestinationPath, existing.Size))
                        {
                            failed++;
                            continue;
                        }
                        actions.Add(new UndoAction("trash", existing.DestinationPath, null));
                        if (ops.Move(source.Path, existing.DestinationPath))
                        {
                            duplicates++;
                            current.Remove(existing);
                            current.Add(existing with { OriginalName = Path.GetFileName(source.Path), OrganizedAt = now });
                            actions.Add(new UndoAction("move", existing.DestinationPath, source.Path));
                        }
                        else failed++;
                        continue;
                    case DuplicateAction.KeepBoth:
                        duplicates++;
                        break; // falls through to a regular move under a unique name
                }
            }
            var folder = Path.Combine([settings.Destination, .. MessagingRules.OrganizerRelativeComponents(settings.Layout, source.Category, source.DownloadedAt)]);
            var destination = MessagingRules.UniqueName(folder, Path.GetFileName(source.Path), ops.Exists);
            if (ops.Move(source.Path, destination))
            {
                moved++;
                current.Add(new OrganizerRecord(digest, destination, Path.GetFileName(source.Path), source.Size, now));
                actions.Add(new UndoAction("move", destination, source.Path));
            }
            else
            {
                failed++;
            }
        }
        var undo = actions.Count == 0 ? null : new UndoTransaction(Guid.NewGuid(), actions, before, current, now);
        return new OrganizerRunResult(moved, duplicates, failed, current, undo, nextEligible);
    }

    // -------------------------------------------------------------------- undo

    public async Task UndoLastRunAsync()
    {
        if (IsBusy) return;
        var transactions = ValidUndoTransactions(DateTime.Now);
        if (transactions.Count == 0) return;
        var transaction = transactions[^1];
        State = Phase.Undoing;
        Raise();
        var ops = Ops;
        var failed = await Task.Run(() => PerformUndo(transaction, ops)).ConfigureAwait(true);
        SaveRecords(RecordsAfterUndo(transaction, LoadRecords()));
        transactions.Remove(transaction);
        SaveUndo(transactions);
        State = failed == 0 ? Phase.Done : Phase.Failed;
        LastResult = (0, 0, failed);
        Raise();
        _ = MessagingDownloads.Shared.ScanAsync();
    }

    /// <summary>Moves organized files back where they came from (newest step first). Copies that
    /// went to the Recycle Bin stay there. Returns the number of steps that could not be undone.</summary>
    public static int PerformUndo(UndoTransaction transaction, IOrganizerFileOps ops)
    {
        var failed = 0;
        foreach (var action in Enumerable.Reverse(transaction.Actions))
        {
            if (action.Kind != "move" || action.RestorePath is null) continue;
            if (!ops.Exists(action.CurrentPath) || ops.Exists(action.RestorePath) || !ops.Move(action.CurrentPath, action.RestorePath)) failed++;
        }
        return failed;
    }

    public static List<OrganizerRecord> RecordsAfterUndo(UndoTransaction transaction, List<OrganizerRecord> current)
    {
        var added = transaction.RecordsAfter.Select(r => r.DestinationPath)
            .Except(transaction.RecordsBefore.Select(r => r.DestinationPath), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current.Where(r => !added.Contains(r.DestinationPath)).ToList();
    }

    // ------------------------------------------------------------- persistence

    public static IReadOnlyList<string> ManagedDestinationPaths() => LoadRecords().Select(r => r.DestinationPath).ToList();

    public static List<OrganizerRecord> LoadRecords() => Load<List<OrganizerRecord>>(RecordsFile) ?? [];

    private static void SaveRecords(List<OrganizerRecord> records) => Save(RecordsFile, records);

    public static List<UndoTransaction> ValidUndoTransactions(DateTime now) =>
        (Load<List<UndoTransaction>>(UndoFile) ?? []).Where(t => MessagingRules.OrganizerUndoIsValid(t.CreatedAt, now)).ToList();

    private static void SaveUndo(List<UndoTransaction> transactions) => Save(UndoFile, transactions);

    private static T? Load<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Organizer: reading {file} failed: {ex.Message}");
            return null;
        }
    }

    private static void Save<T>(string file, T value)
    {
        try
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Organizer: saving {file} failed: {ex.Message}");
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Organizer observer failed", ex); }
    }
}

/// <summary>Marshals watcher callbacks (thread pool) onto the UI thread.</summary>
internal static class UiThread
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.BeginInvoke(action);
    }
}
