// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using Fieldkit.Features.Capture;

namespace Fieldkit.Tests.Capture;

/// <summary>
/// Encodes synthetic frames and two synthetic audio tracks through the production Media Foundation
/// pipeline. No screen, camera or microphone is touched.
/// </summary>
public class RecorderEncodingTests
{
    private static (int Video, int Audio) CountTracks(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var video = 0;
        var audio = 0;
        var marker = "hdlr"u8.ToArray();
        for (var i = 0; i + 16 < bytes.Length; i++)
        {
            if (bytes[i] != marker[0] || !bytes.AsSpan(i, 4).SequenceEqual(marker)) continue;
            var type = Encoding.ASCII.GetString(bytes, i + 12, 4);
            if (type == "vide") video++;
            if (type == "soun") audio++;
        }
        return (video, audio);
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(160, 120)]
    public async Task WritesMp4WithSeparateAudioTracks(int outWidth, int outHeight)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fieldkit-encoder-{Guid.NewGuid():N}.mp4");
        try
        {
            using var encoder = new RecordingEncoder(path, 320, 240, outWidth, outHeight, 30, 1_000_000, audioTracks: 2);
            await encoder.StartAsync();
            var feed = Task.Run(() =>
            {
                const int frames = 45;
                for (var f = 0; f < frames; f++)
                {
                    var data = new byte[320 * 240 * 4];
                    for (var i = 0; i < data.Length; i += 4)
                    {
                        data[i] = (byte)(f * 5);
                        data[i + 1] = (byte)(i / 1280 % 256);
                        data[i + 2] = 200;
                        data[i + 3] = 255;
                    }
                    var ts = TimeSpan.FromSeconds(f / 30.0);
                    while (!encoder.TryAddVideo(data, ts)) Thread.Sleep(5);
                    // 1/30 s of audio per frame on each track: a 440 Hz and an 880 Hz tone.
                    const int chunk = RecordingEncoder.AudioRate / 30;
                    for (var track = 0; track < 2; track++)
                    {
                        var pcm = new byte[chunk * 4];
                        for (var s = 0; s < chunk; s++)
                        {
                            var t = (f * chunk + s) / (double)RecordingEncoder.AudioRate;
                            var v = (short)(Math.Sin(2 * Math.PI * (440 * (track + 1)) * t) * 8000);
                            pcm[s * 4] = (byte)v;
                            pcm[s * 4 + 1] = (byte)(v >> 8);
                            pcm[s * 4 + 2] = (byte)v;
                            pcm[s * 4 + 3] = (byte)(v >> 8);
                        }
                        encoder.AddAudio(track, pcm, TimeSpan.FromSeconds(f * chunk / (double)RecordingEncoder.AudioRate));
                    }
                }
                encoder.Complete();
            });
            await feed;
            var finished = await Task.WhenAny(encoder.Completion, Task.Delay(TimeSpan.FromSeconds(90)));
            Assert.Same(encoder.Completion, finished);
            await encoder.Completion;
            Assert.True(new FileInfo(path).Length > 1000);
            var (video, audio) = CountTracks(path);
            Assert.Equal(1, video);
            Assert.Equal(2, audio);

            // Media Foundation reads it back: 1.5 s, the encoded size, two embedded audio tracks.
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var clip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.3, 1.7);
            Assert.Equal(2, clip.EmbeddedAudioTracks.Count);
            var props = clip.GetVideoEncodingProperties();
            Assert.Equal((uint)outWidth, props.Width);
            Assert.Equal((uint)outHeight, props.Height);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void AudioConverterResamplesAndDownmixes()
    {
        // 44.1 kHz, 4-channel float input → 48 kHz stereo 16-bit.
        var converter = new AudioConverter(44100, 4, AudioSampleKind.Float32);
        var frames = 4410;
        var input = new byte[frames * 4 * 4];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < 4; c++)
                BitConverter.GetBytes(c == 0 ? 0.5f : -0.25f).CopyTo(input, (f * 4 + c) * 4);
        var total = 0;
        for (var chunk = 0; chunk < 10; chunk++)
        {
            var output = converter.Convert(input);
            total += output.Length / 4;
            var left = BitConverter.ToInt16(output, 0);
            var right = BitConverter.ToInt16(output, 2);
            Assert.InRange(left, 16380, 16386);
            Assert.InRange(right, -8195, -8189);
        }
        // 10 × 0.1 s at 48 kHz ≈ 48 000 frames.
        Assert.InRange(total, 47990, 48010);
    }

    [Fact]
    public void AudioConverterPassesThroughPcm16()
    {
        var converter = new AudioConverter(48000, 1, AudioSampleKind.Pcm16);
        var input = new byte[] { 0x10, 0x00, 0xF0, 0xFF };
        var output = converter.Convert(input);
        Assert.Equal(8, output.Length);
        Assert.Equal(16, BitConverter.ToInt16(output, 0));
        Assert.Equal(16, BitConverter.ToInt16(output, 2));
        Assert.Equal(-16, BitConverter.ToInt16(output, 4));
    }
}
