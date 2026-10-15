// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Files.Duplicates;

/// <summary>Where a scan stands, for the progress line.</summary>
public sealed record DuplicateProgress(string Phase, int FilesSeen, int Hashed, int ToHash);

/// <summary>What a scan found.</summary>
public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, int FilesSeen, int CloudSkipped, int Unreadable);

/// <summary>
/// Finds files with identical contents in three passes, each only on what survived the last: same size,
/// same hash of the first 64 KB, same SHA-256 of the whole file. Hidden and system files, links and
/// cloud-only (OneDrive online-only) files are skipped, so a scan never downloads anything (upstream #877).
/// </summary>
public static class DuplicateScanner
{
    public static DuplicateScanResult Scan(IReadOnlyList<string> roots, long minSize, IProgress<DuplicateProgress>? progress,
        CancellationToken ct)
    {
        var files = new List<FileCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cloud = 0;
        var unreadable = 0;
        var lastReport = Environment.TickCount64;

        void Report(string phase, int hashed = 0, int toHash = 0, bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastReport < 150) return;
            lastReport = now;
            progress?.Report(new DuplicateProgress(phase, files.Count, hashed, toHash));
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var pending = new Stack<string>(DuplicateLogic.Normalized(roots).Where(Directory.Exists));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options); }
            catch { continue; }
            try
            {
                foreach (var entry in entries)
                {
                    if (entry is DirectoryInfo sub)
                    {
                        if (!DuplicateLogic.IsSkippedFolder(sub.Name)) pending.Push(sub.FullName);
                        continue;
                    }
                    if (entry is not FileInfo file) continue;
                    if (CloudFiles.IsCloudOnly(file.Attributes))
                    {
                        cloud++;
                        continue;
                    }
                    long size;
                    try { size = file.Length; }
                    catch { continue; }
                    if (size < Math.Max(1, minSize) || !seen.Add(file.FullName)) continue;
                    files.Add(new FileCandidate(file.FullName, size, file.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that vanished or locked mid-enumeration: keep what was read.
            }
            Report("Looking for files");
        }
        Report("Looking for files", force: true);

        // Pass 2: the first 64 KB.
        var sameSize = DuplicateLogic.SameSize(files);
        var toHash = sameSize.Sum(g => g.Count);
        var hashed = 0;
        var candidates = new List<List<FileCandidate>>();
        foreach (var group in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            candidates.AddRange(DuplicateLogic.SameKey(group, f =>
            {
                hashed++;
                Report("Comparing files", hashed, toHash);
                var hash = Hash(f.Path, DuplicateLogic.PartialBytes, ct);
                if (hash is null) unreadable++;
                return hash;
            }));
        }

        // Pass 3: whole files (small files were already read in full).
        var groups = new List<DuplicateGroup>();
        toHash = candidates.Where(g => g[0].Size > DuplicateLogic.PartialBytes).Sum(g => g.Count);
        hashed = 0;
        foreach (var group in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (group[0].Size <= DuplicateLogic.PartialBytes)
            {
                groups.Add(new DuplicateGroup(Hash(group[0].Path, DuplicateLogic.PartialBytes, ct) ?? string.Empty, group[0].Size, group));
                continue;
            }
            var hashes = new Dictionary<FileCandidate, string?>();
            foreach (var same in DuplicateLogic.SameKey(group, f =>
                     {
                         hashed++;
                         Report("Checking contents", hashed, toHash);
                         var hash = Hash(f.Path, long.MaxValue, ct);
                         if (hash is null) unreadable++;
                         hashes[f] = hash;
                         return hash;
                     }))
            {
                groups.Add(new DuplicateGroup(hashes[same[0]]!, same[0].Size, same));
            }
        }
        progress?.Report(new DuplicateProgress("Done", files.Count, hashed, toHash));
        Log.Info($"Duplicate scan: {files.Count} files, {groups.Count} groups, {cloud} cloud-only skipped, {unreadable} unreadable");
        return new DuplicateScanResult(DuplicateLogic.Ordered(groups), files.Count, cloud, unreadable);
    }

    /// <summary>SHA-256 of the first <paramref name="limit"/> bytes, or null when the file cannot be read.</summary>
    public static string? Hash(string path, long limit, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long total = 0;
            while (total < limit)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, limit - total);
                var read = stream.Read(buffer, 0, want);
                if (read <= 0) break;
                sha.AppendData(buffer, 0, read);
                total += read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
