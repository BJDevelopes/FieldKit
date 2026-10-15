// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Fieldkit.Core;
using static Fieldkit.Features.Capture.MediaFoundation;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Writes an H.264 MP4 through the Media Foundation sink writer: BGRA frames in, and each audio
/// source (PC sound, microphone) as its own AAC track. Hardware encoders are used when present.
/// Independent of capture, so it is tested with synthetic frames and tones.
/// </summary>
public sealed class RecordingEncoder : IDisposable
{
    public const int AudioRate = 48000;
    private const int MaxPendingVideo = 12;
    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly int _outWidth;
    private readonly int _outHeight;
    private readonly int _fps;
    private readonly uint _bitrate;
    private readonly int _audioTracks;
    private readonly BlockingCollection<Item> _queue = new(boundedCapacity: 2000);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingVideo;
    private volatile bool _aborted;
    private Thread? _thread;

    private readonly record struct Item(int Track, byte[] Data, long Time, long Duration);

    /// <param name="width">Size of the frames handed in.</param>
    /// <param name="outWidth">Encoded size; half the input for the "Small file" quality.</param>
    public RecordingEncoder(string path, int width, int height, int outWidth, int outHeight, int fps, uint bitrate, int audioTracks)
    {
        _path = path;
        _width = width & ~1;
        _height = height & ~1;
        _outWidth = Math.Max(2, outWidth & ~1);
        _outHeight = Math.Max(2, outHeight & ~1);
        _fps = Math.Clamp(fps, 1, 120);
        _bitrate = bitrate;
        _audioTracks = Math.Clamp(audioTracks, 0, 2);
    }

    public int Width => _width;
    public int Height => _height;
    public int AudioTracks => _audioTracks;

    /// <summary>Completes when the file is finalised (or faults).</summary>
    public Task Completion => _completed.Task;

    public Task StartAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "Fieldkit encoder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return _started.Task;
    }

    private void WriterLoop()
    {
        IMFSinkWriter? writer = null;
        var started = false;
        try
        {
            Check(MFStartup(MF_VERSION, 0));
            started = true;
            writer = CreateWriter(out var videoStream, out var audioStreams);
            writer.BeginWriting();
            _started.TrySetResult();
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_aborted) break;
                var stream = item.Track < 0 ? videoStream : audioStreams[item.Track];
                if (item.Track < 0) Interlocked.Decrement(ref _pendingVideo);
                WriteSample(writer, stream, item);
            }
            if (!_aborted) writer.DoFinalize();
            _completed.TrySetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Encoding the recording failed", ex);
            _started.TrySetException(ex);
            _completed.TrySetException(ex);
        }
        finally
        {
            if (writer is not null) Marshal.ReleaseComObject(writer);
            if (started) MFShutdown();
        }
    }

    private IMFSinkWriter CreateWriter(out int videoStream, out int[] audioStreams)
    {
        Check(MFCreateAttributes(out var attributes, 2));
        attributes.SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        attributes.SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
        if (File.Exists(_path)) File.Delete(_path);
        Check(MFCreateSinkWriterFromURL(_path, IntPtr.Zero, attributes, out var writer));
        Marshal.ReleaseComObject(attributes);

        Check(MFCreateMediaType(out var videoOut));
        videoOut.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        videoOut.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        videoOut.SetUINT32(MF_MT_AVG_BITRATE, _bitrate);
        videoOut.SetUINT32(MF_MT_INTERLACE_MODE, 2); // progressive
        videoOut.SetUINT64(MF_MT_FRAME_SIZE, Pack((uint)_outWidth, (uint)_outHeight));
        videoOut.SetUINT64(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
        videoOut.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        writer.AddStream(videoOut, out videoStream);
        Marshal.ReleaseComObject(videoOut);

        Check(MFCreateMediaType(out var videoIn));
        videoIn.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        videoIn.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        videoIn.SetUINT32(MF_MT_INTERLACE_MODE, 2);
        videoIn.SetUINT64(MF_MT_FRAME_SIZE, Pack((uint)_outWidth, (uint)_outHeight));
        videoIn.SetUINT64(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
        videoIn.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        videoIn.SetUINT32(MF_MT_DEFAULT_STRIDE, (uint)(_outWidth * 4)); // top-down
        videoIn.SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        writer.SetInputMediaType(videoStream, videoIn, null);
        Marshal.ReleaseComObject(videoIn);

        audioStreams = new int[_audioTracks];
        for (var i = 0; i < _audioTracks; i++)
        {
            Check(MFCreateMediaType(out var audioOut));
            audioOut.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            audioOut.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            audioOut.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            audioOut.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
            audioOut.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
            audioOut.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 20000); // 160 kbit/s
            audioOut.SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
            writer.AddStream(audioOut, out audioStreams[i]);
            Marshal.ReleaseComObject(audioOut);

            Check(MFCreateMediaType(out var audioIn));
            audioIn.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            audioIn.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
            audioIn.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            audioIn.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
            audioIn.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
            audioIn.SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
            audioIn.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * 4);
            writer.SetInputMediaType(audioStreams[i], audioIn, null);
            Marshal.ReleaseComObject(audioIn);
        }
        return writer;
    }

    private static void WriteSample(IMFSinkWriter writer, int stream, Item item)
    {
        Check(MFCreateMemoryBuffer(item.Data.Length, out var buffer));
        Check(MFCreateSample(out var sample));
        try
        {
            buffer.Lock(out var ptr, out _, out _);
            Marshal.Copy(item.Data, 0, ptr, item.Data.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(item.Data.Length);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(item.Time);
            sample.SetSampleDuration(item.Duration);
            writer.WriteSample(stream, sample);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }

    private static long Ticks(TimeSpan t) => t.Ticks; // 100 ns units, as Media Foundation expects

    /// <summary>Queues a frame (BGRA, <see cref="Width"/>×<see cref="Height"/>). False when the encoder is behind.</summary>
    public bool TryAddVideo(byte[] bgra, TimeSpan timestamp)
    {
        if (_queue.IsAddingCompleted || bgra.Length != _width * _height * 4) return false;
        if (Volatile.Read(ref _pendingVideo) >= MaxPendingVideo) return false;
        var data = _outWidth == _width && _outHeight == _height ? bgra : PixelOps.Downscale(bgra, _width, _height, _outWidth, _outHeight);
        Interlocked.Increment(ref _pendingVideo);
        try
        {
            if (_queue.TryAdd(new Item(-1, data, Ticks(timestamp), TimeSpan.TicksPerSecond / _fps))) return true;
        }
        catch (InvalidOperationException)
        {
        }
        Interlocked.Decrement(ref _pendingVideo);
        return false;
    }

    /// <summary>Queues 16-bit stereo PCM at <see cref="AudioRate"/> for a track.</summary>
    public void AddAudio(int track, byte[] pcm, TimeSpan timestamp)
    {
        if (track < 0 || track >= _audioTracks || pcm.Length < 4 || _queue.IsAddingCompleted) return;
        var duration = (long)(pcm.Length / 4.0 / AudioRate * TimeSpan.TicksPerSecond);
        try { _queue.TryAdd(new Item(track, pcm, Ticks(timestamp), duration), 2000); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Ends the streams; the writer then finalises the file.</summary>
    public void Complete()
    {
        try { _queue.CompleteAdding(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Gives up without finalising (the file is incomplete and should be deleted).</summary>
    public void Abort()
    {
        _aborted = true;
        Complete();
    }

    public void Dispose()
    {
        if (!_completed.Task.IsCompleted) Abort();
        _thread?.Join(5000);
        _queue.Dispose();
    }
}
