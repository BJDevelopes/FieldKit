// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Island.Notes;

/// <summary>One quick note on the island's Notes page.</summary>
public sealed record QuickNote(Guid Id, string Text, DateTime Created, DateTime Updated, bool Pinned = false);

/// <summary>Limits and decisions for quick notes, in plain static code.</summary>
public static class NotesRules
{
    public const int MaxNotes = 200;
    public const int MaxLength = 4000;

    /// <summary>
    /// A note as it is kept: line endings as \n, no blank lines at the start and no trailing space, and at most
    /// <see cref="MaxLength"/> characters. Empty when there is nothing to keep.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\n', ' ', '\t').TrimEnd();
        return normalised.Length <= MaxLength ? normalised : normalised[..MaxLength].TrimEnd();
    }

    /// <summary>Pinned notes first, then the most recently changed.</summary>
    public static List<QuickNote> Ordered(IEnumerable<QuickNote> notes) =>
        notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();

    /// <summary>How long ago, the way the page shows it: "just now", "5 min ago", "3 h ago", "yesterday", "12 Oct".</summary>
    public static string Age(DateTime now, DateTime then)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (then.Date == now.Date) return $"{(int)span.TotalHours} h ago";
        if (then.Date == now.Date.AddDays(-1)) return "yesterday";
        var culture = CultureInfo.CurrentCulture;
        return then.Year == now.Year
            ? then.ToString("d MMM", culture)
            : then.ToString("d MMM yyyy", culture);
    }

    /// <summary>
    /// Makes loaded notes safe to show: empty or missing notes dropped, repeated ids renewed, dates that lie in the
    /// future or run backwards repaired, and no more than <see cref="MaxNotes"/> kept (pinned first, then newest).
    /// </summary>
    public static List<QuickNote> Sanitize(IEnumerable<QuickNote?>? loaded, DateTime now)
    {
        var seen = new HashSet<Guid>();
        var result = new List<QuickNote>();
        foreach (var note in loaded ?? [])
        {
            if (note is null) continue;
            var text = Clean(note.Text);
            if (text.Length == 0) continue;
            var id = note.Id != Guid.Empty && seen.Add(note.Id) ? note.Id : NewId(seen);
            var created = note.Created > now || note.Created == default ? now : note.Created;
            var updated = note.Updated > now || note.Updated == default ? created : note.Updated;
            if (updated < created) updated = created;
            result.Add(note with { Id = id, Text = text, Created = created, Updated = updated });
        }
        return Ordered(result).Take(MaxNotes).ToList();
    }

    private static Guid NewId(HashSet<Guid> seen)
    {
        Guid id;
        do id = Guid.NewGuid();
        while (!seen.Add(id));
        return id;
    }
}

/// <summary>The notes in memory. Every change raises <see cref="Changed"/> so the store can save and the page can redraw.</summary>
public sealed class NotesCollection
{
    private readonly List<QuickNote> _notes = [];

    public event Action? Changed;

    /// <summary>Pinned notes first, then the most recently changed.</summary>
    public IReadOnlyList<QuickNote> Items => NotesRules.Ordered(_notes);

    public int Count => _notes.Count;

    /// <summary>Replaces everything (loading from disk) without raising <see cref="Changed"/>.</summary>
    public void Reset(IEnumerable<QuickNote> notes)
    {
        _notes.Clear();
        _notes.AddRange(notes);
    }

    /// <summary>
    /// Adds a note and returns it, or null when there is nothing to keep or the collection is full of pinned notes.
    /// When it is full the oldest unpinned note makes room.
    /// </summary>
    public QuickNote? Add(string? text, DateTime now)
    {
        var clean = NotesRules.Clean(text);
        if (clean.Length == 0) return null;
        if (_notes.Count >= NotesRules.MaxNotes)
        {
            var oldest = _notes.Where(n => !n.Pinned).OrderBy(n => n.Updated).FirstOrDefault();
            if (oldest is null) return null;
            _notes.Remove(oldest);
        }
        var note = new QuickNote(Guid.NewGuid(), clean, now, now);
        _notes.Add(note);
        Changed?.Invoke();
        return note;
    }

    /// <summary>Changes a note's text. An empty text changes nothing: the caller decides whether to delete.</summary>
    public bool Update(Guid id, string? text, DateTime now)
    {
        var clean = NotesRules.Clean(text);
        var index = _notes.FindIndex(n => n.Id == id);
        if (clean.Length == 0 || index < 0) return false;
        if (_notes[index].Text == clean) return true;
        _notes[index] = _notes[index] with { Text = clean, Updated = now };
        Changed?.Invoke();
        return true;
    }

    public bool Delete(Guid id)
    {
        if (_notes.RemoveAll(n => n.Id == id) == 0) return false;
        Changed?.Invoke();
        return true;
    }

    public bool TogglePin(Guid id)
    {
        var index = _notes.FindIndex(n => n.Id == id);
        if (index < 0) return false;
        _notes[index] = _notes[index] with { Pinned = !_notes[index].Pinned };
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every note that is not pinned and returns how many went.</summary>
    public int ClearUnpinned()
    {
        var removed = _notes.RemoveAll(n => !n.Pinned);
        if (removed > 0) Changed?.Invoke();
        return removed;
    }
}
