// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>
/// The Dynamic Island itself: owns the window, the media session watcher and the toggle shortcut while the
/// "notch" feature is installed and enabled. Extensions run as their own services and stop with it.
/// </summary>
public sealed class IslandService : FeatureService
{
    private IslandWindow? _window;
    private IDisposable? _hotkey;
    private IDisposable? _observer;
    private string? _lastPlayingTrack;
    private DateTime _lastGroove = DateTime.MinValue;

    public override string FeatureId => "notch";
    public override IReadOnlyList<string> EnableKeys => [IslandKeys.Enabled];
    public override bool EnabledByDefault => IslandDefaults.Enabled;

    public MediaSessionWatcher? Media { get; private set; }
    public IslandWindow? Window => _window;

    protected override void OnStart()
    {
        IslandHub.Island = this;
        Media = new MediaSessionWatcher();
        Media.Changed += OnMediaChanged;
        Media.Start();
        _window = new IslandWindow(IslandContext.Live());
        _window.ShowIsland();
        _hotkey = Hotkeys.Bind(IslandKeys.ShortcutToggle, string.Empty, IslandHub.Toggle);
        // Only what changes the island's shape or contents; page controls (ruler, menus) refresh themselves.
        _observer = Settings.Observe(() => _window?.SettingsChanged(),
            IslandKeys.Size, IslandKeys.CustomWidth, IslandKeys.CustomHeight, IslandKeys.ModuleOrder, IslandKeys.HiddenModules,
            IslandKeys.HiddenControls, IslandKeys.Display, IslandKeys.ShowInCaptures, IslandKeys.IdleContent, IslandKeys.ShowPlayingMusic,
            IslandKeys.AgentsLiveActivity, IslandKeys.AgentsReadout, IslandKeys.TimerHideCountdown, IslandKeys.MascotStyle,
            IslandKeys.MascotShape, IslandKeys.MascotPalette, IslandKeys.ShelfItems);
        ReevaluateExtensions();
    }

    protected override void OnStop()
    {
        _observer?.Dispose();
        _observer = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _window?.CloseIsland();
        _window = null;
        if (Media is not null)
        {
            Media.Changed -= OnMediaChanged;
            Media.Dispose();
            Media = null;
        }
        if (IslandHub.Island == this) IslandHub.Island = null;
        ReevaluateExtensions();
    }

    /// <summary>Extensions only run while the island runs: re-check them when it starts or stops.</summary>
    private static void ReevaluateExtensions()
    {
        foreach (var f in FeatureCatalog.All.Where(f => f.Parent == "notch"))
            FeatureRuntime.Reevaluate(f.Id);
    }

    private void OnMediaChanged()
    {
        var music = Media?.Current;
        if (music is { IsPlaying: true } && music.TrackKey != _lastPlayingTrack)
        {
            var first = _lastPlayingTrack is null;
            _lastPlayingTrack = music.TrackKey;
            if (!first && Settings.GetBool(IslandKeys.TrackChange, true))
                IslandHub.Post(new IslandNotice(IslandNoticeEvent.Track, music.Title, music.Artist, Glyphs.MusicNote, OpenSection: "music"), toastFallback: false);
            if (DateTime.UtcNow - _lastGroove > TimeSpan.FromMinutes(10))
            {
                _lastGroove = DateTime.UtcNow;
                IslandHub.React(MascotEvent.MusicStarted);
            }
        }
        IslandHub.RaiseChanged();
    }
}
