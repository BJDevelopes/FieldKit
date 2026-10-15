// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.Island.Lyrics;

/// <summary>A timed verse. Empty text ends the previous verse (shown as "♪").</summary>
public sealed record LyricLine(double Time, string Text);

/// <summary>What the lyrics panel shows for a track.</summary>
public sealed record LyricsPayload(IReadOnlyList<LyricLine> Lines, string? Plain, bool Instrumental)
{
    public bool IsSynced => Lines.Count > 0;
    public bool IsEmpty => !Instrumental && Lines.Count == 0 && string.IsNullOrWhiteSpace(Plain);
}

/// <summary>LRC parsing and line lookup (port of upstream <c>NotchLyricsSupport.parse</c>).</summary>
public static class LrcParser
{
    public const int MaximumLines = 2000;
    public const int MaximumBytes = 128 * 1024;

    /// <summary>
    /// Parses synced lyrics. Several leading time tags per line are allowed; any other tag stops tag
    /// parsing (metadata lines produce nothing); <c>[offset:ms]</c> shifts every time earlier by that
    /// amount; lines past <paramref name="duration"/> (when known) are dropped; lines with the same time merge.
    /// Returns an empty list when every line is empty.
    /// </summary>
    public static List<LyricLine> Parse(string text, double? duration = null)
    {
        var result = new List<(double Time, string Text, int Order)>();
        double offset = 0;
        var order = 0;
        var bytes = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿').Trim();
            if (line.Length == 0) continue;
            var times = new List<double>();
            var rest = line;
            while (rest.StartsWith('['))
            {
                var close = rest.IndexOf(']');
                if (close < 0) break;
                var tag = rest[1..close];
                if (TryOffset(tag, out var ms))
                {
                    offset = ms / 1000.0;
                    rest = string.Empty;
                    break;
                }
                if (!TryTime(tag, out var t)) break;
                times.Add(t);
                rest = rest[(close + 1)..];
            }
            if (times.Count == 0) continue;
            var verse = rest.Trim();
            foreach (var t in times)
            {
                bytes += Encoding.UTF8.GetByteCount(verse);
                if (bytes > MaximumBytes || result.Count >= MaximumLines) break;
                result.Add((t, verse, order++));
            }
        }

        var shifted = result
            .Select(r => (Time: Math.Max(0, r.Time - offset), r.Text, r.Order))
            .Where(r => duration is not > 0 || r.Time <= duration)
            .OrderBy(r => r.Time).ThenBy(r => r.Order)
            .ToList();
        var merged = new List<LyricLine>();
        foreach (var group in shifted.GroupBy(r => r.Time))
        {
            var texts = group.Select(g => g.Text).Where(t => t.Length > 0).ToList();
            merged.Add(new LyricLine(group.Key, string.Join("\n", texts)));
        }
        return merged.All(l => l.Text.Length == 0) ? [] : merged;
    }

    private static bool TryOffset(string tag, out double ms)
    {
        ms = 0;
        if (!tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!double.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out ms)) return false;
        if (Math.Abs(ms) > 60000) { ms = 0; return true; }
        return true;
    }

    /// <summary>"mm:ss", "mm:ss.xx": minutes ≤ 10080, seconds in [0, 60).</summary>
    public static bool TryTime(string tag, out double seconds)
    {
        seconds = 0;
        var colon = tag.IndexOf(':');
        if (colon <= 0) return false;
        var m = tag[..colon];
        var s = tag[(colon + 1)..];
        if (m.Length == 0 || !m.All(char.IsAsciiDigit) || s.Length == 0 || !s.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        if (!int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > 10080) return false;
        if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secs) || secs is < 0 or >= 60) return false;
        seconds = minutes * 60 + secs;
        return true;
    }

    /// <summary>Index of the last line with time ≤ position − offset, or -1 before the first verse.</summary>
    public static int CurrentIndex(IReadOnlyList<LyricLine> lines, double position, double userOffset = 0)
    {
        var target = position - userOffset;
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= target) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The user timing offset: quarter-second steps clamped to ±10 s.</summary>
    public static double StepOffset(double current, int direction) =>
        Math.Clamp(Math.Round((current + direction * 0.25) * 4) / 4, -10, 10);
}
