// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Features.Island.Views;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Prefs;

/// <summary>
/// Settings → Dynamic Island (port of upstream <c>NotchSettings</c>): layout, the content editor (sections,
/// their order and controls), the resting island, behaviour, then every installed extension's options.
/// </summary>
public static class IslandSettingsPage
{
    private static FrameworkElement Row(string title, string? caption, UIElement control)
    {
        var left = Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption));
        return Ui.Split(left, control);
    }

    private static bool Installed(string id) => FeatureCatalog.IsAvailable(id);

    public static FrameworkElement Build()
    {
        var open = Ui.Button("Open Dynamic Island", () => IslandHub.Open(), glyph: Glyphs.Island);
        var header = Ui.Group(null,
            Ui.ToggleRow("Enable Dynamic Island", "Your music, controls and everyday tools, together at the top of your screen.", IslandKeys.Enabled, IslandDefaults.Enabled),
            Ui.Split(Ui.Caption("Click the island at the top centre of the screen to open it. Drop files on it to keep them on its shelf."), open));

        return Ui.Page("Dynamic Island",
            "Your music, controls and everyday tools, together at the top of your screen. Optional. Turn it off to keep using the separate panels.",
            header,
            Layout(),
            ContentEditor(),
            Activity(),
            Behavior(),
            Music(),
            Installed("notchCalendar") ? Calendar() : null,
            Installed("notchTimer") ? Timer() : null,
            Installed("notchDownloads") ? DownloadsGroup() : null,
            Installed("notchWatch") ? WatchGroup() : null,
            Installed("notchAccessories") ? Ui.Group("Accessories",
                Ui.ToggleRow("Accessory alerts", "Show connected accessories and warn once when their battery falls to 20%.", IslandKeys.AccessoriesEnabled, true)) : null,
            Installed("notchNotifications") ? Ui.Group("Notifications",
                Ui.Caption("Windows lets only packaged apps read other apps' notifications (UserNotificationListener), so the island cannot mirror them yet. " +
                           "Fieldkit's own notices — timers, downloads, AI agents, Watch — appear in the island.")) : null,
            Installed("notchMascot") ? Companion() : null);
    }

    // ------------------------------------------------------------------ layout

    private static FrameworkElement Layout()
    {
        var custom = Ui.Stack(12,
            Ui.SliderRow("Width", IslandKeys.CustomWidth, 360, 600, IslandDefaults.CustomWidth, v => $"{v:0} px", 10),
            Ui.SliderRow("Maximum height", IslandKeys.CustomHeight, 260, 640, IslandDefaults.CustomHeight, v => $"{v:0} px", 10,
                "Controls stay compact. Longer lists use up to this height."));
        void Sync() => custom.Visibility = Settings.GetString(IslandKeys.Size, IslandDefaults.Size) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(custom, () => Settings.Observe(Sync, IslandKeys.Size));
        custom.Margin = new Thickness(0, 12, 0, 0);
        return Ui.Group("Layout", Ui.Stack(0,
            Ui.ChoiceRow("Size", IslandKeys.Size, [("compact", "Compact"), ("spacious", "Spacious"), ("custom", "Custom")], IslandDefaults.Size),
            custom));
    }

    // ------------------------------------------------------------------ content editor

    private static FrameworkElement ContentEditor()
    {
        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            var order = IslandLogic.Order(Settings.GetString(IslandKeys.ModuleOrder, string.Empty), IslandLogic.Sections.Select(s => s.Id));
            var hidden = IslandLogic.Csv(Settings.GetString(IslandKeys.HiddenModules, string.Empty)).ToHashSet();
            for (var i = 0; i < order.Count; i++)
            {
                var section = IslandLogic.Section(order[i])!;
                var index = i;
                var check = new CheckBox { IsChecked = !hidden.Contains(section.Id), VerticalAlignment = VerticalAlignment.Center };
                check.Click += (_, _) =>
                {
                    var h = IslandLogic.Csv(Settings.GetString(IslandKeys.HiddenModules, string.Empty)).ToHashSet();
                    if (check.IsChecked == true) h.Remove(section.Id); else h.Add(section.Id);
                    Settings.Set(IslandKeys.HiddenModules, string.Join(",", h.OrderBy(x => x)));
                };
                var tile = Ui.IconTile(section.Glyph, 28);
                var installed = section.Feature is null || Installed(section.Feature);
                var text = Ui.Stack(1, Ui.Text(section.Title, 13.5, FontWeights.Medium),
                    Ui.Caption(installed ? section.Summary : $"Install \"{FeatureCatalog.Get(section.Feature!).Title}\" in Features to use this section.", 12));
                var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => Move(order, index, -1));
                var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => Move(order, index, 1));
                up.IsEnabled = index > 0;
                down.IsEnabled = index < order.Count - 1;
                var left = Ui.Row(10, check, tile, text);
                var row = Ui.Split(left, Ui.Row(2, up, down));
                row.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 8, 0, 0);
                row.Opacity = installed ? 1 : 0.6;
                list.Children.Add(row);
            }
        }
        Rebuild();
        Ui.Track(list, () => Settings.Observe(Rebuild, IslandKeys.ModuleOrder, IslandKeys.HiddenModules));

        var hiddenControls = IslandLogic.Csv(Settings.GetString(IslandKeys.HiddenControls, IslandDefaults.HiddenControls));
        FrameworkElement ControlToggle(string id, string title)
        {
            var cb = new CheckBox { Content = Ui.Text(title, 13), IsChecked = !hiddenControls.Contains(id), Margin = new Thickness(0, 0, 18, 0) };
            cb.Click += (_, _) =>
            {
                var h = IslandLogic.Csv(Settings.GetString(IslandKeys.HiddenControls, IslandDefaults.HiddenControls)).ToHashSet();
                if (cb.IsChecked == true) h.Remove(id); else h.Add(id);
                Settings.Set(IslandKeys.HiddenControls, string.Join(",", h));
            };
            return cb;
        }
        var controls = new WrapPanel();
        controls.Children.Add(ControlToggle("music", "Now Playing"));
        controls.Children.Add(ControlToggle("volume", "Volume"));
        controls.Children.Add(ControlToggle("mixer", "Volume mixer"));

        return Ui.Group("Content",
            Ui.Caption("Use the arrows to reorder. Uncheck a section to hide it from the island."),
            list,
            Ui.Stack(6, Ui.Text("Controls", 13.5, FontWeights.Medium),
                Ui.Caption("The Controls section shows these, then the quick toggles other features add (Keep awake, Lock screen, …)."), controls));
    }

    private static void Move(List<string> order, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        Settings.Set(IslandKeys.ModuleOrder, string.Join(",", order));
    }

    // ------------------------------------------------------------------ activity / behaviour

    private static FrameworkElement Activity()
    {
        var restOptions = new List<(string, string)>
        {
            ("none", Installed("notchMascot") && Settings.GetBool(IslandKeys.MascotEnabled, false) ? "Companion" : "Nothing"),
            ("battery", "Battery"), ("music", "Music"),
        };
        if (Installed("notchAgents")) restOptions.Add(("agents", "AI limits"));
        var lowTint = Ui.Stack(12,
            Ui.ToggleRow("Turn red when low", "Shows the charge in red at or below this level while on battery.", IslandKeys.LowBatteryTint, false),
            Ui.SliderRow("Red level", IslandKeys.LowBatteryThreshold, 1, 99, 10, v => $"{v:0}%"));
        void Sync() => lowTint.Visibility = Settings.GetString(IslandKeys.IdleContent, IslandDefaults.IdleContent) == "battery" ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(lowTint, () => Settings.Observe(Sync, IslandKeys.IdleContent));
        lowTint.Margin = new Thickness(0, 12, 0, 0);
        return Ui.Group("Activity",
            Ui.Stack(0, Ui.ChoiceRow("At rest", IslandKeys.IdleContent, restOptions, IslandDefaults.IdleContent,
                "What the closed island shows when nothing else is going on. Music shows while a song plays."), lowTint),
            Ui.ToggleRow("New track", "Show the song's title briefly when the track changes.", IslandKeys.TrackChange, true));
    }

    private static FrameworkElement Behavior()
    {
        var displays = new List<(string, string)> { ("automatic", "Automatic"), ("pointer", "Follow pointer") };
        var index = 1;
        foreach (var m in WindowHelper.GetMonitors())
        {
            var name = WindowHelper.GetMonitorInfo(m.Handle).szDevice;
            displays.Add((name, $"Display {index++}{(m.Primary ? " (main)" : string.Empty)} · {m.Bounds.Width}×{m.Bounds.Height}"));
        }
        var reopen = new List<(string, string)> { ("last", "Last page") };
        reopen.AddRange(IslandLogic.Sections.Select(s => (s.Id, s.Title)));
        var reopenChoice = Ui.Choice("notch.reopenUi", reopen, Settings.GetBool(IslandKeys.ReturnHome, false) ? Settings.GetString(IslandKeys.HomeModule, IslandDefaults.HomeModule) : "last", v =>
        {
            Settings.Set(IslandKeys.ReturnHome, v != "last");
            if (v != "last") Settings.Set(IslandKeys.HomeModule, v);
        });
        reopenChoice.MinWidth = 160;
        var hoverRows = Ui.Stack(12,
            Ui.SliderRow("Activation time", IslandKeys.HoverDelay, 0.1, 1.0, IslandDefaults.HoverDelay, v => $"{v:0.00} s", 0.05,
                "Keep the pointer over the island for this long to open it."),
            Ui.SliderRow("Closing time", IslandKeys.CloseDelay, 0.1, 2.0, IslandDefaults.CloseDelay, v => $"{v:0.00} s", 0.05,
                "Wait this long after the pointer leaves the island before closing it."));
        void Sync() => hoverRows.Visibility = Settings.GetString(IslandKeys.Opening, IslandDefaults.Opening) == "hover" ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(hoverRows, () => Settings.Observe(Sync, IslandKeys.Opening));
        hoverRows.Margin = new Thickness(0, 12, 0, 0);
        return Ui.Group("Behavior",
            Ui.Stack(0, Ui.ChoiceRow("Opening", IslandKeys.Opening, [("click", "Click to open"), ("hover", "Expand on hover")], IslandDefaults.Opening), hoverRows),
            Installed("notchGestures")
                ? Ui.ToggleRow("Dynamic Island Gestures",
                    "Scroll down to open. Scroll up over the top row to close. Swipe left or right (or tilt the wheel) over the music to change tracks. Lists keep their own scrolling.",
                    IslandKeys.GesturesEnabled, true)
                : null,
            Row("When reopening", null, reopenChoice),
            Ui.ToggleRow("Open the visible activity", "When the closed island shows music, a timer or another activity, it opens on that page instead.",
                IslandKeys.OpensActivity, true),
            Ui.ChoiceRow("Display", IslandKeys.Display, displays, IslandDefaults.Display),
            Ui.ToggleRow("Hide content in full screen", "Hides the island while a full-screen app or presentation covers its display.", IslandKeys.HideInFullscreen, true),
            Ui.ToggleRow("Show in screenshots and videos", null, IslandKeys.ShowInCaptures, true),
            Ui.ShortcutRow("Open or close Dynamic Island", IslandKeys.ShortcutToggle, string.Empty));
    }

    // ------------------------------------------------------------------ extensions

    private static FrameworkElement Music()
    {
        return Ui.Group("Now Playing",
            Ui.ToggleRow("Show music while playing", null, IslandKeys.ShowPlayingMusic, true),
            Ui.ToggleRow("Automatically include videos and other apps", "Browsers and video players count as players too, after music apps.", IslandKeys.IncludeOtherPlayers, true),
            Installed("notchLyrics") ? Ui.ToggleRow("Show lyrics", "Follow the lyrics of the current song in the Dynamic Island.", IslandKeys.LyricsEnabled, true) : null,
            Installed("notchLyrics") ? Ui.ToggleRow("Find lyrics online",
                "While lyrics are open, send the song title, artist, album and duration to lrclib.net. Imported lyrics stay on this PC.", IslandKeys.LyricsOnline, false) : null,
            Installed("notchQueue") ? Ui.ToggleRow("Show upcoming songs", "Windows media sessions do not share a player's queue, so this panel can only say so for now.", IslandKeys.QueueEnabled, true) : null,
            Installed("notchLiveEqualizer") ? Ui.ToggleRow("Bars follow the music",
                "Listens to the PC's sound output while music plays to move the bars. Nothing is stored or sent. Without it, the bars keep their usual motion.",
                IslandKeys.LiveEqualizer, false) : null);
    }

    private static FrameworkElement Calendar()
    {
        var feeds = Ui.TextInput(IslandKeys.CalendarFeeds, string.Empty, "One iCalendar address or .ics file per line", multiline: true);
        return Ui.Group("Calendar",
            Ui.ToggleRow("Calendar", "Browse your upcoming appointments in the Dynamic Island.", IslandKeys.CalendarEnabled, true),
            Ui.Stack(6, Ui.Text("Calendars shown", 13.5, FontWeights.Medium),
                Ui.Caption("Windows keeps its own calendar private to its apps. Paste the secret iCal address of an Outlook or Google calendar (one per line), or the path of an .ics file. " +
                           "They are read every 15 minutes and events stay on this PC."),
                feeds),
            Ui.ToggleRow("Event countdown", "Show every timed event in the closed island during the hour before it starts. Titles may appear in screen captures.",
                IslandKeys.CalendarCountdown, false),
            Ui.ToggleRow("Time left in current event", "Show a timed event in progress in the closed island during the hour before it ends. Its title may appear in screen captures.",
                IslandKeys.CalendarTimeLeft, false),
            Ui.ToggleRow("Remind me to join online meetings", "A minute before a Teams, Zoom, Meet or Webex meeting starts, the island says so; click it to join. " +
                "Events with a meeting link get a Join button.", IslandKeys.CalendarMeetingNotice, true));
    }

    private static FrameworkElement Timer() => Ui.Group("Timer",
        Ui.ToggleRow("Timer", "Timers, a stopwatch and focused work sessions in the Dynamic Island.", IslandKeys.TimerEnabled, true),
        Ui.ToggleRow("Hide timer countdown", null, IslandKeys.TimerHideCountdown, false),
        Ui.ToggleRow("Play sound when time is up", null, IslandKeys.TimerSound, true),
        Ui.ShortcutRow("Start or pause the timer", IslandKeys.TimerShortcut, string.Empty));

    private static FrameworkElement DownloadsGroup()
    {
        var folder = Ui.Caption(DownloadsService.Folder);
        void Sync() => folder.Text = DownloadsService.Folder;
        Ui.Track(folder, () => Settings.Observe(Sync, IslandKeys.DownloadsFolder));
        var choose = Ui.Button("Choose Folder…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder where your browser saves downloads", InitialDirectory = DownloadsService.Folder };
            if (dialog.ShowDialog() == true) Settings.Set(IslandKeys.DownloadsFolder, dialog.FolderName);
        });
        var reset = Ui.Button("Use Downloads", () => Settings.Remove(IslandKeys.DownloadsFolder));
        return Ui.Group("Downloads",
            Ui.ToggleRow("Downloads", "See files arriving in a folder you choose, directly in the Dynamic Island.", IslandKeys.DownloadsEnabled, true),
            Ui.Split(Ui.Stack(2, Ui.Text("Folder", 13.5, FontWeights.Medium), folder,
                Ui.Caption("Only that folder is watched, not its subfolders.")), Ui.Row(8, reset, choose)));
    }

    private static FrameworkElement WatchGroup() => Ui.Group("Watch",
        Ui.ToggleRow("Watch",
            "Turn any part of the screen into a live activity in the Dynamic Island, with an alert when it changes, finishes or shows what you are waiting for. Text is read offline.",
            IslandKeys.WatchEnabled, true),
        Ui.ToggleRow("Play a sound", null, IslandKeys.WatchSound, true),
        Ui.ShortcutRow("Choose what to watch", IslandKeys.WatchShortcut, string.Empty));

    private static FrameworkElement Companion()
    {
        var stage = new Border { Height = 104, CornerRadius = new CornerRadius(12), Background = Brushes.Black };
        void Draw()
        {
            var mascot = new MascotView(64, animate: true);
            stage.Child = mascot;
            stage.ToolTip = "Click to see it react.";
        }
        Draw();
        stage.MouseLeftButtonUp += (_, _) =>
        {
            if (stage.Child is MascotView m)
                m.React(new[] { MascotReaction.Celebrate, MascotReaction.Love, MascotReaction.Surprised, MascotReaction.Groove, MascotReaction.Perk }[Random.Shared.Next(5)]);
        };
        Ui.Track(stage, () => Settings.ObservePrefix("notchMascot.", Draw));
        var colors = MascotLogic.Palettes.Select(p => (p.Id, p.Name)).ToList();
        return Ui.Group("Companion",
            Ui.ToggleRow("Companion",
                "A little friend who lives in the Dynamic Island. It rests there when nothing else is showing, and comes out over music and activities to react to what happens.",
                IslandKeys.MascotEnabled, false),
            stage,
            Ui.ChoiceRow("Style", IslandKeys.MascotStyle, [("minimal", "Minimal"), ("robot", "Robot")], "minimal"),
            Ui.ChoiceRow("Shape", IslandKeys.MascotShape, MascotLogic.Shapes, "ball"),
            Ui.ChoiceRow("Color", IslandKeys.MascotPalette, colors, "pearl"),
            Ui.ToggleRow("React to what happens", "It comes out to react to music, timers, downloads, AI agents, Watch and more.", IslandKeys.MascotReactions, true));
    }
}
