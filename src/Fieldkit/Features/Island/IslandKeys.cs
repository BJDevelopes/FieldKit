// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island;

/// <summary>
/// Settings keys of the island and its extensions (upstream <c>notch*</c> UserDefaults, renamed to the
/// <c>featureId.name</c> convention). Defaults live next to them in <see cref="IslandDefaults"/>.
/// </summary>
public static class IslandKeys
{
    // ---- notch (core)
    public const string Enabled = "notch.enabled";
    public const string Size = "notch.size";                       // compact | spacious | custom
    public const string CustomWidth = "notch.customWidth";
    public const string CustomHeight = "notch.customHeight";
    public const string Outline = "notch.outline";
    public const string Display = "notch.display";                 // automatic | main | pointer | <device name>
    public const string Placement = "notch.placement";             // taskbar (default) | top
    public const string Opening = "notch.opening";                 // click | hover
    public const string HoverDelay = "notch.hoverDelay";
    public const string CloseDelay = "notch.closeDelay";
    public const string HideInFullscreen = "notch.hideInFullscreen";
    public const string ReturnHome = "notch.returnHome";
    public const string HomeModule = "notch.homeModule";
    public const string OpensActivity = "notch.opensActivity";
    public const string HiddenModules = "notch.hiddenModules";
    public const string ModuleOrder = "notch.moduleOrder";
    public const string HiddenControls = "notch.hiddenControls";
    public const string ControlOrder = "notch.controlOrder";
    public const string IdleContent = "notch.idleContent";         // none | battery | music | agents
    public const string ShowPlayingMusic = "notch.showPlayingMusic";
    public const string IncludeOtherPlayers = "notch.includeOtherPlayers";
    public const string PreferredPlayer = "notch.preferredPlayer";
    public const string TrackChange = "notch.trackChange";
    public const string ShowInCaptures = "notch.showInCaptures";
    public const string Shelf = "notch.shelf";
    public const string ShelfItems = "notch.shelfItems";
    public const string LastModule = "notch.lastModule";
    public const string ShortcutToggle = "notch.shortcut.toggle";
    public const string MusicEnabled = "notch.musicEnabled";
    public const string LyricsEnabled = "notch.lyricsEnabled";
    public const string LyricsOnline = "notch.lyricsOnline";
    public const string LowBatteryTint = "notch.lowBatteryTint";
    public const string LowBatteryThreshold = "notch.lowBatteryThreshold";
    public const string BatteryNotice = "notch.batteryNotice";
    public const string VolumeNotice = "notch.volumeNotice";

    // ---- extensions
    public const string CalendarEnabled = "notchCalendar.enabled";
    public const string CalendarFeeds = "notchCalendar.feeds";              // newline separated ICS URLs or files
    public const string CalendarCountdown = "notchCalendar.countdown";
    public const string CalendarTimeLeft = "notchCalendar.timeLeft";
    public const string CalendarMeetingNotice = "notchCalendar.meetingNotice";
    public const string CalendarWeekNumbers = "notchCalendar.weekNumbers";

    public const string GesturesEnabled = "notchGestures.enabled";

    public const string TimerEnabled = "notchTimer.enabled";
    public const string TimerMode = "notchTimer.mode";
    public const string TimerMinutes = "notchTimer.minutes";
    public const string TimerSound = "notchTimer.sound";
    public const string TimerHideCountdown = "notchTimer.hideCountdown";
    public const string PomodoroFocus = "notchTimer.pomodoroFocus";
    public const string PomodoroShortBreak = "notchTimer.pomodoroShortBreak";
    public const string PomodoroLongBreak = "notchTimer.pomodoroLongBreak";
    public const string PomodoroInterval = "notchTimer.pomodoroInterval";
    public const string PomodoroTotal = "notchTimer.pomodoroTotal";
    public const string TimerShortcut = "notchTimer.shortcut";

    public const string AccessoriesEnabled = "notchAccessories.enabled";

    public const string QueueEnabled = "notchQueue.enabled";
    public const string LiveEqualizer = "notchLiveEqualizer.enabled";

    public const string DownloadsEnabled = "notchDownloads.enabled";
    public const string DownloadsFolder = "notchDownloads.folder";

    public const string AgentsEnabled = "notchAgents.enabled";
    public const string AgentsCardOrder = "notchAgents.cardOrder";
    public const string AgentsHiddenCards = "notchAgents.hiddenCards";
    public const string AgentsPeriod = "notchAgents.period";
    public const string AgentsLimitDisplay = "notchAgents.limitDisplay";
    public const string AgentsLimitFocus = "notchAgents.limitFocus";
    public const string AgentsLiveActivity = "notchAgents.liveActivity";
    public const string AgentsReadout = "notchAgents.readout";
    public const string AgentsFinishAlert = "notchAgents.finishAlert";
    public const string AgentsFinishMinimum = "notchAgents.finishMinimum";
    public const string AgentsLimitAlert = "notchAgents.limitAlert";
    public const string AgentsLimitThreshold = "notchAgents.limitThreshold";
    public const string AgentsDailyBudget = "notchAgents.dailyBudget";
    public static string AgentsProvider(Agents.AgentProvider p) => "notchAgents." + Agents.AgentProviders.Key(p);

    public const string WatchEnabled = "notchWatch.enabled";
    public const string WatchSound = "notchWatch.sound";
    public const string WatchCondition = "notchWatch.condition";
    public const string WatchShortcut = "notchWatch.shortcut";

    public const string MascotEnabled = "notchMascot.enabled";
    public const string MascotVisits = "notchMascot.visits";
    public const string MascotReactions = "notchMascot.reactions";
    public const string MascotHidesWhenIdle = "notchMascot.hidesWhenIdle";
    public const string MascotStyle = "notchMascot.style";
    public const string MascotShape = "notchMascot.shape";
    public const string MascotPalette = "notchMascot.palette";
    public const string MascotVisitFrequency = "notchMascot.visitFrequency";
}

/// <summary>Upstream defaults.</summary>
public static class IslandDefaults
{
    /// <summary>Upstream's master switch defaults off because its feature is installed by default; Fieldkit's
    /// Features hub install is already the opt-in, so the island is on once installed.</summary>
    public const bool Enabled = true;
    public const string Size = "spacious";
    public const double CustomWidth = 440;
    public const double CustomHeight = 480;
    public const string Display = "automatic";
    public const string Opening = "click";
    public const double HoverDelay = 0.25;
    public const double CloseDelay = 0.18;
    public const string HomeModule = "controls";
    public const bool ReturnHome = true; // with nothing happening, open on Controls rather than the last page
    public const string IdleContent = "music";
    public const string HiddenControls = "keyboardLight,microphone";
    public const int TimerMinutes = 15;
    public const double AgentsFinishMinimum = 60;
    public const double AgentsLimitThreshold = 80;
}
