// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fieldkit.Features.Monitor.Logic;

public enum FanMode { System, Manual, Curve }

/// <summary>A temperature the curve can follow (upstream FanControlTemperatureSource, PC flavour).</summary>
public enum FanTemperatureSource { HottestCpu, AverageCpu, HottestGpu, Motherboard }

public sealed record FanCurvePoint(int Temperature, int Level);

public sealed record FanCurve(FanTemperatureSource Sensor, IReadOnlyList<FanCurvePoint> Points);

/// <summary>Fan control policy: levels, curve validation and evaluation (port of upstream FanControlPolicy).</summary>
public static class FanPolicy
{
    public const int MinimumLevel = 0;
    public const int MaximumLevel = 100;
    public const int LevelStep = 5;
    public const int DefaultLevel = MaximumLevel;
    public const int MinimumCurveTemperature = 20;
    public const int MaximumCurveTemperature = 110;
    public const int MinimumPointCount = 2;
    public const int MaximumPointCount = 8;
    public const double Hysteresis = 2.0;

    public static readonly FanCurve DefaultCurve = new(FanTemperatureSource.HottestCpu,
        [new FanCurvePoint(50, 0), new FanCurvePoint(70, 100)]);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool ValidLevel(int level) =>
        level is >= MinimumLevel and <= MaximumLevel && level % LevelStep == 0;

    /// <summary>Rounds a slider value up to the next step and clamps it.</summary>
    public static int SteppedLevel(int level)
    {
        var clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
        var remainder = clamped % LevelStep;
        return remainder == 0 ? clamped : clamped + LevelStep - remainder;
    }

    public static bool ValidCurve(FanCurve curve)
    {
        if (curve.Points.Count is < MinimumPointCount or > MaximumPointCount) return false;
        for (var i = 0; i < curve.Points.Count; i++)
        {
            var p = curve.Points[i];
            if (p.Temperature is < MinimumCurveTemperature or > MaximumCurveTemperature || !ValidLevel(p.Level)) return false;
            if (i > 0)
            {
                var prev = curve.Points[i - 1];
                if (p.Temperature <= prev.Temperature || p.Level < prev.Level) return false;
            }
        }
        return true;
    }

    public static bool ValidCurves(IReadOnlyList<FanCurve> curves) =>
        curves.Count >= 1 && curves.Count <= Enum.GetValues<FanTemperatureSource>().Length
        && curves.Select(c => c.Sensor).Distinct().Count() == curves.Count
        && curves.All(ValidCurve);

    /// <summary>Linear interpolation between points, rounded up to the level step.</summary>
    public static int InterpolatedLevel(IReadOnlyList<FanCurvePoint> points, double temperature)
    {
        if (points.Count == 0) return MinimumLevel;
        var first = points[0];
        var last = points[^1];
        if (temperature <= first.Temperature) return first.Level;
        if (temperature >= last.Temperature) return last.Level;
        for (var i = 1; i < points.Count; i++)
        {
            var upper = points[i];
            if (temperature > upper.Temperature) continue;
            var lower = points[i - 1];
            var progress = (temperature - lower.Temperature) / (upper.Temperature - lower.Temperature);
            var raw = lower.Level + (upper.Level - lower.Level) * progress;
            var stepped = (int)Math.Ceiling(raw / LevelStep - 1e-9) * LevelStep;
            return Math.Clamp(stepped, MinimumLevel, MaximumLevel);
        }
        return last.Level;
    }

    public static bool ValidTemperature(double value) => double.IsFinite(value) && value >= 1 && value < 125;

    /// <summary>
    /// The level every curve agrees on (the highest), with 2 °C of hysteresis when slowing down
    /// so the fans do not hunt around a point. Null when a required sensor has no reading.
    /// </summary>
    public static int? CurveLevel(IReadOnlyList<FanCurve> curves, IReadOnlyDictionary<FanTemperatureSource, double> temperatures,
        int? previousLevel = null)
    {
        var requested = Evaluate(curves, temperatures, 0);
        if (requested is null) return null;
        if (previousLevel is not { } previous || requested >= previous) return requested;
        var held = Evaluate(curves, temperatures, Hysteresis);
        if (held is null) return null;
        return Math.Min(previous, Math.Max(requested.Value, held.Value));
    }

    private static int? Evaluate(IReadOnlyList<FanCurve> curves, IReadOnlyDictionary<FanTemperatureSource, double> temperatures, double offset)
    {
        if (!ValidCurves(curves)) return null;
        var max = MinimumLevel;
        foreach (var curve in curves)
        {
            if (!temperatures.TryGetValue(curve.Sensor, out var t) || !ValidTemperature(t)) return null;
            max = Math.Max(max, InterpolatedLevel(curve.Points, t + offset));
        }
        return max;
    }

    /// <summary>The point "Add point" inserts: middle of the widest gap, else 10 °C past an end.</summary>
    public static FanCurvePoint? NextPoint(IReadOnlyList<FanCurvePoint> points)
    {
        if (points.Count >= MaximumPointCount || points.Count == 0) return null;
        (int Index, int Gap)? best = null;
        for (var i = 1; i < points.Count; i++)
        {
            var gap = points[i].Temperature - points[i - 1].Temperature;
            if (gap > 1 && gap > (best?.Gap ?? 0)) best = (i, gap);
        }
        if (best is { } b)
        {
            var lower = points[b.Index - 1];
            var upper = points[b.Index];
            var level = (int)Math.Round((lower.Level + upper.Level) / 2.0 / LevelStep, MidpointRounding.AwayFromZero) * LevelStep;
            return new FanCurvePoint(lower.Temperature + b.Gap / 2, level);
        }
        var last = points[^1];
        if (last.Temperature < MaximumCurveTemperature)
            return new FanCurvePoint(Math.Min(MaximumCurveTemperature, last.Temperature + 10), last.Level);
        var first = points[0];
        if (first.Temperature > MinimumCurveTemperature)
            return new FanCurvePoint(Math.Max(MinimumCurveTemperature, first.Temperature - 10), first.Level);
        return null;
    }

    public static string EncodeCurves(IReadOnlyList<FanCurve> curves) => JsonSerializer.Serialize(curves, Json);

    public static IReadOnlyList<FanCurve>? DecodeCurves(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var curves = JsonSerializer.Deserialize<List<FanCurve>>(json, Json);
            return curves is not null && ValidCurves(curves) ? curves : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The software duty (%) to send to a fan controller for a cooling level.</summary>
    public static float DutyFor(int level, float minDuty, float maxDuty)
    {
        var lo = float.IsFinite(minDuty) ? Math.Clamp(minDuty, 0, 100) : 0;
        var hi = float.IsFinite(maxDuty) && maxDuty > lo ? Math.Clamp(maxDuty, 0, 100) : 100;
        return lo + (hi - lo) * Math.Clamp(level, 0, 100) / 100f;
    }
}
