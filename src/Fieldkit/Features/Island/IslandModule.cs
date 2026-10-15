// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Island.Accessories;
using Fieldkit.Features.Island.Agents;
using Fieldkit.Features.Island.Calendar;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Lyrics;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Prefs;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Features.Island.Watch;
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>The Dynamic Island and its extensions.</summary>
public sealed class IslandModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("notch", Parity.Partial,
            "Simulated capsule in the taskbar (default) or at the top centre of the chosen display, with music, controls, file shelf, sections and their order. " +
            "Missing: camera mirror, clipboard/captures/scratchpad/mixer/system sections, floating quick-access buttons, lock screen, " +
            "all-displays mirroring, peek preview, haptics, brightness and keyboard-light controls (other features' quick toggles appear instead).");
        r.Implements("notchCalendar", Parity.Partial,
            "Agenda and countdowns from iCalendar feeds (URL or .ics file). The Windows calendar is not readable by an unpackaged app; no month grid, calendar selection or per-event countdowns.");
        r.Implements("notchNotifications", Parity.Planned,
            "Mirroring toasts needs UserNotificationListener, which Windows grants only to packaged apps with the userNotificationListener capability.");
        r.Implements("notchGestures", Parity.Partial,
            "Scroll down to open, scroll up over the top row to close, swipe (touchpad) or tilt the wheel sideways over the music to change tracks. " +
            "Upstream's momentum completion after lifting the fingers is not ported.");
        r.Implements("notchTimer", Parity.Full,
            "Timer, Pomodoro and stopwatch with the minute ruler, notice and a repeating chime.");
        r.Implements("notchAccessories", Parity.Partial,
            "Battery levels Windows keeps for Bluetooth devices (hands-free/LE): connection notices and one low-battery warning per episode. Devices Windows reports no battery for are not shown.");
        r.Implements("notchLyrics", Parity.Partial,
            "Synced lyrics from LRCLIB (/api/get then /api/search), on demand, in memory, with .lrc import and timing offset. Upstream's MusicBrainz alias lookup is not ported.");
        r.Implements("notchQueue", Parity.Planned,
            "Windows media sessions (GSMTC) do not expose a player's queue; the Up next panel says so.");
        r.Implements("notchLiveEqualizer", Parity.Partial,
            "WASAPI loopback of the default output drives upstream's 7-band FFT bars; it hears every app, not only the player's process.");
        r.Implements("notchDownloads", Parity.Partial,
            "Watches the Downloads folder (or a chosen one) for .crdownload/.part/.download files: bytes received, active state and finished notices. Browsers do not record a total, so there is no percentage.");
        r.Implements("notchAgents", Parity.Partial,
            "Claude Code, Codex, OpenCode (SQLite via winsqlite3) and Copilot CLI from local logs: limits, tokens, API value, models, projects, live work and finish notices. " +
            "No Codex account resets (needs Codex's own sign-in), no online price-list updates; the Claude app's limits file location on Windows is assumed.");
        r.Implements("notchWatch", Parity.Partial,
            "Watches a screen area (GDI capture + offline Windows OCR) with all four rules. Follows a fixed screen area rather than a window, so a covered or moved window is not followed.");
        r.Implements("notchMascot", Parity.Partial,
            "Companion with styles, shapes, colours, moods, blinking and reactions to island events; music outranks the resting companion. No visits, petting, dozing or Command Bar face.");

        r.Service("notch", () => new IslandService());
        r.Service("notchTimer", () => new TimerService());
        r.Service("notchAgents", () => new AgentUsageService());
        r.Service("notchDownloads", () => new DownloadsService());
        r.Service("notchCalendar", () => new CalendarService());
        r.Service("notchWatch", () => new WatchService());
        r.Service("notchAccessories", () => new AccessoriesService());
        r.Service("notchLiveEqualizer", () => new LiveEqualizerService());
        r.Service("notchLyrics", () => new LyricsService());
        r.Service("notchMascot", () => new MascotService());
        r.Service("notchGestures", () => new GesturesService());
        r.Service("notchQueue", () => new QueueService());

        r.SettingsPage(new SettingsPageDef("island", SettingsCategory.DynamicIsland, "Dynamic Island", Glyphs.Island, 0, ["notch"],
            IslandSettingsPage.Build,
            ["notch", "island", "music", "lyrics", "equalizer", "timer", "pomodoro", "calendar", "ics", "downloads", "watch", "companion", "mascot", "accessories", "battery", "gestures", "sections"]));
        r.SettingsPage(new SettingsPageDef("agents", SettingsCategory.DynamicIsland, "AI agents", Glyphs.Robot, 10, ["notchAgents"],
            AgentsSettingsPage.Build, ["claude", "codex", "opencode", "copilot", "tokens", "limits", "api value"]));

        r.Action(new ToolAction("notch.toggle", "notch", "Open or close Dynamic Island", Glyphs.Island, IslandHub.Toggle, ShortcutKey: IslandKeys.ShortcutToggle));
        r.Action(new ToolAction("notchTimer.start", "notchTimer", "Start or pause the island timer", Glyphs.Timer, () =>
        {
            if (IslandHub.Timer is not { } t) return;
            if (!t.Session.HasSession) t.Start();
            else t.PauseOrResume();
        }, ShortcutKey: IslandKeys.TimerShortcut));
        r.Action(new ToolAction("notchWatch.choose", "notchWatch", "Watch an area of the screen", Glyphs.Eye, () => IslandHub.Watch?.ChooseArea(),
            ShortcutKey: IslandKeys.WatchShortcut));
        r.Action(new ToolAction("notchAgents.open", "notchAgents", "Show AI agents in Dynamic Island", Glyphs.Robot, () => IslandHub.Open("agents")));
    }
}
