// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.Dsp;
using NAudio.Wave;
using Fieldkit.Core;

namespace Fieldkit.Features.Island.Media;

/// <summary>
/// WASAPI loopback capture of the default output feeding an FFT (the counterpart of upstream's
/// process tap). Only runs while the Live equalizer extension is on AND music is playing, and
/// never records or stores audio: each block is reduced to a handful of band levels.
/// <see cref="Levels"/> is read by the island's bars on the UI thread.
/// </summary>
public sealed class LoopbackSpectrum : IDisposable
{
    public const int FftSize = 1024;
    private const int FftPower = 10;

    private readonly object _gate = new();
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];
    private readonly EqualizerSmoother _smoother;
    private int _filled;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;

    public LoopbackSpectrum(int bars)
    {
        _smoother = new EqualizerSmoother(bars);
    }

    public bool IsRunning => _capture is not null;

    /// <summary>Latest smoothed levels (0..1), one per bar; null until sound has been heard
    /// (the bars keep their synthetic motion meanwhile).</summary>
    public double[]? Levels
    {
        get { lock (_gate) return _smoother.Heard ? _smoother.Levels.ToArray() : null; }
    }

    public void Start()
    {
        if (_capture is not null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            _sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Warn($"Loopback capture stopped: {e.Exception.Message}");
            };
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            Log.Error("Starting the live equalizer failed", ex);
            Stop();
        }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c is null) return;
        try
        {
            c.DataAvailable -= OnData;
            c.StopRecording();
        }
        catch { }
        finally
        {
            c.Dispose();
        }
        lock (_gate) _smoother.Reset();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null) return;
        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Extensible) return;
        if (format.BitsPerSample != 32) return;

        var frameBytes = 4 * channels;
        for (var offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, offset + c * 4);
            _window[_filled++] = sum / channels;
            if (_filled == FftSize)
            {
                Analyse();
                _filled = 0;
            }
        }
    }

    private void Analyse()
    {
        for (var i = 0; i < FftSize; i++)
        {
            // vDSP_HANN_NORM is twice NAudio's Hann scaled by 0.8165 (upstream's window).
            _fft[i].X = (float)(_window[i] * FastFourierTransform.HannWindow(i, FftSize) * 1.633);
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftPower, _fft);
        for (var i = 0; i < _magnitudes.Length; i++)
            _magnitudes[i] = MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
        var bands = EqualizerMath.Bands(_magnitudes, _sampleRate, FftSize);
        lock (_gate) _smoother.Push(bands);
    }

    public void Dispose() => Stop();
}
