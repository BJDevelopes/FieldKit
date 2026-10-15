// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;

namespace Fieldkit.Features.ClipboardTools;

public enum MoveDirection { Up, Down }

/// <summary>How a selection of entries travels on the clipboard.</summary>
public abstract record PasteMode
{
    /// <summary>Only file entries: pasted as the files themselves.</summary>
    public sealed record Files(IReadOnlyList<string> Paths) : PasteMode;
    /// <summary>Text (file entries contribute their paths), joined by line breaks.</summary>
    public sealed record Text(string Value) : PasteMode;
    /// <summary>A single picture.</summary>
    public sealed record Image(ClipboardEntry Entry) : PasteMode;
}

/// <summary>
/// The decisions behind the clipboard history, free of any window or clipboard: de-duplication,
/// ordering, retention limits, pins, moves, "clear unpinned" and how a selection is pasted.
/// Mirrors upstream ClipboardHistorySupport / ClipboardHistoryService.
/// </summary>
public static class ClipboardHistoryLogic
{
    /// <summary>A copied document stays usable, a pathological payload stays bounded.</summary>
    public const int MaxCharacters = 1_000_000;
    public const int MaxStoredTextUtf8Bytes = 64 * 1024 * 1024;
    public const int PreviewCharacters = 2_000;

    /// <summary>The limits Settings offers (0 = unlimited), like upstream.</summary>
    public static readonly int[] AllowedLimits = [20, 50, 100, 250, 500, 1_000, 10_000, 0];
    public const int DefaultLimit = 50;

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".heic", ".heif", ".tiff", ".tif", ".gif", ".webp", ".bmp", ".ico", ".svg", ".avif",
    };

    public static bool IsImageFileName(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static int SanitizedLimit(int value) => AllowedLimits.Contains(value) ? value : DefaultLimit;

    /// <summary>How many days unpinned items are kept (0 = until the limit pushes them out), upstream #2652.</summary>
    public static readonly int[] AllowedAgeDays = [0, 1, 7, 30, 90, 365];

    public static int SanitizedAgeDays(int value) => AllowedAgeDays.Contains(value) ? value : 0;

    /// <summary>Drops unpinned entries copied more than <paramref name="maxAgeDays"/> ago. Pinned entries always stay.</summary>
    public static List<ClipboardEntry> WithinAge(IReadOnlyList<ClipboardEntry> entries, int maxAgeDays, DateTime now) =>
        maxAgeDays <= 0
            ? entries.ToList()
            : entries.Where(e => e.IsPinned || now - e.CopiedAt < TimeSpan.FromDays(maxAgeDays)).ToList();

    /// <summary>The text worth storing, or null (blank or too long).</summary>
    public static string? StorableText(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxCharacters) return null;
        return text;
    }

    /// <summary>Bounded preview; folds line breaks and tabs unless <paramref name="keepLineBreaks"/>.</summary>
    public static string PreviewText(string text, bool keepLineBreaks)
    {
        var cut = text.Length > PreviewCharacters;
        var prefix = cut ? text[..PreviewCharacters] : text;
        var folded = keepLineBreaks
            ? prefix.Replace('\t', ' ').Trim()
            : prefix.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        var visible = folded.Length == 0 ? prefix : folded;
        return cut ? visible + "…" : visible;
    }

    /// <summary>Pinned first, then the rest, each group keeping its order.</summary>
    public static List<ClipboardEntry> Normalized(IEnumerable<ClipboardEntry> entries)
    {
        var list = entries.ToList();
        return [.. list.Where(e => e.IsPinned), .. list.Where(e => !e.IsPinned)];
    }

    /// <summary>
    /// Adds a fresh copy. Copying something already saved refreshes that entry (same id, keeps its
    /// pin) and moves it to the top of its group instead of duplicating it.
    /// </summary>
    /// <param name="keepsSource">A copy made inside the history itself keeps the entry's original app.</param>
    public static (List<ClipboardEntry> Entries, ClipboardEntry Promoted) Promote(
        IReadOnlyList<ClipboardEntry> entries, ClipboardEntry fresh, DateTime now, bool keepsSource = false)
    {
        var existing = entries.FirstOrDefault(e => e.MatchesContent(fresh));
        var promoted = fresh with { CopiedAt = now };
        if (existing is not null)
        {
            promoted = promoted with
            {
                Id = existing.Id,
                PinnedAt = existing.PinnedAt,
                ImageFile = existing.ImageFile ?? fresh.ImageFile,
                ImageWidth = existing.ImageWidth ?? fresh.ImageWidth,
                ImageHeight = existing.ImageHeight ?? fresh.ImageHeight,
                SourcePath = keepsSource ? existing.SourcePath : fresh.SourcePath,
            };
        }
        var rest = entries.Where(e => !e.MatchesContent(fresh)).ToList();
        var pinned = rest.Where(e => e.IsPinned).ToList();
        var recent = rest.Where(e => !e.IsPinned).ToList();
        if (promoted.IsPinned) pinned.Insert(0, promoted);
        else recent.Insert(0, promoted);
        return ([.. pinned, .. recent], promoted);
    }

    /// <summary>
    /// What survives the limit: every pinned entry, then the newest unpinned ones up to
    /// <paramref name="recentLimit"/> (0 = unlimited), all within a total text budget.
    /// </summary>
    public static List<ClipboardEntry> Retained(IReadOnlyList<ClipboardEntry> entries, int recentLimit,
        int textByteLimit = MaxStoredTextUtf8Bytes)
    {
        var remaining = Math.Max(0, textByteLimit);
        List<ClipboardEntry> Take(IEnumerable<ClipboardEntry> candidates, int? limit)
        {
            var result = new List<ClipboardEntry>();
            foreach (var entry in candidates)
            {
                if (limit is int l && result.Count >= l) break;
                var bytes = entry.Kind == ClipboardEntryKind.Text ? Encoding.UTF8.GetByteCount(entry.Text) : 0;
                if (bytes > remaining) continue;
                remaining -= bytes;
                result.Add(entry);
            }
            return result;
        }
        var pinned = Take(entries.Where(e => e.IsPinned), null);
        var recent = Take(entries.Where(e => !e.IsPinned), recentLimit <= 0 ? null : recentLimit);
        return [.. pinned, .. recent];
    }

    /// <summary>Pins (to the top of the pinned group) or unpins (to the top of the recent group).</summary>
    public static List<ClipboardEntry> TogglePin(IReadOnlyList<ClipboardEntry> entries, Guid id, DateTime now)
    {
        var list = entries.ToList();
        var index = list.FindIndex(e => e.Id == id);
        if (index < 0) return list;
        var entry = list[index];
        list.RemoveAt(index);
        if (entry.IsPinned)
        {
            var updated = entry with { PinnedAt = null };
            var firstRecent = list.FindIndex(e => !e.IsPinned);
            list.Insert(firstRecent < 0 ? list.Count : firstRecent, updated);
        }
        else
        {
            list.Insert(0, entry with { PinnedAt = now });
        }
        return Normalized(list);
    }

    /// <summary>Whether <paramref name="id"/> can move one step within its group (pinned or recent).</summary>
    public static bool CanMove(IReadOnlyList<ClipboardEntry> entries, Guid id, MoveDirection direction) =>
        MoveDestination(entries, id, direction) is not null;

    public static List<ClipboardEntry> Move(IReadOnlyList<ClipboardEntry> entries, Guid id, MoveDirection direction)
    {
        var list = entries.ToList();
        var from = list.FindIndex(e => e.Id == id);
        var to = MoveDestination(list, id, direction);
        if (from < 0 || to is not int target) return list;
        (list[from], list[target]) = (list[target], list[from]);
        return list;
    }

    private static int? MoveDestination(IReadOnlyList<ClipboardEntry> entries, Guid id, MoveDirection direction)
    {
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return null;
        var group = Enumerable.Range(0, entries.Count).Where(i => entries[i].IsPinned == entry.IsPinned).ToList();
        var position = group.FindIndex(i => entries[i].Id == id);
        if (direction == MoveDirection.Up) return position > 0 ? group[position - 1] : null;
        return position + 1 < group.Count ? group[position + 1] : null;
    }

    /// <summary>
    /// Entries just pasted or copied again: they get a fresh time and move to the top of the recent
    /// group in the order they were pasted. Pinned entries keep their place.
    /// </summary>
    public static List<ClipboardEntry> Touch(IReadOnlyList<ClipboardEntry> entries, IReadOnlyList<Guid> ids, DateTime now)
    {
        var order = new Dictionary<Guid, int>();
        foreach (var id in ids) order.TryAdd(id, order.Count);
        var pinned = new List<ClipboardEntry>();
        var reused = new List<ClipboardEntry>();
        var others = new List<ClipboardEntry>();
        foreach (var entry in entries)
        {
            var isReused = order.ContainsKey(entry.Id);
            var e = isReused ? entry with { CopiedAt = now } : entry;
            if (e.IsPinned) pinned.Add(e);
            else if (isReused) reused.Add(e);
            else others.Add(e);
        }
        reused.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        return [.. pinned, .. reused, .. others];
    }

    /// <summary>What a "Clear unpinned" confirmation counts: each unpinned entry and when it was last copied.</summary>
    public static Dictionary<Guid, DateTime> RecentSnapshot(IReadOnlyList<ClipboardEntry> entries) =>
        entries.Where(e => !e.IsPinned).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().CopiedAt);

    /// <summary>
    /// Deletes exactly the unpinned entries the confirmation counted, as they were then: a copy that
    /// lands while it is open survives, even one that copies a counted entry again.
    /// </summary>
    public static List<ClipboardEntry> ClearRecent(IReadOnlyList<ClipboardEntry> entries, IReadOnlyDictionary<Guid, DateTime> confirmed) =>
        entries.Where(e => e.IsPinned || !confirmed.TryGetValue(e.Id, out var at) || at != e.CopiedAt).ToList();

    /// <summary>How a selection pastes (see <see cref="PasteMode"/>). Null for an empty selection.</summary>
    public static PasteMode? PasteModeFor(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries.Count == 0) return null;
        if (entries.All(e => e.Kind == ClipboardEntryKind.Files))
            return new PasteMode.Files(entries.SelectMany(e => e.FilePaths).ToList());
        if (entries.Count == 1 && entries[0].Kind == ClipboardEntryKind.Image) return new PasteMode.Image(entries[0]);
        var texts = entries
            .Where(e => e.Kind != ClipboardEntryKind.Image)
            .Select(e => e.Kind == ClipboardEntryKind.Files ? string.Join("\n", e.FilePaths) : e.Text)
            .ToList();
        if (texts.Count == 0) return new PasteMode.Image(entries.First(e => e.Kind == ClipboardEntryKind.Image));
        return new PasteMode.Text(string.Join("\n", texts));
    }

    /// <summary>Shift-click range: from the anchor row to the clicked one, inclusive, either direction.</summary>
    public static List<T> RangeSelection<T>(IReadOnlyList<T> all, int anchor, int target)
    {
        if (all.Count == 0) return [];
        var low = Math.Clamp(Math.Min(anchor, target), 0, all.Count - 1);
        var high = Math.Clamp(Math.Max(anchor, target), 0, all.Count - 1);
        return all.Skip(low).Take(high - low + 1).ToList();
    }

    /// <summary>
    /// The text a copy should be stored as: a lone web link is normalised, and a copied link whose
    /// plain text is just the same address without its scheme is stored as the full link.
    /// </summary>
    public static string? PreferredText(string? webUrl, string? plainText)
    {
        // Keep the copied text exactly (indented code, trailing line breaks of spreadsheet ranges);
        // only the URL checks look at its trimmed form.
        var original = string.IsNullOrWhiteSpace(plainText) ? null : plainText;
        var plain = original?.Trim();
        if (plain is not null && NormalizedWebUrl(plain) is string normalizedPlain) return normalizedPlain;
        var url = NormalizedWebUrl(webUrl);
        if (url is null) return original;
        if (plain is null) return url;
        if (plain.Any(char.IsWhiteSpace)) return original;
        if (plain.StartsWith("//", StringComparison.Ordinal)) return url;
        var lower = url.ToLowerInvariant();
        var withSlashes = lower.StartsWith("http://", StringComparison.Ordinal) ? "//" + url["http://".Length..]
            : lower.StartsWith("https://", StringComparison.Ordinal) ? "//" + url["https://".Length..] : url;
        var withoutSlashes = withSlashes.TrimStart('/');
        return plain == withSlashes || plain == withoutSlashes ? url : original;
    }

    private static string? NormalizedWebUrl(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        return text;
    }
}

/// <summary>Leaves out text that looks like a secret (upstream ClipboardHistorySensitiveText).</summary>
public static class SensitiveText
{
    private static readonly string[] ObviousWords = ["password", "passwd", "secret", "token", "apikey", "api_key", "authorization"];

    public static bool LooksSensitive(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (ObviousWords.Any(lowered.Contains)) return true;
        if (IsWebUrl(text)) return false;
        // An identifier (GUID) is nobody's secret, though it fits the shape below exactly.
        if (IsIdentifierCode(text)) return false;
        if (text.Length < 20 || text.Length > 160 || text.Any(char.IsWhiteSpace)) return false;
        var hasLetter = text.Any(char.IsLetter);
        var hasDigit = text.Any(char.IsDigit);
        var hasSymbol = text.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
        return hasLetter && hasDigit && hasSymbol;
    }

    private static bool IsIdentifierCode(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('{') && value.EndsWith('}')) value = value[1..^1];
        var groups = value.Split('-');
        int[] lengths = [8, 4, 4, 4, 12];
        if (groups.Length != 5) return false;
        for (var i = 0; i < 5; i++)
        {
            if (groups[i].Length != lengths[i] || !groups[i].All(Uri.IsHexDigit)) return false;
        }
        return true;
    }

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
}

/// <summary>What the auto clear poll does with what it just saw (upstream ClipboardAutoClearSupport).</summary>
public enum AutoClearDecision { NoteChange, Clear, Wait }

public static class AutoClearLogic
{
    public const int MinDelaySeconds = 5;
    public const int MaxDelaySeconds = 3_600;
    public const int DefaultDelaySeconds = 20;

    public static int SanitizedDelay(int seconds) =>
        seconds is >= MinDelaySeconds and <= MaxDelaySeconds ? seconds : DefaultDelaySeconds;

    /// <param name="sequence">The clipboard sequence number just read.</param>
    /// <param name="lastSequence">The number last acted on.</param>
    /// <param name="lastClearedSequence">The number our own clear produced (never mistaken for a copy).</param>
    /// <param name="lastChange">When <paramref name="lastSequence"/> was first seen (wall clock, so sleep counts).</param>
    public static AutoClearDecision Decide(uint sequence, uint lastSequence, uint? lastClearedSequence,
        DateTime lastChange, DateTime now, TimeSpan delay)
    {
        if (sequence != lastSequence) return AutoClearDecision.NoteChange;
        if (sequence == lastClearedSequence) return AutoClearDecision.Wait;
        return now - lastChange >= delay ? AutoClearDecision.Clear : AutoClearDecision.Wait;
    }
}
