// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Files.Duplicates;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Files;

public class DuplicateLogicTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FileCandidate F(string path, long size = 10, int days = 0) => new(path, size, T0.AddDays(days));

    private static DuplicateGroup G(params FileCandidate[] files) => new("h", files[0].Size, files);

    [Fact]
    public void OnlySharedSizesAreCandidatesAndEmptyFilesAreIgnored()
    {
        var groups = DuplicateLogic.SameSize([F(@"C:\a", 5), F(@"C:\b", 5), F(@"C:\c", 7), F(@"C:\d", 0), F(@"C:\e", 0)]);
        Assert.Single(groups);
        Assert.Equal([@"C:\a", @"C:\b"], groups[0].Select(f => f.Path));
    }

    [Fact]
    public void FilesThatCannotBeReadDropOut()
    {
        var groups = DuplicateLogic.SameKey([F(@"C:\a"), F(@"C:\b"), F(@"C:\c")], f => f.Path == @"C:\c" ? null : "same");
        Assert.Equal(2, Assert.Single(groups).Count);
    }

    [Theory]
    [InlineData(KeepRule.Oldest, @"C:\old\x")]
    [InlineData(KeepRule.Newest, @"C:\new\deeper\x")]
    [InlineData(KeepRule.ShortestPath, @"C:\s\x")]
    public void KeeperFollowsTheRule(KeepRule rule, string expected)
    {
        var group = G(F(@"C:\old\x", days: 0), F(@"C:\new\deeper\x", days: 9), F(@"C:\s\x", days: 5));
        Assert.Equal(expected, DuplicateLogic.Keeper(group, rule).Path);
    }

    [Fact]
    public void DefaultSelectionLeavesOneCopyPerGroup()
    {
        var a = G(F(@"C:\1\a", days: 0), F(@"C:\2\a", days: 1), F(@"C:\3\a", days: 2));
        var b = G(F(@"C:\1\b", 50), F(@"C:\2\b", 50, days: 3));
        var selected = DuplicateLogic.DefaultSelection([a, b], KeepRule.Oldest);
        Assert.Equal(3, selected.Count);
        Assert.DoesNotContain(@"C:\1\a", selected);
        Assert.DoesNotContain(@"C:\1\b", selected);
    }

    [Fact]
    public void TheLastCopyIsNeverRemovedEvenWhenAllAreTicked()
    {
        var group = G(F(@"C:\1\a", days: 0), F(@"C:\2\a", days: 1));
        var all = new HashSet<string>([@"C:\1\a", @"C:\2\a"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal([@"C:\2\a"], DuplicateLogic.SafeToRemove([group], all, KeepRule.Oldest));
        Assert.Equal(10, DuplicateLogic.SelectedBytes([group], all, KeepRule.Oldest));
    }

    [Fact]
    public void NestedFoldersAreScannedOnce()
    {
        var folders = DuplicateLogic.Normalized([@"C:\Users\me\Documents\Photos", @"C:\Users\me\Documents", @"c:\users\me\documents\", @"C:\Users\me\Docs"]);
        Assert.Equal([@"C:\Users\me\Docs", @"C:\Users\me\Documents"], folders.OrderBy(f => f));
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("AppData", true)]
    [InlineData("Photos", false)]
    public void RepositoriesAndPackageFoldersAreSkipped(string name, bool skipped) =>
        Assert.Equal(skipped, DuplicateLogic.IsSkippedFolder(name));

    [Fact]
    public void BiggestSavingsComeFirst()
    {
        var small = G(F(@"C:\a", 10), F(@"C:\b", 10));
        var big = G(F(@"C:\c", 100), F(@"C:\d", 100), F(@"C:\e", 100));
        Assert.Same(big, DuplicateLogic.Ordered([small, big])[0]);
        Assert.Equal(200, big.Wasted);
    }
}

public sealed class DuplicateScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fieldkit-dupes-" + Guid.NewGuid().ToString("N"));

    public DuplicateScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length, byte seed, int differAt = -1)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(seed + i * 7);
        if (differAt >= 0) data[differAt] ^= 0xFF;
        return data;
    }

    [Fact]
    public void FindsTrueDuplicatesOnly()
    {
        var big = Bytes(200_000, 1);
        Write(@"a\movie.bin", big);
        Write(@"b\movie copy.bin", big);
        // Same size and same first 64 KB, different further on: not a duplicate.
        Write(@"c\almost.bin", Bytes(200_000, 1, differAt: 150_000));
        var small = Bytes(3_000, 9);
        Write(@"a\note.txt", small);
        Write(@"b\note.txt", small);
        Write(@"a\other.txt", Bytes(3_000, 10));
        // Never entered or skipped.
        Write(@".git\objects\note.txt", small);
        var hidden = Write(@"b\hidden.txt", small);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = DuplicateScanner.Scan([_root], minSize: 0, progress: null, CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(200_000, result.Groups[0].Size); // biggest savings first
        Assert.Equal(["movie copy.bin", "movie.bin"], result.Groups[0].Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n));
        Assert.Equal(2, result.Groups[1].Files.Count);
        Assert.DoesNotContain(result.Groups.SelectMany(g => g.Files), f => f.Path.Contains(".git") || f.Path.EndsWith("hidden.txt"));
    }

    [Fact]
    public void MinimumSizeLeavesSmallFilesOut()
    {
        var small = Bytes(3_000, 9);
        Write(@"a\note.txt", small);
        Write(@"b\note.txt", small);
        var result = DuplicateScanner.Scan([_root], minSize: 100_000, progress: null, CancellationToken.None);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public void ACancelledScanStops()
    {
        Write(@"a\x.txt", Bytes(10, 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DuplicateScanner.Scan([_root], 0, null, cts.Token));
    }

    [Fact]
    public void RendersThePreview()
    {
        var path = StaRunner.PreviewPath("duplicates.png");
        var groups = new List<DuplicateGroup>
        {
            new("a", 1_400_000_000, [new(@"C:\Users\me\Videos\Holiday 2025.mp4", 1_400_000_000, DateTime.UtcNow.AddDays(-90)),
                new(@"C:\Users\me\Downloads\Holiday 2025.mp4", 1_400_000_000, DateTime.UtcNow.AddDays(-2))]),
            new("b", 4_200_000, [new(@"C:\Users\me\Pictures\IMG_2041.jpg", 4_200_000, DateTime.UtcNow.AddDays(-30)),
                new(@"C:\Users\me\Pictures\Phone backup\IMG_2041.jpg", 4_200_000, DateTime.UtcNow.AddDays(-29)),
                new(@"C:\Users\me\Desktop\IMG_2041.jpg", 4_200_000, DateTime.UtcNow.AddDays(-1))]),
        };
        StaRunner.Run(() => PreviewRenderer.Render(DuplicateFinderWindow.BuildPreview(new DuplicateScanResult(groups, 18_204, 37, 0)), path, 780, 700));
        Assert.True(new FileInfo(path).Length > 10_000);
    }
}
