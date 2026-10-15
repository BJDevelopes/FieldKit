// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Fieldkit.Features.Capture;

/// <summary>A span of source time in seconds.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => Math.Max(0, End - Start);
    public bool Contains(double t) => t >= Start && t <= End;
}

/// <summary>A click during the recording, position normalised to the recorded frame (0…1).</summary>
public readonly record struct RecordedClick(double Time, double X, double Y);

/// <summary>A zoom: holds <see cref="Amount"/>× on a focus point (0…1) between Start and End (eased at the edges).</summary>
public sealed record ZoomSegment(double Start, double End, double FocusX, double FocusY, double Amount)
{
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>A privacy blur over a normalised area for a span of source time.</summary>
public sealed record BlurRegion(double Start, double End, double X, double Y, double Width, double Height);

/// <summary>Everything the editor changes. Times are SOURCE times; the master file is never rewritten.</summary>
public sealed class RecordingEdit
{
    public double Duration { get; set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public List<TimeRange> Cuts { get; set; } = [];
    public List<ZoomSegment> Zooms { get; set; } = [];
    public List<BlurRegion> Blurs { get; set; } = [];
    public double Speed { get; set; } = 1;
    public double ZoomAmount { get; set; } = RecordingEditMath.DefaultZoomAmount;
    public bool SystemAudioRemoved { get; set; }
    public double SystemAudioVolume { get; set; } = 1;
    public bool MicrophoneRemoved { get; set; }
    public double MicrophoneVolume { get; set; } = 1;

    public static RecordingEdit ForDuration(double duration) => new() { Duration = duration, TrimEnd = duration };

    public RecordingEdit Clone() => JsonSerializer.Deserialize<RecordingEdit>(JsonSerializer.Serialize(this))!;
}

/// <summary>Pure timeline maths of the recording editor (ports of upstream RecorderTimeline/RecorderMotion).</summary>
public static class RecordingEditMath
{
    public const double MinimumSegment = 0.4;
    public const double MinimumCut = 0.1;
    public const double DefaultZoomAmount = 1.8;
    public const double MinimumZoomAmount = 1.2;
    public const double MaximumZoomAmount = 3.0;

    public const double ZoomLeadIn = 0.3;
    public const double ZoomHold = 2.5;
    public const double ZoomMinimumDuration = 0.9;
    public const double ZoomIgnoreTail = 1.0;
    public const double ZoomEndMargin = 0.8;
    public const double ZoomRampIn = 0.42;
    public const double ZoomRampOut = 0.65;

    public static readonly double[] SpeedPresets = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
    public const double MinimumSpeed = 0.25;
    public const double MaximumSpeed = 4;

    public static double SanitizedSpeed(double speed) =>
        double.IsFinite(speed) ? Math.Round(Math.Clamp(speed, MinimumSpeed, MaximumSpeed), 2) : 1;

    public static double SanitizedZoomAmount(double amount) =>
        double.IsFinite(amount) ? Math.Clamp(amount, MinimumZoomAmount, MaximumZoomAmount) : DefaultZoomAmount;

    /// <summary>Cuts in order, merged where they touch, inside the recording, never invisibly short.</summary>
    public static List<TimeRange> NormalizedCuts(IEnumerable<TimeRange> cuts, double duration)
    {
        if (duration <= 0) return [];
        var clamped = cuts
            .Where(c => double.IsFinite(c.Start) && double.IsFinite(c.End))
            .Select(c => new TimeRange(Math.Clamp(Math.Min(c.Start, c.End), 0, duration), Math.Clamp(Math.Max(c.Start, c.End), 0, duration)))
            .Where(c => c.Duration >= MinimumCut)
            .OrderBy(c => c.Start)
            .ToList();
        var merged = new List<TimeRange>();
        foreach (var cut in clamped)
        {
            if (merged.Count > 0 && cut.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, cut.End) };
            else
                merged.Add(cut);
        }
        return merged;
    }

    /// <summary>The parts of the source that make it into the export: the trim window minus the cuts.</summary>
    public static List<TimeRange> KeptSegments(RecordingEdit edit)
    {
        var start = Math.Clamp(edit.TrimStart, 0, edit.Duration);
        var end = Math.Clamp(edit.TrimEnd <= 0 ? edit.Duration : edit.TrimEnd, start, edit.Duration);
        var segments = new List<TimeRange>();
        var cursor = start;
        foreach (var cut in NormalizedCuts(edit.Cuts, edit.Duration))
        {
            if (cut.End <= cursor) continue;
            if (cut.Start >= end) break;
            if (cut.Start > cursor) segments.Add(new TimeRange(cursor, cut.Start));
            cursor = Math.Max(cursor, cut.End);
        }
        if (end > cursor) segments.Add(new TimeRange(cursor, end));
        return segments.Where(s => s.Duration >= 0.05).ToList();
    }

    /// <summary>Export duration: what remains after trimming and cutting, divided by the speed.</summary>
    public static double ExportDuration(RecordingEdit edit) =>
        KeptSegments(edit).Sum(s => s.Duration) / SanitizedSpeed(edit.Speed);

    /// <summary>Maps a source time to export time, or null when that moment was cut.</summary>
    public static double? SourceToExport(RecordingEdit edit, double source)
    {
        double elapsed = 0;
        foreach (var s in KeptSegments(edit))
        {
            if (source < s.Start) return null;
            if (source <= s.End) return (elapsed + source - s.Start) / SanitizedSpeed(edit.Speed);
            elapsed += s.Duration;
        }
        return null;
    }

    /// <summary>Upstream automatic zooms: one per click burst, starting before the click.</summary>
    public static List<ZoomSegment> AutomaticZooms(IEnumerable<RecordedClick> clicks, double duration, double amount)
    {
        if (duration <= 0) return [];
        var presses = clicks.Where(c => c.Time < duration - ZoomIgnoreTail).OrderBy(c => c.Time).ToList();
        var merged = new List<(double Start, double End, List<RecordedClick> Clicks)>();
        foreach (var press in presses)
        {
            var start = Math.Max(0.001, press.Time - ZoomLeadIn);
            var end = press.Time + ZoomHold;
            if (merged.Count > 0 && start - merged[^1].End <= ZoomHold)
            {
                var last = merged[^1];
                last.Clicks.Add(press);
                merged[^1] = (last.Start, Math.Max(last.End, end), last.Clicks);
            }
            else
            {
                merged.Add((start, end, [press]));
            }
        }
        var limit = Math.Max(0, duration - ZoomEndMargin);
        var result = new List<ZoomSegment>();
        foreach (var (start, end, group) in merged)
        {
            var clampedEnd = Math.Min(end, limit);
            if (clampedEnd - start < ZoomMinimumDuration) continue;
            // The focus is the middle of the burst so every click in it stays in view.
            var fx = (group.Min(c => c.X) + group.Max(c => c.X)) / 2;
            var fy = (group.Min(c => c.Y) + group.Max(c => c.Y)) / 2;
            result.Add(new ZoomSegment(start, clampedEnd, Math.Clamp(fx, 0, 1), Math.Clamp(fy, 0, 1), SanitizedZoomAmount(amount)));
        }
        return result;
    }

    /// <summary>Zooms in order, never overlapping (a later start moves), never shorter than a beat.</summary>
    public static List<ZoomSegment> NormalizedZooms(IEnumerable<ZoomSegment> zooms, double duration)
    {
        if (duration <= 0) return [];
        var sorted = zooms
            .Where(z => double.IsFinite(z.Start) && double.IsFinite(z.End))
            .Select(z => z with
            {
                Start = Math.Clamp(z.Start, 0, duration),
                End = Math.Clamp(z.End, 0, duration),
                FocusX = Math.Clamp(z.FocusX, 0, 1),
                FocusY = Math.Clamp(z.FocusY, 0, 1),
                Amount = SanitizedZoomAmount(z.Amount),
            })
            .OrderBy(z => z.Start)
            .ToList();
        var result = new List<ZoomSegment>();
        foreach (var z in sorted)
        {
            var adjusted = result.Count > 0 && z.Start < result[^1].End ? z with { Start = result[^1].End } : z;
            if (adjusted.Duration >= MinimumSegment) result.Add(adjusted);
        }
        return result;
    }

    public static double SmootherStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>0 outside a zoom, 1 while it holds, eased in and out at the edges.</summary>
    public static double ZoomProgress(double time, IReadOnlyList<ZoomSegment> segments)
    {
        foreach (var s in segments)
        {
            if (time < s.Start - 0.001 || time > s.End + ZoomRampOut) continue;
            if (time <= s.Start + ZoomRampIn) return SmootherStep((time - s.Start) / ZoomRampIn);
            if (time >= s.End) return SmootherStep(1 - (time - s.End) / ZoomRampOut);
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// The visible part of the frame at a zoom factor around a focus point, kept inside the frame
    /// (normalised x, y, width, height).
    /// </summary>
    public static (double X, double Y, double Width, double Height) Viewport(double zoom, double focusX, double focusY)
    {
        zoom = Math.Max(1, zoom);
        var w = 1 / zoom;
        var x = Math.Clamp(focusX - w / 2, 0, 1 - w);
        var y = Math.Clamp(focusY - w / 2, 0, 1 - w);
        return (x, y, w, w);
    }

    /// <summary>GIF frames for a duration (0 when nothing to encode).</summary>
    public static int GifFrameCount(double duration, int fps) =>
        duration > 0 && fps > 0 && double.IsFinite(duration) ? (int)Math.Floor(duration * fps) : 0;

    /// <summary>Recorder quality presets (upstream RecorderSupport.Quality).</summary>
    public static (double OutputScale, double BitsPerPixel, double KeyframeSeconds) Quality(string raw) => raw switch
    {
        "small" => (0.5, 0.05, 10),
        "high" => (1, 0.09, 2),
        _ => (1, 0.06, 10),
    };

    /// <summary>Encoder bitrate for a frame size, rate and quality, in bits per second.</summary>
    public static uint VideoBitrate(int width, int height, int fps, string quality)
    {
        var q = Quality(quality);
        var bits = width * q.OutputScale * height * q.OutputScale * Math.Max(1, fps) * q.BitsPerPixel;
        return (uint)Math.Clamp(bits, 500_000, 80_000_000);
    }

    /// <summary>Upstream GIF sizes: long edge for small / medium / large.</summary>
    public static int GifLongEdge(string size) => size switch
    {
        "small" => 420,
        "large" => 800,
        _ => 600,
    };

    public static readonly int[] GifFrameRates = [8, 12, 15];
    public static readonly int[] FrameRates = [30, 60];
    public static readonly int[] Countdowns = [0, 3, 5, 10];
}
