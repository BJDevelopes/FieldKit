// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.UI;

namespace Fieldkit.Core;

/// <summary>Hub sections, in display order (mirrors the upstream FeatureGroup).</summary>
public enum FeatureGroup
{
    WindowsTaskbar,
    MouseKeyboard,
    ClipboardFiles,
    Sound,
    EnergyDisplay,
    Tools,
    DynamicIsland,
    Monitor,
}

/// <summary>What an installed, enabled feature keeps alive (shown as a badge in the Features hub).</summary>
public enum EnergyProfile
{
    Idle,
    Mouse,
    Keyboard,
    Inputs,
    Periodic,
}

/// <summary>How the Windows build covers an upstream feature.</summary>
public enum Parity
{
    /// <summary>Not built yet.</summary>
    Planned,
    /// <summary>Built, but some upstream behaviour is missing (see the module's note).</summary>
    Partial,
    /// <summary>Behaviour matches upstream.</summary>
    Full,
    /// <summary>Windows already does this natively; the feature points at the system setting.</summary>
    Native,
}

/// <param name="Id">Stable identity, reusing the upstream raw value. Never rename.</param>
/// <param name="WindowsApproach">How the Windows build implements (or maps) the feature.</param>
/// <param name="Extra">A Fieldkit addition that Vorssaint does not ship (often an idea from one of its pull
/// requests). Extras start uninstalled and stay out of the parity counts.</param>
public sealed record FeatureInfo(
    string Id,
    FeatureGroup Group,
    string Title,
    string Description,
    string Glyph,
    EnergyProfile Energy,
    string WindowsApproach,
    bool Beta = false,
    string? Parent = null,
    bool Extra = false)
{
    public string AvailabilityKey => $"feature.{Id}.available";

    /// <summary>Installed in the Features hub. Everything starts installed, like upstream.</summary>
    public bool IsAvailable => Settings.GetBool(AvailabilityKey, !Beta && !Extra);

    public void SetAvailable(bool value) => Settings.Set(AvailabilityKey, value);
}

/// <summary>
/// Every feature of the upstream app, mapped to Windows. Ids are the upstream
/// <c>AppFeature</c> raw values so the parity map can be traced line for line.
/// </summary>
public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureInfo> All { get; } = Build();

    /// <summary>The features Vorssaint ships (what parity is measured against).</summary>
    public static IReadOnlyList<FeatureInfo> Upstream { get; } = All.Where(f => !f.Extra).ToList();

    private static readonly Dictionary<string, FeatureInfo> ById =
        All.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public static FeatureInfo Get(string id) =>
        ById.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unknown feature '{id}'.");

    public static bool TryGet(string id, out FeatureInfo info) => ById.TryGetValue(id, out info!);

    public static bool IsAvailable(string id) => TryGet(id, out var f) && f.IsAvailable;

    public static string GroupTitle(FeatureGroup g) => g switch
    {
        FeatureGroup.WindowsTaskbar => "Windows and Taskbar",
        FeatureGroup.MouseKeyboard => "Mouse and keyboard",
        FeatureGroup.ClipboardFiles => "Clipboard and files",
        FeatureGroup.Sound => "Sound",
        FeatureGroup.EnergyDisplay => "Energy and display",
        FeatureGroup.Tools => "Tools",
        FeatureGroup.DynamicIsland => "Dynamic Island",
        FeatureGroup.Monitor => "System monitor",
        _ => g.ToString(),
    };

    public static string EnergyLabel(EnergyProfile e) => e switch
    {
        EnergyProfile.Idle => "Nothing at rest",
        EnergyProfile.Mouse => "Listens to the mouse",
        EnergyProfile.Keyboard => "Listens to the keyboard",
        EnergyProfile.Inputs => "Listens to mouse and keyboard",
        EnergyProfile.Periodic => "Checks on an interval",
        _ => string.Empty,
    };

    private static List<FeatureInfo> Build()
    {
        var W = FeatureGroup.WindowsTaskbar;
        var M = FeatureGroup.MouseKeyboard;
        var C = FeatureGroup.ClipboardFiles;
        var S = FeatureGroup.Sound;
        var E = FeatureGroup.EnergyDisplay;
        var T = FeatureGroup.Tools;
        var I = FeatureGroup.DynamicIsland;
        var Mo = FeatureGroup.Monitor;

        return
        [
            // Windows and Taskbar (upstream: Windows and Dock)
            new("switcher", W, "App switcher", "Switch apps and windows with previews", Glyphs.Switcher, EnergyProfile.Keyboard,
                "Alt+Tab replacement (optional takeover) using DWM live thumbnails, search, per-display filter and a simple icon-only mode."),
            new("dockPreview", W, "Taskbar Preview", "Window previews when hovering the taskbar", Glyphs.Taskbar, EnergyProfile.Mouse,
                "Hovering a taskbar button shows larger DWM previews of every window across virtual desktops, with close, move and snap actions (UI Automation on the taskbar)."),
            new("dockClick", W, "Taskbar clicks", "Click a taskbar icon to minimize or cycle windows", Glyphs.Taskbar, EnergyProfile.Mouse,
                "Clicking the active app's taskbar button minimizes all its windows, hides them, or cycles through them."),
            new("windowMaximizer", W, "Maximize windows", "Fill the screen without the maximized state", Glyphs.Maximize, EnergyProfile.Mouse,
                "Windows maximizes natively. Adds a fill-the-work-area action (keeps the window resizable/movable) on title-bar double-click or the maximize button with a modifier."),
            new("windowLayout", W, "Window layout", "Arrange windows with shortcuts or edge snapping, then adjust them by dragging", Glyphs.Layout, EnergyProfile.Idle,
                "Global shortcuts for halves, thirds, quarters, sixths, center, next display and restore; modifier-drag move/resize; custom edge zones beside Windows Snap."),
            new("autoQuit", W, "Quit on close", "Quit apps when their last window closes", Glyphs.CloseBox, EnergyProfile.Idle,
                "WinEvent hook on window destruction: when a chosen app's last top-level window closes, its process is asked to exit (useful for apps that hide to the tray)."),
            new("spacesOrder", W, "Fixed desktop order", "Keep virtual desktops in the order you set", Glyphs.Desktops, EnergyProfile.Idle,
                "Windows never reorders virtual desktops by recent use, so this is native behaviour."),

            // Mouse and keyboard
            new("scrollInverter", M, "Scroll direction", "Invert the mouse wheel direction", Glyphs.ArrowsUpDown, EnergyProfile.Mouse,
                "Low-level mouse hook inverts wheel (vertical and/or horizontal) for mice only; precision touchpads keep their own setting."),
            new("scrollHorizontal", M, "Scroll sideways", "Scroll sideways while holding a key", Glyphs.ArrowsLeftRight, EnergyProfile.Mouse,
                "While the chosen key is held, vertical wheel input is re-sent as horizontal wheel input."),
            new("focusFollowsMouse", M, "Focus follows mouse", "Bring the window under the pointer forward", Glyphs.Pointer, EnergyProfile.Mouse,
                "Activates the window under the pointer after an adjustable pause, optionally without raising it."),
            new("smoothScroll", M, "Smooth scrolling", "Smooth, animated mouse scrolling", Glyphs.Mouse, EnergyProfile.Mouse,
                "Wheel notches are swallowed and replayed as an eased stream of high-resolution wheel deltas."),
            new("linearScroll", M, "Linear scrolling", "Every wheel notch scrolls the same distance", Glyphs.ArrowsUpDown, EnergyProfile.Mouse,
                "Normalises accelerated wheel deltas from vendor drivers to one notch each."),
            new("mouseAcceleration", M, "Pointer acceleration", "Turn off mouse acceleration", Glyphs.Pointer, EnergyProfile.Idle,
                "Turns off 'Enhance pointer precision' (SPI_SETMOUSE) and restores the previous value when disabled."),
            new("mouseNavigation", M, "Side buttons", "Side mouse buttons go back and forward", Glyphs.Mouse, EnergyProfile.Mouse,
                "Windows maps X1/X2 to Back/Forward natively; adds per-app overrides for apps that ignore them."),
            new("mouseButtonShortcuts", M, "Mouse button shortcuts", "Assign keys and gestures to extra buttons", Glyphs.Mouse, EnergyProfile.Mouse,
                "Map middle/X1/X2 buttons to shortcuts; button-drag gestures switch virtual desktops or open Task View."),
            new("middleClick", M, "Middle click", "Three finger click acts as a middle click", Glyphs.Mouse, EnergyProfile.Idle,
                "Precision touchpads offer three-finger tap = middle click natively; opens the touchpad settings."),
            new("mouseClickDebounce", M, "Extra click filter", "Ignore accidental rapid clicks from worn buttons", Glyphs.Mouse, EnergyProfile.Mouse,
                "Low-level mouse hook drops a second press of the same button within the threshold."),
            new("keyboardDebounce", M, "Key debounce", "Ignore accidental double key presses", Glyphs.Keyboard, EnergyProfile.Keyboard,
                "Low-level keyboard hook drops repeated presses of the same key within the threshold."),
            new("textSnippets", M, "Text snippets", "Expand short triggers into text", Glyphs.Note, EnergyProfile.Keyboard,
                "Keyboard hook watches typed triggers and replaces them via SendInput, with clipboard/date/time variables and a searchable library."),
            new("superKey", M, "Super key", "Use Caps Lock or a right-side modifier as a shortcut combination", Glyphs.Keyboard, EnergyProfile.Keyboard,
                "Caps Lock or right Ctrl/Alt becomes Ctrl+Alt+Shift+Win (Hyper) when held, with a separate tap action and app exclusions."),
            new("quitWindowProtection", M, "Quit and close protection", "Prevent accidental Alt+F4, Ctrl+Q or Ctrl+W", Glyphs.Shield, EnergyProfile.Keyboard,
                "Keyboard hook requires a hold, double press or extra modifier per app before Alt+F4 / Ctrl+Q / Ctrl+W pass through."),

            // Clipboard and files
            new("clipboardHistory", C, "Clipboard history", "Keep a local history of what you copy", Glyphs.Clipboard, EnergyProfile.Idle,
                "Clipboard format listener stores text, images and file lists locally with search, pins, preview and paste shortcuts."),
            new("pastePlain", C, "Paste as plain text", "Paste text without formatting", Glyphs.Document, EnergyProfile.Idle,
                "A shortcut pastes the clipboard's plain text and then restores the original rich content."),
            new("finderCutPaste", C, "Explorer shortcuts", "Paste copied images as PNG files in Explorer", Glyphs.Cut, EnergyProfile.Keyboard,
                "Explorer already cuts and pastes files; adds Ctrl+V of a copied image into the open folder as a PNG file."),
            new("finderRename", C, "Rename with F2", "Rename files with F2", Glyphs.Rename, EnergyProfile.Idle,
                "Explorer renames with F2 natively."),
            new("shelf", C, "Shelf", "Park files, text and links while dragging", Glyphs.Tray, EnergyProfile.Mouse,
                "A floating drop shelf appears near the pointer when you shake while dragging (or by shortcut); drag items back out later."),
            new("urlCleaner", C, "Clean URL", "Copied links lose their tracking junk", Glyphs.Link, EnergyProfile.Idle,
                "Clipboard listener strips tracking parameters (utm_*, fbclid, gclid, ...) automatically or on demand."),
            new("diskImageInstaller", C, "Installer helper", "Install from a disk image or archive and clean up", Glyphs.Disc, EnergyProfile.Idle,
                "Mounts an ISO/VHD or extracts a ZIP, runs its installer, then ejects and optionally deletes the download."),

            // Sound
            new("mixer", S, "Volume mixer", "Per-app volume, pinning and custom order", Glyphs.Mixer, EnergyProfile.Idle,
                "WASAPI audio sessions: per-app volume, mute, live level, pin and reorder; boost past 100% via process-loopback re-render."),
            new("soundOutputSwitcher", S, "Output switcher", "Cycle sound outputs with a shortcut", Glyphs.Speaker, EnergyProfile.Idle,
                "Switches the default render endpoint (PolicyConfig) by shortcut and lowers volume when headphones disconnect."),
            new("audioPriority", S, "Audio device priority", "Automatically use your preferred audio devices", Glyphs.Headphones, EnergyProfile.Idle,
                "Watches endpoint arrivals and selects the highest-priority output and input."),
            new("micMute", S, "Microphone tools", "Mute the microphone from anywhere", Glyphs.Microphone, EnergyProfile.Idle,
                "Mutes every capture endpoint by shortcut, picks a preferred input and adjusts its level."),
            new("musicBlock", S, "Music app blocker", "Block music launches from detected media keys", Glyphs.MusicNote, EnergyProfile.Keyboard,
                "When Play is pressed with no media session, stops the chosen music app from being launched."),

            // Energy and display
            new("keepAwake", E, "Keep awake", "Keep the PC awake on demand", Glyphs.Moon, EnergyProfile.Idle,
                "SetThreadExecutionState with timers and automations (apps, power, displays), plus lid-close action override with restore."),
            new("brightness", E, "Displays", "Control display brightness, with hardware control where supported", Glyphs.Brightness, EnergyProfile.Keyboard,
                "DDC/CI (dxva2) for external monitors, WMI for laptop panels, software dimming overlay as fallback and below minimum."),
            new("extraBrightness", E, "Extra brightness", "Extra brightness on HDR displays", Glyphs.Brightness, EnergyProfile.Idle,
                "Raises the SDR content brightness on HDR displays beyond the usual maximum.", Beta: true),
            new("bluetoothSleep", E, "Bluetooth on sleep", "Turn Bluetooth off during sleep", Glyphs.Bluetooth, EnergyProfile.Idle,
                "Turns the Bluetooth radio off on suspend and back on at resume only if the app turned it off."),

            // Tools
            new("quickLauncher", T, "Quick panel", "A floating panel with your favorite tools", Glyphs.Grid, EnergyProfile.Idle,
                "A floating palette of favorite tools, summoned with Ctrl+Win+V."),
            new("quickToggles", T, "Quick toggles", "Switch appearance, hide desktop icons, lock and more", Glyphs.Toggles, EnergyProfile.Idle,
                "Dark mode, hide desktop icons, empty Recycle Bin, lock screen, eject drives, show hidden files and more."),
            new("colorPicker", T, "Color picker", "Pick any color on screen", Glyphs.Eyedropper, EnergyProfile.Idle,
                "Full-screen magnifier loupe; copies HEX, RGB, HSL or WPF/CSS code."),
            new("screenOCR", T, "Copy text from screen", "Copy text or QR codes from anything on screen", Glyphs.TextScan, EnergyProfile.Idle,
                "Area selection + Windows.Media.Ocr (offline) and a QR decoder."),
            new("cleaningMode", T, "Cleaning Mode", "Lock keyboard and screen for cleaning", Glyphs.Keyboard, EnergyProfile.Idle,
                "Low-level hooks swallow keyboard input behind a black screen or a small indicator until the exit gesture."),
            new("mediaTools", T, "Media", "Compress videos, images and GIFs", Glyphs.Photo, EnergyProfile.Idle,
                "Image conversion/compression/watermarks via WIC; video compression, trimming and GIFs via a local ffmpeg."),
            new("cleaner", T, "Cleaner", "Clear caches and junk files", Glyphs.Broom, EnergyProfile.Idle,
                "Temp folders, app caches, logs, crash dumps, thumbnail cache and leftovers, manually or on a schedule."),
            new("uninstaller", T, "Uninstaller", "Remove apps and their leftovers", Glyphs.Delete, EnergyProfile.Idle,
                "Runs the registered uninstaller, then reviews leftover AppData folders, registry keys and shortcuts."),
            new("homebrew", T, "Packages", "Search, install and remove winget packages", Glyphs.Package, EnergyProfile.Idle,
                "The Homebrew manager maps to winget: search, install, upgrade and uninstall without a terminal."),
            new("appUpdates", T, "App updates", "Check every app's updates in one list", Glyphs.Download, EnergyProfile.Periodic,
                "winget upgrade + Microsoft Store updates in one list; install together or open an app's own updater."),
            new("screenshot", T, "Screenshot", "Capture, annotate and share screenshots", Glyphs.Camera, EnergyProfile.Idle,
                "Area/window/screen/scrolling capture with an annotation editor, pinning and backgrounds."),
            new("cameraPreview", T, "Camera preview", "Check your camera in a floating mirror", Glyphs.Webcam, EnergyProfile.Idle,
                "Floating always-on-top mirror using MediaCapture."),
            new("radialMenu", T, "Radial menu", "A wheel of apps and tools around the pointer", Glyphs.Radial, EnergyProfile.Keyboard,
                "Shortcut opens a customizable wheel of apps, files, shortcuts and tools with profiles and submenus."),
            new("scratchpad", T, "Scratchpad", "Autosaved notes in tabs", Glyphs.Note, EnergyProfile.Idle,
                "Floating tabbed notes with autosave, Markdown preview and export."),
            new("commandBar", T, "Command Bar", "Search apps, windows, files and more from one field", Glyphs.Command, EnergyProfile.Keyboard,
                "Launcher for apps, open windows, files, clipboard history, snippets and app menus, with calculator, units, emoji and scripts."),
            new("screenRecorder", T, "Screen recording", "Record with system audio and microphone", Glyphs.Record, EnergyProfile.Idle,
                "Windows.Graphics.Capture + Media Foundation encoder with separate system-audio and mic tracks, trim and GIF export."),
            new("wallpaper", T, "Wallpaper", "Change and rotate wallpapers", Glyphs.Wallpaper, EnergyProfile.Idle,
                "IDesktopWallpaper per monitor with folders and rotation."),
            new("killProcess", T, "Kill Process", "Stop unresponsive or chosen processes", Glyphs.Stop, EnergyProfile.Idle,
                "Lists processes with usage and ends the chosen one (gracefully first).", Beta: true),
            new("portManager", T, "Port manager", "Find processes listening on ports", Glyphs.Plug, EnergyProfile.Idle,
                "GetExtendedTcpTable/UdpTable listing listening ports with their processes and a stop button."),

            // Dynamic Island
            new("notch", I, "Dynamic Island", "Music, timers, notifications and controls at the top of the screen", Glyphs.Island, EnergyProfile.Mouse,
                "A simulated island at the top center of the screen that expands on hover, driven by the system media session and the extensions below."),
            new("notchCalendar", I, "Calendar", "Upcoming events in the island", Glyphs.Calendar, EnergyProfile.Periodic,
                "Upcoming events from ICS feeds or the Windows calendar.", Parent: "notch"),
            new("notchNotifications", I, "Notifications", "Notifications appear in the island", Glyphs.Bell, EnergyProfile.Idle,
                "Mirrors toast notifications into the island.", Parent: "notch"),
            new("notchGestures", I, "Gestures", "Swipe and scroll on the island", Glyphs.Gesture, EnergyProfile.Mouse,
                "Scroll for volume, swipe for tracks over the island.", Parent: "notch"),
            new("notchTimer", I, "Timer", "Timers and stopwatch in the island", Glyphs.Timer, EnergyProfile.Idle,
                "Timers and a stopwatch living in the island.", Parent: "notch"),
            new("notchAccessories", I, "Accessories", "Battery of connected devices", Glyphs.Battery, EnergyProfile.Periodic,
                "Battery levels of Bluetooth devices and low-battery alerts.", Parent: "notch"),
            new("notchLyrics", I, "Lyrics", "Synced lyrics for the playing song", Glyphs.Lyrics, EnergyProfile.Periodic,
                "Synced lyrics fetched from LRCLIB for the current media session.", Parent: "notch"),
            new("notchQueue", I, "Queue", "Up next from the player", Glyphs.List, EnergyProfile.Idle,
                "Shows the player's queue where the media session exposes it.", Parent: "notch"),
            new("notchLiveEqualizer", I, "Live equalizer", "Bars that follow the music", Glyphs.Waveform, EnergyProfile.Periodic,
                "Process loopback capture of the player feeds an FFT for the bars.", Parent: "notch"),
            new("notchDownloads", I, "Downloads", "Download progress in the island", Glyphs.Download, EnergyProfile.Idle,
                "Watches the Downloads folder for in-progress and finished downloads.", Parent: "notch"),
            new("notchAgents", I, "AI agents", "Follow Claude, Codex and other agents", Glyphs.Robot, EnergyProfile.Periodic,
                "Reads local Claude Code / Codex / OpenCode / Copilot session logs in the user profile for limits, tokens and live work.", Parent: "notch"),
            new("notchWatch", I, "Watch", "Get alerted when a screen area changes", Glyphs.Eye, EnergyProfile.Periodic,
                "Periodically captures a chosen screen area and alerts on change.", Parent: "notch"),
            new("notchMascot", I, "Companion", "A small companion in the island", Glyphs.Smile, EnergyProfile.Idle,
                "An animated companion that reacts to island events.", Parent: "notch"),

            // System monitor
            new("monitorCPU", Mo, "CPU", "Processor usage and temperature", Glyphs.Cpu, EnergyProfile.Periodic,
                "GetSystemTimes/per-core PDH counters and LibreHardwareMonitor temperatures."),
            new("monitorGPU", Mo, "GPU", "Graphics usage and temperature", Glyphs.Gpu, EnergyProfile.Periodic,
                "PDH 'GPU Engine' utilisation counters and LibreHardwareMonitor temperatures."),
            new("monitorMemory", Mo, "Memory", "Memory use and pressure", Glyphs.Memory, EnergyProfile.Periodic,
                "GlobalMemoryStatusEx with a pressure level derived from commit charge and available memory."),
            new("monitorNetwork", Mo, "Network", "Network speed and usage", Glyphs.Network, EnergyProfile.Periodic,
                "Interface byte counters for live speed and session totals, local IP and a speed test."),
            new("monitorDisk", Mo, "Disk", "Disk space and activity", Glyphs.HardDrive, EnergyProfile.Periodic,
                "Drive free space and PDH disk read/write rates."),
            new("monitorPower", Mo, "Power", "Battery, power and charging", Glyphs.Bolt, EnergyProfile.Periodic,
                "GetSystemPowerStatus and battery WMI (design/full capacity, cycle count, rate)."),
            new("connectedDevices", Mo, "Connected devices", "USB and Bluetooth devices", Glyphs.Usb, EnergyProfile.Idle,
                "Lists connected USB and Bluetooth devices with battery where available."),
            new("fanControl", Mo, "Fan Control", "Manual fan speeds or temperature curves", Glyphs.Fan, EnergyProfile.Periodic,
                "LibreHardwareMonitor fan controllers where the board exposes them (requires administrator).", Beta: true),

            // Fieldkit extras (not in Vorssaint; mostly ideas from its open pull requests)
            new("breakReminders", T, "Break reminders", "Reminders to rest your eyes and get up", Glyphs.Eye, EnergyProfile.Periodic,
                "Notifications every 20 minutes to look away for 20 seconds (20-20-20) and every hour to stand up and move; " +
                "a break you already took (away from the PC) resets them, and full-screen apps are left alone. Upstream PR #2524.",
                Extra: true),
            new("duplicateFinder", T, "Duplicate files", "Find copies of the same file and free up space", Glyphs.Copy, EnergyProfile.Idle,
                "Scans chosen folders for files with identical contents (size, then a hash of the first 64 KB, then SHA-256), " +
                "skipping hidden, system and online-only OneDrive files; extra copies go to the Recycle Bin. Upstream issue #3022.",
                Extra: true),
            new("diskUsage", T, "Disk usage", "See what takes up space on a drive", Glyphs.PieChart, EnergyProfile.Idle,
                "Measures a drive or folder (in parallel, without following links or opening online-only files) and shows it as a " +
                "sunburst with the largest items listed; anything can go to the Recycle Bin from there. Upstream issue #3071.",
                Extra: true),
            new("keyRemap", M, "Key remapping", "Make a key or shortcut act as another", Glyphs.Keyboard, EnergyProfile.Keyboard,
                "The keyboard hook swallows a remapped key or shortcut and sends its target, per app if wanted; plain keys keep " +
                "their modifiers, shortcuts lift the ones the target does not use. Upstream pull request #2742.",
                Extra: true),
            new("blockApps", T, "Block apps", "Keep distracting apps closed, always or on a schedule", Glyphs.Stop, EnergyProfile.Idle,
                "An out-of-process window-show hook closes listed apps as they open (no administrator rights, no system-wide " +
                "change); a schedule asks open ones to close normally when it starts. Upstream issue #2965.",
                Extra: true),
            new("browserPicker", T, "Browser picker", "Choose which browser opens each link", Glyphs.Link, EnergyProfile.Idle,
                "Registers as a browser for the current user (HKCU, no administrator rights); chosen as the default in Windows' " +
                "Default apps, each http(s) link shows a picker of installed browsers, or opens straight in the one a site rule names. " +
                "Upstream issue #2861.",
                Extra: true),
        ];
    }
}
