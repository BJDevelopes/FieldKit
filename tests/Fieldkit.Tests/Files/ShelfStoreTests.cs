// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public sealed class ShelfStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fieldkit-shelf-tests", Guid.NewGuid().ToString("N"));

    public ShelfStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string MakeFile(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void SerializeRoundTripsEveryKind()
    {
        ShelfItem[] items =
        [
            new() { Kind = ShelfItemKind.File, Path = @"C:\a.txt", Title = "a.txt", IsPinned = true },
            new() { Kind = ShelfItemKind.Text, Text = "note", Title = "note" },
            new() { Kind = ShelfItemKind.Link, Text = "https://a.b", Title = "a.b" },
            new() { Kind = ShelfItemKind.Image, Path = @"C:\img.png", Title = "Image" },
        ];
        var json = ShelfStore.Serialize(items);
        Assert.Contains("\"Link\"", json);
        Assert.DoesNotContain("HoldsFile", json);
        Assert.Equal(items, ShelfStore.Deserialize(json));
    }

    [Fact]
    public void DeserializeOfGarbageIsNull() => Assert.Null(ShelfStore.Deserialize("{ not json"));

    [Fact]
    public void SanitizeDropsWhatCannotBeHonoured()
    {
        var id = Guid.NewGuid();
        ShelfItem?[] items =
        [
            new ShelfItem { Id = id, Kind = ShelfItemKind.File, Path = @"C:\exists.txt", Title = "" },
            new ShelfItem { Id = id, Kind = ShelfItemKind.Text, Text = "duplicate id" },
            null,
            new ShelfItem { Kind = ShelfItemKind.File, Path = @"C:\gone.txt" },
            new ShelfItem { Kind = ShelfItemKind.File },
            new ShelfItem { Kind = ShelfItemKind.Text, Text = "   " },
            new ShelfItem { Kind = ShelfItemKind.Text, Text = new string('t', ShelfRules.MaxTextLength + 5) },
            new ShelfItem { Kind = ShelfItemKind.Link, Text = "javascript:alert(1)" },
            new ShelfItem { Kind = ShelfItemKind.Link, Text = "https://ok.example", Path = @"C:\stray" },
            new ShelfItem { Id = Guid.Empty, Kind = ShelfItemKind.Text, Text = "no id" },
        ];
        var result = ShelfStore.Sanitize(items, p => p == @"C:\exists.txt");
        Assert.Equal(3, result.Count);
        Assert.Equal("exists.txt", result[0].Title);
        Assert.Equal(ShelfRules.MaxTextLength, result[1].Text!.Length);
        Assert.Equal("ok.example", result[2].Title);
        Assert.Null(result[2].Path);
    }

    [Fact]
    public void SanitizeKeepsAtMostTwoHundred()
    {
        var items = Enumerable.Range(0, 250).Select(i => new ShelfItem { Kind = ShelfItemKind.Text, Text = i.ToString() });
        Assert.Equal(ShelfRules.MaxItems, ShelfStore.Sanitize(items, _ => true).Count);
    }

    [Fact]
    public void SaveAndLoadRoundTripOnDisk()
    {
        var file = MakeFile("report.txt");
        var store = Path.Combine(_dir, "Shelf", "shelf.json");
        ShelfItem[] items =
        [
            new() { Kind = ShelfItemKind.File, Path = file, Title = "report.txt", IsPinned = true },
            new() { Kind = ShelfItemKind.Text, Text = "hello", Title = "hello" },
            new() { Kind = ShelfItemKind.File, Path = Path.Combine(_dir, "missing.txt"), Title = "missing.txt" },
        ];
        ShelfStore.Save(store, items);
        var loaded = ShelfStore.Load(store);
        Assert.Equal(items.Take(2), loaded);
        Assert.False(File.Exists(store + ".tmp"));
    }

    [Fact]
    public void LoadOfAMissingFileIsEmptyAndACorruptOneIsKept()
    {
        Assert.Empty(ShelfStore.Load(Path.Combine(_dir, "nope.json")));
        var corrupt = MakeFile("shelf.json", "[{ broken");
        Assert.Empty(ShelfStore.Load(corrupt));
        Assert.True(File.Exists(corrupt + ".corrupt"));
    }

    [Fact]
    public void FileSurvivesWhenItExistsOrItsDriveIsAbsent()
    {
        Assert.True(ShelfStore.FileSurvives(MakeFile("here.txt")));
        Assert.True(ShelfStore.FileSurvives(_dir));
        Assert.False(ShelfStore.FileSurvives(Path.Combine(_dir, "gone.txt")));
        Assert.True(ShelfStore.FileSurvives(@"\\some-server\share\file.txt"));
        var absent = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => $"{c}:\\").FirstOrDefault(root => !Directory.Exists(root));
        if (absent is not null) Assert.True(ShelfStore.FileSurvives(absent + @"folder\file.txt"));
    }

    [Fact]
    public void OwnedMeansInsideTheItemsFolder()
    {
        var items = Path.Combine(_dir, "Items");
        Assert.True(ShelfStore.IsOwned(Path.Combine(items, "a.png"), items));
        Assert.False(ShelfStore.IsOwned(Path.Combine(_dir, "a.png"), items));
        Assert.False(ShelfStore.IsOwned(items + "Other\\a.png", items));
        Assert.False(ShelfStore.IsOwned(null, items));
    }

    [Fact]
    public void WriteImageAndSweepOnlyDeleteUnreferencedPictures()
    {
        var items = Path.Combine(_dir, "Items");
        var kept = ShelfStore.WriteImage([1, 2, 3], items)!;
        var orphan = ShelfStore.WriteImage([4, 5, 6], items)!;
        Assert.NotEqual(kept, orphan);
        Assert.True(ShelfStore.IsOwned(kept, items));
        var outside = MakeFile("user.png");
        ShelfItem[] live =
        [
            new() { Kind = ShelfItemKind.Image, Path = kept, Title = "Image" },
        ];
        // Too young: nothing goes yet.
        ShelfStore.SweepOrphans(live, TimeSpan.FromMinutes(5), items);
        Assert.True(File.Exists(orphan));
        ShelfStore.SweepOrphans(live, TimeSpan.Zero, items);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void OrphansIgnoresCaseAndRelativeSpelling()
    {
        var a = Path.Combine(_dir, "a.png");
        var b = Path.Combine(_dir, "b.png");
        var orphans = ShelfStore.Orphans([a, b], [new ShelfItem { Kind = ShelfItemKind.Image, Path = a.ToUpperInvariant() }]);
        Assert.Equal(new[] { b }, orphans);
    }
}
