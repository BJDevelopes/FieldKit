// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using Fieldkit.Core;
using Fieldkit.Features.Island.Accessories;
using Fieldkit.Features.Island.Agents;
using Fieldkit.Features.Island.Calendar;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Lyrics;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Features.Island.Watch;

namespace Fieldkit.Features.Island;

/// <summary>A Watch reading as the island shows it.</summary>
public sealed record WatchView(bool Active, string Headline, string Text, BitmapSource? Preview, WatchOutcome? Outcome, DateTime Since, string? Area);

/// <summary>A file held on the island's shelf.</summary>
public sealed record ShelfItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
}

/// <summary>
/// Everything the island's views read and do, behind delegates: the live island reads its services, the
/// preview (<see cref="IslandPreview"/>) supplies sample data. Views never reach services directly.
/// </summary>
public sealed class IslandContext
{
    public bool IsPreview { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    // Music
    public Func<MediaSnapshot?> Music { get; init; } = () => null;
    public Func<IReadOnlyList<MediaSnapshot>> Players { get; init; } = () => [];
    public Func<string?> ManualPlayer { get; init; } = () => null;
    public Action<string?> ChoosePlayer { get; init; } = _ => { };
    public Action TogglePlay { get; init; } = () => { };
    public Action Next { get; init; } = () => { };
    public Action Previous { get; init; } = () => { };
    public Action<TimeSpan> Seek { get; init; } = _ => { };
    public Action ToggleShuffle { get; init; } = () => { };
    public Func<double[]?> EqualizerLevels { get; init; } = () => null;
    public Func<double> AnimationTime { get; init; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Lyrics / queue
    public Func<bool> LyricsAvailable { get; init; } = () => false;
    public Func<bool> QueueAvailable { get; init; } = () => false;
    public Func<LyricsService?> Lyrics { get; init; } = () => null;

    // Volume
    public Func<(double Level, bool Muted)?> Volume { get; init; } = () => null;
    public Action<double> SetVolume { get; init; } = _ => { };
    public Action ToggleMute { get; init; } = () => { };

    // Extensions
    public Func<TimerService?> TimerService { get; init; } = () => null;
    public Func<TimerSession?> Timer { get; init; } = () => null;
    public Func<double> TimerClock { get; init; } = () => Fieldkit.Features.Island.Timer.TimerService.Now;
    public Func<AgentUsageSnapshot?> Agents { get; init; } = () => null;
    public Func<bool> AgentsOn { get; init; } = () => false;
    public Func<IReadOnlyList<DownloadItem>?> Downloads { get; init; } = () => null;
    public Func<string> DownloadsFolder { get; init; } = () => DownloadsService.Folder;
    public Func<bool> DownloadsUnavailable { get; init; } = () => false;
    public Func<IReadOnlyList<CalendarEvent>?> Events { get; init; } = () => null;
    public Func<CalendarCountdown?> Countdown { get; init; } = () => null;
    public Func<WatchView?> Watch { get; init; } = () => null;
    public Func<WatchService?> WatchService { get; init; } = () => null;
    public Func<IReadOnlyList<AccessoryBattery>?> Accessories { get; init; } = () => null;
    public Func<bool> MascotOn { get; init; } = () => false;
    public Func<bool> GesturesOn { get; init; } = () => false;

    // Shelf
    public Func<IReadOnlyList<ShelfItem>> Shelf { get; init; } = () => [];
    public Action<IEnumerable<string>> AddToShelf { get; init; } = _ => { };
    public Action<ShelfItem> RemoveFromShelf { get; init; } = _ => { };
    public Action ClearShelf { get; init; } = () => { };

    /// <summary>The live island: reads the running services through the hub.</summary>
    public static IslandContext Live()
    {
        var volume = new VolumeControl();
        return new IslandContext
        {
            Music = () => IslandHub.Island?.Media?.Current,
            Players = () => IslandHub.Island?.Media?.All ?? [],
            ManualPlayer = () => IslandHub.Island?.Media?.ManualChoice,
            ChoosePlayer = id => IslandHub.Island?.Media?.Choose(id),
            TogglePlay = () => IslandHub.Island?.Media?.TogglePlayPause(),
            Next = () => IslandHub.Island?.Media?.Next(),
            Previous = () => IslandHub.Island?.Media?.Previous(),
            Seek = t => IslandHub.Island?.Media?.Seek(t),
            ToggleShuffle = () => IslandHub.Island?.Media?.ToggleShuffle(),
            EqualizerLevels = () => IslandHub.Equalizer?.Levels,
            LyricsAvailable = () => IslandHub.Lyrics is not null,
            QueueAvailable = () => IslandHub.Queue is not null,
            Lyrics = () => IslandHub.Lyrics,
            Volume = volume.Read,
            SetVolume = volume.Set,
            ToggleMute = volume.ToggleMute,
            TimerService = () => IslandHub.Timer,
            Timer = () => IslandHub.Timer?.Session,
            Agents = () => IslandHub.Agents?.Snapshot,
            AgentsOn = () => IslandHub.Agents is not null,
            Downloads = () => IslandHub.Downloads?.Items,
            DownloadsUnavailable = () => IslandHub.Downloads?.FolderUnavailable ?? false,
            Events = () => IslandHub.Calendar?.Events,
            Countdown = () => IslandHub.Calendar?.Countdown,
            Watch = () => IslandHub.Watch is { } w
                ? new WatchView(w.Active, w.Headline, w.Text, w.Preview, w.Outcome, w.Since, w.Target is { } r ? $"{r.Width}×{r.Height}" : null)
                : null,
            WatchService = () => IslandHub.Watch,
            Accessories = () => IslandHub.Accessories?.Devices,
            MascotOn = () => IslandHub.Mascot is not null,
            GesturesOn = () => IslandHub.Gestures is not null,
            Shelf = () => ShelfStore.Items,
            AddToShelf = ShelfStore.Add,
            RemoveFromShelf = ShelfStore.Remove,
            ClearShelf = ShelfStore.Clear,
        };
    }
}

/// <summary>The island's file shelf: paths dropped on the island, kept across restarts (never copied).</summary>
public static class ShelfStore
{
    public const int Maximum = 200;

    public static IReadOnlyList<ShelfItem> Items =>
        Settings.Get<List<string>>(IslandKeys.ShelfItems, []).Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => new ShelfItem(p)).ToList();

    public static void Add(IEnumerable<string> paths)
    {
        var list = Settings.Get<List<string>>(IslandKeys.ShelfItems, []);
        foreach (var p in paths)
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        Settings.Set(IslandKeys.ShelfItems, list.TakeLast(Maximum).ToList());
        IslandHub.React(Mascot.MascotEvent.FilesDropped);
    }

    public static void Remove(ShelfItem item)
    {
        var list = Settings.Get<List<string>>(IslandKeys.ShelfItems, []);
        list.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
        Settings.Set(IslandKeys.ShelfItems, list);
    }

    public static void Clear() => Settings.Set(IslandKeys.ShelfItems, new List<string>());
}

/// <summary>The default output's volume (Core Audio endpoint volume; no capture).</summary>
internal sealed class VolumeControl
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private DateTime _acquired;

    /// <summary>The default output, re-read every few seconds so a device switch is followed.</summary>
    private AudioEndpointVolume? Endpoint()
    {
        try
        {
            if (_device is null || DateTime.UtcNow - _acquired > TimeSpan.FromSeconds(5))
            {
                _enumerator ??= new MMDeviceEnumerator();
                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device?.Dispose();
                _device = device;
                _acquired = DateTime.UtcNow;
            }
            return _device.AudioEndpointVolume;
        }
        catch
        {
            _device?.Dispose();
            _device = null;
            return null;
        }
    }

    public (double Level, bool Muted)? Read() => Endpoint() is { } e ? (e.MasterVolumeLevelScalar, e.Mute) : null;

    public void Set(double level)
    {
        if (Endpoint() is { } e) e.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);
    }

    public void ToggleMute()
    {
        if (Endpoint() is { } e) e.Mute = !e.Mute;
    }
}
