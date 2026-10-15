// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Fieldkit.Features.ClipboardTools;

namespace Fieldkit.Tests.ClipboardTools;

public class ClipboardHistoryLogicTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0);

    private static ClipboardEntry Text(string text, int minutes = 0, bool pinned = false) =>
        new() { Text = text, CopiedAt = T0.AddMinutes(minutes), PinnedAt = pinned ? T0 : null };

    [Fact]
    public void CopyingTheSameTextAgainRefreshesOneEntry()
    {
        var a = Text("alpha", 1);
        var b = Text("beta", 2);
        var (list, promoted) = ClipboardHistoryLogic.Promote([b, a], new ClipboardEntry { Text = "alpha", SourcePath = @"C:\new.exe" }, T0.AddHours(1));
        Assert.Equal(2, list.Count);
        Assert.Equal(a.Id, promoted.Id);
        Assert.Equal("alpha", list[0].Text);
        Assert.Equal(T0.AddHours(1), list[0].CopiedAt);
        Assert.Equal(@"C:\new.exe", list[0].SourcePath);
    }

    [Fact]
    public void ACopyMadeInTheHistoryKeepsTheOriginalApp()
    {
        var a = Text("alpha") with { SourcePath = @"C:\word.exe" };
        var (list, _) = ClipboardHistoryLogic.Promote([a], new ClipboardEntry { Text = "alpha" }, T0, keepsSource: true);
        Assert.Equal(@"C:\word.exe", list[0].SourcePath);
    }

    [Fact]
    public void APinnedEntryStaysPinnedWhenCopiedAgain()
    {
        var pinned = Text("pinned", pinned: true);
        var other = Text("other");
        var (list, promoted) = ClipboardHistoryLogic.Promote([other, pinned], new ClipboardEntry { Text = "pinned" }, T0);
        Assert.True(promoted.IsPinned);
        Assert.Equal(pinned.Id, list[0].Id);
        Assert.Equal(other.Id, list[1].Id);
    }

    [Fact]
    public void NewCopiesGoBelowThePins()
    {
        var pinned = Text("pinned", pinned: true);
        var (list, _) = ClipboardHistoryLogic.Promote([pinned], new ClipboardEntry { Text = "new" }, T0);
        Assert.Equal(["pinned", "new"], list.Select(e => e.Text));
    }

    [Fact]
    public void ImagesDeduplicateByHashAndFilesByPaths()
    {
        var image = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageHash = "abc", ImageFile = "1.png", ImageWidth = 10, ImageHeight = 20 };
        var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\a.txt", @"C:\b.txt"] };
        var (list, promoted) = ClipboardHistoryLogic.Promote([image, files],
            new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageHash = "abc", ImageFile = "2.png" }, T0);
        Assert.Equal(2, list.Count);
        Assert.Equal("1.png", promoted.ImageFile);
        (list, _) = ClipboardHistoryLogic.Promote(list, new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"c:\A.txt", @"C:\b.txt"] }, T0);
        Assert.Equal(2, list.Count);
        (list, _) = ClipboardHistoryLogic.Promote(list, new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\b.txt", @"C:\a.txt"] }, T0);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void TextAndAnImageNeverMatch()
    {
        var text = new ClipboardEntry { Text = "x" };
        Assert.False(text.MatchesContent(new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageHash = "x" }));
        Assert.False(new ClipboardEntry { Kind = ClipboardEntryKind.Image }.MatchesContent(new ClipboardEntry { Kind = ClipboardEntryKind.Image }));
    }

    [Fact]
    public void RetentionKeepsEveryPinAndTheNewestRecent()
    {
        var entries = new List<ClipboardEntry> { Text("p1", pinned: true), Text("p2", pinned: true) };
        entries.AddRange(Enumerable.Range(0, 30).Select(i => Text($"r{i}")));
        var retained = ClipboardHistoryLogic.Retained(entries, 20);
        Assert.Equal(22, retained.Count);
        Assert.Equal("r19", retained[^1].Text);
        Assert.Equal(32, ClipboardHistoryLogic.Retained(entries, 0).Count); // unlimited
    }

    [Fact]
    public void AgeLimitDropsOldUnpinnedItemsOnly()
    {
        var now = T0.AddDays(10);
        var fresh = Text("fresh", minutes: 60 * 24 * 9); // 1 day old
        var old = Text("old"); // 10 days old
        var oldPin = Text("pin", pinned: true);
        var kept = ClipboardHistoryLogic.WithinAge([fresh, old, oldPin], 7, now);
        Assert.Equal(["fresh", "pin"], kept.Select(e => e.Text));
        Assert.Equal(3, ClipboardHistoryLogic.WithinAge([fresh, old, oldPin], 0, now).Count); // forever
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    public void AgeSettingIsSanitized(int stored, int expected) =>
        Assert.Equal(expected, ClipboardHistoryLogic.SanitizedAgeDays(stored));

    [Fact]
    public void RetentionRespectsTheTextBudget()
    {
        var big = Text(new string('x', 600));
        var small = Text("y");
        var retained = ClipboardHistoryLogic.Retained([big, small], 0, textByteLimit: 500);
        Assert.Equal(["y"], retained.Select(e => e.Text));
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 0)]
    [InlineData(10_000, 10_000)]
    [InlineData(7, 50)]
    [InlineData(-1, 50)]
    public void LimitsAreSanitized(int stored, int expected) => Assert.Equal(expected, ClipboardHistoryLogic.SanitizedLimit(stored));

    [Fact]
    public void PinningMovesToTheTopAndUnpinningToTheTopOfRecent()
    {
        var p = Text("p", pinned: true);
        var a = Text("a");
        var b = Text("b");
        var list = ClipboardHistoryLogic.TogglePin([p, a, b], b.Id, T0);
        Assert.Equal(["b", "p", "a"], list.Select(e => e.Text));
        Assert.True(list[0].IsPinned);
        list = ClipboardHistoryLogic.TogglePin(list, p.Id, T0);
        Assert.Equal(["b", "p", "a"], list.Select(e => e.Text));
        Assert.False(list[1].IsPinned);
    }

    [Fact]
    public void MovesStayInsideTheirGroup()
    {
        var p = Text("p", pinned: true);
        var a = Text("a");
        var b = Text("b");
        List<ClipboardEntry> list = [p, a, b];
        Assert.False(ClipboardHistoryLogic.CanMove(list, a.Id, MoveDirection.Up));
        Assert.False(ClipboardHistoryLogic.CanMove(list, p.Id, MoveDirection.Down));
        Assert.True(ClipboardHistoryLogic.CanMove(list, a.Id, MoveDirection.Down));
        Assert.Equal(["p", "b", "a"], ClipboardHistoryLogic.Move(list, a.Id, MoveDirection.Down).Select(e => e.Text));
        Assert.Equal(list.Select(e => e.Text), ClipboardHistoryLogic.Move(list, b.Id, MoveDirection.Down).Select(e => e.Text));
    }

    [Fact]
    public void TouchedEntriesMoveUpInPasteOrder()
    {
        var p = Text("p", pinned: true);
        var a = Text("a");
        var b = Text("b");
        var c = Text("c");
        var list = ClipboardHistoryLogic.Touch([p, a, b, c], [c.Id, b.Id, p.Id], T0.AddDays(1));
        Assert.Equal(["p", "c", "b", "a"], list.Select(e => e.Text));
        Assert.Equal(T0.AddDays(1), list[1].CopiedAt);
        Assert.Equal(T0, list[3].CopiedAt);
    }

    [Fact]
    public void ClearUnpinnedRemovesExactlyWhatWasCounted()
    {
        var p = Text("p", pinned: true);
        var a = Text("a");
        var b = Text("b");
        var snapshot = ClipboardHistoryLogic.RecentSnapshot([p, a, b]);
        Assert.Equal(2, snapshot.Count);
        // While the confirmation is open: b is copied again and c arrives.
        var c = Text("c", 5);
        var bAgain = b with { CopiedAt = T0.AddMinutes(9) };
        var list = ClipboardHistoryLogic.ClearRecent([p, c, bAgain, a], snapshot);
        Assert.Equal(["p", "c", "b"], list.Select(e => e.Text));
    }

    [Fact]
    public void SelectionsPasteAsFilesImageOrText()
    {
        var f1 = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\a"] };
        var f2 = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\b", @"C:\c"] };
        var image = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageFile = "i.png" };
        var text = Text("hello");
        Assert.Null(ClipboardHistoryLogic.PasteModeFor([]));
        Assert.Equal([@"C:\a", @"C:\b", @"C:\c"], Assert.IsType<PasteMode.Files>(ClipboardHistoryLogic.PasteModeFor([f1, f2])).Paths);
        Assert.Same(image, Assert.IsType<PasteMode.Image>(ClipboardHistoryLogic.PasteModeFor([image])).Entry);
        Assert.Equal("hello\nC:\\a", Assert.IsType<PasteMode.Text>(ClipboardHistoryLogic.PasteModeFor([text, f1])).Value);
        Assert.Equal("hello", Assert.IsType<PasteMode.Text>(ClipboardHistoryLogic.PasteModeFor([text, image])).Value);
    }

    [Fact]
    public void RangeSelectionWorksInBothDirections()
    {
        var all = new[] { 0, 1, 2, 3, 4 };
        Assert.Equal([1, 2, 3], ClipboardHistoryLogic.RangeSelection(all, 1, 3));
        Assert.Equal([1, 2, 3], ClipboardHistoryLogic.RangeSelection(all, 3, 1));
        Assert.Equal([3, 4], ClipboardHistoryLogic.RangeSelection(all, 9, 3));
        Assert.Empty(ClipboardHistoryLogic.RangeSelection(Array.Empty<int>(), 0, 1));
    }

    [Theory]
    [InlineData(null, "  hello  ", "  hello  ")] // copied text is kept exactly (indentation, trailing breaks)
    [InlineData("https://example.com/a", "example.com/a", "https://example.com/a")]
    [InlineData("https://example.com/a", "//example.com/a", "https://example.com/a")]
    [InlineData("https://example.com/a", "Example page", "Example page")]
    [InlineData("https://example.com/a", null, "https://example.com/a")]
    [InlineData(null, " https://example.com/x ", "https://example.com/x")]
    [InlineData(null, "   ", null)]
    public void PreferredTextNormalisesLinks(string? url, string? plain, string? expected) =>
        Assert.Equal(expected, ClipboardHistoryLogic.PreferredText(url, plain));

    [Fact]
    public void StorableTextTrimsAndBounds()
    {
        Assert.Equal("x", ClipboardHistoryLogic.StorableText("  x \n"));
        Assert.Null(ClipboardHistoryLogic.StorableText(" \t\n"));
        Assert.Null(ClipboardHistoryLogic.StorableText(new string('a', ClipboardHistoryLogic.MaxCharacters + 1)));
    }

    [Fact]
    public void PreviewsFoldWhitespaceAndStayBounded()
    {
        Assert.Equal("a b c", ClipboardHistoryLogic.PreviewText("a\nb\tc", keepLineBreaks: false));
        Assert.Equal("a\nb c", ClipboardHistoryLogic.PreviewText("a\nb\tc", keepLineBreaks: true));
        var longText = new string('z', ClipboardHistoryLogic.PreviewCharacters + 10);
        Assert.EndsWith("…", ClipboardHistoryLogic.PreviewText(longText, false));
        Assert.Equal(ClipboardHistoryLogic.PreviewCharacters + 1, ClipboardHistoryLogic.PreviewText(longText, false).Length);
    }

    [Fact]
    public void EntriesDescribeThemselves()
    {
        var image = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageWidth = 1280, ImageHeight = 720 };
        Assert.Equal("1280×720", image.Preview);
        Assert.Equal("Image png 1280×720", image.SearchableText("Image"));
        var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = [@"C:\x\photo.JPG", @"C:\x\notes.txt"] };
        Assert.Equal("photo.JPG, notes.txt", files.Preview);
        Assert.Equal("Image photo.JPG notes.txt", files.SearchableText("Image"));
    }

    [Fact]
    public void DropFilesBlockIsWideAndDoubleTerminated()
    {
        var bytes = ClipboardAccess.DropFiles([@"C:\a.txt", @"D:\b"]);
        Assert.Equal(20, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(1, BitConverter.ToInt32(bytes, 16));
        Assert.Equal("C:\\a.txt\0D:\\b\0\0", Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20));
    }
}

public class SensitiveTextTests
{
    [Theory]
    [InlineData("my password is hunter2", true)]
    [InlineData("Authorization: Bearer abc", true)]
    [InlineData("sk_live_51Hx9Ab2Cd3Ef4Gh5Ij", true)]
    [InlineData("Xy7#kLm9$pQr2&sTu5vW", true)]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", false)]
    [InlineData("https://example.com/a1b2c3d4e5f6g7h8i9j0?x=1", false)]
    [InlineData("hello world this is prose 123!", false)]
    [InlineData("short1!", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", false)]
    public void LooksSensitive(string text, bool expected) => Assert.Equal(expected, SensitiveText.LooksSensitive(text));
}

public class ClipboardPrivacyTests
{
    [Fact]
    public void PasswordManagerMarksArePrivate()
    {
        Assert.True(ClipboardPrivacy.IsPrivate(["CF_UNICODETEXT", "ExcludeClipboardContentFromMonitorProcessing"], null));
        Assert.True(ClipboardPrivacy.IsPrivate(["Clipboard Viewer Ignore"], null));
        Assert.True(ClipboardPrivacy.IsPrivate(["CF_UNICODETEXT", "CanIncludeInClipboardHistory"], 0));
        Assert.False(ClipboardPrivacy.IsPrivate(["CF_UNICODETEXT", "CanIncludeInClipboardHistory"], 1));
        Assert.False(ClipboardPrivacy.IsPrivate(["CF_UNICODETEXT", "HTML Format"], null));
    }
}

public class AutoClearLogicTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void ANewCopyRestartsTheClock() =>
        Assert.Equal(AutoClearDecision.NoteChange, AutoClearLogic.Decide(5, 4, null, T0, T0.AddHours(1), TimeSpan.FromSeconds(20)));

    [Fact]
    public void ClearsOnlyAfterTheDelay()
    {
        Assert.Equal(AutoClearDecision.Wait, AutoClearLogic.Decide(5, 5, null, T0, T0.AddSeconds(19), TimeSpan.FromSeconds(20)));
        Assert.Equal(AutoClearDecision.Clear, AutoClearLogic.Decide(5, 5, null, T0, T0.AddSeconds(20), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void OurOwnClearIsNeverClearedAgain() =>
        Assert.Equal(AutoClearDecision.Wait, AutoClearLogic.Decide(7, 7, 7, T0, T0.AddHours(5), TimeSpan.FromSeconds(20)));

    [Theory]
    [InlineData(20, 20)]
    [InlineData(5, 5)]
    [InlineData(3600, 3600)]
    [InlineData(4, 20)]
    [InlineData(3601, 20)]
    public void DelayIsSanitized(int value, int expected) => Assert.Equal(expected, AutoClearLogic.SanitizedDelay(value));
}

public class ClipboardJsonFormatTests
{
    [Fact]
    public void LaysOutJsonWithoutChangingTokens()
    {
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    true,\n    \"x y\"\n  ],\n  \"c\": {}\n}",
            ClipboardJsonFormat.Pretty("{\"a\":1,\"b\":[true, \"x y\"],\"c\":{ }}"));
    }

    [Fact]
    public void KeepsEscapedQuotesInsideStrings() =>
        Assert.Equal("[\n  \"a\\\"b,{\"\n]", ClipboardJsonFormat.Pretty("[\"a\\\"b,{\"]"));

    [Theory]
    [InlineData("not json")]
    [InlineData("{broken")]
    [InlineData("42")]
    [InlineData("\"string\"")]
    public void LeavesOtherTextAlone(string text) => Assert.Null(ClipboardJsonFormat.Pretty(text));
}
