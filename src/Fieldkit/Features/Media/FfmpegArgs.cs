// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fieldkit.Features.Capture;

namespace Fieldkit.Features.Media;

/// <summary>One audio track of a recording master and what the editor does to it.</summary>
public readonly record struct AudioTrackEdit(int StreamIndex, double Volume, bool Removed);

/// <summary>What ffmpeg reported about an input (parsed from its banner).</summary>
public sealed record MediaProbe(double Duration, int Width, int Height, double FrameRate, bool HasVideo, int AudioStreams);

/// <summary>Options of the Media → Video tool.</summary>
public sealed record VideoCompressOptions
{
    public double Start { get; init; }
    public double End { get; init; }
    /// <summary>Upstream quality 0…1 (Low compression 0.88, Medium 0.68, High 0.28).</summary>
    public double Quality { get; init; } = 0.68;
    public int MaxDimension { get; init; } = 1280;
    public double Fps { get; init; } = 30;
    public bool KeepAudio { get; init; } = true;
    public bool Hevc { get; init; }
    public bool StripMetadata { get; init; } = true;
    /// <summary>When set, aim at this many bytes instead of a resolution.</summary>
    public long? TargetBytes { get; init; }
}

/// <summary>Options of the Media → GIF tool.</summary>
public sealed record GifOptions
{
    public double Start { get; init; }
    public double End { get; init; }
    public int Width { get; init; } = 720;
    public double Fps { get; init; } = 12;
    public double Quality { get; init; } = 0.74;
    public bool Loop { get; init; } = true;
}

/// <summary>A video size plan for a target file size (upstream MediaSupport.videoSizePlan).</summary>
public readonly record struct VideoSizePlan(int Width, int Height, int VideoBitRate, int AudioBitRate);

/// <summary>
/// Builds ffmpeg argument lists (passed through ProcessStartInfo.ArgumentList, so nothing needs
/// shell quoting). Pure so every filter graph is unit tested.
/// </summary>
public static partial class FfmpegArgs
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string F(double v) => Math.Round(v, 4).ToString("0.####", Inv);

    // ------------------------------------------------------------- recorder

    /// <summary>
    /// Export of an edited recording: blurs and zooms on the source timeline, then trim/cut,
    /// speed, audio mix, then MP4 or GIF. Returns null when nothing is left after the cuts.
    /// </summary>
    public static List<string>? RecordingExport(string input, string output, RecordingEdit edit, int sourceWidth, int sourceHeight,
        int fps, IReadOnlyList<AudioTrackEdit> audio, bool gif, int gifLongEdge = 600, int gifFps = 12)
    {
        var segments = RecordingEditMath.KeptSegments(edit);
        if (segments.Count == 0 || sourceWidth < 2 || sourceHeight < 2) return null;
        var w = sourceWidth & ~1;
        var h = sourceHeight & ~1;
        fps = Math.Clamp(fps, 1, 120);
        var speed = RecordingEditMath.SanitizedSpeed(edit.Speed);
        var graph = new StringBuilder();
        void Add(string part)
        {
            if (graph.Length > 0) graph.Append(';');
            graph.Append(part);
        }

        // Constant frame rate first: zoompan counts frames, so timing must be regular.
        Add($"[0:v]fps={fps},scale={w}:{h},setsar=1[v0]");
        var current = "v0";

        // Privacy blurs (source time).
        var blurs = edit.Blurs.Where(b => b.End > b.Start && b.Width > 0 && b.Height > 0).ToList();
        for (var i = 0; i < blurs.Count; i++)
        {
            var b = blurs[i];
            var bx = Even(b.X * w);
            var by = Even(b.Y * h);
            var bw = Math.Max(2, Math.Min(Even(b.Width * w), w - bx));
            var bh = Math.Max(2, Math.Min(Even(b.Height * h), h - by));
            var sigma = F(Math.Max(8, Math.Min(w, h) * 0.02));
            Add($"[{current}]split=2[b{i}a][b{i}b]");
            Add($"[b{i}b]crop={bw}:{bh}:{bx}:{by},gblur=sigma={sigma}[b{i}c]");
            Add($"[b{i}a][b{i}c]overlay={bx}:{by}:enable='between(t,{F(b.Start)},{F(b.End)})'[b{i}]");
            current = $"b{i}";
        }

        // Zooms (source time), eased like the editor preview.
        var zooms = RecordingEditMath.NormalizedZooms(edit.Zooms, edit.Duration);
        if (zooms.Count > 0)
        {
            var z = ZoomExpression(zooms, "it");
            var fx = FocusExpression(zooms, z => z.FocusX, "it");
            var fy = FocusExpression(zooms, z => z.FocusY, "it");
            Add($"[{current}]zoompan=z='{z}':x='clip(({fx})*iw-iw/zoom/2,0,iw-iw/zoom)':y='clip(({fy})*ih-ih/zoom/2,0,ih-ih/zoom)':d=1:s={w}x{h}:fps={fps}[z0]");
            current = "z0";
        }

        // Audio: each kept track at its volume, mixed to one track (GIFs have no sound, and an
        // unconnected audio branch would make ffmpeg refuse the graph).
        var tracks = gif ? [] : audio.Where(a => !a.Removed).ToList();
        string? audioLabel = null;
        if (tracks.Count > 0)
        {
            for (var i = 0; i < tracks.Count; i++)
                Add($"[0:a:{tracks[i].StreamIndex}]volume={F(Math.Clamp(tracks[i].Volume, 0, 4))},aresample=async=1:first_pts=0[t{i}]");
            if (tracks.Count == 1)
            {
                audioLabel = "t0";
            }
            else
            {
                Add($"{string.Concat(Enumerable.Range(0, tracks.Count).Select(i => $"[t{i}]"))}amix=inputs={tracks.Count}:duration=longest:normalize=0[am]");
                audioLabel = "am";
            }
        }

        // Trim and cuts.
        var n = segments.Count;
        if (n == 1)
        {
            var s = segments[0];
            Add($"[{current}]trim=start={F(s.Start)}:end={F(s.End)},setpts=PTS-STARTPTS[vc]");
            if (audioLabel is not null) Add($"[{audioLabel}]atrim=start={F(s.Start)}:end={F(s.End)},asetpts=PTS-STARTPTS[ac]");
        }
        else
        {
            Add($"[{current}]split={n}{string.Concat(Enumerable.Range(0, n).Select(i => $"[vs{i}]"))}");
            if (audioLabel is not null) Add($"[{audioLabel}]asplit={n}{string.Concat(Enumerable.Range(0, n).Select(i => $"[as{i}]"))}");
            var concatInputs = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                var s = segments[i];
                Add($"[vs{i}]trim=start={F(s.Start)}:end={F(s.End)},setpts=PTS-STARTPTS[vt{i}]");
                concatInputs.Append($"[vt{i}]");
                if (audioLabel is not null)
                {
                    Add($"[as{i}]atrim=start={F(s.Start)}:end={F(s.End)},asetpts=PTS-STARTPTS[at{i}]");
                    concatInputs.Append($"[at{i}]");
                }
            }
            Add($"{concatInputs}concat=n={n}:v=1:a={(audioLabel is null ? 0 : 1)}[vc]{(audioLabel is null ? "" : "[ac]")}");
        }

        // Speed.
        var videoOut = "vc";
        var audioOut = audioLabel is null ? null : "ac";
        if (Math.Abs(speed - 1) > 0.001)
        {
            Add($"[vc]setpts=PTS/{F(speed)}[vsp]");
            videoOut = "vsp";
            if (audioOut is not null && !gif)
            {
                Add($"[ac]{AtempoChain(speed)}[asp]");
                audioOut = "asp";
            }
        }

        var args = new List<string> { "-hide_banner", "-y", "-i", input };
        if (gif)
        {
            var scale = w >= h ? $"'min({gifLongEdge},iw)':-2" : $"-2:'min({gifLongEdge},ih)'";
            Add($"[{videoOut}]fps={Math.Clamp(gifFps, 1, 50)},scale={scale}:flags=lanczos,split[g1][g2]");
            Add("[g1]palettegen=stats_mode=diff[pal]");
            Add("[g2][pal]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[gif]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[gif]", "-loop", "0", output]);
            return args;
        }

        args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[{videoOut}]"]);
        if (audioOut is not null) args.AddRange(["-map", $"[{audioOut}]"]);
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]);
        if (audioOut is not null) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return args;
    }

    /// <summary>atempo only accepts 0.5–2 per instance, so larger changes chain several.</summary>
    public static string AtempoChain(double speed)
    {
        var parts = new List<string>();
        var s = speed;
        while (s > 2.0 + 1e-9) { parts.Add("atempo=2"); s /= 2; }
        while (s < 0.5 - 1e-9) { parts.Add("atempo=0.5"); s /= 0.5; }
        parts.Add($"atempo={F(s)}");
        return string.Join(",", parts);
    }

    /// <summary>ffmpeg expression for the eased zoom factor at time <paramref name="t"/>.</summary>
    public static string ZoomExpression(IReadOnlyList<ZoomSegment> zooms, string t)
    {
        // smootherstep(u) = u^3 (u (6u - 15) + 10), with u stored in register 0.
        static string Smoother(string u) => $"st(0,clip({u},0,1))*ld(0)*ld(0)*(ld(0)*(ld(0)*6-15)+10)";
        var expr = "1";
        for (var i = zooms.Count - 1; i >= 0; i--)
        {
            var z = zooms[i];
            var a = F(z.Amount - 1);
            var s = F(z.Start);
            var e = F(z.End);
            var rin = F(RecordingEditMath.ZoomRampIn);
            var rout = F(RecordingEditMath.ZoomRampOut);
            var ramp = $"if(lt({t},{s}+{rin}),{Smoother($"({t}-{s})/{rin}")},if(lt({t},{e}),1,{Smoother($"1-({t}-{e})/{rout}")}))";
            expr = $"if(between({t},{s},{e}+{rout}),1+{a}*({ramp}),{expr})";
        }
        return expr;
    }

    public static string FocusExpression(IReadOnlyList<ZoomSegment> zooms, Func<ZoomSegment, double> pick, string t)
    {
        var expr = "0.5";
        for (var i = zooms.Count - 1; i >= 0; i--)
        {
            var z = zooms[i];
            expr = $"if(between({t},{F(z.Start - 0.001)},{F(z.End + RecordingEditMath.ZoomRampOut)}),{F(pick(z))},{expr})";
        }
        return expr;
    }

    // ---------------------------------------------------------------- media

    /// <summary>CRF for an upstream quality value (0.28 → 32, 0.68 → 26, 0.88 → 20).</summary>
    public static int Crf(double quality, bool hevc)
    {
        var q = Math.Clamp(quality, 0, 1);
        double crf = q <= 0.68
            ? 32 - (q - 0.28) / 0.40 * 6
            : 26 - (q - 0.68) / 0.20 * 6;
        crf = Math.Clamp(crf, 14, 40);
        return (int)Math.Round(crf) + (hevc ? 4 : 0);
    }

    /// <summary>Keeps the aspect ratio, shrinking only, with even sides.</summary>
    public static string ScaleToMax(int maxDimension) =>
        $"scale='if(gte(iw,ih),min({maxDimension},iw),-2)':'if(gte(iw,ih),-2,min({maxDimension},ih))'";

    private static void AddTrim(List<string> args, double start, double end)
    {
        if (start > 0.001) args.AddRange(["-ss", F(start)]);
        if (end > start + 0.001) args.AddRange(["-to", F(end)]);
    }

    public static List<string> CompressVideo(string input, string output, VideoCompressOptions o, VideoSizePlan? plan = null)
    {
        var args = new List<string> { "-hide_banner", "-y" };
        AddTrim(args, o.Start, o.End);
        args.AddRange(["-i", input]);
        var fps = Math.Clamp(o.Fps, 1, 60);
        var scale = plan is { } p ? $"scale={p.Width}:{p.Height}" : ScaleToMax(Math.Max(64, o.MaxDimension));
        args.AddRange(["-vf", $"fps={F(fps)},{scale},setsar=1", "-map", "0:v:0"]);
        if (o.KeepAudio) args.AddRange(["-map", "0:a:0?"]);
        if (o.Hevc) args.AddRange(["-c:v", "libx265", "-tag:v", "hvc1"]);
        else args.AddRange(["-c:v", "libx264"]);
        args.AddRange(["-preset", "medium", "-pix_fmt", "yuv420p"]);
        if (plan is { } bp)
        {
            args.AddRange(["-b:v", bp.VideoBitRate.ToString(Inv), "-maxrate", ((int)(bp.VideoBitRate * 1.5)).ToString(Inv),
                "-bufsize", (bp.VideoBitRate * 2).ToString(Inv)]);
        }
        else
        {
            args.AddRange(["-crf", Crf(o.Quality, o.Hevc).ToString(Inv)]);
        }
        if (o.KeepAudio) args.AddRange(["-c:a", "aac", "-b:a", plan is { AudioBitRate: > 0 } ap ? ap.AudioBitRate.ToString(Inv) : "128k"]);
        else args.Add("-an");
        if (o.StripMetadata) args.AddRange(["-map_metadata", "-1"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return args;
    }

    public static List<string> MakeGif(string input, string output, GifOptions o)
    {
        var args = new List<string> { "-hide_banner", "-y" };
        AddTrim(args, o.Start, o.End);
        args.AddRange(["-i", input]);
        var colors = (int)Math.Round(32 + Math.Clamp(o.Quality, 0, 1) * 224);
        var dither = o.Quality >= 0.5 ? "sierra2_4a" : "bayer:bayer_scale=4";
        var width = Math.Clamp(o.Width, 16, 3840);
        args.AddRange(["-filter_complex",
            $"[0:v]fps={F(Math.Clamp(o.Fps, 1, 50))},scale='min({width},iw)':-2:flags=lanczos,split[a][b];[a]palettegen=max_colors={colors}:stats_mode=diff[p];[b][p]paletteuse=dither={dither}",
            "-loop", o.Loop ? "0" : "-1", output]);
        return args;
    }

    /// <summary>Asks ffmpeg to report progress as key=value lines on stdout.</summary>
    public static List<string> WithProgress(List<string> args)
    {
        var result = new List<string>(args.Count + 3);
        result.AddRange(args.Take(args.Count - 1));
        result.AddRange(["-progress", "pipe:1", "-nostats"]);
        result.Add(args[^1]);
        return result;
    }

    // ---------------------------------------------------------------- plans

    public const double TargetSizeHeadroom = 0.94;
    private const double TargetBitsPerPixel = 0.07;
    public const int MinimumTargetVideoBitRate = 240_000;

    public static int TargetAudioBitRate(long targetBytes, double duration) =>
        duration <= 0 || targetBytes <= 0 ? 128_000 : targetBytes * 8.0 / duration < 1_200_000 ? 64_000 : 128_000;

    /// <summary>
    /// A target size is a bitrate budget, which then decides how many pixels are worth keeping
    /// (upstream videoSizePlan). Null when the budget cannot carry a watchable video.
    /// </summary>
    public static VideoSizePlan? VideoSizePlanFor(long targetBytes, double duration, int sourceWidth, int sourceHeight,
        double frameRate, bool hasAudio, double scale = 1)
    {
        if (targetBytes <= 0 || duration <= 0 || !double.IsFinite(duration) || sourceWidth <= 0 || sourceHeight <= 0 || scale <= 0)
            return null;
        var fps = Math.Clamp(double.IsFinite(frameRate) && frameRate > 0 ? frameRate : 30, 1, 60);
        var audioRate = hasAudio ? TargetAudioBitRate(targetBytes, duration) : 0;
        var budget = (int)Math.Floor(targetBytes * 8 * TargetSizeHeadroom / duration) - audioRate;
        var video = (int)(budget * Math.Min(1, scale));
        if (video < MinimumTargetVideoBitRate) return null;
        var affordablePixels = video / (fps * TargetBitsPerPixel);
        var sourcePixels = (double)sourceWidth * sourceHeight;
        var longest = (double)Math.Max(sourceWidth, sourceHeight);
        var ratio = Math.Min(1, Math.Sqrt(affordablePixels / sourcePixels));
        var floorRatio = Math.Min(1, 480 / longest);
        var maxDimension = (int)Math.Round(longest * Math.Max(ratio, floorRatio));
        var (ew, eh) = ScaledEvenSize(sourceWidth, sourceHeight, maxDimension);
        return new VideoSizePlan(ew, eh, video, audioRate);
    }

    /// <summary>Next pass scale after a pass overshot the target; gives up at least a tenth.</summary>
    public static double? TargetRetryScale(double current, long actualBytes, long targetBytes)
    {
        if (current <= 0 || actualBytes <= 0 || targetBytes <= 0 || actualBytes <= targetBytes) return null;
        var next = current * targetBytes / actualBytes * TargetSizeHeadroom;
        return next > 0 && double.IsFinite(next) ? Math.Min(current * 0.9, next) : null;
    }

    public static (int Width, int Height) ScaledEvenSize(int width, int height, int maxDimension)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        var maxSide = Math.Max(2, maxDimension);
        var s = Math.Min(1, (double)maxSide / Math.Max(w, h));
        return (Math.Max(2, (int)Math.Round(w * s)) & ~1, Math.Max(2, (int)Math.Round(h * s)) & ~1);
    }

    /// <summary>Next GIF pass after overshooting: drop frames first, then pixels (upstream gifSizePlan).</summary>
    public static (int Width, double Fps)? GifSizePlan(int width, double fps, long actualBytes, long targetBytes)
    {
        if (actualBytes <= 0 || targetBytes <= 0 || actualBytes <= targetBytes) return null;
        var currentWidth = Math.Max(1, width);
        var currentFps = Math.Clamp(double.IsFinite(fps) && fps > 0 ? fps : 12, 1, 30);
        var ratio = Math.Min(0.9, targetBytes * TargetSizeHeadroom / actualBytes);
        var nextFps = Math.Max(6, Math.Round(currentFps * ratio));
        var remaining = Math.Min(1, ratio / (nextFps / currentFps));
        var nextWidth = Math.Max(160, (int)Math.Round(currentWidth * Math.Sqrt(remaining)));
        return nextWidth < currentWidth || nextFps < currentFps ? (nextWidth, nextFps) : null;
    }

    // ---------------------------------------------------------------- probe

    /// <summary>Parses the stream banner ffmpeg prints for "-i input".</summary>
    public static MediaProbe ParseProbe(string stderr)
    {
        double duration = 0;
        var d = DurationRegex().Match(stderr);
        if (d.Success)
        {
            duration = int.Parse(d.Groups[1].Value, Inv) * 3600 + int.Parse(d.Groups[2].Value, Inv) * 60
                       + double.Parse(d.Groups[3].Value, Inv);
        }
        int width = 0, height = 0;
        double frameRate = 0;
        var hasVideo = false;
        var audio = 0;
        foreach (var line in stderr.Split('\n'))
        {
            if (!line.Contains("Stream #", StringComparison.Ordinal)) continue;
            if (line.Contains(": Video:", StringComparison.Ordinal) && !hasVideo && !line.Contains("attached pic", StringComparison.Ordinal))
            {
                hasVideo = true;
                var size = SizeRegex().Match(line);
                if (size.Success)
                {
                    width = int.Parse(size.Groups[1].Value, Inv);
                    height = int.Parse(size.Groups[2].Value, Inv);
                }
                var fps = FpsRegex().Match(line);
                if (fps.Success) frameRate = double.Parse(fps.Groups[1].Value, Inv);
            }
            else if (line.Contains(": Audio:", StringComparison.Ordinal))
            {
                audio++;
            }
        }
        return new MediaProbe(duration, width, height, frameRate, hasVideo, audio);
    }

    /// <summary>Reads "out_time_us=" (or out_time_ms, also microseconds) from a -progress line.</summary>
    public static double? ParseProgressSeconds(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        if (key is not ("out_time_us" or "out_time_ms")) return null;
        return long.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer, Inv, out var us) && us >= 0 ? us / 1_000_000.0 : null;
    }

    private static int Even(double v) => Math.Max(0, (int)Math.Round(v)) & ~1;

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@",\s*(\d{2,5})x(\d{2,5})[\s,\[]")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([\d.]+)\s*fps")]
    private static partial Regex FpsRegex();
}
