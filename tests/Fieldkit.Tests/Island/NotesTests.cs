// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Island;
using Fieldkit.Features.Island.Notes;

namespace Fieldkit.Tests.Island;

public sealed class NotesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fieldkit-notes-tests", Guid.NewGuid().ToString("N"));

    public NotesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static QuickNote Note(string text, DateTime? updated = null, bool pinned = false) =>
        new(Guid.NewGuid(), text, updated ?? Now, updated ?? Now, pinned);

    // ------------------------------------------------------------- rules

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \r\n  ", "")]
    [InlineData("  hello  ", "hello")]
    [InlineData("\n\nhello", "hello")]
    [InlineData("one\r\ntwo\rthree", "one\ntwo\nthree")]
    public void CleanTidiesAText(string? raw, string expected) => Assert.Equal(expected, NotesRules.Clean(raw));

    [Fact]
    public void CleanCapsTheLength()
    {
        var clean = NotesRules.Clean(new string('a', NotesRules.MaxLength + 500));
        Assert.Equal(NotesRules.MaxLength, clean.Length);
    }

    [Fact]
    public void PinnedNotesComeFirstThenTheNewest()
    {
        var old = Note("old", Now.AddDays(-3));
        var fresh = Note("fresh", Now);
        var pinnedOld = Note("pinned", Now.AddDays(-9), pinned: true);

        var ordered = NotesRules.Ordered([old, fresh, pinnedOld]);

        Assert.Equal(["pinned", "fresh", "old"], ordered.Select(n => n.Text));
    }

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    public void AgeIsShortAndPlain(int secondsAgo, string expected) =>
        Assert.Equal(expected, NotesRules.Age(Now, Now.AddSeconds(-secondsAgo)));

    [Fact]
    public void AgeNamesYesterdayAndOlderDays()
    {
        Assert.Equal("yesterday", NotesRules.Age(Now, Now.AddDays(-1)));
        Assert.Contains("Oct", NotesRules.Age(Now, new DateTime(2026, 10, 3, 9, 0, 0)));
        Assert.Contains("2025", NotesRules.Age(Now, new DateTime(2025, 10, 3, 9, 0, 0)));
    }

    [Fact]
    public void SanitizeDropsEmptyNotesAndRenewsRepeatedIds()
    {
        var id = Guid.NewGuid();
        var a = new QuickNote(id, "first", Now, Now);
        var b = new QuickNote(id, "second", Now, Now);
        var empty = new QuickNote(Guid.NewGuid(), "   ", Now, Now);

        var result = NotesRules.Sanitize([a, null, empty, b], Now);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Select(n => n.Id).Distinct().Count());
        Assert.DoesNotContain(result, n => n.Id == Guid.Empty);
    }

    [Fact]
    public void SanitizeRepairsDatesThatAreInTheFutureOrBackwards()
    {
        var future = new QuickNote(Guid.NewGuid(), "future", Now.AddDays(5), Now.AddDays(6));
        var backwards = new QuickNote(Guid.NewGuid(), "backwards", Now.AddDays(-1), Now.AddDays(-3));
        var unset = new QuickNote(Guid.NewGuid(), "unset", default, default);

        var result = NotesRules.Sanitize([future, backwards, unset], Now).ToDictionary(n => n.Text);

        Assert.Equal(Now, result["future"].Created);
        Assert.Equal(Now, result["future"].Updated);
        Assert.Equal(result["backwards"].Created, result["backwards"].Updated);
        Assert.Equal(Now, result["unset"].Updated);
    }

    [Fact]
    public void SanitizeKeepsPinnedNotesWhenThereAreTooMany()
    {
        var notes = Enumerable.Range(0, NotesRules.MaxNotes + 20).Select(i => Note($"n{i}", Now.AddMinutes(-i))).ToList();
        notes[^1] = notes[^1] with { Pinned = true }; // the oldest

        var result = NotesRules.Sanitize(notes, Now);

        Assert.Equal(NotesRules.MaxNotes, result.Count);
        Assert.Contains(result, n => n.Pinned);
    }

    // ------------------------------------------------------------- collection

    [Fact]
    public void AddingKeepsTheNoteAndSaysSo()
    {
        var notes = new NotesCollection();
        var changes = 0;
        notes.Changed += () => changes++;

        var note = notes.Add("  buy milk  ", Now);

        Assert.NotNull(note);
        Assert.Equal("buy milk", note.Text);
        Assert.Equal(1, notes.Count);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnEmptyNoteIsNotKept()
    {
        var notes = new NotesCollection();
        var changes = 0;
        notes.Changed += () => changes++;

        Assert.Null(notes.Add("   ", Now));
        Assert.Null(notes.Add(null, Now));
        Assert.Equal(0, notes.Count);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void WhenFullTheOldestUnpinnedNoteMakesRoom()
    {
        var notes = new NotesCollection();
        var keep = Note("pinned and oldest", Now.AddDays(-30), pinned: true);
        var goes = Note("oldest unpinned", Now.AddDays(-20));
        var rest = Enumerable.Range(0, NotesRules.MaxNotes - 2).Select(i => Note($"n{i}", Now.AddMinutes(-i)));
        notes.Reset(new[] { keep, goes }.Concat(rest));

        var added = notes.Add("one more", Now);

        Assert.NotNull(added);
        Assert.Equal(NotesRules.MaxNotes, notes.Count);
        Assert.Contains(notes.Items, n => n.Id == keep.Id);
        Assert.DoesNotContain(notes.Items, n => n.Id == goes.Id);
    }

    [Fact]
    public void WhenEveryNoteIsPinnedNothingCanBeAdded()
    {
        var notes = new NotesCollection();
        notes.Reset(Enumerable.Range(0, NotesRules.MaxNotes).Select(i => Note($"n{i}", Now, pinned: true)));

        Assert.Null(notes.Add("no room", Now));
        Assert.Equal(NotesRules.MaxNotes, notes.Count);
    }

    [Fact]
    public void UpdatingChangesTheTextAndWhenItChanged()
    {
        var notes = new NotesCollection();
        var note = notes.Add("draft", Now)!;
        var later = Now.AddHours(2);

        Assert.True(notes.Update(note.Id, "final\r\ntext", later));

        var updated = notes.Items.Single();
        Assert.Equal("final\ntext", updated.Text);
        Assert.Equal(later, updated.Updated);
        Assert.Equal(Now, updated.Created);
    }

    [Fact]
    public void UpdatingWithNothingChangesNothing()
    {
        var notes = new NotesCollection();
        var note = notes.Add("keep me", Now)!;
        var changes = 0;
        notes.Changed += () => changes++;

        Assert.False(notes.Update(note.Id, "  ", Now.AddHours(1)));
        Assert.False(notes.Update(Guid.NewGuid(), "text", Now));
        Assert.True(notes.Update(note.Id, "keep me", Now.AddHours(1))); // same text: no change, no save

        Assert.Equal("keep me", notes.Items.Single().Text);
        Assert.Equal(Now, notes.Items.Single().Updated);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void PinningDeletingAndClearing()
    {
        var notes = new NotesCollection();
        var a = notes.Add("a", Now)!;
        var b = notes.Add("b", Now.AddMinutes(1))!;
        var c = notes.Add("c", Now.AddMinutes(2))!;

        Assert.True(notes.TogglePin(a.Id));
        Assert.Equal("a", notes.Items[0].Text); // pinned goes first

        Assert.True(notes.Delete(b.Id));
        Assert.False(notes.Delete(b.Id));
        Assert.False(notes.TogglePin(b.Id));

        Assert.Equal(1, notes.ClearUnpinned()); // c goes, the pinned a stays
        Assert.Equal([a.Id], notes.Items.Select(n => n.Id));
        Assert.Equal(0, notes.ClearUnpinned());
        Assert.DoesNotContain(notes.Items, n => n.Id == c.Id);
    }

    // ------------------------------------------------------------- store

    [Fact]
    public void NotesSurviveSavingAndLoading()
    {
        var file = Path.Combine(_dir, "Notes", "notes.json");
        var notes = new[] { Note("one\ntwo", Now.AddMinutes(-5), pinned: true), Note("three") };

        NotesStore.Save(file, notes);
        var loaded = NotesStore.Load(file);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("one\ntwo", loaded[0].Text); // pinned first
        Assert.True(loaded[0].Pinned);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void NoFileMeansNoNotes() => Assert.Empty(NotesStore.Load(Path.Combine(_dir, "missing.json")));

    [Fact]
    public void AnUnreadableFileIsKeptAsACopyAndTheNotesStartEmpty()
    {
        var file = Path.Combine(_dir, "notes.json");
        File.WriteAllText(file, "{ this is not json");

        Assert.Empty(NotesStore.Load(file));
        Assert.True(File.Exists(file + ".corrupt"));
        Assert.Equal("{ this is not json", File.ReadAllText(file + ".corrupt"));
    }

    [Fact]
    public void LoadingRepairsWhatIsInTheFile()
    {
        var file = Path.Combine(_dir, "notes.json");
        File.WriteAllText(file, NotesStore.Serialize([new QuickNote(Guid.Empty, "  tidy me  ", Now.AddYears(5), Now.AddYears(5)), new QuickNote(Guid.NewGuid(), " ", Now, Now)]));

        var loaded = NotesStore.Load(file);

        var note = Assert.Single(loaded);
        Assert.Equal("tidy me", note.Text);
        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.True(note.Updated <= DateTime.Now);
    }

    [Fact]
    public void GarbageIsNotNotes() => Assert.Null(NotesStore.Deserialize("[1, 2"));

    // ------------------------------------------------------------- the section

    [Fact]
    public void TheNotesSectionHasItsOwnShortcutLetterAndFeature()
    {
        var notes = IslandLogic.Section("notes");

        Assert.NotNull(notes);
        Assert.Equal("notchNotes", notes.Feature);
        Assert.Equal(IslandLogic.Sections.Length, IslandLogic.Sections.Select(s => s.Hotkey).Distinct().Count());
        Assert.Equal(IslandLogic.Sections.Length, IslandLogic.Sections.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void TheNotesSectionOnlyShowsWhileItsFeatureIsOn()
    {
        Assert.Contains(IslandLogic.VisibleSections(null, null, _ => true), s => s.Id == "notes");
        Assert.DoesNotContain(IslandLogic.VisibleSections(null, null, f => f != "notchNotes"), s => s.Id == "notes");
        Assert.DoesNotContain(IslandLogic.VisibleSections(null, "notes", _ => true), s => s.Id == "notes");
    }
}
