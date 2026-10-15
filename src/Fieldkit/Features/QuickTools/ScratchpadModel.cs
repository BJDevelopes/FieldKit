// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Fieldkit.Features.QuickTools;

public sealed class ScratchPad
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime? ModifiedAt { get; set; }
}

public sealed class ScratchpadDocument
{
    public List<ScratchPad> Pads { get; set; } = [];
    public string? SelectedId { get; set; }
}

public enum ScratchpadRetention { Never, Day, Week, Month }

/// <summary>Pure Scratchpad rules ported from upstream ScratchpadSupport (unit-tested).</summary>
public static partial class ScratchpadLogic
{
    public const int MaxPads = 12;
    public const int MaxNameLength = 40;
    public const string DefaultName = "Scratchpad";
    public const double MinTextSize = 10;
    public const double MaxTextSize = 22;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Trims, collapses whitespace and caps at 40 characters; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var s = Whitespace().Replace(name.Trim(), " ");
        if (s.Length == 0) return null;
        return s.Length > MaxNameLength ? s[..MaxNameLength].TrimEnd() : s;
    }

    /// <summary>"Scratchpad N" with the first free N from 1 to 12, else count + 1.</summary>
    public static string NextName(IReadOnlyCollection<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.CurrentCultureIgnoreCase);
        for (var n = 1; n <= MaxPads; n++)
        {
            var candidate = $"{DefaultName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{DefaultName} {existing.Count + 1}";
    }

    /// <summary>At most 12 pads, unique ids, non-empty names, a valid selection, at least one pad.</summary>
    public static ScratchpadDocument Sanitized(ScratchpadDocument? doc)
    {
        var result = new ScratchpadDocument();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pad in doc?.Pads ?? [])
        {
            if (result.Pads.Count >= MaxPads) break;
            if (string.IsNullOrEmpty(pad.Id) || !ids.Add(pad.Id)) continue;
            result.Pads.Add(new ScratchPad { Id = pad.Id, Name = pad.Name ?? string.Empty, Text = pad.Text ?? string.Empty, ModifiedAt = pad.ModifiedAt });
        }
        foreach (var pad in result.Pads)
        {
            var clean = CleanName(pad.Name);
            pad.Name = clean ?? NextName(result.Pads.Where(p => p != pad).Select(p => p.Name).ToList());
        }
        if (result.Pads.Count == 0) result.Pads.Add(new ScratchPad { Name = NextName([]) });
        result.SelectedId = result.Pads.Any(p => p.Id == doc?.SelectedId) ? doc!.SelectedId : result.Pads[0].Id;
        return result;
    }

    public static ScratchpadRetention ParseRetention(string? value) => value switch
    {
        "day" => ScratchpadRetention.Day,
        "week" => ScratchpadRetention.Week,
        "month" => ScratchpadRetention.Month,
        _ => ScratchpadRetention.Never,
    };

    public static TimeSpan? RetentionLimit(ScratchpadRetention r) => r switch
    {
        ScratchpadRetention.Day => TimeSpan.FromDays(1),
        ScratchpadRetention.Week => TimeSpan.FromDays(7),
        ScratchpadRetention.Month => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>Empties pads not edited for longer than the limit (keeps tab and name). Returns true when anything changed.</summary>
    public static bool ApplyRetention(ScratchpadDocument doc, ScratchpadRetention retention, DateTime now)
    {
        if (RetentionLimit(retention) is not { } limit) return false;
        var changed = false;
        foreach (var pad in doc.Pads)
        {
            if (pad.ModifiedAt is not { } modified || pad.Text.Length == 0) continue;
            if (now - modified <= limit) continue;
            pad.Text = string.Empty;
            pad.ModifiedAt = null;
            changed = true;
        }
        return changed;
    }

    public static double ClampTextSize(double size) => double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinTextSize, MaxTextSize) : 13;

    /// <summary>"&lt;name&gt; yyyy-MM-dd.txt" with "/" and ":" (and other invalid characters) replaced by "-".</summary>
    public static string SuggestedFileName(string padName, DateTime date, string extension = ".txt")
    {
        var name = padName;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars().Concat(['/', ':'])) name = name.Replace(c, '-');
        return $"{name} {date:yyyy-MM-dd}{extension}";
    }

    /// <summary>Index to select after closing the pad at <paramref name="closedIndex"/>.</summary>
    public static int SelectionAfterClose(int closedIndex, int remainingCount) =>
        remainingCount == 0 ? 0 : Math.Min(closedIndex, remainingCount - 1);
}
