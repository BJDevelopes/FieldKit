// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Wallpaper;

namespace Fieldkit.Tests.Wallpaper;

public sealed class TempTree : IDisposable
{
    public TempTree() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fieldkit-wall-tests", Guid.NewGuid().ToString("N"));

    public string File(string relative, FileAttributes attributes = FileAttributes.Normal)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        if (attributes != FileAttributes.Normal) System.IO.File.SetAttributes(path, attributes);
        return path;
    }

    public string Dir(string relative, FileAttributes attributes = FileAttributes.Normal)
    {
        var path = Path.Combine(Root, relative);
        var info = Directory.CreateDirectory(path);
        if (attributes != FileAttributes.Normal) info.Attributes |= attributes;
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch { }
    }
}

public class WallpaperSupportTests
{
    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.heic", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.jfif", true)]
    [InlineData("a.Png", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.mp4", false)]
    [InlineData("jpg", false)]
    [InlineData("folder", false)]
    public void IsStillImage_matches_upstream_extensions(string name, bool expected) =>
        Assert.Equal(expected, WallpaperSupport.IsStillImage(name));

    [Fact]
    public void Dialog_filter_lists_every_extension()
    {
        foreach (var ext in WallpaperSupport.ImageExtensions) Assert.Contains("*" + ext, WallpaperSupport.DialogFilter);
    }

    [Fact]
    public void Normalize_trims_separators_but_keeps_roots()
    {
        Assert.Equal(@"C:\Pictures", WallpaperSupport.Normalize(@"C:\Pictures\"));
        Assert.Equal(@"C:\Pictures\a.jpg", WallpaperSupport.Normalize(@"C:\Pictures\sub\..\a.jpg"));
        Assert.Equal(@"C:\", WallpaperSupport.Normalize(@"C:\"));
    }

    [Fact]
    public void IsInside_is_strict_and_case_insensitive()
    {
        Assert.True(WallpaperSupport.IsInside(@"C:\Pics\a.jpg", @"c:\pics"));
        Assert.True(WallpaperSupport.IsInside(@"C:\Pics\sub\a.jpg", @"C:\Pics\"));
        Assert.False(WallpaperSupport.IsInside(@"C:\Pics", @"C:\Pics"));
        Assert.False(WallpaperSupport.IsInside(@"C:\Pictures\a.jpg", @"C:\Pic"));
    }

    [Fact]
    public void Cloud_placeholders_are_detected()
    {
        Assert.True(WallpaperSupport.IsCloudPlaceholder((FileAttributes)0x00400000 | FileAttributes.Archive));
        Assert.True(WallpaperSupport.IsCloudPlaceholder(FileAttributes.Offline));
        Assert.False(WallpaperSupport.IsCloudPlaceholder(FileAttributes.Archive | FileAttributes.ReadOnly));
    }

    [Fact]
    public void ImagesInFolder_recurses_skips_hidden_and_sorts_by_name()
    {
        using var t = new TempTree();
        t.File("b.png");
        t.File("A.jpg");
        t.File("notes.txt");
        t.File(@"sub\c.JPEG");
        t.File(@"sub\deeper\d.gif");
        t.File(".dot.jpg");
        t.File("hidden.jpg", FileAttributes.Hidden);
        t.File(@".git\e.jpg");
        t.Dir("secret", FileAttributes.Hidden);
        t.File(@"secret\f.jpg");

        var names = WallpaperSupport.ImagesInFolder(t.Root).Select(Path.GetFileName).ToList();

        Assert.Equal(["A.jpg", "b.png", "c.JPEG", "d.gif"], names);
    }

    [Fact]
    public void ImagesInFolder_does_not_follow_junctions()
    {
        using var t = new TempTree();
        var target = t.Dir("target");
        t.File(@"target\x.jpg");
        var inner = t.Dir("inner");
        var link = Path.Combine(inner, "link");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using (var p = System.Diagnostics.Process.Start(psi)) p!.WaitForExit(10000);
        if (!Directory.Exists(link)) return; // junctions unavailable here

        var files = WallpaperSupport.ImagesInFolder(inner);
        Assert.Empty(files);
        Directory.Delete(link);
    }

    [Fact]
    public void ImagesInFolder_stops_when_cancelled()
    {
        using var t = new TempTree();
        t.File("a.jpg");
        Assert.Empty(WallpaperSupport.ImagesInFolder(t.Root, () => false));
        Assert.Empty(WallpaperSupport.ImagesInFolder(Path.Combine(t.Root, "missing")));
    }

    [Fact]
    public void SystemEntries_prefix_theme_folders_and_sort_by_title()
    {
        using var t = new TempTree();
        t.File(@"Windows\img0.jpg");
        t.File(@"ThemeA\img20.jpg");
        t.File("loose.png");

        var entries = WallpaperSupport.SystemEntries(t.Root);

        Assert.Equal(["loose", "ThemeA img20", "Windows img0"], entries.Select(e => e.Title));
        Assert.All(entries, e => Assert.Equal(WallpaperSource.System, e.Source));
        Assert.Empty(WallpaperSupport.SystemEntries(Path.Combine(t.Root, "nope")));
    }

    [Fact]
    public void OwnEntries_deduplicate_case_insensitively()
    {
        var entries = WallpaperSupport.OwnEntries([@"C:\P\a.jpg", @"c:\p\A.JPG", @"C:\P\b.png"]);
        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].Title);
        Assert.All(entries, e => Assert.Equal(WallpaperSource.Own, e.Source));
    }

    [Fact]
    public void Merge_keeps_system_entry_for_duplicates_and_sorts()
    {
        var system = new List<WallpaperEntry> { new(@"C:\W\z.jpg", @"C:\W\z.jpg", "z", WallpaperSource.System) };
        var own = WallpaperSupport.OwnEntries([@"C:\W\z.jpg", @"C:\P\a.jpg"]);

        var merged = WallpaperSupport.Merge(system, own);

        Assert.Equal(["a", "z"], merged.Select(e => e.Title));
        Assert.Equal(WallpaperSource.System, merged[1].Source);
    }

    [Fact]
    public void Filtered_and_filter_values()
    {
        var entries = new List<WallpaperEntry>
        {
            new("1", "1", "a", WallpaperSource.System),
            new("2", "2", "b", WallpaperSource.Own),
        };
        Assert.Equal(2, WallpaperSupport.Filtered(entries, WallpaperFilter.All).Count);
        Assert.Equal("2", Assert.Single(WallpaperSupport.Filtered(entries, WallpaperFilter.Own)).Id);
        Assert.Equal("1", Assert.Single(WallpaperSupport.Filtered(entries, WallpaperFilter.System)).Id);
        foreach (var f in Enum.GetValues<WallpaperFilter>())
            Assert.Equal(f, WallpaperSupport.ParseFilter(WallpaperSupport.FilterValue(f)));
        Assert.Equal(WallpaperFilter.All, WallpaperSupport.ParseFilter("apple"));
        Assert.Equal(WallpaperFilter.All, WallpaperSupport.ParseFilter(null));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(24, 1)]
    [InlineData(25, 2)]
    [InlineData(48, 2)]
    [InlineData(49, 3)]
    public void PageCount_matches_upstream(int items, int pages) =>
        Assert.Equal(pages, WallpaperSupport.PageCount(items));

    [Fact]
    public void Paging_clamps_and_slices()
    {
        var items = Enumerable.Range(1, 50).ToList();
        Assert.Equal(1, WallpaperSupport.ClampedPage(0, 50));
        Assert.Equal(3, WallpaperSupport.ClampedPage(9, 50));
        Assert.Equal(1, WallpaperSupport.ClampedPage(5, 0));
        Assert.Equal(Enumerable.Range(25, 24), WallpaperSupport.PageSlice(items, 2));
        Assert.Equal([49, 50], WallpaperSupport.PageSlice(items, 3));
        Assert.Equal([49, 50], WallpaperSupport.PageSlice(items, 99));
        Assert.Empty(WallpaperSupport.PageSlice(new List<int>(), 1));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(24, 8)]
    [InlineData(25, 8)]
    [InlineData(100, 8)]
    public void GalleryRows_keep_full_height_when_paged(int items, int rows) =>
        Assert.Equal(rows, WallpaperSupport.GalleryRows(items));
}

public class WallpaperSourcesTests
{
    private static WallpaperSourceState State(string[] sources, string[]? excluded = null) => new(sources, excluded ?? []);

    [Fact]
    public void Add_ignores_non_images_dedups_and_moves_to_end()
    {
        var state = State([@"C:\P\a.jpg", @"C:\Folder"]);
        var next = WallpaperSources.Add(state, [(@"c:\p\A.JPG", false), (@"C:\P\notes.txt", false), (@"C:\Other", true)]);
        Assert.Equal(3, next.Sources.Count);
        Assert.Equal(@"C:\Folder", next.Sources[0]);
        Assert.Equal(@"c:\p\a.jpg", next.Sources[1], ignoreCase: true);
        Assert.Equal(@"C:\Other", next.Sources[2]);
    }

    [Fact]
    public void Add_clears_prior_hides_of_the_path_and_of_folder_children()
    {
        var state = State([], [@"C:\F\x.jpg", @"C:\F\sub\y.jpg", @"C:\G\z.jpg"]);
        var next = WallpaperSources.Add(state, [(@"C:\F", true)]);
        Assert.Equal([@"C:\G\z.jpg"], next.Excluded);

        var again = WallpaperSources.Add(State([], [@"C:\G\z.jpg"]), [(@"C:\G\z.jpg", false)]);
        Assert.Empty(again.Excluded);
    }

    [Fact]
    public void RemoveSource_folder_forgets_hidden_children()
    {
        var state = State([@"C:\F", @"C:\a.jpg"], [@"C:\F\x.jpg", @"C:\H\y.jpg"]);
        var next = WallpaperSources.RemoveSource(state, @"C:\F", isFolder: true);
        Assert.Equal([@"C:\a.jpg"], next.Sources);
        Assert.Equal([@"C:\H\y.jpg"], next.Excluded);

        var file = WallpaperSources.RemoveSource(state, @"c:\A.jpg", isFolder: false);
        Assert.Equal([@"C:\F"], file.Sources);
    }

    [Fact]
    public void RemoveEntry_drops_file_sources_and_hides_folder_pictures()
    {
        var state = State([@"C:\F", @"C:\a.jpg"]);

        var dropped = WallpaperSources.RemoveEntry(state, @"C:\a.jpg");
        Assert.Equal([@"C:\F"], dropped.Sources);
        Assert.Empty(dropped.Excluded);

        var hidden = WallpaperSources.RemoveEntry(state, @"C:\F\x.jpg");
        Assert.Equal(state.Sources, hidden.Sources);
        Assert.Equal([@"C:\F\x.jpg"], hidden.Excluded);
        Assert.Same(hidden, WallpaperSources.RemoveEntry(hidden, @"c:\f\X.jpg"));
    }

    [Fact]
    public void CollectImages_scans_folders_files_and_honours_exclusions()
    {
        using var t = new TempTree();
        var folder = t.Dir("F");
        var a = t.File(@"F\a.jpg");
        var b = t.File(@"F\sub\b.png");
        var loose = t.File("loose.jpg");
        var state = State([folder, loose, Path.Combine(t.Root, "gone"), folder], [WallpaperSupport.Normalize(b)]);

        var images = WallpaperSources.CollectImages(state);

        Assert.Equal([WallpaperSupport.Normalize(a), WallpaperSupport.Normalize(loose)], images);
        Assert.Empty(WallpaperSources.CollectImages(state, () => false));
    }

    [Fact]
    public void Describe_reports_kind_and_reachability()
    {
        using var t = new TempTree();
        var folder = t.Dir("Holiday");
        var file = t.File("pic.jpg");

        Assert.Equal(new WallpaperSourceInfo(folder, "Holiday", true, true), WallpaperSources.Describe(folder));
        Assert.Equal(new WallpaperSourceInfo(file, "pic.jpg", false, true), WallpaperSources.Describe(file));
        var gone = Path.Combine(t.Root, "gone.png");
        Assert.Equal(new WallpaperSourceInfo(gone, "gone.png", false, false), WallpaperSources.Describe(gone));
        var goneFolder = Path.Combine(t.Root, "GoneFolder");
        Assert.True(WallpaperSources.Describe(goneFolder).IsFolder);
    }

    [Fact]
    public void RemovableChips_follow_upstream_rules()
    {
        var folder = new WallpaperSourceInfo(@"C:\F", "F", true, true);
        var shown = new WallpaperSourceInfo(@"C:\a.jpg", "a.jpg", false, true);
        var orphan = new WallpaperSourceInfo(@"C:\b.jpg", "b.jpg", false, true);
        var missing = new WallpaperSourceInfo(@"C:\c.jpg", "c.jpg", false, false);
        var ids = new HashSet<string>([@"c:\A.jpg"], WallpaperSupport.PathComparer);

        Assert.Equal([folder, orphan, missing], WallpaperSources.RemovableChips([folder, shown, orphan, missing], ids, isLoading: false));
        Assert.Equal([folder, missing], WallpaperSources.RemovableChips([folder, shown, orphan, missing], ids, isLoading: true));
    }
}
