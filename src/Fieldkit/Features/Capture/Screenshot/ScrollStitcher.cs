// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Capture;

public enum ScrollStep { End, Advanced, Unmatched }

/// <summary>How two successive views of a scrolling area relate.</summary>
/// <param name="Advance">Rows the content moved up between the two views.</param>
/// <param name="FixedTop">Rows at the top that stayed put (sticky header).</param>
/// <param name="FixedBottom">Rows at the bottom that stayed put (sticky footer).</param>
public readonly record struct ScrollMatch(ScrollStep Step, int Advance = 0, int FixedTop = 0, int FixedBottom = 0);

/// <summary>
/// Joins successive views of a scrolled area into one tall image. Matching only accepts an
/// overlap that is clearly better than every other offset, so repeated blank bands end the
/// capture instead of producing a seam (the upstream policy). Pure; tested on synthetic pages.
/// </summary>
public static class ScrollStitcher
{
    /// <summary>Upstream safety limits.</summary>
    public const int MaximumFrames = 512;
    public const long MaximumPixels = 60_000_000;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(120);

    private const int SampleColumns = 160;

    /// <summary>Luma of a BGRA buffer.</summary>
    public static byte[] ToGray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, j = 0; j < gray.Length; i += 4, j++)
            gray[j] = (byte)((bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8);
        return gray;
    }

    /// <summary>Mean absolute difference of two rows over the sampled columns.</summary>
    private static double RowDifference(byte[] a, int rowA, byte[] b, int rowB, int width, int[] columns)
    {
        var sum = 0;
        var oa = rowA * width;
        var ob = rowB * width;
        foreach (var c in columns) sum += Math.Abs(a[oa + c] - b[ob + c]);
        return (double)sum / columns.Length;
    }

    private static int[] Columns(int width)
    {
        var n = Math.Min(width, SampleColumns);
        var cols = new int[n];
        for (var i = 0; i < n; i++) cols[i] = (int)((i + 0.5) * width / n);
        return cols;
    }

    public static int FixedTopRows(byte[] prev, byte[] cur, int width, int height)
    {
        var cols = Columns(width);
        var rows = 0;
        while (rows < height && RowDifference(prev, rows, cur, rows, width, cols) <= 2) rows++;
        return rows;
    }

    public static int FixedBottomRows(byte[] prev, byte[] cur, int width, int height)
    {
        var cols = Columns(width);
        var rows = 0;
        while (rows < height && RowDifference(prev, height - 1 - rows, cur, height - 1 - rows, width, cols) <= 2) rows++;
        return rows;
    }

    /// <summary>Finds how far the content moved between two equally sized grey frames.</summary>
    public static ScrollMatch Match(byte[] prev, byte[] cur, int width, int height)
    {
        if (prev.Length != width * height || cur.Length != prev.Length || height < 24 || width < 1)
            return new ScrollMatch(ScrollStep.Unmatched);

        var cols = Columns(width);
        var top = FixedTopRows(prev, cur, width, height);
        if (top >= height) return new ScrollMatch(ScrollStep.End);
        var bottom = FixedBottomRows(prev, cur, width, height);

        // A sticky bar is at most a third of the view; more than that is shared content.
        top = Math.Min(top, height / 3);
        bottom = Math.Min(bottom, height / 3);
        var band = height - top - bottom;
        if (band < 24) return new ScrollMatch(ScrollStep.End);

        var minOverlap = Math.Max(8, band / 10);
        var maxAdvance = band - minOverlap;
        var best = double.MaxValue;
        var bestAdvance = -1;
        var scores = new double[maxAdvance + 1];
        for (var a = 1; a <= maxAdvance; a++)
        {
            var overlap = band - a;
            double total = 0;
            var limit = (best + 0.75) * overlap;
            var y = 0;
            for (; y < overlap; y++)
            {
                total += RowDifference(prev, top + a + y, cur, top + y, width, cols);
                if (total > limit && bestAdvance >= 0) break;
            }
            var score = y < overlap ? double.MaxValue : total / overlap;
            scores[a] = score;
            if (score < best)
            {
                best = score;
                bestAdvance = a;
            }
        }
        if (bestAdvance < 0 || best > 4) return new ScrollMatch(ScrollStep.Unmatched);

        // A unique match is required: equally good offsets elsewhere mean flat or repeating content.
        for (var a = 1; a <= maxAdvance; a++)
        {
            if (Math.Abs(a - bestAdvance) <= 2) continue;
            if (scores[a] <= best + 0.75) return new ScrollMatch(ScrollStep.Unmatched);
        }

        // The matched overlap must carry some detail, or any offset would have fitted.
        if (!HasDetail(cur, width, top, band - bestAdvance, cols)) return new ScrollMatch(ScrollStep.Unmatched);
        return new ScrollMatch(ScrollStep.Advanced, bestAdvance, top, bottom);
    }

    private static bool HasDetail(byte[] gray, int width, int fromRow, int rows, int[] cols)
    {
        int min = 255, max = 0;
        for (var y = fromRow; y < fromRow + rows; y++)
        {
            foreach (var c in cols)
            {
                var v = gray[y * width + c];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        return max - min >= 12;
    }

    /// <summary>
    /// Joins BGRA frames using the matches between each pair (<paramref name="matches"/>[i] relates
    /// frame i and i+1). A sticky footer is kept once, at the end.
    /// </summary>
    public static (byte[] Bgra, int Height) Stitch(IReadOnlyList<byte[]> frames, IReadOnlyList<ScrollMatch> matches, int width, int height)
    {
        if (frames.Count == 0) return ([], 0);
        var advanced = matches.Where(m => m.Step == ScrollStep.Advanced).ToList();
        var footer = advanced.Count == 0 ? 0 : advanced.Min(m => m.FixedBottom);
        var stride = width * 4;
        var rows = height - footer + advanced.Sum(m => m.Advance) + footer;
        var output = new byte[(long)rows * stride];
        var cursor = 0;

        void Append(byte[] frame, int fromRow, int count)
        {
            Buffer.BlockCopy(frame, fromRow * stride, output, cursor * stride, count * stride);
            cursor += count;
        }

        Append(frames[0], 0, height - footer);
        var last = frames[0];
        for (var i = 0; i < matches.Count && i + 1 < frames.Count; i++)
        {
            if (matches[i].Step != ScrollStep.Advanced) continue;
            var a = matches[i].Advance;
            last = frames[i + 1];
            Append(last, height - footer - a, a);
        }
        if (footer > 0) Append(last, height - footer, footer);
        return (output, cursor);
    }
}
