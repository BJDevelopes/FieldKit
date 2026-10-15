// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using Fieldkit.UI;
using Windows.Media.Control;

namespace Fieldkit.Features.Island.Media;

/// <summary>
/// Follows every Windows media session (GlobalSystemMediaTransportControlsSessionManager — the
/// counterpart of upstream's MediaRemote adapter), keeps a <see cref="MediaSnapshot"/> per
/// session and picks the one the island shows with <see cref="MediaSelection"/>.
/// Created and disposed by the island service; raises <see cref="Changed"/> on the UI thread.
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private readonly Dictionary<string, Tracked> _sessions = new(StringComparer.Ordinal);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _disposed;
    private string? _lastChosen;

    public event Action? Changed;

    /// <summary>The session the island shows, or null when nothing is worth showing.</summary>
    public MediaSnapshot? Current { get; private set; }

    public IReadOnlyList<MediaSnapshot> All => _sessions.Values.Select(t => t.Snapshot).Where(s => s.HasTrack).ToList();

    /// <summary>A player chosen in the island's source menu (memory only, like upstream); null = Automatic.</summary>
    public string? ManualChoice { get; private set; }

    public void Choose(string? sourceApp)
    {
        ManualChoice = sourceApp;
        Recompute();
    }

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed || _manager is null) return;
            _manager.SessionsChanged += (_, _) => Ui.OnUi(SyncSessions);
            _manager.CurrentSessionChanged += (_, _) => Ui.OnUi(Recompute);
            Ui.OnUi(SyncSessions);
        }
        catch (Exception ex)
        {
            Log.Error("Media sessions are unavailable", ex);
        }
    }

    private void SyncSessions()
    {
        if (_disposed || _manager is null) return;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _manager.GetSessions(); }
        catch (Exception ex) { Log.Error("Listing media sessions failed", ex); return; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            string id;
            try { id = s.SourceAppUserModelId ?? string.Empty; }
            catch { continue; }
            if (!seen.Add(id)) continue;
            if (_sessions.ContainsKey(id)) continue;
            var tracked = new Tracked(this, id, s);
            _sessions[id] = tracked;
            tracked.Attach();
        }
        foreach (var gone in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _sessions[gone].Detach();
            _sessions.Remove(gone);
            if (ManualChoice == gone) ManualChoice = null;
        }
        Recompute();
    }

    private void Recompute()
    {
        if (_disposed) return;
        string? systemCurrent = null;
        try { systemCurrent = _manager?.GetCurrentSession()?.SourceAppUserModelId; } catch { }
        var choice = MediaSelection.Choose(
            _sessions.Values.Select(t => t.Snapshot).ToList(),
            new MediaSelectionContext(
                ManualChoice: ManualChoice,
                IncludeOtherPlayers: Settings.GetBool(IslandKeys.IncludeOtherPlayers, true),
                SystemCurrent: systemCurrent,
                PreviousChoice: _lastChosen));
        _lastChosen = choice?.SourceApp;
        Current = choice;
        Changed?.Invoke();
    }

    private GlobalSystemMediaTransportControlsSession? SessionFor(MediaSnapshot? s) =>
        s is not null && _sessions.TryGetValue(s.SourceApp, out var t) ? t.Session : null;

    public void TogglePlayPause() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public void Next() => Run(s => s.TrySkipNextAsync().AsTask());
    public void Previous() => Run(s => s.TrySkipPreviousAsync().AsTask());

    public void Seek(TimeSpan position) => Run(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    public void ToggleShuffle()
    {
        var on = Current?.Shuffle ?? false;
        Run(s => s.TryChangeShuffleActiveAsync(!on).AsTask());
    }

    public void CycleRepeat()
    {
        var next = MediaSelection.NextRepeat(Current?.Repeat ?? 0);
        Run(s => s.TryChangeAutoRepeatModeAsync((Windows.Media.MediaPlaybackAutoRepeatMode)next).AsTask());
    }

    private void Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        var session = SessionFor(Current);
        if (session is null) return;
        _ = RunAsync();
        async Task RunAsync()
        {
            try { await command(session); }
            catch (Exception ex) { Log.Error("Media command failed", ex); }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var t in _sessions.Values) t.Detach();
        _sessions.Clear();
        _manager = null;
        Current = null;
        Changed = null;
    }

    /// <summary>One session plus its latest snapshot.</summary>
    private sealed class Tracked(MediaSessionWatcher owner, string id, GlobalSystemMediaTransportControlsSession session)
    {
        private bool _attached;
        public GlobalSystemMediaTransportControlsSession Session => session;
        public MediaSnapshot Snapshot { get; private set; } = new() { SourceApp = id };

        public void Attach()
        {
            _attached = true;
            session.MediaPropertiesChanged += OnProperties;
            session.PlaybackInfoChanged += OnPlayback;
            session.TimelinePropertiesChanged += OnTimeline;
            _ = RefreshPropertiesAsync();
            RefreshPlayback();
            RefreshTimeline();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            try
            {
                session.MediaPropertiesChanged -= OnProperties;
                session.PlaybackInfoChanged -= OnPlayback;
                session.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }
        }

        private void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
            Ui.OnUi(() => _ = RefreshPropertiesAsync());

        private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
            Ui.OnUi(() => { RefreshPlayback(); owner.Recompute(); });

        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
            Ui.OnUi(() => { RefreshTimeline(); owner.Recompute(); });

        private async Task RefreshPropertiesAsync()
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (!_attached || props is null) return;
                var title = props.Title ?? string.Empty;
                var artist = props.Artist is { Length: > 0 } a ? a : props.AlbumArtist ?? string.Empty;
                var album = props.AlbumTitle ?? string.Empty;
                var changed = title != Snapshot.Title || artist != Snapshot.Artist;
                BitmapSource? art = changed ? null : Snapshot.Artwork;
                if (props.Thumbnail is not null && (changed || art is null))
                    art = await LoadArtworkAsync(props.Thumbnail);
                if (!_attached) return;
                Snapshot = Snapshot with
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Artwork = art,
                    LastActivity = changed ? DateTimeOffset.Now : Snapshot.LastActivity,
                };
                owner.Recompute();
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading media properties of {id} failed: {ex.Message}");
            }
        }

        private void RefreshPlayback()
        {
            try
            {
                var info = session.GetPlaybackInfo();
                if (info is null) return;
                var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                Snapshot = Snapshot with
                {
                    IsPlaying = playing,
                    CanGoNext = info.Controls?.IsNextEnabled ?? true,
                    CanGoPrevious = info.Controls?.IsPreviousEnabled ?? true,
                    CanSeek = info.Controls?.IsPlaybackPositionEnabled ?? false,
                    Shuffle = info.Controls?.IsShuffleEnabled == true ? info.IsShuffleActive : null,
                    Repeat = info.Controls?.IsRepeatEnabled == true && info.AutoRepeatMode is { } r ? (int)r : null,
                    LastActivity = playing != Snapshot.IsPlaying ? DateTimeOffset.Now : Snapshot.LastActivity,
                    // Resync the extrapolation anchor when play state flips.
                    Position = playing != Snapshot.IsPlaying ? Snapshot.PositionAt(DateTimeOffset.Now) : Snapshot.Position,
                    PositionUpdatedAt = playing != Snapshot.IsPlaying ? DateTimeOffset.Now : Snapshot.PositionUpdatedAt,
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading playback info of {id} failed: {ex.Message}");
            }
        }

        private void RefreshTimeline()
        {
            try
            {
                var t = session.GetTimelineProperties();
                if (t is null) return;
                var duration = t.EndTime - t.StartTime;
                Snapshot = Snapshot with
                {
                    Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    Position = t.Position - t.StartTime,
                    PositionUpdatedAt = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime : DateTimeOffset.Now,
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading timeline of {id} failed: {ex.Message}");
            }
        }

        private static async Task<BitmapSource?> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
        {
            try
            {
                using var stream = await reference.OpenReadAsync();
                using var net = stream.AsStreamForRead();
                var memory = new MemoryStream();
                await net.CopyToAsync(memory);
                memory.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 200;
                image.StreamSource = memory;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
