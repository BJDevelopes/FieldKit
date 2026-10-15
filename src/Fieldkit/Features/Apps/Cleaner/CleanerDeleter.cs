// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>Everything the clean pass does to the system, behind one seam so tests never touch it.</summary>
public interface ICleanerDeleter
{
    /// <summary>Moves a file or folder to the Recycle Bin. Must fail (never delete permanently)
    /// when the Recycle Bin cannot keep it.</summary>
    bool Recycle(string path, long size, bool allowProtected);

    /// <summary>Removes HKCU Run value <paramref name="name"/> if it still holds <paramref name="expectedCommand"/>,
    /// after a .reg backup of the key succeeded.</summary>
    bool RemoveRunValue(string name, string expectedCommand);

    /// <summary>Empties the Recycle Bin (permanent).</summary>
    bool EmptyRecycleBin();

    bool IsAnyRunning(IReadOnlyCollection<string> processNames);
}

/// <summary>The real thing: Recycle Bin moves through the shared <see cref="RecycleBin"/>, guarded
/// so nothing is ever deleted for good by a Recycle Bin that cannot hold it.</summary>
public sealed class SystemCleanerDeleter : ICleanerDeleter
{
    public static readonly SystemCleanerDeleter Instance = new();

    public bool Recycle(string path, long size, bool allowProtected)
    {
        // RecycleBin.Send refuses anything the Recycle Bin cannot keep.
        var result = RecycleBin.Send([path], _ => size, allowProtected);
        if (result.Recycled.Count != 1)
            Log.Warn($"Cleaner: {path} was left in place ({result.Failed.FirstOrDefault().Item2})");
        return result.Recycled.Count == 1;
    }

    public bool RemoveRunValue(string name, string expectedCommand)
    {
        try
        {
            using (var read = Registry.CurrentUser.OpenSubKey(CleanerScanner.RunKey))
            {
                if (read?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string current
                    || current != expectedCommand) return false;
            }
            var backup = RegistryBackup.ExportAsync($@"HKEY_CURRENT_USER\{CleanerScanner.RunKey}", "Startup " + name)
                .GetAwaiter().GetResult();
            if (backup is null) return false;
            using var key = Registry.CurrentUser.OpenSubKey(CleanerScanner.RunKey, writable: true);
            if (key is null) return false;
            key.DeleteValue(name, throwOnMissingValue: false);
            Log.Info($"Cleaner: removed orphaned startup entry {name} (backup {backup})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleaner: removing startup entry {name} failed: {ex.Message}");
            return false;
        }
    }

    public bool EmptyRecycleBin() => CleanerNative.EmptyRecycleBin();

    public bool IsAnyRunning(IReadOnlyCollection<string> processNames)
    {
        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (running) return true;
        }
        return false;
    }
}

/// <summary>The clean pass itself: re-checks every item, then removes it through the deleter.</summary>
public static class CleanerEngine
{
    public sealed record Outcome(long Freed, int Failed);

    /// <param name="ownerTokens">Living-owner tokens for re-checking leftovers right before removal
    /// (null skips that check only when no leftover is selected).</param>
    public static Outcome Clean(IReadOnlyList<CleanerItem> chosen, ICleanerDeleter deleter, CleanerPaths paths,
        IReadOnlySet<string>? ownerTokens, CancellationToken ct = default)
    {
        long freed = 0;
        var failed = 0;
        var critical = CleanerRules.CriticalPaths(paths).ToList();

        // Emptying the Recycle Bin MUST come first: the row means the Recycle Bin as it was
        // scanned. Running it after the moves would wipe what this clean just made recoverable.
        foreach (var item in chosen.Where(i => i.Kind == CleanerItemKind.RecycleBin))
        {
            if (deleter.EmptyRecycleBin()) freed += item.Size;
            else failed++;
        }

        foreach (var item in chosen.Where(i => i.Kind != CleanerItemKind.RecycleBin))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Processes.Length > 0 && deleter.IsAnyRunning(item.Processes))
            {
                failed++;
                continue;
            }
            switch (item.Kind)
            {
                case CleanerItemKind.RunValue:
                    var removed = item.RunValueName is not null && item.RunCommand is not null
                                  && deleter.RemoveRunValue(item.RunValueName, item.RunCommand);
                    if (!removed) failed++;
                    break;
                case CleanerItemKind.FolderContents:
                    if (!MayRemoveFolderContents(item, paths)) { failed++; break; }
                    foreach (var child in CleanerScanner.SafeEntries(item.Path, "*"))
                    {
                        if (CleanerNative.IsReparsePoint(child) || !CleanerRules.MayRemovePath(child, critical)) { failed++; continue; }
                        var (size, _) = TreeStats.Measure(child, ct);
                        if (deleter.Recycle(child, size, allowProtected: true)) freed += size;
                        else failed++;
                    }
                    break;
                default:
                    if (!MayRemove(item, paths, critical, ownerTokens)) { failed++; break; }
                    if (deleter.Recycle(item.Path, item.Size, allowProtected: false)) freed += item.Size;
                    else failed++;
                    break;
            }
        }
        return new Outcome(freed, failed);
    }

    /// <summary>The last line of defense, right before a path is moved (upstream mayRemove).</summary>
    public static bool MayRemove(CleanerItem item, CleanerPaths paths, IReadOnlyList<string> critical, IReadOnlySet<string>? ownerTokens)
    {
        var path = item.Path;
        if (!CleanerRules.MayRemovePath(path, critical)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        if (CleanerNative.IsReparsePoint(path)) return false;
        if (item.Identity is null || CleanerNative.Identity(path) != item.Identity) return false;
        if (!string.Equals(Path.GetFullPath(path).TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        switch (item.Category)
        {
            case CleanerCategory.Leftovers:
                var parent = Path.GetDirectoryName(path);
                if (parent is null || !CleanerScanner.LeftoverRoots(paths).Any(r => string.Equals(r.TrimEnd('\\'), parent, StringComparison.OrdinalIgnoreCase))) return false;
                if (!CleanerRules.IsLeftoverCandidateName(item.Name)) return false;
                if (ownerTokens is null) return false;
                var children = CleanerScanner.SafeDirectories(path).Select(Path.GetFileName).OfType<string>();
                if (CleanerRules.IsOwned(item.Name, children, ownerTokens)) return false;
                break;
            case CleanerCategory.Screenshots:
                if (!string.Equals(Path.GetDirectoryName(path), paths.Screenshots.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                if (!CleanerRules.ScreenshotKeepsDefaultName(Path.GetFileName(path), File.GetCreationTime(path))) return false;
                break;
            case CleanerCategory.Temp:
                if (!string.Equals(Path.GetDirectoryName(path), paths.Temp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case CleanerCategory.LoginItems:
                if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case CleanerCategory.SystemCaches:
                return false; // only ever cleaned as folder contents
        }
        return true;
    }

    private static bool MayRemoveFolderContents(CleanerItem item, CleanerPaths paths)
    {
        if (item.Category != CleanerCategory.SystemCaches) return false;
        if (!CleanerRules.SystemCacheLocations.Any(l => string.Equals(paths.Expand(l.Pattern), item.Path, StringComparison.OrdinalIgnoreCase))) return false;
        return Directory.Exists(item.Path) && !CleanerNative.IsReparsePoint(item.Path)
               && item.Identity is not null && CleanerNative.Identity(item.Path) == item.Identity;
    }
}
