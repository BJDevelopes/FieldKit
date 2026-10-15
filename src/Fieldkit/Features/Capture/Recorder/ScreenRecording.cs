// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Fieldkit.Core;
using Fieldkit.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Fieldkit.Features.Capture;

/// <summary>What a finished recording hands to the editor.</summary>
public sealed record RecordingResult(string Path, double Duration, int Width, int Height, int Fps,
    bool HasSystemAudio, bool HasMicrophone, IReadOnlyList<RecordedClick> Clicks);

/// <summary>Recording options read from settings when a take starts.</summary>
public sealed record RecordingOptions(int Fps, string Quality, bool SystemAudio, bool Microphone);

/// <summary>A pausable clock that only counts recorded time.</summary>
internal sealed class RecordingClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private TimeSpan _paused;
    private TimeSpan? _pausedAt;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_watch) return (_pausedAt ?? _watch.Elapsed) - _paused;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_watch) return _pausedAt is not null;
        }
    }

    public void Pause()
    {
        lock (_watch) _pausedAt ??= _watch.Elapsed;
    }

    public void Resume()
    {
        lock (_watch)
        {
            if (_pausedAt is { } at) _paused += _watch.Elapsed - at;
            _pausedAt = null;
        }
    }
}

/// <summary>
/// One take: Windows.Graphics.Capture frames (monitor, window or an area of a monitor) sampled at a
/// constant frame rate, WASAPI loopback (PC sound) and the default microphone as separate tracks,
/// all fed to <see cref="RecordingEncoder"/>. Clicks are recorded for automatic zooms.
/// </summary>
internal sealed class ScreenRecording : IDisposable
{
    private readonly CaptureOutcome.Region _region;
    private readonly RecordingOptions _options;
    private readonly string _path;
    private readonly RecordingClock _clock = new();
    private readonly List<RecordedClick> _clicks = [];
    private readonly object _frameGate = new();
    private readonly List<AudioSource> _audio = [];
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private RecordingEncoder? _encoder;
    private Thread? _pump;
    private volatile bool _stopping;
    private int _converting;
    private byte[]? _latest;
    private Windows.Graphics.SizeInt32 _poolSize;
    private PixelRect _crop;        // inside the captured frame
    private PixelRect _screenRect;  // what is recorded, in screen pixels
    private int _width;
    private int _height;
    private IDisposable? _mouse;

    public ScreenRecording(CaptureOutcome.Region region, RecordingOptions options, string path)
    {
        _region = region;
        _options = options;
        _path = path;
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsPaused => _clock.IsPaused;
    public PixelRect ScreenRect => _screenRect;
    public bool HasSystemAudio => _audio.Any(a => !a.IsMicrophone);
    public bool HasMicrophone => _audio.Any(a => a.IsMicrophone);
    public event Action<string>? Warning;

    public async Task StartAsync()
    {
        if (!CaptureInterop.IsSupported) throw new InvalidOperationException("Screen recording needs Windows 10 version 1903 or later.");
        _device = CaptureInterop.CreateDevice();
        if (_region.Window != IntPtr.Zero)
        {
            _item = CaptureInterop.ForWindow(_region.Window);
            var b = WindowHelper.GetVisibleBounds(_region.Window);
            _screenRect = PixelRect.FromLtrb(b.Left, b.Top, b.Right, b.Bottom);
            _crop = new PixelRect(0, 0, _item.Size.Width, _item.Size.Height).EvenSize();
        }
        else
        {
            _item = CaptureInterop.ForMonitor(_region.Monitor);
            var info = WindowHelper.GetMonitorInfo(_region.Monitor).rcMonitor;
            var monitor = PixelRect.FromLtrb(info.Left, info.Top, info.Right, info.Bottom);
            _screenRect = _region.ScreenRect.Intersect(monitor);
            _crop = _screenRect.Offset(-monitor.X, -monitor.Y).EvenSize();
        }
        _width = _crop.Width;
        _height = _crop.Height;
        if (_width < 16 || _height < 16) throw new InvalidOperationException("The area is too small to record.");

        StartAudio();
        var quality = RecordingEditMath.Quality(_options.Quality);
        var outW = Math.Max(2, (int)(_width * quality.OutputScale)) & ~1;
        var outH = Math.Max(2, (int)(_height * quality.OutputScale)) & ~1;
        _encoder = new RecordingEncoder(_path, _width, _height, outW, outH, _options.Fps,
            RecordingEditMath.VideoBitrate(_width, _height, _options.Fps, _options.Quality), _audio.Count);
        await _encoder.StartAsync();

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        _item.Closed += (_, _) => Warning?.Invoke("The recorded window was closed");
        _session.StartCapture();
        foreach (var a in _audio) a.Start();

        _mouse = InputHooks.AddMouseHandler(OnMouse);
        _pump = new Thread(Pump) { IsBackground = true, Name = "Fieldkit recorder", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    private void StartAudio()
    {
        if (_options.SystemAudio)
        {
            try { _audio.Add(new AudioSource(new WasapiLoopbackCapture(), _clock, isMicrophone: false)); }
            catch (Exception ex) { Log.Warn($"PC sound unavailable: {ex.Message}"); }
        }
        if (_options.Microphone)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) throw new InvalidOperationException("No microphone");
                _audio.Add(new AudioSource(new WasapiCapture(), _clock, isMicrophone: true));
            }
            catch (Exception ex)
            {
                Log.Warn($"Microphone unavailable: {ex.Message}");
                Warning?.Invoke("Microphone unavailable");
            }
        }
    }

    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn || e.Action != MouseAction.LeftDown || _clock.IsPaused || _stopping) return false;
        var r = _screenRect;
        if (r.Contains(e.X, e.Y))
        {
            var click = new RecordedClick(_clock.Elapsed.TotalSeconds, (e.X - r.X) / (double)r.Width, (e.Y - r.Y) / (double)r.Height);
            lock (_clicks) _clicks.Add(click);
        }
        return false;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || _stopping) return;
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            // A resized window: recreate the pool at the new size (the output keeps its size).
            _poolSize = frame.ContentSize;
            try { sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize); }
            catch (Exception ex) { Log.Warn($"Recreating the frame pool failed: {ex.Message}"); }
            return;
        }
        if (Interlocked.CompareExchange(ref _converting, 1, 0) != 0) return; // still converting the previous one
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            var w = bitmap.PixelWidth;
            var h = bitmap.PixelHeight;
            var full = new byte[w * h * 4];
            bitmap.CopyToBuffer(full.AsBuffer());
            var output = new byte[_width * _height * 4];
            // Copy the recorded part; a window that shrank leaves black where it no longer reaches.
            var rows = Math.Min(_height, h - _crop.Y);
            var cols = Math.Min(_width, w - _crop.X);
            if (rows > 0 && cols > 0)
            {
                for (var y = 0; y < rows; y++)
                    Buffer.BlockCopy(full, ((_crop.Y + y) * w + _crop.X) * 4, output, y * _width * 4, cols * 4);
            }
            PixelOps.MakeOpaque(output);
            lock (_frameGate) _latest = output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Converting a captured frame failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _converting, 0);
        }
    }

    /// <summary>Constant-rate sampler: the newest frame every 1/fps of recorded time, and the audio timeline.</summary>
    private void Pump()
    {
        var black = new byte[_width * _height * 4];
        PixelOps.MakeOpaque(black);
        long frameIndex = 0;
        var fps = _options.Fps;
        while (!_stopping)
        {
            var now = _clock.Elapsed;
            foreach (var a in _audio) a.Pump(now, _encoder!, _audio.IndexOf(a));
            var due = TimeSpan.FromSeconds(frameIndex / (double)fps);
            if (_clock.IsPaused || now < due)
            {
                Thread.Sleep(Math.Clamp((int)(due - now).TotalMilliseconds, 1, 10));
                continue;
            }
            byte[] frame;
            lock (_frameGate) frame = _latest ?? black;
            // A full queue means the encoder is behind: drop this frame rather than drift.
            _encoder!.TryAddVideo(frame, due);
            frameIndex++;
        }
    }

    public void Pause()
    {
        _clock.Pause();
        foreach (var a in _audio) a.Clear();
    }

    public void Resume() => _clock.Resume();

    /// <summary>Stops capturing and waits for the file to be finalised.</summary>
    public async Task<RecordingResult> StopAsync()
    {
        var duration = _clock.Elapsed.TotalSeconds;
        Shutdown();
        _encoder!.Complete();
        await _encoder.Completion;
        List<RecordedClick> clicks;
        lock (_clicks) clicks = [.. _clicks];
        return new RecordingResult(_path, duration, _width, _height, _options.Fps, HasSystemAudio, HasMicrophone, clicks);
    }

    private void Shutdown()
    {
        if (_stopping) return;
        _stopping = true;
        _mouse?.Dispose();
        _mouse = null;
        _pump?.Join(2000);
        foreach (var a in _audio) a.Dispose();
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null;
        _pool = null;
    }

    public void Dispose()
    {
        Shutdown();
        _encoder?.Dispose();
    }

    /// <summary>One WASAPI source turned into a gap-free 48 kHz stereo track.</summary>
    private sealed class AudioSource : IDisposable
    {
        private const int Rate = RecordingEncoder.AudioRate;
        private const long Slack = Rate * 3 / 10; // 300 ms
        private readonly IWaveIn _capture;
        private readonly RecordingClock _clock;
        private readonly AudioConverter _converter;
        private readonly Queue<byte[]> _chunks = new();
        private long _buffered;
        private long _written;

        public bool IsMicrophone { get; }

        public AudioSource(IWaveIn capture, RecordingClock clock, bool isMicrophone)
        {
            _capture = capture;
            _clock = clock;
            IsMicrophone = isMicrophone;
            var format = capture.WaveFormat;
            var kind = format.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat => AudioSampleKind.Float32,
                WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => AudioSampleKind.Pcm16,
                WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => AudioSampleKind.Pcm24,
                WaveFormatEncoding.Pcm => AudioSampleKind.Pcm32,
                // Shared-mode mix formats are WAVEFORMATEXTENSIBLE; 32-bit ones are float.
                _ => format.BitsPerSample switch { 16 => AudioSampleKind.Pcm16, 24 => AudioSampleKind.Pcm24, _ => AudioSampleKind.Float32 },
            };
            _converter = new AudioConverter(format.SampleRate, format.Channels, kind, Rate);
            capture.DataAvailable += OnData;
        }

        public void Start() => _capture.StartRecording();

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (_clock.IsPaused || e.BytesRecorded <= 0) return;
            var pcm = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (pcm.Length == 0) return;
            lock (_chunks)
            {
                _chunks.Enqueue(pcm);
                _buffered += pcm.Length / 4;
            }
        }

        public void Clear()
        {
            lock (_chunks)
            {
                _chunks.Clear();
                _buffered = 0;
            }
        }

        /// <summary>Emits audio up to the recorded time; silence fills gaps (loopback sends nothing during silence).</summary>
        public void Pump(TimeSpan now, RecordingEncoder encoder, int track)
        {
            var expected = (long)(now.TotalSeconds * Rate);
            List<byte[]> emit = [];
            lock (_chunks)
            {
                // Far ahead of the clock (device drift): drop the oldest audio.
                while (_buffered > 0 && _written + _buffered > expected + Slack * 2 && _chunks.Count > 1)
                {
                    var dropped = _chunks.Dequeue();
                    _buffered -= dropped.Length / 4;
                }
                while (_chunks.Count > 0 && _written < expected + Slack)
                {
                    var chunk = _chunks.Dequeue();
                    _buffered -= chunk.Length / 4;
                    emit.Add(chunk);
                    _written += chunk.Length / 4;
                }
            }
            var start = _written - emit.Sum(c => c.Length / 4L);
            foreach (var chunk in emit)
            {
                encoder.AddAudio(track, chunk, TimeSpan.FromSeconds(start / (double)Rate));
                start += chunk.Length / 4;
            }
            var missing = expected - _written;
            if (missing > Slack)
            {
                var frames = (int)Math.Min(missing, Rate);
                encoder.AddAudio(track, new byte[frames * 4], TimeSpan.FromSeconds(_written / (double)Rate));
                _written += frames;
            }
        }

        public void Dispose()
        {
            try { _capture.StopRecording(); } catch { }
            _capture.DataAvailable -= OnData;
            _capture.Dispose();
        }
    }
}
