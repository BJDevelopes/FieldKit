// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using Fieldkit.Features.Island.Media;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Lyrics;

/// <summary>
/// Lyrics for the playing song (port of upstream <c>NotchLyricsService</c>): looked up only while the Lyrics
/// panel is open and "Find lyrics online" is on, kept in memory for one track, or imported from an .lrc file.
/// </summary>
public sealed class LyricsService : IslandExtensionService
{
    private LyricsClient? _client;
    private CancellationTokenSource? _cts;

    public override string FeatureId => "notchLyrics";
    protected override string EnableKey => IslandKeys.LyricsEnabled;

    public string? TrackKey { get; private set; }
    public LyricsPayload? Payload { get; private set; }
    public LyricsLookupStatus? Status { get; private set; }
    public bool Loading { get; private set; }
    /// <summary>The user's timing offset for this track (quarter seconds, ±10 s).</summary>
    public double Offset { get; set; }

    public static bool Online => Settings.GetBool(IslandKeys.LyricsOnline, false);

    protected override void OnStart()
    {
        IslandHub.Lyrics = this;
        _client = new LyricsClient();
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
        Payload = null;
        if (IslandHub.Lyrics == this) IslandHub.Lyrics = null;
    }

    /// <summary>Called while the panel is open: clears on a new track and looks it up when allowed.</summary>
    public void Follow(MediaSnapshot? music)
    {
        var key = music is null ? null : music.TrackKey + "\u001f" + Math.Round(music.Duration.TotalSeconds);
        if (key != TrackKey)
        {
            _cts?.Cancel();
            TrackKey = key;
            Payload = null;
            Status = null;
            Offset = 0;
            Loading = false;
        }
        if (music is null || Payload is not null || Loading || Status is not null || !Online || _client is null) return;
        _ = LookupAsync(music, key!);
    }

    public void TryAgain(MediaSnapshot? music)
    {
        _client?.ForgetFailures();
        Status = null;
        Follow(music);
    }

    /// <summary>Stops a lookup when the panel closes (what was found stays in memory).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        if (Loading) { Loading = false; Status = null; }
    }

    /// <summary>Sample state for <see cref="IslandPreview"/> (no lookup).</summary>
    internal void ShowPreview(string trackKey, LyricsPayload payload, double offset)
    {
        TrackKey = trackKey;
        Payload = payload;
        Status = LyricsLookupStatus.Found;
        Offset = offset;
    }

    public void Import(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > LrcParser.MaximumBytes) return;
            var text = File.ReadAllText(path);
            var lines = LrcParser.Parse(text);
            Payload = lines.Count > 0 ? new LyricsPayload(lines, null, false) : new LyricsPayload([], text.Trim(), false);
            Status = LyricsLookupStatus.Found;
            IslandHub.RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"Importing lyrics failed: {ex.Message}");
        }
    }

    private async Task LookupAsync(MediaSnapshot music, string key)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Loading = true;
        IslandHub.RaiseChanged();
        try
        {
            var (status, payload) = await _client!.LookupAsync(new LyricsTrack(music.Title, music.Artist, music.Album, music.Duration.TotalSeconds), cts.Token);
            if (cts.IsCancellationRequested || key != TrackKey) return;
            Status = status;
            Payload = payload;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (key == TrackKey) Loading = false;
        }
        Ui.OnUi(IslandHub.RaiseChanged);
    }
}
