// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner.Messaging;

/// <summary>The folders messaging downloads are looked for in (injected so tests use fixtures).</summary>
public sealed record MessagingPaths(string Downloads, string TelegramFolder, string WhatsAppMedia)
{
    public const string WhatsAppPackage = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm";

    public static MessagingPaths Current()
    {
        var downloads = CleanerNative.DownloadsFolder()
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new MessagingPaths(downloads, Path.Combine(downloads, "Telegram Desktop"),
            Path.Combine(local, "Packages", WhatsAppPackage, "LocalState", "shared", "transfers"));
    }
}

/// <summary>The rule inputs of one scan, read once from settings.</summary>
public sealed record MessagingSnapshot(
    int RetentionDays,
    IReadOnlySet<MessagingCategory> Categories,
    bool IncludeExisting,
    DateTime? AutomaticStart,
    bool WhatsApp,
    bool Telegram)
{
    public static MessagingSnapshot FromSettings() => new(
        MessagingRules.SanitizedRetentionDays(Settings.GetInt(MessagingKeys.RetentionDays, 7)),
        MessagingRules.DecodeCategories(Settings.Has(MessagingKeys.Categories) ? Settings.GetString(MessagingKeys.Categories) : null),
        Settings.GetBool(MessagingKeys.IncludeExisting, false),
        CleanerSchedule.FromStamp(Settings.GetDouble(MessagingKeys.AutomaticStart, 0)),
        Settings.GetBool(MessagingKeys.SourceWhatsApp, true),
        Settings.GetBool(MessagingKeys.SourceTelegram, true));
}

/// <summary>One confirmed messaging download.</summary>
public sealed class MessagingCandidate
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Root { get; init; }
    public bool AllowsDescendants { get; init; }
    public MessagingSource Source { get; init; }
    public MessagingApp App { get; init; }
    public long Size { get; init; }
    public DateTime DownloadedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public MessagingCategory Category { get; init; }
    public bool Organized => Source == MessagingSource.Organized;
    public bool EligibleForRules { get; init; }
    public bool EligibleForAutomaticCleanup { get; init; }
    public bool Excluded { get; set; }
    public bool Include { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Scans only fixed messaging locations, surfaces files Windows attributes to WhatsApp or
/// Telegram, and moves reviewed / eligible ones to the Recycle Bin (upstream
/// WhatsAppDownloadManager). It never reads file contents.
/// </summary>
public sealed class MessagingDownloads
{
    public static MessagingDownloads Shared { get; } = new();

    private MessagingDownloads() { }

    public enum Phase { Idle, Scanning, Results, Cleaning, Done, Failed }

    public Phase State { get; private set; } = Phase.Idle;
    public List<MessagingCandidate> Candidates { get; private set; } = [];
    public bool WhatsAppSkipped { get; private set; }
    public (int Moved, long Bytes, int Failed) LastResult { get; private set; }
    /// <summary>Set while the review window is open: automation never rewrites the user's checkboxes.</summary>
    public bool ReviewVisible { get; set; }

    public Func<MessagingPaths> PathsFactory { get; set; } = MessagingPaths.Current;
    public Func<string, long, bool> Recycler { get; set; } = DefaultRecycle;

    public event Action? Changed;

    private int _token;

    public int SelectedCount => Candidates.Count(c => c.Include);
    public long SelectedBytes => Candidates.Where(c => c.Include).Sum(c => c.Size);
    public long TotalBytes => Candidates.Sum(c => c.Size);
    public int EligibleCount => Candidates.Count(c => c.EligibleForRules && !c.Excluded);
    public bool IsBusy => State is Phase.Scanning or Phase.Cleaning;

    public void Reset()
    {
        _token++;
        Candidates = [];
        State = Phase.Idle;
        Raise();
    }

    public async Task ScanAsync()
    {
        if (IsBusy) return;
        var token = ++_token;
        Candidates = [];
        State = Phase.Scanning;
        Raise();
        var paths = PathsFactory();
        var snapshot = MessagingSnapshot.FromSettings();
        var exclusions = Exclusions();
        var organized = MessagingOrganizer.ManagedDestinationPaths();
        try
        {
            var (found, skipped) = await Task.Run(() => ScanCore(paths, snapshot, exclusions, organized, DateTime.Now, ReadZoneIdentifier,
                IsWhatsAppRunning())).ConfigureAwait(true);
            if (token != _token) return;
            Candidates = found;
            WhatsAppSkipped = skipped;
            State = Phase.Results;
        }
        catch (Exception ex)
        {
            Log.Warn($"Messaging downloads scan failed: {ex.Message}");
            if (token != _token) return;
            State = Phase.Failed;
        }
        Raise();
    }

    public void SetInclude(MessagingCandidate c, bool include)
    {
        if (c.Excluded) return;
        c.Include = include;
        Raise();
    }

    public void SelectRules()
    {
        foreach (var c in Candidates) c.Include = c.EligibleForRules && !c.Excluded;
        Raise();
    }

    public void SelectAutomaticRules()
    {
        foreach (var c in Candidates) c.Include = c.EligibleForAutomaticCleanup && !c.Excluded;
        Raise();
    }

    /// <summary>"Keep": the file is remembered by identity and never selected again.</summary>
    public void Exclude(MessagingCandidate c)
    {
        var set = Exclusions();
        set.Add(c.Id);
        Settings.Set(MessagingKeys.Exclusions, set.Order(StringComparer.Ordinal).ToArray());
        c.Excluded = true;
        c.Include = false;
        Raise();
    }

    public void RemoveExclusion(MessagingCandidate c)
    {
        var set = Exclusions();
        set.Remove(c.Id);
        Settings.Set(MessagingKeys.Exclusions, set.Order(StringComparer.Ordinal).ToArray());
        c.Excluded = false;
        c.Include = c.EligibleForRules;
        Raise();
    }

    public static HashSet<string> Exclusions() =>
        new(Settings.Get<string[]>(MessagingKeys.Exclusions, []) ?? [], StringComparer.Ordinal);

    public async Task CleanSelectedAsync(bool automatic)
    {
        var chosen = Candidates.Where(c => c.Include).ToList();
        if (chosen.Count == 0 || IsBusy) return;
        var token = ++_token;
        State = Phase.Cleaning;
        Raise();
        var snapshot = MessagingSnapshot.FromSettings();
        var recycler = Recycler;
        var result = await Task.Run(() => CleanCore(chosen, automatic, snapshot, DateTime.Now, ReadZoneIdentifier, recycler)).ConfigureAwait(true);
        if (token != _token) return;
        Candidates.RemoveAll(c => result.MovedIds.Contains(c.Id));
        Record(result.MovedIds.Count, result.Bytes, result.Failed);
        LastResult = (result.MovedIds.Count, result.Bytes, result.Failed);
        State = Phase.Done;
        Raise();
    }

    /// <summary>A scheduled pass with nothing eligible is still proof the automation is alive.</summary>
    public void CompleteAutomaticWithoutCleaning()
    {
        Record(0, 0, 0);
        LastResult = (0, 0, 0);
        State = Phase.Done;
        Raise();
    }

    private static void Record(int moved, long bytes, int failed)
    {
        Settings.Set(MessagingKeys.LastCleanup, CleanerSchedule.ToStamp(DateTime.Now));
        Settings.Set(MessagingKeys.LastCleanupCount, moved);
        Settings.Set(MessagingKeys.LastCleanupBytes, bytes);
        Settings.Set(MessagingKeys.LastCleanupFailed, failed);
    }

    private void Raise()
    {
        void Invoke()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("Messaging downloads observer failed", ex); }
        }
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Invoke();
        else d.BeginInvoke(Invoke);
    }

    // ------------------------------------------------------------------ core

    public sealed record CleanResult(HashSet<string> MovedIds, long Bytes, int Failed);

    /// <summary>Every confirmed messaging download, newest first. Read-only.</summary>
    public static (List<MessagingCandidate> Found, bool WhatsAppSkipped) ScanCore(MessagingPaths paths, MessagingSnapshot settings,
        IReadOnlySet<string> exclusions, IEnumerable<string> organizedPaths, DateTime now, Func<string, string?> readZone, bool whatsAppRunning)
    {
        var found = new List<MessagingCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(MessagingCandidate? c)
        {
            if (c is not null && seen.Add(c.Path)) found.Add(c);
        }

        foreach (var file in CleanerScanner.SafeFiles(paths.Downloads, "*"))
        {
            var app = MessagingRules.AttributedApp(readZone(file));
            if (app is null || !SourceEnabled(app.Value, settings)) continue;
            Add(Candidate(file, paths.Downloads, false, MessagingSource.Downloads, app.Value, settings, exclusions, now));
        }
        if (settings.Telegram)
        {
            foreach (var file in CleanerScanner.SafeFiles(paths.TelegramFolder, "*"))
                Add(Candidate(file, paths.TelegramFolder, false, MessagingSource.TelegramFolder, MessagingApp.Telegram, settings, exclusions, now));
        }
        var skipped = false;
        if (settings.WhatsApp && Directory.Exists(paths.WhatsAppMedia))
        {
            if (whatsAppRunning)
            {
                skipped = true;
            }
            else
            {
                foreach (var file in RecursiveFiles(paths.WhatsAppMedia))
                    Add(Candidate(file, paths.WhatsAppMedia, true, MessagingSource.WhatsAppMedia, MessagingApp.WhatsApp, settings, exclusions, now));
            }
        }
        foreach (var organized in organizedPaths)
        {
            var dir = Path.GetDirectoryName(organized);
            if (dir is null || string.Equals(dir, paths.Downloads.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            Add(Candidate(organized, dir, false, MessagingSource.Organized, MessagingApp.WhatsApp, settings, exclusions, now));
        }
        var sorted = found.OrderByDescending(c => c.DownloadedAt).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return (sorted, skipped);
    }

    private static bool SourceEnabled(MessagingApp app, MessagingSnapshot s) => app == MessagingApp.WhatsApp ? s.WhatsApp : s.Telegram;

    /// <summary>Checks one file and builds its candidate, or null when it does not qualify.</summary>
    public static MessagingCandidate? Candidate(string path, string root, bool allowsDescendants, MessagingSource source, MessagingApp app,
        MessagingSnapshot settings, IReadOnlySet<string> exclusions, DateTime now)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root).TrimEnd('\\');
            var inLocation = allowsDescendants
                ? ProtectedPaths.IsInside(full, rootFull) && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetDirectoryName(full), rootFull, StringComparison.OrdinalIgnoreCase);
            if (!inLocation || MessagingRules.IsIncompleteFile(Path.GetExtension(full))) return null;
            var fi = new FileInfo(full);
            if (!fi.Exists) return null;
            if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            if (fi.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || fi.Name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return null;
            var identity = CleanerNative.Identity(full);
            if (identity is null) return null;
            var downloaded = fi.CreationTime;
            var modified = fi.LastWriteTime;
            var category = MessagingRules.Category(fi.Extension);
            var rules = MessagingRules.IsEligibleForRules(category, downloaded, modified, now, settings.RetentionDays, settings.Categories);
            var automatic = source != MessagingSource.Organized && MessagingRules.IsEligibleForAutomaticCleanup(category, downloaded, modified, now,
                settings.RetentionDays, settings.Categories, settings.IncludeExisting, settings.AutomaticStart);
            var id = identity.Value.ToString();
            var excluded = exclusions.Contains(id);
            return new MessagingCandidate
            {
                Id = id,
                Path = full,
                Root = rootFull,
                AllowsDescendants = allowsDescendants,
                Source = source,
                App = app,
                Size = fi.Length,
                DownloadedAt = downloaded,
                ModifiedAt = modified,
                Category = category,
                EligibleForRules = rules,
                EligibleForAutomaticCleanup = automatic,
                Excluded = excluded,
                Include = rules && !excluded,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Re-checks each chosen file right before moving it; an automatic pass also skips
    /// anything no longer eligible for automation.</summary>
    public static CleanResult CleanCore(IReadOnlyList<MessagingCandidate> chosen, bool automatic, MessagingSnapshot settings, DateTime now,
        Func<string, string?> readZone, Func<string, long, bool> recycle)
    {
        var moved = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        var failed = 0;
        foreach (var original in chosen)
        {
            var current = Candidate(original.Path, original.Root, original.AllowsDescendants, original.Source, original.App, settings,
                new HashSet<string>(), now);
            if (current is null || current.Id != original.Id)
            {
                failed++;
                continue;
            }
            if (original.Source == MessagingSource.Downloads && MessagingRules.AttributedApp(readZone(current.Path)) is null)
            {
                failed++;
                continue;
            }
            if (automatic && !current.EligibleForAutomaticCleanup) continue;
            if (recycle(current.Path, current.Size))
            {
                moved.Add(current.Id);
                bytes += current.Size;
            }
            else
            {
                failed++;
            }
        }
        return new CleanResult(moved, bytes, failed);
    }

    public static string? ReadZoneIdentifier(string path)
    {
        try
        {
            return File.ReadAllText(path + ":Zone.Identifier");
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWhatsAppRunning()
    {
        var processes = Process.GetProcessesByName("WhatsApp");
        var running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
    }

    private static IEnumerable<string> RecursiveFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).Take(20_000).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A detached instance showing given results (previews only).</summary>
    internal static MessagingDownloads ForPreview(List<MessagingCandidate> candidates) =>
        new() { Candidates = candidates, State = Phase.Results };

    private static bool DefaultRecycle(string path, long size) => SystemCleanerDeleter.Instance.Recycle(path, size, allowProtected: false);
}
