// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Island.Watch;

public enum WatchCondition { Changes, Settles, Contains, Reaches }

public abstract record WatchOutcome
{
    public sealed record Changed : WatchOutcome;
    public sealed record Settled : WatchOutcome;
    public sealed record Shows(string Match) : WatchOutcome;
    public sealed record Reached(string Value) : WatchOutcome;
    public sealed record Closed : WatchOutcome;

    public string Text => this switch
    {
        Changed => "It changed",
        Settled => "It stopped changing",
        Shows s => $"It shows {s.Match}",
        Reached r => $"It reached {r.Value}",
        _ => "The window closed",
    };
}

/// <summary>
/// Watch's change detection (port of upstream <c>NotchWatchSupport</c>): a 16×16 grey fingerprint with
/// 16 levels per cell, the recognised text as the signature when there is any, and the four rules.
/// </summary>
public static partial class WatchLogic
{
    public const double ChangeConfirmation = 3;
    public const double SettleInterval = 30;

    /// <summary>16×16 cells, each the mean grey / 16 (0..15), from a BGRA frame.</summary>
    public static byte[] Fingerprint(WatchFrame frame)
    {
        var cells = new byte[256];
        var sums = new double[256];
        var counts = new int[256];
        for (var y = 0; y < frame.Height; y++)
        {
            var cy = Math.Min(15, y * 16 / frame.Height);
            for (var x = 0; x < frame.Width; x++)
            {
                var cx = Math.Min(15, x * 16 / frame.Width);
                var i = (y * frame.Width + x) * 4;
                var grey = 0.114 * frame.Bgra[i] + 0.587 * frame.Bgra[i + 1] + 0.299 * frame.Bgra[i + 2];
                sums[cy * 16 + cx] += grey;
                counts[cy * 16 + cx]++;
            }
        }
        for (var c = 0; c < 256; c++) cells[c] = (byte)Math.Min(15, counts[c] == 0 ? 0 : (int)(sums[c] / counts[c] / 16));
        return cells;
    }

    /// <summary>No cell moved by more than one level, and at most three cells nudged by one.</summary>
    public static bool SameFingerprint(IReadOnlyList<byte> a, IReadOnlyList<byte> b)
    {
        if (a.Count != b.Count) return false;
        int moved = 0, nudged = 0;
        for (var i = 0; i < a.Count; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d > 1) moved++;
            if (d != 0) nudged++;
        }
        return moved == 0 && nudged <= 3;
    }

    /// <summary>Case, accent and width folded, whitespace collapsed.</summary>
    public static string Normalized(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }

    public static string Signature(string text, byte[] fingerprint)
    {
        var n = Normalized(text);
        return n.Length > 0 ? n : "image:" + string.Join(".", fingerprint);
    }

    public static bool SameSignature(string a, string b)
    {
        if (a == b) return true;
        if (!a.StartsWith("image:", StringComparison.Ordinal) || !b.StartsWith("image:", StringComparison.Ordinal)) return false;
        static byte[] Cells(string s) => s[6..].Split('.').Select(v => byte.TryParse(v, out var x) ? x : (byte)0).ToArray();
        return SameFingerprint(Cells(a), Cells(b));
    }

    /// <summary>The island's short reading: a percentage, a short first line, a clock, an amount, or the start of the text.</summary>
    public static string Headline(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return string.Empty;
        if (PercentRegex().Match(t) is { Success: true } p) return p.Value.Replace(" ", string.Empty);
        var first = t.Split('\n')[0].Trim();
        if (first.Length is > 0 and <= 22) return first;
        if (ClockRegex().Match(t) is { Success: true } c) return c.Value;
        if (AmountRegex().Match(t) is { Success: true } a) return a.Value.Trim();
        return t.Length <= 21 ? t : t[..21] + "…";
    }

    /// <summary>The first percentage, otherwise the first number, read in the current culture or invariant.</summary>
    public static double? FirstNumber(string text)
    {
        var m = PercentRegex().Match(text);
        var source = m.Success ? m.Value : NumberRegex().Match(text) is { Success: true } n ? n.Value : null;
        if (source is null) return null;
        source = source.Replace("%", string.Empty).Replace(" ", string.Empty).Trim();
        return ParseLocaleNumber(source);
    }

    public static double? ParseLocaleNumber(string s)
    {
        // "1,234.5" / "1.234,5" / "12,5": the last separator followed by 1–2 digits (or not 3) is the decimal mark.
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        string normal;
        if (lastComma >= 0 && lastDot >= 0) normal = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0) normal = s.Length - lastComma - 1 == 3 && lastComma > 0 ? s.Replace(",", "") : s.Replace(',', '.');
        else if (lastDot >= 0 && s.Count(c => c == '.') > 1) normal = s.Replace(".", "");
        else normal = s;
        return double.TryParse(normal, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?\s?%")] private static partial Regex PercentRegex();
    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\b")] private static partial Regex ClockRegex();
    [GeneratedRegex(@"[$€£¥]?\s?-?\d[\d.,]*\s?(?:[A-Za-z]{1,4}|[$€£¥])?")] private static partial Regex AmountRegex();
    [GeneratedRegex(@"-?\d[\d.,]*")] private static partial Regex NumberRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}

/// <summary>Applies a Watch rule to successive readings (upstream <c>NotchWatchTracker</c>).</summary>
public sealed class WatchTracker(WatchCondition condition, string? match = null, double? target = null)
{
    private string? _baseline;
    private double? _changedSince;
    private string? _lastSignature;
    private double _lastChange;
    private bool _sawChange;
    private double? _start;

    public WatchCondition Condition => condition;

    /// <summary>Feeds a reading at <paramref name="now"/> (seconds); returns the outcome once the rule holds.</summary>
    public WatchOutcome? Observe(string signature, string text, double now)
    {
        switch (condition)
        {
            case WatchCondition.Changes:
                if (_baseline is null) { _baseline = signature; return null; }
                if (WatchLogic.SameSignature(signature, _baseline)) { _changedSince = null; return null; }
                _changedSince ??= now;
                return now - _changedSince >= WatchLogic.ChangeConfirmation ? new WatchOutcome.Changed() : null;

            case WatchCondition.Settles:
                if (_lastSignature is null) { _lastSignature = signature; _lastChange = now; return null; }
                if (!WatchLogic.SameSignature(signature, _lastSignature))
                {
                    _lastSignature = signature;
                    _lastChange = now;
                    _sawChange = true;
                    return null;
                }
                return _sawChange && now - _lastChange >= WatchLogic.SettleInterval ? new WatchOutcome.Settled() : null;

            case WatchCondition.Contains:
                if (string.IsNullOrWhiteSpace(match)) return null;
                return WatchLogic.Normalized(text).Contains(WatchLogic.Normalized(match), StringComparison.Ordinal)
                    ? new WatchOutcome.Shows(match.Trim())
                    : null;

            case WatchCondition.Reaches:
                if (target is not { } goal || WatchLogic.FirstNumber(text) is not { } value) return null;
                if (_start is null)
                {
                    _start = value;
                    if (value == goal) return new WatchOutcome.Reached(WatchLogic.Headline(text));
                    return null;
                }
                var reached = _start < goal ? value >= goal : value <= goal;
                return reached ? new WatchOutcome.Reached(WatchLogic.Headline(text)) : null;
        }
        return null;
    }
}
