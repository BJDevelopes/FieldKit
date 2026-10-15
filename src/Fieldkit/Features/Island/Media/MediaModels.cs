// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media.Imaging;

namespace Fieldkit.Features.Island.Media;

/// <summary>What one media session reports, flattened into plain data (testable, UI-friendly).</summary>
public sealed record MediaSnapshot
{
    /// <summary>App user model id of the player ("Spotify.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "chrome").</summary>
    public string SourceApp { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public bool HasTrack => Title.Length > 0;
    /// <summary>Total duration, zero when the player does not say.</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>Position when <see cref="PositionUpdatedAt"/> was taken.</summary>
    public TimeSpan Position { get; init; }
    public DateTimeOffset PositionUpdatedAt { get; init; }
    public bool CanGoNext { get; init; } = true;
    public bool CanGoPrevious { get; init; } = true;
    public bool CanSeek { get; init; }
    public bool? Shuffle { get; init; }
    /// <summary>0 = off, 1 = track, 2 = list; null when unsupported.</summary>
    public int? Repeat { get; init; }
    /// <summary>When this session last changed track or play state (for "most recent" ranking).</summary>
    public DateTimeOffset LastActivity { get; init; }
    public BitmapSource? Artwork { get; init; }

    /// <summary>Position now, extrapolated from the last report while playing and clamped to the duration.</summary>
    public TimeSpan PositionAt(DateTimeOffset now) => TrackTime.Extrapolate(Position, PositionUpdatedAt, IsPlaying, Duration, now);

    /// <summary>A key that changes when the track changes (lyrics and artwork caches use it).</summary>
    public string TrackKey => $"{Artist}\u001f{Title}\u001f{Album}";
}

/// <summary>Timeline maths shared by the island views and lyrics sync.</summary>
public static class TrackTime
{
    public static TimeSpan Extrapolate(TimeSpan position, DateTimeOffset updatedAt, bool playing, TimeSpan duration, DateTimeOffset now)
    {
        var value = position;
        if (playing && updatedAt != default)
        {
            var elapsed = now - updatedAt;
            if (elapsed > TimeSpan.Zero) value += elapsed;
        }
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && value > duration) value = duration;
        return value;
    }

    /// <summary>"3:07", "1:02:03"; negative values get a leading minus ("-1:12" for remaining time).</summary>
    public static string Format(TimeSpan value)
    {
        var negative = value < TimeSpan.Zero;
        var t = negative ? value.Negate() : value;
        var total = (long)Math.Floor(t.TotalSeconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        var text = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        return negative ? "-" + text : text;
    }

    /// <summary>0..1 progress, 0 when the duration is unknown.</summary>
    public static double Progress(TimeSpan position, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
}
