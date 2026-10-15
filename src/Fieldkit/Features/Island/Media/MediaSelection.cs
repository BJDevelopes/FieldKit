// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Media;

/// <param name="ManualChoice">A player picked in the island's source menu, or null for Automatic.</param>
/// <param name="IncludeOtherPlayers">"Automatically include videos and other apps".</param>
/// <param name="SystemCurrent">The session Windows itself calls current.</param>
/// <param name="PreviousChoice">The player the island followed last (stickiness).</param>
public sealed record MediaSelectionContext(string? ManualChoice, bool IncludeOtherPlayers, string? SystemCurrent, string? PreviousChoice);

/// <summary>
/// Which media session the island shows (port of upstream <c>NotchPlaybackSource.preferred</c>):
/// playing music beats playing video, which beats a paused player already followed, which beats paused music.
/// </summary>
public static class MediaSelection
{
    private static readonly string[] MusicApps =
    [
        "spotify", "zunemusic", "applemusic", "itunes", "foobar2000", "musicbee", "aimp", "tidal", "deezer",
        "amazonmusic", "winamp", "dopamine", "plexamp", "cider", "youtubemusic", "qobuz", "vlc",
    ];

    /// <summary>Whether a session's app id names a music player (vs. a browser or video app).</summary>
    public static bool IsMusicApp(string sourceApp)
    {
        var id = sourceApp.ToLowerInvariant().Replace(".", string.Empty).Replace(" ", string.Empty);
        return MusicApps.Any(id.Contains);
    }

    public static MediaSnapshot? Choose(IReadOnlyList<MediaSnapshot> sessions, MediaSelectionContext ctx)
    {
        var available = sessions.Where(s => s.HasTrack).OrderBy(s => s.SourceApp, StringComparer.Ordinal).ToList();
        if (ctx.ManualChoice is { } manual && available.FirstOrDefault(s => s.SourceApp == manual) is { } chosen) return chosen;
        var music = available.Where(s => IsMusicApp(s.SourceApp)).ToList();
        var other = ctx.IncludeOtherPlayers ? available.Where(s => !IsMusicApp(s.SourceApp)).ToList() : [];
        var groups = new[]
        {
            music.Where(s => s.IsPlaying).ToList(),
            other.Where(s => s.IsPlaying).ToList(),
            other.Where(s => s.SourceApp == ctx.PreviousChoice).ToList(),
            music,
        };
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            if (group.FirstOrDefault(s => s.SourceApp == ctx.PreviousChoice) is { } previous) return previous;
            if (group.FirstOrDefault(s => s.SourceApp == ctx.SystemCurrent) is { } system) return system;
            return group[0];
        }
        return ctx.IncludeOtherPlayers ? available.FirstOrDefault(s => s.SourceApp == ctx.SystemCurrent) : null;
    }

    /// <summary>Off → list → track → off (Windows MediaPlaybackAutoRepeatMode values 0/2/1).</summary>
    public static int NextRepeat(int current) => current switch { 0 => 2, 2 => 1, _ => 0 };

    /// <summary>A friendly name for a session's app id ("Spotify", "Chrome", "Media Player").</summary>
    public static string FriendlyName(string sourceApp)
    {
        if (string.IsNullOrWhiteSpace(sourceApp)) return "Player";
        var id = sourceApp;
        var bang = id.IndexOf('!');
        if (bang >= 0) id = id[..bang];
        var underscore = id.IndexOf('_');
        if (underscore > 0) id = id[..underscore];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        var lower = id.ToLowerInvariant();
        if (lower.Contains("zunemusic")) return "Media Player";
        if (lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("msedge")) return "Edge";
        var dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];
        return id.Length == 0 ? "Player" : char.ToUpperInvariant(id[0]) + id[1..];
    }
}
