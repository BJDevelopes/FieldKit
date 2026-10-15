// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Media;

/// <summary>
/// Band levels and the bars' motion (port of upstream <c>NotchAudioLevelSupport</c> and
/// <c>NotchEqualizerBars</c>).
/// </summary>
public static class EqualizerMath
{
    /// <summary>Band edges in Hz: seven bands from 50 Hz to 12 kHz.</summary>
    public static readonly double[] Edges = [50, 120, 250, 500, 1000, 2000, 4500, 12000];

    /// <summary>
    /// Mean magnitude per band, as 0..1 levels: dB = 20·log10(mean), level = clamp((dB + 60) / 60).
    /// <paramref name="magnitudes"/> are |X[k]|/N for k &lt; N/2.
    /// </summary>
    public static double[] Bands(ReadOnlySpan<float> magnitudes, int sampleRate, int fftSize, int bandCount = 7)
    {
        var bands = Math.Min(bandCount, Edges.Length - 1);
        var result = new double[bands];
        var half = fftSize / 2;
        var binWidth = (double)sampleRate / fftSize;
        for (var i = 0; i < bands; i++)
        {
            var lower = Math.Clamp((int)Math.Floor(Edges[i] / binWidth), 1, half - 1);
            var upper = Math.Clamp((int)Math.Floor(Edges[i + 1] / binWidth), lower + 1, half);
            double sum = 0;
            var count = 0;
            for (var k = lower; k < upper && k < magnitudes.Length; k++)
            {
                sum += magnitudes[k];
                count++;
            }
            var mean = count > 0 ? sum / count : 0;
            result[i] = mean <= 0 ? 0 : Math.Clamp((20 * Math.Log10(mean) + 60) / 60, 0, 1);
        }
        return result;
    }

    /// <summary>Which band drives bar <paramref name="bar"/> of <paramref name="bars"/>.</summary>
    public static int BandForBar(int bar, int bars, int bands) =>
        Math.Min(bands - 1, (int)Math.Floor((bar + 0.5) / bars * bands));

    /// <summary>Upstream's swing: a triangle wave eased with a cosine, 0..1..0 over p = 0..2.</summary>
    public static double Swing(double p)
    {
        var x = p % 2;
        if (x < 0) x += 2;
        var o = x <= 1 ? x : 2 - x;
        return (1 - Math.Cos(Math.PI * o)) / 2;
    }

    /// <summary>
    /// The synthetic motion the bars keep when the live equalizer is off (or has not heard anything yet).
    /// Centre bars swing higher; each bar has its own period and phase.
    /// </summary>
    public static double SyntheticHeight(int i, int n, double t, double height, double barWidth)
    {
        var center = (n - 1) / 2.0;
        var distance = Math.Abs(i - center) / Math.Max(1, center);
        var envelope = Math.Pow(1 - distance, 1.5);
        var low = Math.Max(barWidth, height * (0.12 + envelope * 0.25));
        var high = Math.Max(barWidth, height * (0.12 + envelope * 0.88));
        var rise = Math.PI / (5.2 + i * 0.61);
        var lead = i * 0.17;
        return low + (high - low) * Swing((t + lead) / rise);
    }

    /// <summary>Height of a bar from a live level.</summary>
    public static double LiveHeight(double level, double height, double barWidth) =>
        Math.Max(barWidth, height * (0.1 + 0.9 * level));

    /// <summary>Width of a row of <paramref name="n"/> bars (gaps are 0.85 bar widths).</summary>
    public static double RowWidth(int n, double barWidth) => n * barWidth + (n - 1) * barWidth * 0.85;
}

/// <summary>Auto-gain and attack/decay smoothing of band levels (upstream constants).</summary>
public sealed class EqualizerSmoother(int count)
{
    private double _ceiling = 0.05;

    public int Count { get; } = count;
    public double[] Levels { get; } = new double[count];

    /// <summary>True once something above the noise floor has been heard.</summary>
    public bool Heard { get; private set; }

    public void Push(IReadOnlyList<double> raw)
    {
        var peak = raw.Count == 0 ? 0 : raw.Max();
        _ceiling = Math.Max(Math.Max(peak, _ceiling * 0.995), 0.05);
        var gain = 0.92 / _ceiling;
        if (peak * gain > 0.001) Heard = true;
        for (var i = 0; i < Count; i++)
        {
            var band = EqualizerMath.BandForBar(i, Count, Math.Max(1, raw.Count));
            var target = raw.Count == 0 ? 0 : Math.Clamp(raw[band] * gain, 0, 1);
            Levels[i] += (target - Levels[i]) * (target > Levels[i] ? 0.55 : 0.12);
        }
    }

    public void Reset()
    {
        _ceiling = 0.05;
        Heard = false;
        Array.Clear(Levels);
    }
}
