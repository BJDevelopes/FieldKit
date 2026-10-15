// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media.Imaging;
using Fieldkit.Features.Capture;
using Fieldkit.Features.Media;

namespace Fieldkit.Tests.Media;

/// <summary>
/// Runs the real pipelines on generated files in a temp folder: WIC image conversion, and (when a
/// local ffmpeg is already installed) the recording export and Media video/GIF arguments.
/// </summary>
public class MediaIntegrationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldkit-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePng(string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % w);
            pixels[i + 1] = (byte)(i / 4 / w);
            pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var s = File.Create(path);
        enc.Save(s);
    }

    private static (int W, int H) Size(string path)
    {
        using var s = File.OpenRead(path);
        var d = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return (d.Frames[0].PixelWidth, d.Frames[0].PixelHeight);
    }

    [Fact]
    public async Task ConvertsResizesAndRenamesImages()
    {
        var dir = TempDir();
        try
        {
            var a = Path.Combine(dir, "first.png");
            var b = Path.Combine(dir, "second.png");
            WritePng(a, 800, 600);
            WritePng(b, 300, 900);
            var options = new ImageOptions
            {
                Format = ImageOutputFormat.Jpeg,
                Resize = ImageResizeKind.MaxDimension,
                MaxDimension = 400,
                Watermark = ImageWatermarkKind.Text,
                WatermarkText = "Fieldkit",
                RenamePattern = "{name}-{index:02}",
                SaveInSubfolder = true,
            };
            var results = await ImageProcessor.RunBatchAsync([a, b], options, null, CancellationToken.None);
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(Path.Combine(dir, "Converted", "first-01.jpg"), results[0].Output);
            Assert.Equal((400, 300), Size(results[0].Output!));
            Assert.Equal((133, 400), Size(results[1].Output!));

            // Exact fill to PNG keeps the requested size.
            var fill = await ImageProcessor.RunBatchAsync([a], new ImageOptions
            {
                Format = ImageOutputFormat.Png,
                Resize = ImageResizeKind.Exact,
                Width = 200,
                Height = 200,
                ExactMode = ImageExactMode.Fill,
            }, null, CancellationToken.None);
            Assert.Equal((200, 200), Size(fill[0].Output!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string> SyntheticRecording(string dir, double seconds)
    {
        var path = Path.Combine(dir, "take.mp4");
        using var encoder = new RecordingEncoder(path, 320, 240, 320, 240, 30, 800_000, audioTracks: 2);
        await encoder.StartAsync();
        var frames = (int)(seconds * 30);
        await Task.Run(() =>
        {
            for (var f = 0; f < frames; f++)
            {
                var data = new byte[320 * 240 * 4];
                for (var i = 0; i < data.Length; i += 4)
                {
                    data[i] = (byte)(f * 4);
                    data[i + 1] = (byte)(i / 1280);
                    data[i + 2] = 90;
                    data[i + 3] = 255;
                }
                while (!encoder.TryAddVideo(data, TimeSpan.FromSeconds(f / 30.0))) Thread.Sleep(5);
                const int chunk = RecordingEncoder.AudioRate / 30;
                for (var t = 0; t < 2; t++) encoder.AddAudio(t, new byte[chunk * 4], TimeSpan.FromSeconds(f * chunk / (double)RecordingEncoder.AudioRate));
            }
            encoder.Complete();
        });
        await encoder.Completion;
        return path;
    }

    [Fact]
    public async Task ExportsEditedRecordingWithLocalFfmpeg()
    {
        if (Ffmpeg.Locate(refresh: true) is null) return; // ffmpeg is optional and never installed by tests
        var dir = TempDir();
        try
        {
            var master = await SyntheticRecording(dir, 4);
            var probe = await Ffmpeg.ProbeAsync(master);
            Assert.True(probe.HasVideo);
            Assert.Equal(2, probe.AudioStreams);

            var edit = RecordingEdit.ForDuration(4);
            edit.Cuts = [new TimeRange(1, 1.5)];
            edit.Blurs = [new BlurRegion(0.5, 3, 0.1, 0.1, 0.3, 0.3)];
            edit.Zooms = [new ZoomSegment(2, 3.2, 0.7, 0.3, 2)];
            edit.Speed = 1.5;
            edit.MicrophoneVolume = 0.5;
            var mp4 = Path.Combine(dir, "export.mp4");
            var args = FfmpegArgs.RecordingExport(master, mp4, edit, 320, 240, 30,
                [new AudioTrackEdit(0, 1, false), new AudioTrackEdit(1, 0.5, false)], gif: false)!;
            await Ffmpeg.RunAsync(args, RecordingEditMath.ExportDuration(edit), null, CancellationToken.None);
            var exported = await Ffmpeg.ProbeAsync(mp4);
            Assert.InRange(exported.Duration, RecordingEditMath.ExportDuration(edit) - 0.25, RecordingEditMath.ExportDuration(edit) + 0.25);
            Assert.Equal(1, exported.AudioStreams);
            Assert.Equal(320, exported.Width);

            var gif = Path.Combine(dir, "export.gif");
            var gifArgs = FfmpegArgs.RecordingExport(master, gif, edit, 320, 240, 30, [new AudioTrackEdit(0, 1, false)], gif: true, 200, 12)!;
            await Ffmpeg.RunAsync(gifArgs, RecordingEditMath.ExportDuration(edit), null, CancellationToken.None);
            Assert.True(new FileInfo(gif).Length > 1000);

            var compressed = Path.Combine(dir, "small.mp4");
            await Ffmpeg.RunAsync(FfmpegArgs.CompressVideo(master, compressed, new VideoCompressOptions { Start = 0.5, End = 3, MaxDimension = 160 }), 2.5, null, CancellationToken.None);
            var small = await Ffmpeg.ProbeAsync(compressed);
            Assert.Equal(160, small.Width);
            Assert.InRange(small.Duration, 2.2, 2.8);

            var madeGif = Path.Combine(dir, "clip.gif");
            await Ffmpeg.RunAsync(FfmpegArgs.MakeGif(master, madeGif, new GifOptions { Width = 120, Fps = 8 }), 4, null, CancellationToken.None);
            Assert.True(new FileInfo(madeGif).Length > 500);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
