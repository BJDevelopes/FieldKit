// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.Capture;
using Fieldkit.Features.Media;

namespace Fieldkit.Tests.Media;

public class FfmpegArgsTests
{
    private static string Graph(List<string> args) => args[args.IndexOf("-filter_complex") + 1];

    [Fact]
    public void PlainRecordingKeepsEverything()
    {
        var edit = RecordingEdit.ForDuration(10);
        var args = FfmpegArgs.RecordingExport("in.mp4", "out.mp4", edit, 1921, 1080, 60, [new AudioTrackEdit(0, 1, false)], gif: false)!;
        var g = Graph(args);
        Assert.Contains("[0:v]fps=60,scale=1920:1080", g);
        Assert.Contains("trim=start=0:end=10", g);
        Assert.Contains("[0:a:0]volume=1", g);
        Assert.DoesNotContain("zoompan", g);
        Assert.Equal("out.mp4", args[^1]);
        Assert.Contains("libx264", args);
        Assert.Contains("[ac]", args);
    }

    [Fact]
    public void CutsBecomeConcat()
    {
        var edit = RecordingEdit.ForDuration(10);
        edit.Cuts = [new TimeRange(2, 3)];
        var args = FfmpegArgs.RecordingExport("in.mp4", "out.mp4", edit, 640, 480, 30, [], gif: false)!;
        var g = Graph(args);
        Assert.Contains("split=2[vs0][vs1]", g);
        Assert.Contains("[vs1]trim=start=3:end=10", g);
        Assert.Contains("concat=n=2:v=1:a=0[vc]", g);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void TwoTracksMixAndRemovedTracksDrop()
    {
        var edit = RecordingEdit.ForDuration(5);
        var both = Graph(FfmpegArgs.RecordingExport("i", "o.mp4", edit, 100, 100, 30,
            [new AudioTrackEdit(0, 0.5, false), new AudioTrackEdit(1, 1.5, false)], false)!);
        Assert.Contains("amix=inputs=2", both);
        Assert.Contains("[0:a:1]volume=1.5", both);
        var one = Graph(FfmpegArgs.RecordingExport("i", "o.mp4", edit, 100, 100, 30,
            [new AudioTrackEdit(0, 1, true), new AudioTrackEdit(1, 1, false)], false)!);
        Assert.DoesNotContain("amix", one);
        Assert.DoesNotContain("[0:a:0]", one);
    }

    [Fact]
    public void BlursZoomsSpeedAndGif()
    {
        var edit = RecordingEdit.ForDuration(10);
        edit.Blurs = [new BlurRegion(1, 2, 0.1, 0.1, 0.25, 0.25)];
        edit.Zooms = [new ZoomSegment(3, 5, 0.25, 0.75, 2)];
        edit.Speed = 3;
        var gifArgs = FfmpegArgs.RecordingExport("i", "o.gif", edit, 400, 200, 30, [new AudioTrackEdit(0, 1, false)], gif: true, 600, 12)!;
        var g = Graph(gifArgs);
        Assert.Contains("crop=100:50:40:20", g);
        Assert.Contains("enable='between(t,1,2)'", g);
        Assert.Contains("zoompan=z='if(between(it,3,5+0.65)", g);
        Assert.Contains("setpts=PTS/3", g);
        Assert.Contains("palettegen", g);
        Assert.Contains("-loop", gifArgs);
        Assert.DoesNotContain("libx264", gifArgs);
        Assert.DoesNotContain("[0:a:", g); // no dangling audio branch in a GIF graph
    }

    [Fact]
    public void NothingLeftReturnsNull()
    {
        var edit = RecordingEdit.ForDuration(5);
        edit.Cuts = [new TimeRange(0, 5)];
        Assert.Null(FfmpegArgs.RecordingExport("i", "o", edit, 100, 100, 30, [], false));
    }

    [Theory]
    [InlineData(1, "atempo=1")]
    [InlineData(4, "atempo=2,atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(3, "atempo=2,atempo=1.5")]
    public void AtempoChains(double speed, string expected) => Assert.Equal(expected, FfmpegArgs.AtempoChain(speed));

    [Fact]
    public void CrfFollowsCompression()
    {
        Assert.Equal(20, FfmpegArgs.Crf(0.88, false));
        Assert.Equal(26, FfmpegArgs.Crf(0.68, false));
        Assert.Equal(32, FfmpegArgs.Crf(0.28, false));
        Assert.Equal(30, FfmpegArgs.Crf(0.68, true));
    }

    [Fact]
    public void CompressVideoArguments()
    {
        var args = FfmpegArgs.CompressVideo("a.mov", "b.mp4", new VideoCompressOptions { Start = 1.5, End = 4, KeepAudio = false, Hevc = true });
        Assert.Equal(["-hide_banner", "-y", "-ss", "1.5", "-to", "4", "-i", "a.mov"], args.Take(8));
        Assert.Contains("libx265", args);
        Assert.Contains("-an", args);
        Assert.Contains("-map_metadata", args);
        var planned = FfmpegArgs.CompressVideo("a", "b.mp4", new VideoCompressOptions(), new VideoSizePlan(640, 360, 800_000, 64_000));
        Assert.Contains("800000", planned);
        Assert.Contains("64000", planned);
        Assert.Contains(planned, a => a.Contains("scale=640:360"));
        var progress = FfmpegArgs.WithProgress(planned);
        Assert.Equal("b.mp4", progress[^1]);
        Assert.Contains("pipe:1", progress);
    }

    [Fact]
    public void GifArguments()
    {
        var args = FfmpegArgs.MakeGif("a.mp4", "a.gif", new GifOptions { Width = 480, Fps = 10, Loop = false });
        var g = Graph(args);
        Assert.Contains("fps=10", g);
        Assert.Contains("min(480,iw)", g);
        Assert.Equal("-1", args[args.IndexOf("-loop") + 1]);
    }

    [Fact]
    public void SizePlanScalesDownForSmallTargets()
    {
        var plan = FfmpegArgs.VideoSizePlanFor(5_000_000, 60, 1920, 1080, 30, true)!.Value;
        Assert.True(plan.Width < 1920);
        Assert.True(plan.Width % 2 == 0 && plan.Height % 2 == 0);
        Assert.Equal(64_000, plan.AudioBitRate);
        Assert.Null(FfmpegArgs.VideoSizePlanFor(100_000, 600, 1920, 1080, 30, true));
        Assert.Equal(1920, FfmpegArgs.VideoSizePlanFor(500_000_000, 10, 1920, 1080, 30, false)!.Value.Width);
    }

    [Fact]
    public void RetryAndGifPlans()
    {
        Assert.Null(FfmpegArgs.TargetRetryScale(1, 100, 200));
        Assert.True(FfmpegArgs.TargetRetryScale(1, 300, 200) <= 0.9);
        var next = FfmpegArgs.GifSizePlan(800, 15, 20_000_000, 10_000_000)!.Value;
        Assert.True(next.Fps < 15);
    }

    [Fact]
    public void ParsesProbeBanner()
    {
        const string banner = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':
              Duration: 00:01:02.50, start: 0.000000, bitrate: 4000 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 3800 kb/s, 59.94 fps, 60 tbr
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s
              Stream #0:2[0x3](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, mono, fltp, 64 kb/s
            """;
        var p = FfmpegArgs.ParseProbe(banner);
        Assert.Equal(62.5, p.Duration, 6);
        Assert.Equal(1920, p.Width);
        Assert.Equal(1080, p.Height);
        Assert.Equal(59.94, p.FrameRate, 6);
        Assert.True(p.HasVideo);
        Assert.Equal(2, p.AudioStreams);
        Assert.Equal(1.5, FfmpegArgs.ParseProgressSeconds("out_time_us=1500000"));
        Assert.Null(FfmpegArgs.ParseProgressSeconds("frame=12"));
    }
}

public class MediaImagePlanTests
{
    [Fact]
    public void ProportionalModesNeverEnlarge()
    {
        var o = new ImageOptions { Resize = ImageResizeKind.MaxDimension, MaxDimension = 1600 };
        Assert.Equal((800, 600), (MediaImagePlan.Plan(800, 600, o).Width, MediaImagePlan.Plan(800, 600, o).Height));
        var big = MediaImagePlan.Plan(4000, 3000, o);
        Assert.Equal(1600, big.Width);
        Assert.Equal(1200, big.Height);
        var byHeight = MediaImagePlan.Plan(4000, 2000, o with { Resize = ImageResizeKind.Height, Height = 500 });
        Assert.Equal(1000, byHeight.Width);
    }

    [Fact]
    public void ExactModes()
    {
        var o = new ImageOptions { Resize = ImageResizeKind.Exact, Width = 100, Height = 100 };
        var fit = MediaImagePlan.Plan(200, 100, o with { ExactMode = ImageExactMode.Fit });
        Assert.Equal(new Rect(0, 25, 100, 50), fit.Destination);
        var fill = MediaImagePlan.Plan(200, 100, o with { ExactMode = ImageExactMode.Fill });
        Assert.Equal(new Rect(50, 0, 100, 100), fill.SourceCrop);
        var stretch = MediaImagePlan.Plan(200, 100, o);
        Assert.Equal(new Rect(0, 0, 100, 100), stretch.Destination);
    }

    [Fact]
    public void RenamePatterns()
    {
        var date = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        Assert.Equal("photo", MediaImagePlan.OutputBaseName(@"C:\x\photo.png", "", 1, date, 10, 10, ImageOutputFormat.Jpeg));
        Assert.Equal("photo-003-20240506-10x20.jpg",
            MediaImagePlan.OutputBaseName(@"C:\x\photo.png", "{name}-{index:03}-{date}-{width}x{height}.{ext}", 3, date, 10, 20, ImageOutputFormat.Jpeg));
        Assert.Equal("a-b", MediaImagePlan.OutputBaseName("a.png", "a/b", 1, date, 1, 1, ImageOutputFormat.Png));
        Assert.Equal("Output", MediaImagePlan.VisibleBaseName(".png"));
    }

    [Fact]
    public void WatermarkPlacement()
    {
        var image = new Size(1000, 500);
        Assert.Equal(new Point(32, 32), MediaImagePlan.WatermarkOrigin(image, new Size(100, 50), ImageWatermarkPosition.TopLeft, 32));
        Assert.Equal(new Point(868, 418), MediaImagePlan.WatermarkOrigin(image, new Size(100, 50), ImageWatermarkPosition.BottomRight, 32));
        Assert.Equal(new Size(180, 90), MediaImagePlan.LogoSize(image, new Size(200, 100), 0.18));
    }

    [Fact]
    public void Safety()
    {
        Assert.True(MediaImagePlan.IsSafeSize(8000, 8000));
        Assert.False(MediaImagePlan.IsSafeSize(20_000, 20_000));
        Assert.False(MediaImagePlan.IsSafeSize(0, 10));
    }
}
