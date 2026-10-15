// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Fieldkit.Features.Files.Duplicates;

/// <summary>One file the scan found.</summary>
public sealed record FileCandidate(string Path, long Size, DateTime ModifiedUtc);

/// <summary>Files with identical contents.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<FileCandidate> Files)
{
    /// <summary>What removing every copy but one frees.</summary>
    public long Wasted => Size * Math.Max(0, Files.Count - 1);
}

/// <summary>Which copy stays when the list is pre-selected.</summary>
public enum KeepRule { Oldest, Newest, ShortestPath }

/// <summary>Pure rules of the duplicate finder (unit tested).</summary>
public static class DuplicateLogic
{
    /// <summary>Bytes read from the start of each file before a full read is worth it.</summary>
    public const int PartialBytes = 64 * 1024;

    public static readonly long[] MinSizeChoices = [0, 100_000, 1_000_000, 10_000_000, 100_000_000];
    public const long DefaultMinSize = 1_000_000;

    public static long SanitizedMinSize(long value) => MinSizeChoices.Contains(value) ? value : DefaultMinSize;

    /// <summary>
    /// Folders never entered: version control and package caches (removing a "duplicate" there breaks
    /// a repository or a project), and Windows' own bookkeeping folders.
    /// </summary>
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", ".venv", "venv", "__pycache__", "packages", ".nuget",
        "$RECYCLE.BIN", "System Volume Information", "AppData",
    };

    public static bool IsSkippedFolder(string name) => SkippedFolderNames.Contains(name);

    /// <summary>The usual places people keep their own files.</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        return Normalized(list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)));
    }

    /// <summary>Distinct full paths, dropping any folder already inside another one in the list.</summary>
    public static List<string> Normalized(IEnumerable<string> folders)
    {
        var full = folders
            .Select(f => Path.GetFullPath(f).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Keeps the given order; a folder inside another listed folder goes.
        return full.Where(folder => !full.Any(other => !other.Equals(folder, StringComparison.OrdinalIgnoreCase) && IsInside(folder, other)))
            .ToList();
    }

    private static bool IsInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files that share a size with at least one other (only those can be duplicates). Empty files are ignored.</summary>
    public static List<List<FileCandidate>> SameSize(IEnumerable<FileCandidate> files) =>
        files.Where(f => f.Size > 0)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

    /// <summary>Groups of two or more files that share <paramref name="keyOf"/> (a hash); files without a key drop out.</summary>
    public static List<List<FileCandidate>> SameKey(IEnumerable<FileCandidate> files, Func<FileCandidate, string?> keyOf) =>
        files.Select(f => (File: f, Key: keyOf(f)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.File).ToList())
            .ToList();

    /// <summary>The copy <paramref name="rule"/> keeps.</summary>
    public static FileCandidate Keeper(DuplicateGroup group, KeepRule rule) => rule switch
    {
        KeepRule.Newest => group.Files.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Path.Length).First(),
        KeepRule.ShortestPath => group.Files.OrderBy(f => f.Path.Length).ThenBy(f => f.ModifiedUtc).First(),
        _ => group.Files.OrderBy(f => f.ModifiedUtc).ThenBy(f => f.Path.Length).First(),
    };

    /// <summary>Every copy except the keeper of each group, ready to remove.</summary>
    public static HashSet<string> DefaultSelection(IEnumerable<DuplicateGroup> groups, KeepRule rule)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var keep = Keeper(group, rule);
            foreach (var file in group.Files)
            {
                if (!ReferenceEquals(file, keep)) selected.Add(file.Path);
            }
        }
        return selected;
    }

    /// <summary>
    /// The selected paths that may really be removed: a group whose every copy is selected keeps its
    /// <paramref name="rule"/> keeper, so the last copy of a file is never thrown away.
    /// </summary>
    public static List<string> SafeToRemove(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selected, KeepRule rule)
    {
        var result = new List<string>();
        foreach (var group in groups)
        {
            var chosen = group.Files.Where(f => selected.Contains(f.Path)).ToList();
            if (chosen.Count == group.Files.Count) chosen.Remove(Keeper(group, rule));
            result.AddRange(chosen.Select(f => f.Path));
        }
        return result;
    }

    /// <summary>Bytes the removable selection frees.</summary>
    public static long SelectedBytes(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selected, KeepRule rule)
    {
        var removable = new HashSet<string>(SafeToRemove(groups, selected, rule), StringComparer.OrdinalIgnoreCase);
        return groups.SelectMany(g => g.Files.Where(f => removable.Contains(f.Path)).Select(_ => g.Size)).Sum();
    }

    /// <summary>Biggest savings first.</summary>
    public static List<DuplicateGroup> Ordered(IEnumerable<DuplicateGroup> groups) =>
        groups.OrderByDescending(g => g.Wasted).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
}
