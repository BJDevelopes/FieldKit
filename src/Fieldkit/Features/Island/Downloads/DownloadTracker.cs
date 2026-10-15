// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Fieldkit.Features.Island.Downloads;

/// <summary>A file in the watched folder: in progress (a browser's partial file) or saved.</summary>
public sealed record DownloadItem(
    string Path,
    string Name,
    long ReceivedBytes,
    double? Fraction,
    bool Completed,
    bool Active,
    DateTime Date);

/// <summary>A partial file seen in a scan.</summary>
public sealed record PartialFile(string Path, string ExpectedPath, long Size, DateTime Modified, long Identity);

/// <summary>A folder scan's result.</summary>
public sealed record DownloadScan(IReadOnlyList<PartialFile> Partials, IReadOnlyList<DownloadItem> Files);

/// <summary>
/// The downloads logic (port of upstream <c>NotchDownloadSupport</c>): partial files are
/// <c>.crdownload</c> (Chrome, Edge), <c>.download</c> and <c>.part</c> (Firefox); progress is the bytes
/// received (browsers do not record the total), "active" means written in the last 120 s, and a download
/// finishes when its partial disappears and the expected file exists as the same file (renamed in place).
/// </summary>
public static class DownloadTracker
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(120);
    public const int MaximumPartials = 32;
    public const int MaximumListed = 200;
    public const int MaximumFinished = 5;
    public static readonly TimeSpan FinishedLinger = TimeSpan.FromSeconds(15);

    /// <summary>The file a partial will become, or null when it is not a partial.</summary>
    public static string? ExpectedPath(string partial)
    {
        var ext = System.IO.Path.GetExtension(partial).TrimStart('.').ToLowerInvariant();
        return ext is "crdownload" or "download" or "part" ? partial[..^(ext.Length + 1)] : null;
    }

    /// <summary>Items for a scan: partials (newest first) then files, newest first, capped.</summary>
    public static List<DownloadItem> Items(DownloadScan scan, IReadOnlyList<DownloadItem> finished, DateTime now)
    {
        var items = new List<DownloadItem>();
        foreach (var p in scan.Partials.Take(MaximumPartials))
        {
            items.Add(new DownloadItem(p.Path, System.IO.Path.GetFileName(p.ExpectedPath), p.Size, null, false,
                now - p.Modified < ActiveWindow, p.Modified));
        }
        var seen = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var f in finished)
            if (seen.Add(f.Path)) items.Add(f);
        foreach (var f in scan.Files)
            if (seen.Add(f.Path)) items.Add(f);
        return items
            .OrderByDescending(i => !i.Completed && i.Active)
            .ThenByDescending(i => i.Date)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumListed)
            .ToList();
    }

    /// <summary>
    /// Compares the partials of two scans: a partial that disappeared while its expected file now exists as the
    /// same file (same identity) completed; one whose expected file is missing too failed.
    /// </summary>
    public static (List<PartialFile> Completed, List<PartialFile> Failed) Compare(
        IReadOnlyList<PartialFile> before, IReadOnlyList<PartialFile> after, Func<string, long?> identityOf)
    {
        var completed = new List<PartialFile>();
        var failed = new List<PartialFile>();
        var current = after.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = after.Select(p => p.ExpectedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in before)
        {
            if (current.Contains(old.Path) || identityOf(old.Path) is not null) continue;
            var expected = identityOf(old.ExpectedPath);
            if (expected is { } id)
            {
                // Renamed in place: the finished file is the partial itself.
                if (old.Identity == 0 || id == old.Identity) completed.Add(old);
            }
            else if (!targets.Contains(old.ExpectedPath))
            {
                failed.Add(old);
            }
        }
        return (completed, failed);
    }

    /// <summary>Scans a folder's direct children (hidden files and links skipped).</summary>
    public static DownloadScan Scan(string folder, Func<string, long> identity)
    {
        var partials = new List<PartialFile>();
        var files = new List<DownloadItem>();
        var dir = new DirectoryInfo(folder);
        foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }))
        {
            var expected = ExpectedPath(info.FullName);
            if (expected is not null && partials.Count < MaximumPartials)
            {
                var size = info is FileInfo fi ? fi.Length : 0;
                partials.Add(new PartialFile(info.FullName, expected, size, info.LastWriteTime, identity(info.FullName)));
                continue;
            }
            if (expected is not null) continue;
            var date = info.CreationTime > info.LastWriteTime ? info.CreationTime : info.LastWriteTime;
            files.Add(new DownloadItem(info.FullName, info.Name, info is FileInfo f ? f.Length : 0, 1, true, false, date));
        }
        return new DownloadScan(
            partials.OrderByDescending(p => p.Modified).ToList(),
            files.OrderByDescending(f => f.Date).Take(MaximumListed).ToList());
    }

    /// <summary>"12.4 MB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[u]}";
    }
}
