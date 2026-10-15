// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public class ShelfShakeDetectorTests
{
    private static bool Feed(ShelfShakeDetector detector, double start, double step, params double[] xs)
    {
        var shook = false;
        for (var i = 0; i < xs.Length; i++) shook |= detector.AddSample(start + i * step, xs[i]);
        return shook;
    }

    [Fact]
    public void QuickSideToSideShakeIsDetected()
    {
        var detector = new ShelfShakeDetector();
        Assert.False(detector.AddSample(0.00, 0));
        Assert.False(detector.AddSample(0.05, 60));
        Assert.False(detector.AddSample(0.10, 0));
        Assert.False(detector.AddSample(0.15, 60));
        // Fifth sample: 3 reversals, 240 px of travel.
        Assert.True(detector.AddSample(0.20, 0));
        Assert.Equal(0, detector.SampleCount);
    }

    [Fact]
    public void NeedsAtLeastFiveSamples()
    {
        var detector = new ShelfShakeDetector();
        Assert.False(Feed(detector, 0, 0.05, 0, 300, 0, 300));
    }

    [Fact]
    public void NeedsThreeReversals()
    {
        var detector = new ShelfShakeDetector();
        // Two reversals only, lots of travel.
        Assert.False(Feed(detector, 0, 0.05, 0, 100, 200, 100, 0, 100));
    }

    [Fact]
    public void NeedsMoreThan220OfTravel()
    {
        var detector = new ShelfShakeDetector();
        // Many reversals, 40 px each way: 6 × 40 = 240 > 220 would pass, so use 30 (180 total).
        Assert.False(Feed(detector, 0, 0.05, 0, 30, 0, 30, 0, 30, 0));
    }

    [Fact]
    public void JitterBelowSixPixelsIsNotAReversal()
    {
        var detector = new ShelfShakeDetector();
        // Long straight drag with tiny wobble: never three real reversals.
        Assert.False(Feed(detector, 0, 0.02, 0, 100, 95, 200, 196, 300, 297, 400, 396, 500));
    }

    [Fact]
    public void StraightDragIsNotAShake()
    {
        var detector = new ShelfShakeDetector();
        Assert.False(Feed(detector, 0, 0.02, Enumerable.Range(0, 25).Select(i => i * 40.0).ToArray()));
    }

    [Fact]
    public void SlowWavesOutsideTheHalfSecondWindowDoNotCount()
    {
        var detector = new ShelfShakeDetector();
        Assert.False(Feed(detector, 0, 0.2, 0, 100, 0, 100, 0, 100, 0, 100));
    }

    [Fact]
    public void SecondShakeWithinOneSecondIsIgnored()
    {
        var detector = new ShelfShakeDetector();
        Assert.True(Feed(detector, 0, 0.05, 0, 60, 0, 60, 0));
        Assert.False(Feed(detector, 0.5, 0.05, 0, 60, 0, 60, 0));
        detector.Reset();
        Assert.True(Feed(detector, 1.5, 0.05, 0, 60, 0, 60, 0));
    }

    [Fact]
    public void ResetForgetsSamples()
    {
        var detector = new ShelfShakeDetector();
        Feed(detector, 0, 0.05, 0, 60, 0, 60);
        detector.Reset();
        Assert.Equal(0, detector.SampleCount);
        Assert.False(detector.AddSample(0.2, 0));
    }
}

public class ShelfRulesTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(199, 1, true)]
    [InlineData(200, 1, false)]
    [InlineData(150, 50, true)]
    [InlineData(150, 51, false)]
    [InlineData(10, 0, false)]
    [InlineData(-1, 1, false)]
    public void CanAddRespectsTheTwoHundredItemCeiling(int existing, int added, bool expected) =>
        Assert.Equal(expected, ShelfRules.CanAdd(existing, added));

    [Fact]
    public void BoundedTextCutsAndRejectsBlank()
    {
        Assert.Null(ShelfRules.BoundedText(null));
        Assert.Null(ShelfRules.BoundedText("  \r\n\t "));
        Assert.Equal("hi", ShelfRules.BoundedText("hi"));
        var huge = new string('a', ShelfRules.MaxTextLength + 10);
        Assert.Equal(ShelfRules.MaxTextLength, ShelfRules.BoundedText(huge)!.Length);
    }

    [Fact]
    public void TitlesForTextAndLinks()
    {
        Assert.Equal("First line", ShelfRules.TextTitle("\n\n  First line  \nsecond"));
        Assert.Equal(48, ShelfRules.TextTitle(new string('x', 100)).Length);
        Assert.Equal("example.com", ShelfRules.LinkTitle("https://example.com/a?b=c"));
        Assert.Equal("not a url", ShelfRules.LinkTitle("not a url"));
        Assert.Equal("report.pdf", ShelfRules.FileTitle(@"C:\Users\me\report.pdf"));
        Assert.Equal("Docs", ShelfRules.FileTitle(@"C:\Users\me\Docs\"));
        Assert.Equal(@"D:\", ShelfRules.FileTitle(@"D:\"));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("  http://example.com/path?q=1  ", "http://example.com/path?q=1")]
    [InlineData("ftp://example.com", null)]
    [InlineData("www.example.com", null)]
    [InlineData("see https://example.com", null)]
    [InlineData("https://", null)]
    [InlineData("", null)]
    public void AsWebLinkAcceptsOnlyAPlainHttpAddress(string input, string? expected) =>
        Assert.Equal(expected, ShelfRules.AsWebLink(input));

    [Fact]
    public void ClassifyPrefersFilesThenPicturesThenLinksThenText()
    {
        var all = new ShelfDropData([@"C:\a.txt"], "hello", "https://x.com", HasBitmap: true);
        Assert.Equal(ShelfItemKind.File, Assert.Single(ShelfRules.Classify(all)).Kind);
        Assert.Equal(ShelfItemKind.Image, Assert.Single(ShelfRules.Classify(all with { Files = null })).Kind);
        var link = Assert.Single(ShelfRules.Classify(all with { Files = null, HasBitmap = false }));
        Assert.Equal(ShelfItemKind.Link, link.Kind);
        Assert.Equal("https://x.com", link.Text);
        Assert.Equal("x.com", link.Title);
        var text = Assert.Single(ShelfRules.Classify(new ShelfDropData(Text: "  hello\nworld")));
        Assert.Equal(ShelfItemKind.Text, text.Kind);
        Assert.Equal("  hello\nworld", text.Text);
        Assert.Equal("hello", text.Title);
    }

    [Fact]
    public void ClassifyTurnsTextThatIsAnAddressIntoALink()
    {
        var item = Assert.Single(ShelfRules.Classify(new ShelfDropData(Text: "https://github.com/x\n")));
        Assert.Equal(ShelfItemKind.Link, item.Kind);
        Assert.Equal("https://github.com/x", item.Text);
    }

    [Fact]
    public void ClassifySkipsDuplicateAndAlreadyShelvedFiles()
    {
        var shelved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\b.txt" };
        var items = ShelfRules.Classify(new ShelfDropData([@"C:\a.txt", @"c:\A.TXT", @"C:\B.txt", "", @"C:\c.txt"]), shelved);
        Assert.Equal(new string?[] { @"C:\a.txt", @"C:\c.txt" }, items.Select(i => i.Path));
        Assert.All(items, i => Assert.Equal(ShelfItemKind.File, i.Kind));
        Assert.Equal("a.txt", items[0].Title);
    }

    [Fact]
    public void ClassifyOfNothingUsableIsEmpty()
    {
        Assert.Empty(ShelfRules.Classify(new ShelfDropData()));
        Assert.Empty(ShelfRules.Classify(new ShelfDropData(Text: "   ")));
        Assert.False(ShelfRules.CanAccept(new ShelfDropData(Text: " ")));
        Assert.True(ShelfRules.CanAccept(new ShelfDropData(HasBitmap: true)));
        Assert.True(ShelfRules.CanAccept(new ShelfDropData(Url: "https://a.b")));
    }

    [Fact]
    public void MiddleTruncateKeepsTheExtension()
    {
        Assert.Equal("short.txt", ShelfRules.MiddleTruncate("short.txt", 20));
        var cut = ShelfRules.MiddleTruncate("a very long file name indeed.docx", 15);
        Assert.Equal(15, cut.Length);
        Assert.StartsWith("a very ", cut);
        Assert.EndsWith(".docx", cut);
        Assert.Contains('…', cut);
    }

    [Fact]
    public void HoldOpenWhenAnythingKeepsTheShelf()
    {
        Assert.False(ShelfRules.ShouldHoldOpen(false, 0, false, false, false));
        Assert.True(ShelfRules.ShouldHoldOpen(true, 0, false, false, false));
        Assert.True(ShelfRules.ShouldHoldOpen(false, 1, false, false, false));
        Assert.True(ShelfRules.ShouldHoldOpen(false, 0, true, false, false));
        Assert.True(ShelfRules.ShouldHoldOpen(false, 0, false, true, false));
        Assert.True(ShelfRules.ShouldHoldOpen(false, 0, false, false, true));
    }

    [Fact]
    public void ExceptionsMatchExecutableNamesIgnoringCase()
    {
        string[] excluded = ["Photoshop.exe"];
        Assert.True(ShelfRules.AllowsAutomaticOpen(null, excluded));
        Assert.True(ShelfRules.AllowsAutomaticOpen(@"C:\Windows\explorer.exe", excluded));
        Assert.False(ShelfRules.AllowsAutomaticOpen(@"C:\Program Files\Adobe\photoshop.EXE", excluded));
        Assert.False(ShelfRules.AllowsAutomaticOpen("Photoshop.exe", excluded));
        Assert.True(ShelfRules.AllowsAutomaticOpen("Photoshop.exe", []));
    }

    [Fact]
    public void NormalizeExclusionsCleansTheList()
    {
        Assert.Equal(new[] { "a.exe", "b.exe" }, ShelfRules.NormalizeExclusions([" b.exe", @"C:\x\a.exe", "", "B.exe", "A.EXE"]));
        Assert.Empty(ShelfRules.NormalizeExclusions(null));
    }

    [Theory]
    [InlineData(true, 2, true, false, true)]
    [InlineData(false, 2, true, false, false)]
    [InlineData(true, 0, true, false, false)]
    [InlineData(true, 2, false, false, false)]
    [InlineData(true, 2, true, true, false)]
    public void CloseAfterDropOnlyForAcceptedUnpinnedDrops(bool accepted, int count, bool option, bool pinned, bool expected) =>
        Assert.Equal(expected, ShelfRules.ShouldCloseAfterDrag(accepted, count, option, pinned));

    [Theory]
    [InlineData(true, 1, true, true)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(true, 1, false, false)]
    public void RemoveAfterDropOnlyForAcceptedDrops(bool accepted, int count, bool option, bool expected) =>
        Assert.Equal(expected, ShelfRules.ShouldRemoveAfterDrag(accepted, count, option));

    [Fact]
    public void PinnedItemsAlwaysStayAfterADrop()
    {
        var a = new ShelfItem { Kind = ShelfItemKind.Text, Text = "a" };
        var b = new ShelfItem { Kind = ShelfItemKind.Text, Text = "b", IsPinned = true };
        Assert.Equal(new[] { a.Id }, ShelfRules.RemovableAfterDrag([a, b]));
    }

    [Fact]
    public void RangeSelectionWorksBothWays()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(ids.Skip(1).Take(3), ShelfRules.RangeSelection(ids, ids[1], ids[3]));
        Assert.Equal(ids.Skip(1).Take(3), ShelfRules.RangeSelection(ids, ids[3], ids[1]));
        Assert.Equal(new[] { ids[2] }, ShelfRules.RangeSelection(ids, null, ids[2]));
        Assert.Equal(new[] { ids[2] }, ShelfRules.RangeSelection(ids, Guid.NewGuid(), ids[2]));
        Assert.Empty(ShelfRules.RangeSelection(ids, ids[0], Guid.NewGuid()));
    }

    [Fact]
    public void ActionsUseTheSelectionOnlyWhenTheClickedTileIsInIt()
    {
        var items = Enumerable.Range(0, 4).Select(i => new ShelfItem { Kind = ShelfItemKind.Text, Text = i.ToString() }).ToList();
        var selection = new HashSet<Guid> { items[3].Id, items[1].Id };
        Assert.Equal(new[] { items[1], items[3] }, ShelfRules.ActionTargets(items, selection, items[3]));
        Assert.Equal(new[] { items[0] }, ShelfRules.ActionTargets(items, selection, items[0]));
        Assert.Equal(new[] { items[1], items[3] }, ShelfRules.SelectionOrEverything(items, selection));
        Assert.Equal(items, ShelfRules.SelectionOrEverything(items, new HashSet<Guid>()));
    }

    [Fact]
    public void JoinedTextTakesNotesAndLinksOnly()
    {
        ShelfItem[] items =
        [
            new() { Kind = ShelfItemKind.Text, Text = "one" },
            new() { Kind = ShelfItemKind.File, Path = @"C:\x.txt" },
            new() { Kind = ShelfItemKind.Link, Text = "https://a.b" },
        ];
        Assert.Equal("one" + Environment.NewLine + "https://a.b", ShelfRules.JoinedText(items));
        Assert.Null(ShelfRules.JoinedText([items[1]]));
    }

    [Fact]
    public void TooltipIsCapped()
    {
        var note = new ShelfItem { Kind = ShelfItemKind.Text, Text = new string('n', 600) };
        Assert.Equal(501, ShelfRules.Tooltip(note).Length);
        Assert.Equal(@"C:\x.txt", ShelfRules.Tooltip(new ShelfItem { Kind = ShelfItemKind.File, Path = @"C:\x.txt", Title = "x.txt" }));
    }

    [Fact]
    public void HoldsFileOnlyForFilesAndPicturesWithAPath()
    {
        Assert.True(new ShelfItem { Kind = ShelfItemKind.File, Path = @"C:\a" }.HoldsFile);
        Assert.True(new ShelfItem { Kind = ShelfItemKind.Image, Path = @"C:\a.png" }.HoldsFile);
        Assert.False(new ShelfItem { Kind = ShelfItemKind.Image }.HoldsFile);
        Assert.False(new ShelfItem { Kind = ShelfItemKind.Link, Text = "https://a.b" }.HoldsFile);
        Assert.True(ShelfRules.IsImageFile(@"C:\a.JPG"));
        Assert.False(ShelfRules.IsImageFile(@"C:\a.txt"));
    }
}

public class ShelfCollectionTests
{
    private static ShelfItem Note(string text, bool pinned = false) =>
        new() { Kind = ShelfItemKind.Text, Text = text, Title = text, IsPinned = pinned };

    [Fact]
    public void AddRefusesADropThatDoesNotFitAsAWhole()
    {
        var shelf = new ShelfCollection();
        Assert.True(shelf.Add(Enumerable.Range(0, 199).Select(i => Note(i.ToString())).ToList()));
        Assert.False(shelf.Add([Note("a"), Note("b")]));
        Assert.Equal(199, shelf.Count);
        Assert.True(shelf.Add([Note("last")]));
        Assert.False(shelf.Add([Note("over")]));
        Assert.Equal(ShelfRules.MaxItems, shelf.Count);
    }

    [Fact]
    public void ClearKeepsPinnedItems()
    {
        var shelf = new ShelfCollection();
        var pinned = Note("keep", pinned: true);
        shelf.Add([Note("a"), pinned, Note("b")]);
        var removed = shelf.Clear();
        Assert.Equal(2, removed.Count);
        Assert.Equal(new[] { pinned }, shelf.Items);
    }

    [Fact]
    public void TrashRemovesTheSelectionOrClears()
    {
        var shelf = new ShelfCollection();
        var a = Note("a");
        var b = Note("b", pinned: true);
        var c = Note("c");
        shelf.Add([a, b, c]);
        shelf.Click(b.Id, extend: false);
        shelf.RemoveSelectedOrClear(); // a selected pinned item is removed on purpose
        Assert.Equal(new[] { a, c }, shelf.Items);
        Assert.Empty(shelf.Selection);
        shelf.RemoveSelectedOrClear();
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public void ClickTogglesAndShiftExtends()
    {
        var shelf = new ShelfCollection();
        var items = Enumerable.Range(0, 5).Select(i => Note(i.ToString())).ToList();
        shelf.Add(items);
        shelf.Click(items[1].Id, extend: false);
        Assert.Equal(new[] { items[1].Id }, shelf.Selection);
        shelf.Click(items[3].Id, extend: true);
        Assert.Equal(items.Skip(1).Take(3).Select(i => i.Id).ToHashSet(), shelf.Selection.ToHashSet());
        shelf.Click(items[2].Id, extend: false);
        Assert.DoesNotContain(items[2].Id, shelf.Selection);
        shelf.ClearSelection();
        Assert.Empty(shelf.Selection);
    }

    [Fact]
    public void RemoveForgetsSelectionAndRaisesChanged()
    {
        var shelf = new ShelfCollection();
        var a = Note("a");
        shelf.Add([a, Note("b")]);
        shelf.Click(a.Id, extend: false);
        var changes = 0;
        shelf.Changed += () => changes++;
        Assert.Single(shelf.Remove([a.Id]));
        Assert.Empty(shelf.Selection);
        Assert.Equal(1, changes);
        Assert.Empty(shelf.Remove([Guid.NewGuid()]));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TogglePinAndShelvedPaths()
    {
        var shelf = new ShelfCollection();
        var file = new ShelfItem { Kind = ShelfItemKind.File, Path = @"C:\Docs\a.txt", Title = "a.txt" };
        shelf.Add([file, Note("n")]);
        shelf.TogglePin(file.Id);
        Assert.True(shelf.Find(file.Id)!.IsPinned);
        shelf.TogglePin(file.Id);
        Assert.False(shelf.Find(file.Id)!.IsPinned);
        Assert.Contains(@"c:\docs\A.TXT", shelf.ShelvedPaths());
        Assert.Single(shelf.ShelvedPaths());
    }
}
