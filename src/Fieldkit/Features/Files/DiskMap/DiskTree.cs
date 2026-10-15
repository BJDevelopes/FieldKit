// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Files.DiskMap;

public enum DiskNodeKind { Folder, File, OtherFiles }

/// <summary>A folder, a large file, or the small files of a folder taken together.</summary>
public sealed class DiskNode
{
    public DiskNode(string name, DiskNodeKind kind, DiskNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public DiskNodeKind Kind { get; }
    public DiskNode? Parent { get; private set; }
    public long Size { get; set; }
    /// <summary>Files inside, counting subfolders.</summary>
    public int FileCount { get; set; }
    public List<DiskNode> Children { get; } = [];

    public bool IsFolder => Kind == DiskNodeKind.Folder;

    /// <summary>The full path (the "other files" node has its folder's path).</summary>
    public string Path => Kind == DiskNodeKind.OtherFiles || Parent is null
        ? (Parent?.Path ?? Name)
        : System.IO.Path.Combine(Parent.Path, Name);

    /// <summary>Takes a child out (after it was moved to the Recycle Bin) and shrinks every folder above it.</summary>
    public void Remove(DiskNode child)
    {
        if (!Children.Remove(child)) return;
        child.Parent = null;
        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size -= child.Size;
            node.FileCount -= child.FileCount;
        }
    }

    public IEnumerable<DiskNode> Largest() => Children.OrderByDescending(c => c.Size).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Where a scan stands.</summary>
public sealed record DiskScanProgress(int Files, int Folders, string Current);

/// <summary>What a scan found.</summary>
public sealed record DiskScanResult(DiskNode Root, int Unreadable, long DriveTotal, long DriveFree);

/// <summary>
/// Measures a folder tree. Files of at least <see cref="OwnNodeBytes"/> keep their own node (up to
/// <see cref="FilesPerFolder"/> per folder); the rest of a folder's files become one "other files" node, so a
/// whole drive fits in a few tens of MB. Links are not followed and online-only OneDrive files count as
/// zero (they take no space on this PC and are never opened).
/// </summary>
public static class DiskScanner
{
    public const long OwnNodeBytes = 1_000_000;
    public const int FilesPerFolder = 12;

    public static DiskScanResult Scan(string root, IProgress<DiskScanProgress>? progress, CancellationToken ct)
    {
        root = System.IO.Path.GetFullPath(root);
        var state = new ScanState(progress, ct);
        var name = root.Length <= 3 ? root : root.TrimEnd('\\');
        var node = new DiskNode(name, DiskNodeKind.Folder, null);
        ScanFolder(node, root, state, parallel: true);
        long total = 0, free = 0;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(root)!);
            if (drive.IsReady)
            {
                total = drive.TotalSize;
                free = drive.TotalFreeSpace;
            }
        }
        catch { }
        progress?.Report(new DiskScanProgress(state.Files, state.Folders, string.Empty));
        Log.Info($"Disk scan of {root}: {state.Files} files, {state.Folders} folders, {state.Unreadable} unreadable");
        return new DiskScanResult(node, state.Unreadable, total, free);
    }

    private sealed class ScanState(IProgress<DiskScanProgress>? progress, CancellationToken ct)
    {
        public int Files;
        public int Folders;
        public int Unreadable;
        private long _lastReport = Environment.TickCount64;
        public CancellationToken Token => ct;

        public void Report(string current)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastReport) < 150) return;
            Interlocked.Exchange(ref _lastReport, now);
            progress?.Report(new DiskScanProgress(Files, Folders, current));
        }
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static void ScanFolder(DiskNode node, string path, ScanState state, bool parallel)
    {
        state.Token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref state.Folders);
        var subfolders = new List<(DiskNode Node, string Path)>();
        var files = new List<(string Name, long Size)>();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options))
            {
                if (entry is DirectoryInfo dir)
                {
                    subfolders.Add((new DiskNode(dir.Name, DiskNodeKind.Folder, node), dir.FullName));
                }
                else if (entry is FileInfo file)
                {
                    long size;
                    try { size = CloudFiles.IsCloudOnly(file.Attributes) ? 0 : file.Length; }
                    catch { size = 0; }
                    files.Add((file.Name, size));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Interlocked.Increment(ref state.Unreadable);
        }
        Interlocked.Add(ref state.Files, files.Count);
        state.Report(path);

        if (parallel && subfolders.Count > 1)
        {
            Parallel.ForEach(subfolders, new ParallelOptions { CancellationToken = state.Token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                sub => ScanFolder(sub.Node, sub.Path, state, parallel: false));
        }
        else
        {
            foreach (var sub in subfolders) ScanFolder(sub.Node, sub.Path, state, parallel: false);
        }

        foreach (var (sub, _) in subfolders)
        {
            node.Children.Add(sub);
            node.Size += sub.Size;
            node.FileCount += sub.FileCount;
        }
        AddFiles(node, files);
    }

    /// <summary>Large files get their own node; the rest are summed into one (public for tests).</summary>
    public static void AddFiles(DiskNode folder, IReadOnlyList<(string Name, long Size)> files)
    {
        var own = files.Where(f => f.Size >= OwnNodeBytes).OrderByDescending(f => f.Size).Take(FilesPerFolder).ToList();
        var ownNames = new HashSet<string>(own.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, size) in own)
        {
            folder.Children.Add(new DiskNode(name, DiskNodeKind.File, folder) { Size = size, FileCount = 1 });
        }
        var rest = files.Where(f => !ownNames.Contains(f.Name)).ToList();
        if (rest.Count > 0)
        {
            var restSize = rest.Sum(f => f.Size);
            if (restSize > 0)
            {
                var label = rest.Count == 1 ? rest[0].Name : $"{rest.Count:N0} smaller files";
                folder.Children.Add(new DiskNode(label, rest.Count == 1 ? DiskNodeKind.File : DiskNodeKind.OtherFiles, folder)
                    { Size = restSize, FileCount = rest.Count });
            }
        }
        folder.Size += files.Sum(f => f.Size);
        folder.FileCount += files.Count;
    }
}
