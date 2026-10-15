// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Media;

/// <summary>
/// Live equalizer (off by default, like upstream): while music plays, listens to the default output through
/// WASAPI loopback and turns each 1024-sample block into seven band levels. Nothing is stored or sent. The
/// capture stops 1.5 s after playback pauses and when the extension or the island turns off; if nothing is
/// heard within 8 s the bars keep their usual motion for that track.
/// </summary>
public sealed class LiveEqualizerService : IslandExtensionService
{
    public const int Bars = 7;
    private readonly DispatcherTimer _pauseGrace = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _giveUp = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
    private LoopbackSpectrum? _spectrum;
    private string? _gaveUpOn;
    private string? _track;

    public override string FeatureId => "notchLiveEqualizer";
    protected override string EnableKey => IslandKeys.LiveEqualizer;
    public override bool EnabledByDefault => false;

    /// <summary>Live levels, or null when the bars should keep their synthetic motion.</summary>
    public double[]? Levels => _spectrum?.Levels;

    public LiveEqualizerService()
    {
        _pauseGrace.Tick += (_, _) => { _pauseGrace.Stop(); StopCapture(); };
        _giveUp.Tick += (_, _) =>
        {
            _giveUp.Stop();
            if (_spectrum is { Levels: null })
            {
                _gaveUpOn = _track;
                StopCapture();
            }
        };
    }

    protected override void OnStart()
    {
        IslandHub.Equalizer = this;
        IslandHub.Changed += Follow;
        Follow();
    }

    protected override void OnStop()
    {
        IslandHub.Changed -= Follow;
        _pauseGrace.Stop();
        _giveUp.Stop();
        StopCapture();
        if (IslandHub.Equalizer == this) IslandHub.Equalizer = null;
    }

    private void Follow()
    {
        var music = IslandHub.Island?.Media?.Current;
        var playing = music is { IsPlaying: true };
        var track = music?.TrackKey;
        if (track != _track)
        {
            _track = track;
            _gaveUpOn = null;
        }
        if (playing && _gaveUpOn != track)
        {
            _pauseGrace.Stop();
            if (_spectrum is null)
            {
                _spectrum = new LoopbackSpectrum(Bars);
                _spectrum.Start();
                _giveUp.Stop();
                _giveUp.Start();
            }
        }
        else if (_spectrum is not null && !_pauseGrace.IsEnabled)
        {
            _pauseGrace.Start();
        }
    }

    private void StopCapture()
    {
        _spectrum?.Dispose();
        _spectrum = null;
    }
}
