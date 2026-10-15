// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;
using Windows.Media.Control;

namespace Fieldkit.Features.Launcher.RadialMenu;

public enum RadialNowPlayingKind { Loading, NothingPlaying, Playing }

/// <summary>What the system media session reports for the Now Playing slice.</summary>
/// <param name="AppId">The session's SourceAppUserModelId ("Spotify.exe", "…_8wekyb3d8bbwe!App").</param>
public sealed record RadialNowPlayingSnapshot(string? Title, string? Artist, string? Album, byte[]? ArtworkData, string? AppId)
{
    /// <summary>"Title\nArtist" for the hub, or null when neither is known.</summary>
    public string? RadialLabel
    {
        get
        {
            var parts = new[] { Title, Artist }.Where(p => p is not null).ToList();
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }
}

public sealed record RadialNowPlayingState(RadialNowPlayingKind Kind, RadialNowPlayingSnapshot? Snapshot = null)
{
    public static RadialNowPlayingState Loading { get; } = new(RadialNowPlayingKind.Loading);
    public static RadialNowPlayingState NothingPlaying { get; } = new(RadialNowPlayingKind.NothingPlaying);
    public static RadialNowPlayingState Playing(RadialNowPlayingSnapshot snapshot) => new(RadialNowPlayingKind.Playing, snapshot);
}

/// <summary>Pure sanitisation of media-session metadata (upstream <c>RadialNowPlayingSupport</c>).</summary>
public static class RadialNowPlayingSupport
{
    public const int MaximumArtworkBytes = 12 * 1024 * 1024;
    public const int MaximumTextLength = 300;

    public static string? SanitizedText(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        var clean = new string(trimmed.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray());
        if (clean.Length == 0) return null;
        return clean.Length <= MaximumTextLength ? clean : clean[..MaximumTextLength];
    }

    public static string? SanitizedAppId(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 255 || trimmed.Any(char.IsControl)) return null;
        return trimmed;
    }

    /// <summary>A snapshot only while something plays and something identifies it.</summary>
    public static RadialNowPlayingSnapshot? Snapshot(string? title, string? artist, string? album, byte[]? artwork,
        string? appId, bool isPlaying)
    {
        if (!isPlaying) return null;
        var t = SanitizedText(title);
        var id = SanitizedAppId(appId);
        if (t is null && id is null) return null;
        var art = artwork is { Length: > 0 and <= MaximumArtworkBytes } ? artwork : null;
        return new RadialNowPlayingSnapshot(t, SanitizedText(artist), SanitizedText(album), art, id);
    }

    /// <summary>The executable a desktop AUMID names ("Spotify.exe" → "Spotify"), or null for packaged apps.</summary>
    public static string? ProcessNameOf(string? appId)
    {
        if (string.IsNullOrEmpty(appId) || appId.Contains('!')) return null;
        return appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId[..^4] : appId;
    }
}

/// <summary>
/// Reads the system media session (GlobalSystemMediaTransportControls) once per wheel that shows
/// the Now Playing slice. Nothing polls: each session asks once and forgets.
/// </summary>
public static class RadialNowPlaying
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static async Task<RadialNowPlayingSnapshot?> FetchAsync()
    {
        try
        {
            var work = FetchCoreAsync();
            var finished = await Task.WhenAny(work, Task.Delay(Timeout)).ConfigureAwait(false);
            return finished == work ? await work.ConfigureAwait(false) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Now Playing read failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<RadialNowPlayingSnapshot?> FetchCoreAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null) return null;
        var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (!playing) return null;
        var props = await session.TryGetMediaPropertiesAsync();
        byte[]? artwork = null;
        if (props?.Thumbnail is { } thumb)
        {
            try
            {
                using var stream = await thumb.OpenReadAsync();
                if (stream.Size is > 0 and <= RadialNowPlayingSupport.MaximumArtworkBytes)
                {
                    using var managed = stream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await managed.CopyToAsync(buffer).ConfigureAwait(false);
                    artwork = buffer.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Now Playing artwork failed: {ex.Message}");
            }
        }
        return RadialNowPlayingSupport.Snapshot(props?.Title, props?.Artist, props?.AlbumTitle, artwork,
            session.SourceAppUserModelId, playing);
    }

    /// <summary>The running executable behind a session, if it is a desktop app.</summary>
    public static string? ProcessPath(RadialNowPlayingSnapshot snapshot)
    {
        var name = RadialNowPlayingSupport.ProcessNameOf(snapshot.AppId);
        if (name is null) return null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                var path = WindowHelper.GetProcessPath((uint)process.Id);
                if (path.Length > 0) return path;
            }
        }
        return null;
    }

    public static string? AppName(RadialNowPlayingSnapshot snapshot)
    {
        if (ProcessPath(snapshot) is { } path)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception)
            {
                // fall through to the file name
            }
            return Path.GetFileNameWithoutExtension(path);
        }
        if (snapshot.AppId is { Length: > 0 } id)
            return id.Contains('!') ? RadialCatalog.FileDisplayName("shell:AppsFolder\\" + id) : RadialNowPlayingSupport.ProcessNameOf(id);
        return null;
    }

    public static ImageSource? AppIcon(RadialNowPlayingSnapshot snapshot) =>
        ProcessPath(snapshot) is { } path ? AppIcons.ForPath(path) : null;

    /// <summary>Brings the player forward (its window, or launches it again when it has none).</summary>
    public static void OpenApp(RadialNowPlayingSnapshot snapshot)
    {
        try
        {
            var path = ProcessPath(snapshot);
            if (path is not null)
            {
                var window = WindowHelper.GetAltTabWindows()
                    .FirstOrDefault(w => string.Equals(w.ProcessPath, path, StringComparison.OrdinalIgnoreCase));
                if (window is not null)
                {
                    WindowHelper.Activate(window.Handle);
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (snapshot.AppId is { } id && id.Contains('!'))
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening the player failed: {ex.Message}");
        }
    }
}
