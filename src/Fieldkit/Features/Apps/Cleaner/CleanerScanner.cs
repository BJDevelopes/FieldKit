// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>A startup entry: a value of HKCU\...\Run or a shortcut in the Startup folder.</summary>
public sealed record StartupEntry(string Name, string Command, bool IsRunValue, string Location);

/// <summary>What one scan looks at. Everything has a production default; tests inject fixtures.</summary>
public sealed class CleanerScanOptions
{
    /// <summary>False for a pass nobody watches: skips the screenshot search (the user's own folders).</summary>
    public bool Attended { get; init; } = true;
    public int TempAgeDays { get; init; } = CleanerRules.DefaultTempAgeDays;
    public int ScreenshotAgeDays { get; init; } = CleanerRules.DefaultScreenshotAgeDays;
    /// <summary>Windows Update caches: only when running as administrator AND the user opted in.</summary>
    public bool IncludeSystemCaches { get; init; }
    public bool IncludeRecycleBin { get; init; } = true;
    public bool IncludeStartup { get; init; } = true;
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Installed apps (null = read from the registry and Store).</summary>
    public IReadOnlyList<InstalledApp>? InstalledApps { get; init; }
    /// <summary>Names of running processes (null = ask Windows).</summary>
    public IReadOnlyCollection<string>? RunningProcesses { get; init; }
    public Func<IEnumerable<StartupEntry>>? StartupEntries { get; init; }
}

/// <summary>
/// Finds junk in fixed, well-known locations, read-only, and reports it category by category.
/// Links and junctions are never followed and each item is bound to the identity it had when seen.
/// </summary>
public static class CleanerScanner
{
    /// <summary>Scan order: leftovers first so the cache and log passes can skip what they claimed.</summary>
    public static readonly CleanerCategory[] Order =
    [
        CleanerCategory.Leftovers, CleanerCategory.LoginItems, CleanerCategory.Caches, CleanerCategory.Logs,
        CleanerCategory.Developer, CleanerCategory.Temp, CleanerCategory.SystemCaches, CleanerCategory.Trash,
        CleanerCategory.DeviceBackups, CleanerCategory.Screenshots,
    ];

    /// <summary>Runs every category, reporting each one before (<paramref name="starting"/>) and after (<paramref name="found"/>).</summary>
    public static void Scan(CleanerPaths paths, CleanerScanOptions options, CancellationToken ct,
        Action<CleanerCategory>? starting = null, Action<CleanerCategory, List<CleanerItem>>? found = null)
    {
        var running = new HashSet<string>(options.RunningProcesses ?? RunningProcessNames(), StringComparer.OrdinalIgnoreCase);
        var claimed = new List<string>();
        var catalogPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Order)
        {
            ct.ThrowIfCancellationRequested();
            starting?.Invoke(category);
            List<CleanerItem> items;
            try
            {
                items = category switch
                {
                    CleanerCategory.Leftovers => ScanLeftovers(paths, options, running, ct),
                    CleanerCategory.LoginItems => options.IncludeStartup ? ScanStartup(options) : [],
                    CleanerCategory.Caches => ScanCaches(paths, CleanerCategory.Caches, claimed, catalogPaths, running, ct),
                    CleanerCategory.Logs => ScanLogs(paths, claimed, ct),
                    CleanerCategory.Developer => ScanCaches(paths, CleanerCategory.Developer, claimed, catalogPaths, running, ct),
                    CleanerCategory.Temp => ScanTemp(paths, options, ct),
                    CleanerCategory.SystemCaches => options.IncludeSystemCaches ? ScanSystemCaches(paths, ct) : [],
                    CleanerCategory.Trash => options.IncludeRecycleBin ? ScanRecycleBin() : [],
                    CleanerCategory.DeviceBackups => ScanDeviceBackups(paths, ct),
                    CleanerCategory.Screenshots => options.Attended ? ScanScreenshots(paths, options, ct) : [],
                    _ => [],
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"Cleaner: scanning {category} failed: {ex.Message}");
                items = [];
            }
            if (category == CleanerCategory.Leftovers) claimed.AddRange(items.Select(i => i.Path));
            foreach (var i in items) i.Include = i.Recommended && !i.IsBlocked;
            found?.Invoke(category, items.OrderByDescending(i => i.Size).ToList());
        }
    }

    /// <summary>Convenience for tests and the unattended pass: every find in scan order.</summary>
    public static List<CleanerItem> ScanAll(CleanerPaths paths, CleanerScanOptions options, CancellationToken ct = default)
    {
        var all = new List<CleanerItem>();
        Scan(paths, options, ct, found: (_, items) => all.AddRange(items));
        return all;
    }

    // ------------------------------------------------------------- leftovers

    public static IEnumerable<string> LeftoverRoots(CleanerPaths p) => [p.RoamingAppData, p.LocalAppData, p.LocalLow];

    private static List<CleanerItem> ScanLeftovers(CleanerPaths paths, CleanerScanOptions options, IReadOnlyCollection<string> running, CancellationToken ct)
    {
        var apps = options.InstalledApps ?? InstalledAppsProvider.Load();
        var tokens = CleanerRules.OwnerTokens(apps, running);
        var candidates = new List<(string Dir, string Name, long Size, DateTime Newest)>();
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in LeftoverRoots(paths))
        {
            foreach (var dir in SafeDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (!CleanerRules.IsLeftoverCandidateName(name)) continue;
                var children = SafeDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList();
                if (CleanerRules.IsOwned(name, children, tokens))
                {
                    owned.Add(name);
                    continue;
                }
                var (size, newest) = TreeStats.Measure(dir, ct);
                candidates.Add((dir, name, size, newest));
            }
        }
        // A folder of the same name that is owned, or still in use, under another AppData root
        // proves a living (portable or unregistered) app: none of its folders is a leftover.
        var alive = candidates.Where(c => !CleanerRules.IsIdleFor(c.Newest, options.NowUtc, CleanerRules.LeftoverIdleDays))
            .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        alive.UnionWith(owned);
        var found = new List<CleanerItem>();
        foreach (var (dir, name, size, newest) in candidates)
        {
            if (size <= 0 || alive.Contains(name)) continue;
            found.Add(new CleanerItem
            {
                Path = dir,
                Name = name,
                Category = CleanerCategory.Leftovers,
                Size = size,
                Detail = $"Untouched since {newest.ToLocalTime():d}",
                Recommended = CleanerRules.PrecheckLeftovers,
                Identity = CleanerNative.Identity(dir),
            });
        }
        return found;
    }

    // --------------------------------------------------------------- startup

    private static List<CleanerItem> ScanStartup(CleanerScanOptions options)
    {
        var found = new List<CleanerItem>();
        foreach (var entry in (options.StartupEntries ?? StartupEntriesFromSystem)())
        {
            if (CleanerRules.IsProtectedStartupName(entry.Name)) continue;
            if (!CleanerRules.IsOrphanedCommand(entry.Command, File.Exists, IsFixedDrive)) continue;
            found.Add(new CleanerItem
            {
                Path = entry.Location,
                Name = entry.Name,
                Category = CleanerCategory.LoginItems,
                Size = entry.IsRunValue ? 0 : SafeLength(entry.Location),
                Detail = entry.Command,
                Recommended = CleanerRules.PrecheckLoginItems,
                Kind = entry.IsRunValue ? CleanerItemKind.RunValue : CleanerItemKind.Path,
                Identity = entry.IsRunValue ? null : CleanerNative.Identity(entry.Location),
                RunValueName = entry.IsRunValue ? entry.Name : null,
                RunCommand = entry.IsRunValue ? entry.Command : null,
            });
        }
        return found;
    }

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static IEnumerable<StartupEntry> StartupEntriesFromSystem()
    {
        var entries = new List<StartupEntry>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                        entries.Add(new StartupEntry(name, command, true, $@"HKEY_CURRENT_USER\{RunKey}\{name}"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleaner: reading Run key failed: {ex.Message}");
        }
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        foreach (var lnk in SafeFiles(startup, "*.lnk"))
        {
            var target = CleanerNative.ShortcutTarget(lnk);
            if (target is not null) entries.Add(new StartupEntry(Path.GetFileNameWithoutExtension(lnk), Quote(target), false, lnk));
        }
        return entries;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    public static bool IsFixedDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal)
                   && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- caches

    private static List<CleanerItem> ScanCaches(CleanerPaths paths, CleanerCategory category, List<string> claimed,
        HashSet<string> catalogPaths, IReadOnlyCollection<string> running, CancellationToken ct)
    {
        var found = new List<CleanerItem>();
        foreach (var rule in CleanerRules.CacheCatalog.Where(r => r.Category == category))
        {
            foreach (var path in ExpandPattern(paths.Expand(rule.Pattern)))
            {
                ct.ThrowIfCancellationRequested();
                if (!catalogPaths.Add(path) || IsClaimed(path, claimed)) continue;
                if (File.Exists(path) && IsLocked(path)) continue; // e.g. thumbcache in use by Explorer
                var (size, _) = TreeStats.Measure(path, ct);
                if (size <= 0) continue;
                var blocked = rule.Processes.Length > 0 && rule.Processes.Any(running.Contains);
                found.Add(new CleanerItem
                {
                    Path = path,
                    Name = DisplayName(rule, path, paths),
                    Category = category,
                    Size = size,
                    Detail = rule.App,
                    Recommended = rule.Recommended,
                    Identity = CleanerNative.Identity(path),
                    Processes = rule.Processes,
                    BlockedBy = blocked ? rule.App : null,
                });
            }
        }
        if (category != CleanerCategory.Caches) return found;

        // Chromium/Electron apps keep "Cache", "Code Cache", "GPUCache" directly in their AppData folder.
        foreach (var root in new[] { paths.RoamingAppData, paths.LocalAppData })
        {
            foreach (var dir in SafeDirectories(root))
            {
                var owner = Path.GetFileName(dir);
                if (CleanerRules.IsExcludedCacheOwner(owner) || CleanerRules.ExcludedLeftoverNames.Contains(owner)) continue;
                foreach (var cacheName in CleanerRules.AppCacheFolderNames)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.Combine(dir, cacheName);
                    if (!Directory.Exists(path) || CleanerNative.IsReparsePoint(path)) continue;
                    if (!catalogPaths.Add(path) || IsClaimed(path, claimed)) continue;
                    var (size, _) = TreeStats.Measure(path, ct);
                    if (size <= 0) continue;
                    found.Add(new CleanerItem
                    {
                        Path = path,
                        Name = $"{owner} · {cacheName}",
                        Category = CleanerCategory.Caches,
                        Size = size,
                        Detail = owner,
                        Recommended = true,
                        Identity = CleanerNative.Identity(path),
                    });
                }
            }
        }
        return found;
    }

    private static string DisplayName(CacheRule rule, string path, CleanerPaths paths)
    {
        var leaf = Path.GetFileName(path);
        var expanded = paths.Expand(rule.Pattern);
        var parts = expanded.Split('\\');
        var actual = path.Split('\\');
        var star = Array.FindIndex(parts, p => p == "*");
        var profile = star >= 0 && star < actual.Length - 1 ? actual[star] : null;
        return profile is null ? $"{rule.App} · {leaf}" : $"{rule.App} ({profile}) · {leaf}";
    }

    private static bool IsClaimed(string path, List<string> claimed) => claimed.Any(c => ProtectedPaths.IsInside(path, c));

    /// <summary>Expands a pattern whose segments may contain <c>*</c>. Never follows links.</summary>
    public static IEnumerable<string> ExpandPattern(string pattern)
    {
        var segments = pattern.Split('\\');
        var wild = Array.FindIndex(segments, s => s.Contains('*'));
        if (wild < 0)
        {
            if ((Directory.Exists(pattern) || File.Exists(pattern)) && !CleanerNative.IsReparsePoint(pattern)) yield return pattern;
            yield break;
        }
        var baseDir = string.Join('\\', segments[..wild]);
        if (baseDir.EndsWith(':')) baseDir += "\\";
        if (!Directory.Exists(baseDir)) yield break;
        var isLast = wild == segments.Length - 1;
        var rest = string.Join('\\', segments[(wild + 1)..]);
        IEnumerable<string> matches = isLast
            ? SafeEntries(baseDir, segments[wild])
            : SafeDirectories(baseDir, segments[wild]);
        foreach (var match in matches)
        {
            if (CleanerNative.IsReparsePoint(match)) continue;
            if (isLast)
            {
                yield return match;
                continue;
            }
            foreach (var deeper in ExpandPattern(Path.Combine(match, rest))) yield return deeper;
        }
    }

    // ------------------------------------------------------------------ logs

    private static List<CleanerItem> ScanLogs(CleanerPaths paths, List<string> claimed, CancellationToken ct)
    {
        var found = new List<CleanerItem>();
        foreach (var (name, pattern) in CleanerRules.CrashReportLocations)
        {
            var path = paths.Expand(pattern);
            if (!Directory.Exists(path) || CleanerNative.IsReparsePoint(path)) continue;
            var (size, _) = TreeStats.Measure(path, ct);
            if (size <= 0) continue;
            found.Add(new CleanerItem
            {
                Path = path, Name = name, Category = CleanerCategory.Logs, Size = size, Detail = name,
                Recommended = CleanerRules.PrecheckLogs, Identity = CleanerNative.Identity(path),
            });
        }
        foreach (var root in new[] { paths.RoamingAppData, paths.LocalAppData })
        {
            foreach (var dir in SafeDirectories(root))
            {
                var owner = Path.GetFileName(dir);
                if (CleanerRules.IsExcludedCacheOwner(owner) || CleanerRules.ExcludedLeftoverNames.Contains(owner)) continue;
                foreach (var logName in CleanerRules.AppLogFolderNames)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = SafeDirectories(dir).FirstOrDefault(d => string.Equals(Path.GetFileName(d), logName, StringComparison.OrdinalIgnoreCase));
                    if (path is null || CleanerNative.IsReparsePoint(path) || IsClaimed(path, claimed)) continue;
                    var (size, _) = TreeStats.Measure(path, ct);
                    if (size <= 0) continue;
                    found.Add(new CleanerItem
                    {
                        Path = path, Name = $"{owner} · {Path.GetFileName(path)}", Category = CleanerCategory.Logs, Size = size,
                        Detail = owner, Recommended = CleanerRules.PrecheckLogs, Identity = CleanerNative.Identity(path),
                    });
                }
            }
        }
        return found;
    }

    // ------------------------------------------------------------------ temp

    private static List<CleanerItem> ScanTemp(CleanerPaths paths, CleanerScanOptions options, CancellationToken ct)
    {
        var found = new List<CleanerItem>();
        foreach (var entry in SafeEntries(paths.Temp, "*"))
        {
            ct.ThrowIfCancellationRequested();
            if (CleanerNative.IsReparsePoint(entry)) continue;
            var (size, newest) = TreeStats.Measure(entry, ct);
            if (size <= 0 || !CleanerRules.TempIsOldEnough(newest, options.NowUtc, options.TempAgeDays)) continue;
            found.Add(new CleanerItem
            {
                Path = entry, Name = Path.GetFileName(entry), Category = CleanerCategory.Temp, Size = size,
                Recommended = CleanerRules.PrecheckTemp, Identity = CleanerNative.Identity(entry),
            });
        }
        return found;
    }

    // --------------------------------------------------------- system caches

    private static List<CleanerItem> ScanSystemCaches(CleanerPaths paths, CancellationToken ct)
    {
        var found = new List<CleanerItem>();
        foreach (var (name, pattern) in CleanerRules.SystemCacheLocations)
        {
            var path = paths.Expand(pattern);
            if (!Directory.Exists(path) || CleanerNative.IsReparsePoint(path)) continue;
            var (size, _) = TreeStats.Measure(path, ct);
            if (size <= 0) continue;
            found.Add(new CleanerItem
            {
                Path = path, Name = name, Category = CleanerCategory.SystemCaches, Size = size, Detail = name,
                Recommended = CleanerRules.PrecheckSystemCaches, Kind = CleanerItemKind.FolderContents,
                Identity = CleanerNative.Identity(path),
            });
        }
        return found;
    }

    // ----------------------------------------------------------- recycle bin

    private static List<CleanerItem> ScanRecycleBin()
    {
        var (bytes, count) = CleanerNative.QueryRecycleBin();
        if (bytes <= 0 || count <= 0) return [];
        return
        [
            new CleanerItem
            {
                Path = "Recycle Bin", Name = S.CatTrash, Category = CleanerCategory.Trash, Size = bytes,
                Detail = $"{count} items", Recommended = false, Kind = CleanerItemKind.RecycleBin,
            },
        ];
    }

    // -------------------------------------------------------- device backups

    private static List<CleanerItem> ScanDeviceBackups(CleanerPaths paths, CancellationToken ct)
    {
        var found = new List<CleanerItem>();
        foreach (var pattern in CleanerRules.DeviceBackupRoots)
        {
            foreach (var dir in SafeDirectories(paths.Expand(pattern)))
            {
                ct.ThrowIfCancellationRequested();
                if (CleanerNative.IsReparsePoint(dir)) continue;
                var (size, _) = TreeStats.Measure(dir, ct);
                if (size <= 0) continue;
                string? device = null;
                DateTime? date = null;
                var info = Path.Combine(dir, "Info.plist");
                try
                {
                    if (File.Exists(info)) (device, date) = CleanerRules.ParseBackupInfo(File.ReadAllText(info));
                }
                catch { }
                var detail = string.Join(", ", new[] { device, date?.ToLocalTime().ToString("d") }.Where(s => !string.IsNullOrEmpty(s)));
                found.Add(new CleanerItem
                {
                    Path = dir, Name = device ?? Path.GetFileName(dir), Category = CleanerCategory.DeviceBackups, Size = size,
                    Detail = detail.Length == 0 ? Path.GetFileName(dir) : detail,
                    Recommended = CleanerRules.PrecheckDeviceBackups, Identity = CleanerNative.Identity(dir),
                });
            }
        }
        return found;
    }

    // ----------------------------------------------------------- screenshots

    private static List<CleanerItem> ScanScreenshots(CleanerPaths paths, CleanerScanOptions options, CancellationToken ct)
    {
        var days = CleanerRules.SanitizedScreenshotAgeDays(options.ScreenshotAgeDays);
        if (days <= 0 || !Directory.Exists(paths.Screenshots)) return [];
        var found = new List<CleanerItem>();
        var now = options.NowUtc.ToLocalTime();
        foreach (var file in SafeFiles(paths.Screenshots, "*"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(file);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || fi.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                if (!CleanerRules.ScreenshotKeepsDefaultName(fi.Name, fi.CreationTime)) continue;
                if (!CleanerRules.IsForgottenScreenshot(fi.CreationTime, fi.LastWriteTime, fi.LastAccessTime, now, days)) continue;
                if (fi.Length <= 0) continue;
                found.Add(new CleanerItem
                {
                    Path = file, Name = fi.Name, Category = CleanerCategory.Screenshots, Size = fi.Length,
                    Detail = fi.CreationTime.ToString("d"), Recommended = CleanerRules.PrecheckScreenshots,
                    Identity = CleanerNative.Identity(file),
                });
            }
            catch { }
        }
        return found;
    }

    // --------------------------------------------------------------- helpers

    internal static IEnumerable<string> SafeDirectories(string root, string pattern = "*")
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root, pattern, new EnumerationOptions
            {
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false,
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false,
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static IEnumerable<string> SafeEntries(string root, string pattern)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFileSystemEntries(root, pattern, new EnumerationOptions
            {
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false,
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Opening for read without sharing fails while another process holds the file.</summary>
    internal static bool IsLocked(string file)
    {
        try
        {
            using var _ = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static IReadOnlyCollection<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName); }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return names;
    }
}

/// <summary>Size and newest write time of a file or folder tree, never following links.</summary>
public static class TreeStats
{
    public static (long Size, DateTime NewestWriteUtc) Measure(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return (fi.Length, fi.LastWriteTimeUtc);
            }
            if (!Directory.Exists(path)) return (0, DateTime.MinValue);
            var dir = new DirectoryInfo(path);
            long total = 0;
            var newest = dir.LastWriteTimeUtc;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (entry is FileInfo f) total += f.Length;
                    if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
                }
                catch { }
            }
            return (total, newest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (0, DateTime.MinValue);
        }
    }
}
