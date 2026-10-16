// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>Compact activities of the closed island, in upstream priority order.</summary>
public enum IslandActivity { Timer, Watch, Downloads, Agents, Calendar, Music, KeepAwake }

/// <summary>What the closed island shows when nothing is live ("At rest").</summary>
public enum IslandIdleContent { None, Battery, Music, Agents }

/// <summary>Transient notices (upstream NotchNotice events).</summary>
public enum IslandNoticeEvent { Volume, Brightness, Capture, Timer, Watch, Battery, SystemNotification, Accessory, Agents, Clipboard, Download, Track, Meeting }

/// <summary>A notice shown briefly in the closed island.</summary>
/// <param name="Level">0..1 for level notices (volume) and accessory batteries.</param>
/// <param name="Tint">Theme brush key for the symbol (Orange, Green, ...); null = white.</param>
/// <param name="OpenSection">The section a click opens.</param>
public sealed record IslandNotice(
    IslandNoticeEvent Event,
    string Title,
    string Detail,
    string Glyph,
    string? Tint = null,
    double? Level = null,
    string? OpenSection = null);

/// <summary>A section (tab) of the open island.</summary>
public sealed record IslandSection(string Id, string Title, string Glyph, string Summary, string Hotkey, string? Feature);

/// <summary>
/// Island decisions in plain static code (port of upstream <c>NotchSupport</c>): section order and
/// visibility, the closed island's activity priority, how music relates to the resting companion,
/// and which notice replaces which.
/// </summary>
public static class IslandLogic
{
    /// <summary>Upstream sections that exist on Windows, in default order.</summary>
    public static readonly IslandSection[] Sections =
    [
        new("controls", "Controls", Glyphs.Toggles, "Playback, volume, brightness and your shortcuts.", "C", null),
        new("music", "Now Playing", Glyphs.MusicNote, "The song playing now, with its controls.", "M", null),
        new("files", "Files", Glyphs.Tray, "A shelf for the files you drop on the island.", "F", null),
        new("notes", "Notes", Glyphs.Note, "Quick notes to jot down, pin and copy.", "N", "notchNotes"),
        new("calendar", "Calendar", Glyphs.Calendar, "Today's events and the month ahead.", "A", "notchCalendar"),
        new("timer", "Timer", Glyphs.Timer, "Timer, Pomodoro and stopwatch.", "R", "notchTimer"),
        new("downloads", "Downloads", Glyphs.Download, "Downloads in progress and just finished.", "D", "notchDownloads"),
        new("agents", "AI Agents", Glyphs.Robot, "Claude Code, Codex, OpenCode and GitHub Copilot usage, limits and costs.", "G", "notchAgents"),
        new("watch", "Watch", Glyphs.Eye, "Any part of a window, read live, with an alert when it changes.", "O", "notchWatch"),
    ];

    public static IslandSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    public static List<string> Csv(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Saved order first (unknown ids dropped), then any section missing from it in default order.</summary>
    public static List<string> Order(string? savedOrder, IEnumerable<string> defaults)
    {
        var all = defaults.ToList();
        var order = Csv(savedOrder).Where(all.Contains).Distinct().ToList();
        order.AddRange(all.Where(id => !order.Contains(id)));
        return order;
    }

    /// <summary>The sections the island shows: ordered, not hidden, with their feature installed and enabled.</summary>
    public static List<IslandSection> VisibleSections(string? order, string? hidden, Func<string, bool> featureOn)
    {
        var hiddenSet = Csv(hidden).ToHashSet();
        return Order(order, Sections.Select(s => s.Id))
            .Select(id => Section(id)!)
            .Where(s => !hiddenSet.Contains(s.Id) && (s.Feature is null || featureOn(s.Feature)))
            .ToList();
    }

    /// <summary>Upstream <c>compactActivities</c>: every live activity, highest priority first.</summary>
    public static List<IslandActivity> CompactActivities(bool timer, bool watch, bool downloads, bool agents, bool calendar, bool music, bool keepAwake)
    {
        var result = new List<IslandActivity>();
        if (timer) result.Add(IslandActivity.Timer);
        if (watch) result.Add(IslandActivity.Watch);
        if (downloads) result.Add(IslandActivity.Downloads);
        if (agents) result.Add(IslandActivity.Agents);
        if (calendar) result.Add(IslandActivity.Calendar);
        if (music) result.Add(IslandActivity.Music);
        if (keepAwake) result.Add(IslandActivity.KeepAwake);
        return result;
    }

    /// <summary>The activity the picker chose while it is still live, otherwise the first.</summary>
    public static IslandActivity? Current(IReadOnlyList<IslandActivity> available, IslandActivity? preferred) =>
        preferred is { } p && available.Contains(p) ? p : available.Count > 0 ? available[0] : null;

    /// <summary>Upstream <c>watchesMusicActivity</c>: playing music counts as an activity when the resting
    /// island would show music, or when the companion holds the rest.</summary>
    public static bool WatchesMusicActivity(IslandIdleContent idle, bool companionEnabled, bool showPlayingMusic) =>
        showPlayingMusic && (idle != IslandIdleContent.None || companionEnabled);

    public static bool ShowsMusicActivity(bool isPlaying, IslandIdleContent idle, bool companionEnabled, bool showPlayingMusic) =>
        isPlaying && WatchesMusicActivity(idle, companionEnabled, showPlayingMusic);

    /// <summary>
    /// Upstream <c>visibleIdleContent</c> ("prioritize music over idle companion"): playing music takes the
    /// resting companion's place; paused music shows nothing at rest.
    /// </summary>
    public static IslandIdleContent VisibleIdleContent(IslandIdleContent idle, bool isPlaying, bool companionEnabled, bool showPlayingMusic)
    {
        if (ShowsMusicActivity(isPlaying, idle, companionEnabled, showPlayingMusic) &&
            (idle == IslandIdleContent.Music || (idle == IslandIdleContent.None && companionEnabled)))
            return IslandIdleContent.Music;
        return idle == IslandIdleContent.Music ? IslandIdleContent.None : idle;
    }

    /// <summary>The companion rests in view only when nothing else is at rest and no activity is live.</summary>
    public static bool CompanionRestsInView(bool companionEnabled, IslandIdleContent visibleIdle, bool tucked, IslandActivity? compact) =>
        companionEnabled && visibleIdle == IslandIdleContent.None && !tucked && compact is null;

    public static IslandIdleContent ParseIdle(string value) => value switch
    {
        "none" => IslandIdleContent.None,
        "battery" => IslandIdleContent.Battery,
        "agents" => IslandIdleContent.Agents,
        _ => IslandIdleContent.Music,
    };

    public static int Priority(IslandNoticeEvent e) => e switch
    {
        IslandNoticeEvent.Volume or IslandNoticeEvent.Brightness => 3,
        IslandNoticeEvent.Capture or IslandNoticeEvent.Timer or IslandNoticeEvent.Watch or IslandNoticeEvent.Meeting => 2,
        IslandNoticeEvent.Battery or IslandNoticeEvent.SystemNotification or IslandNoticeEvent.Accessory or IslandNoticeEvent.Agents => 1,
        _ => 0,
    };

    public static TimeSpan Duration(IslandNoticeEvent e) => TimeSpan.FromSeconds(e switch
    {
        IslandNoticeEvent.Volume or IslandNoticeEvent.Brightness => 1.6,
        IslandNoticeEvent.Capture => 12,
        IslandNoticeEvent.Timer or IslandNoticeEvent.Watch or IslandNoticeEvent.Download => 6,
        IslandNoticeEvent.Battery or IslandNoticeEvent.Accessory => 4,
        IslandNoticeEvent.SystemNotification or IslandNoticeEvent.Track => 3,
        IslandNoticeEvent.Agents => 5,
        IslandNoticeEvent.Clipboard => 2.5,
        IslandNoticeEvent.Meeting => 20,
        _ => 3,
    });

    /// <summary>Upstream <c>shouldReplace</c>: a held (hovered) notice gives way only to the same event or a higher priority.</summary>
    public static bool ShouldReplace(IslandNoticeEvent? current, IslandNoticeEvent incoming, bool held)
    {
        if (current is not { } c) return true;
        if (held) return incoming == c || Priority(incoming) > Priority(c);
        return Priority(incoming) >= Priority(c);
    }

    /// <summary>The section a click on a notice opens.</summary>
    public static string NoticeSection(IslandNoticeEvent e) => e switch
    {
        IslandNoticeEvent.Download => "downloads",
        IslandNoticeEvent.Timer => "timer",
        IslandNoticeEvent.Agents => "agents",
        IslandNoticeEvent.Track => "music",
        IslandNoticeEvent.Watch => "watch",
        IslandNoticeEvent.Meeting => "calendar",
        _ => "controls",
    };

    public static string ActivitySection(IslandActivity a) => a switch
    {
        IslandActivity.Timer => "timer",
        IslandActivity.Watch => "watch",
        IslandActivity.Downloads => "downloads",
        IslandActivity.Agents => "agents",
        IslandActivity.Calendar => "calendar",
        IslandActivity.Music => "music",
        _ => "controls",
    };

    /// <summary>Expanded island width for a size choice (compact 480, spacious 560, custom clamped 360–600).</summary>
    public static double ExpandedWidth(string size, double customWidth) => size switch
    {
        "compact" => 480,
        "custom" => Math.Clamp(customWidth, 360, 600),
        _ => 560,
    };

    /// <summary>Content height budget below the header (compact 180, spacious 264, custom from its height).</summary>
    public static double ContentBudget(string size, double customHeight) => size switch
    {
        "compact" => 180,
        "custom" => Math.Clamp(customHeight, 260, 640) - 76,
        _ => 264,
    };
}
