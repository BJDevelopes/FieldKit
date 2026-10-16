// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Island.Calendar;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Lyrics;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Features.Island.Watch;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Views;

/// <summary>How a page talks back to the island window.</summary>
public sealed class IslandNav
{
    public string Section { get; set; } = "controls";
    public double Width { get; init; } = 560;
    public double Budget { get; init; } = 264;
    public Action<string> Navigate { get; init; } = _ => { };
    public Action Collapse { get; init; } = () => { };
    /// <summary>Takes keyboard focus for the island (a page with a text box calls this when it is clicked).</summary>
    public Action Focus { get; init; } = () => { };
    /// <summary>The page asks for more height (lyrics/queue panels add 216).</summary>
    public Action Relayout { get; init; } = () => { };
    public bool LyricsOpen { get; set; }
    public bool QueueOpen { get; set; }
}

/// <summary>
/// The open island: a header with the section's title, the section tabs and settings, then the page
/// (port of upstream <c>NotchView</c> and its section views). Sections only appear while their feature runs.
/// </summary>
public static class IslandPages
{
    public const double HeaderHeight = 36;
    public const double Inset = 22;

    public static FrameworkElement Build(IslandContext ctx, IslandNav nav, IReadOnlyList<IslandSection> sections)
    {
        if (!sections.Any(s => s.Id == nav.Section)) nav.Section = sections.FirstOrDefault()?.Id ?? "controls";
        var section = IslandLogic.Section(nav.Section) ?? IslandLogic.Sections[0];

        var title = IslandUi.Text(section.Title, 15, FontWeights.SemiBold);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var s in sections)
        {
            var selected = s.Id == nav.Section;
            var tab = IslandUi.Button(s.Glyph, $"{s.Title}  (Ctrl+Alt+{s.Hotkey})", () => nav.Navigate(s.Id), 28, 13,
                selected ? IslandPalette.Text : IslandPalette.Secondary, selected ? IslandPalette.CardHover : null);
            tab.Margin = new Thickness(2, 0, 0, 0);
            tabs.Children.Add(tab);
        }
        var gear = IslandUi.Button(Glyphs.Settings, "Settings…", () => { nav.Collapse(); PanelHost.OpenSettings("island"); }, 28, 13, IslandPalette.Secondary);
        gear.Margin = new Thickness(8, 0, 0, 0);
        var close = IslandUi.Button(Glyphs.ChevronUp, "Collapse", nav.Collapse, 28, 12, IslandPalette.Secondary);
        tabs.Children.Add(gear);
        tabs.Children.Add(close);
        var header = new Grid { Height = HeaderHeight, Margin = new Thickness(Inset, 8, Inset - 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(tabs, 1);
        header.Children.Add(tabs);

        FrameworkElement page;
        try
        {
            page = nav.Section switch
            {
                "music" => Music(ctx, nav),
                "files" => Files(ctx, nav),
                "calendar" => CalendarPage(ctx, nav),
                "timer" => TimerPage(ctx, nav),
                "downloads" => DownloadsPage(ctx, nav),
                "agents" => AgentsPage.Build(ctx, nav),
                "watch" => WatchPage(ctx, nav),
                "notes" => NotesPage.Build(ctx, nav),
                _ => Controls(ctx, nav, sections),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Building island page {nav.Section} failed", ex);
            page = IslandUi.Text("This page could not be shown.", 13, brush: IslandPalette.Secondary);
        }
        page.Margin = new Thickness(Inset, 4, Inset, 16);
        return Ui.Stack(0, header, page);
    }

    // ------------------------------------------------------------------ controls

    private static FrameworkElement Controls(IslandContext ctx, IslandNav nav, IReadOnlyList<IslandSection> sections)
    {
        var hidden = IslandLogic.Csv(Settings.GetString(IslandKeys.HiddenControls, IslandDefaults.HiddenControls)).ToHashSet();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        if (!hidden.Contains("music")) top.Children.Add(MusicCard(ctx, nav));
        if (!hidden.Contains("volume"))
        {
            var levels = VolumeCard(ctx);
            levels.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(levels, 1);
            top.Children.Add(levels);
        }

        var tiles = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Tile(string glyph, string label, Action run, Func<bool>? on = null, Brush? accent = null)
        {
            var icon = IslandUi.Icon(glyph, 16);
            var plate = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = IslandPalette.Card, Child = icon, HorizontalAlignment = HorizontalAlignment.Center };
            var text = IslandUi.Text(label, 10.5, FontWeights.Medium, IslandPalette.Secondary, wrap: true);
            text.TextAlignment = TextAlignment.Center;
            text.MaxWidth = 72;
            text.Height = 28;
            var stack = Ui.Stack(4, plate, text);
            var tile = new Border { Width = 76, Margin = new Thickness(0, 0, 8, 4), Child = stack, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = label };
            void Refresh()
            {
                var active = on?.Invoke() ?? false;
                plate.Background = active ? accent ?? IslandPalette.Text : IslandPalette.Card;
                icon.Foreground = active ? Brushes.Black : IslandPalette.Text;
            }
            Refresh();
            tile.MouseEnter += (_, _) => plate.Opacity = 0.8;
            tile.MouseLeave += (_, _) => plate.Opacity = 1;
            tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; Ui.Guard(run); Refresh(); };
            tiles.Children.Add(tile);
        }
        foreach (var s in sections.Where(s => s.Id is "timer" or "calendar" or "downloads" or "agents" or "watch" or "notes"))
            Tile(s.Glyph, s.Title, () => nav.Navigate(s.Id));
        // Shortcuts other features expose (Keep awake, Lock screen, ...): the "your shortcuts" part of upstream's Controls.
        foreach (var t in FeatureRuntime.Registry.QuickToggles.OrderBy(t => t.Order).Take(8))
            Tile(t.Glyph, t.Title, t.Toggle, t.IsOn, t.Id.Contains("keepAwake", StringComparison.OrdinalIgnoreCase) ? IslandPalette.Yellow : null);
        if (FeatureRuntime.Registry.PanelSections.FirstOrDefault(p => p.Id == "mixer") is { } mixer && !hidden.Contains("mixer"))
            Tile(mixer.Glyph, mixer.Title, () => { nav.Collapse(); PanelHost.Show("mixer"); });
        return Ui.Stack(0, top, tiles);
    }

    private static FrameworkElement MusicCard(IslandContext ctx, IslandNav nav)
    {
        var art = new ContentControl();
        var title = IslandUi.Text(string.Empty, 13.5, FontWeights.SemiBold);
        var artist = IslandUi.Text(string.Empty, 12, brush: IslandPalette.Secondary);
        var play = IslandUi.Button(Glyphs.Play, "Play or pause", ctx.TogglePlay, 32, 16);
        var transport = IslandUi.Row(4,
            IslandUi.Button(Glyphs.Previous, "Previous track", ctx.Previous, 30, 13),
            play,
            IslandUi.Button(Glyphs.Next, "Next track", ctx.Next, 30, 13));
        var text = Ui.Stack(2, title, artist, transport);
        text.Margin = new Thickness(12, 0, 0, 0);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(art);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var card = IslandUi.Card(row);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) => { if (!e.Handled) nav.Navigate("music"); };
        string? shown = null;
        IslandUi.Live(card, TimeSpan.FromSeconds(1), () =>
        {
            var m = ctx.Music();
            title.Text = m?.Title is { Length: > 0 } t ? t : "Nothing playing";
            artist.Text = m?.Artist ?? "Your music and playback controls appear here.";
            ((TextBlock)play.Child).Text = m is { IsPlaying: true } ? Glyphs.Pause : Glyphs.Play;
            if (shown != m?.TrackKey || art.Content is null)
            {
                shown = m?.TrackKey;
                art.Content = IslandUi.Artwork(m?.Artwork, 64);
            }
        });
        return card;
    }

    private static FrameworkElement VolumeCard(IslandContext ctx)
    {
        var mute = IslandUi.Button(Glyphs.Volume, "Mute", ctx.ToggleMute, 26, 13);
        var percent = IslandUi.Text(string.Empty, 12, FontWeights.SemiBold, IslandPalette.Secondary, mono: true);
        var slider = new LevelSlider { Margin = new Thickness(0, 8, 0, 0) };
        slider.ValueChanged += v =>
        {
            ctx.SetVolume(v);
            percent.Text = AgentsFormatPercent(v);
        };
        var head = Ui.Split(IslandUi.Row(6, mute, IslandUi.Text("Volume", 13, FontWeights.SemiBold)), percent);
        var card = IslandUi.Card(Ui.Stack(0, head, slider));
        IslandUi.Live(card, TimeSpan.FromSeconds(1), () =>
        {
            if (ctx.Volume() is not { } v)
            {
                percent.Text = "—";
                return;
            }
            if (!slider.IsDragging) slider.Value = v.Level;
            percent.Text = AgentsFormatPercent(v.Level);
            ((TextBlock)mute.Child).Text = v.Muted || v.Level <= 0 ? Glyphs.Mute : Glyphs.Volume;
            ((TextBlock)mute.Child).Foreground = v.Muted ? IslandPalette.Red : IslandPalette.Text;
        });
        return card;
    }

    private static string AgentsFormatPercent(double f) => Agents.AgentFormat.Percent(f);

    // ------------------------------------------------------------------ music

    private static FrameworkElement Music(IslandContext ctx, IslandNav nav)
    {
        var roomy = nav.Budget >= 200;
        var size = roomy ? 148 : 120;
        var artHost = new ContentControl { Width = size, Height = size, Cursor = Cursors.Hand };
        var title = IslandUi.Text(string.Empty, roomy ? 20 : 16, FontWeights.SemiBold);
        var artist = IslandUi.Text(string.Empty, 13, brush: IslandPalette.Secondary);
        var source = new ContentControl();
        var bars = new EqualizerBars(ctx, 3, 2.5, 12);
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(title);
        var side = IslandUi.Row(8, source, bars);
        Grid.SetColumn(side, 1);
        titleRow.Children.Add(side);

        var elapsed = IslandUi.Text("0:00", 10, FontWeights.Medium, IslandPalette.Secondary, mono: true);
        var remaining = IslandUi.Text("-0:00", 10, FontWeights.Medium, IslandPalette.Secondary, mono: true);
        elapsed.MinWidth = remaining.MinWidth = 38;
        remaining.TextAlignment = TextAlignment.Right;
        var seek = new LevelSlider { Margin = new Thickness(6, 0, 6, 0) };
        var meter = IslandUi.Meter(0, 5, IslandPalette.Text);
        meter.Margin = new Thickness(6, 0, 6, 0);
        var timeline = new Grid { Height = 16 };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timeline.Children.Add(elapsed);
        Grid.SetColumn(seek, 1);
        Grid.SetColumn(meter, 1);
        Grid.SetColumn(remaining, 2);
        timeline.Children.Add(seek);
        timeline.Children.Add(meter);
        timeline.Children.Add(remaining);
        seek.Committed += v =>
        {
            if (ctx.Music() is { } m && m.Duration > TimeSpan.Zero) ctx.Seek(TimeSpan.FromSeconds(v * m.Duration.TotalSeconds));
        };

        var shuffle = IslandUi.Button("\uE8B1", "Shuffle", ctx.ToggleShuffle, roomy ? 44 : 36, roomy ? 16 : 14, IslandPalette.Secondary);
        var prev = IslandUi.Button(Glyphs.Previous, "Previous track", ctx.Previous, roomy ? 44 : 36, roomy ? 19 : 16);
        var play = IslandUi.Button(Glyphs.Play, "Play or pause", ctx.TogglePlay, roomy ? 44 : 36, roomy ? 26 : 22);
        var next = IslandUi.Button(Glyphs.Next, "Next track", ctx.Next, roomy ? 44 : 36, roomy ? 19 : 16);
        var spacer = new Border { Width = roomy ? 44 : 36 };
        var transport = IslandUi.Row(roomy ? 18 : 12, shuffle, prev, play, next, spacer);
        transport.HorizontalAlignment = HorizontalAlignment.Center;

        var details = Ui.Stack(6, titleRow, artist, timeline, transport);
        details.VerticalAlignment = VerticalAlignment.Center;
        details.Margin = new Thickness(roomy ? 20 : 16, 0, 0, 0);
        var player = new Grid { Height = size };
        player.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        player.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        player.Children.Add(artHost);
        Grid.SetColumn(details, 1);
        player.Children.Add(details);

        // Controls row: volume, then Lyrics and Up next toggles (mutually exclusive).
        var volume = new LevelSlider { Width = 140 };
        volume.ValueChanged += ctx.SetVolume;
        var mute = IslandUi.Button(Glyphs.Volume, "Mute", ctx.ToggleMute, 28, 13, IslandPalette.Secondary);
        var extras = new ContentControl();
        var lyricsButton = ctx.LyricsAvailable() ? IslandUi.Button(Glyphs.Lyrics, "Lyrics", () => { nav.LyricsOpen = !nav.LyricsOpen; nav.QueueOpen = false; nav.Relayout(); }, 28, 13,
            nav.LyricsOpen ? IslandPalette.Text : IslandPalette.Secondary, nav.LyricsOpen ? IslandPalette.CardHover : null) : null;
        var queueButton = ctx.QueueAvailable() ? IslandUi.Button(Glyphs.List, "Up next", () => { nav.QueueOpen = !nav.QueueOpen; nav.LyricsOpen = false; nav.Relayout(); }, 28, 13,
            nav.QueueOpen ? IslandPalette.Text : IslandPalette.Secondary, nav.QueueOpen ? IslandPalette.CardHover : null) : null;
        var controls = Ui.Split(IslandUi.Row(6, mute, volume), IslandUi.Row(4, lyricsButton, queueButton));
        controls.Height = 32;
        controls.Margin = new Thickness(0, 10, 0, 0);

        if (nav.LyricsOpen && ctx.LyricsAvailable()) extras.Content = LyricsPanel(ctx);
        else if (nav.QueueOpen && ctx.QueueAvailable()) extras.Content = QueuePanel();

        string? shownArt = null;
        string? shownSources = null;
        var root = Ui.Stack(0, player, controls, extras);
        IslandUi.Live(root, TimeSpan.FromMilliseconds(500), () =>
        {
            var m = ctx.Music();
            var now = ctx.Now();
            title.Text = m?.Title is { Length: > 0 } t ? t : m is null ? "Nothing playing" : "Now Playing";
            artist.Text = m is null ? "Your music and playback controls appear here." : m.Artist.Length > 0 ? m.Artist : m.Album;
            ((TextBlock)play.Child).Text = m is { IsPlaying: true } ? Glyphs.Pause : Glyphs.Play;
            bars.Visibility = m is { IsPlaying: true } ? Visibility.Visible : Visibility.Collapsed;
            prev.Visibility = m is null || m.CanGoPrevious ? Visibility.Visible : Visibility.Hidden;
            next.Visibility = m is null || m.CanGoNext ? Visibility.Visible : Visibility.Hidden;
            shuffle.Visibility = m?.Shuffle is null ? Visibility.Hidden : Visibility.Visible;
            ((TextBlock)shuffle.Child).Foreground = m?.Shuffle == true ? IslandPalette.Pink : IslandPalette.Secondary;
            var position = m?.PositionAt(now) ?? TimeSpan.Zero;
            var duration = m?.Duration ?? TimeSpan.Zero;
            timeline.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
            elapsed.Text = TrackTime.Format(position);
            remaining.Text = TrackTime.Format(position - duration);
            var canSeek = m is { CanSeek: true } && duration > TimeSpan.Zero && !ctx.IsPreview;
            seek.Visibility = canSeek ? Visibility.Visible : Visibility.Collapsed;
            meter.Visibility = canSeek ? Visibility.Collapsed : Visibility.Visible;
            if (!seek.IsDragging) seek.Value = TrackTime.Progress(position, duration);
            IslandUi.SetMeter(meter, TrackTime.Progress(position, duration));
            if (m?.TrackKey != shownArt || artHost.Content is null)
            {
                shownArt = m?.TrackKey;
                var art = IslandUi.Artwork(m?.Artwork, size);
                if (m is { IsPlaying: false }) art.RenderTransform = new ScaleTransform(0.94, 0.94, size / 2, size / 2);
                artHost.Content = art;
            }
            var players = ctx.Players();
            var key = string.Join("|", players.Select(p => p.SourceApp)) + ctx.ManualPlayer();
            if (key != shownSources)
            {
                shownSources = key;
                source.Content = players.Count > 1 || ctx.ManualPlayer() is not null ? SourcePicker(ctx, players) : null;
            }
            if (ctx.Volume() is { } v)
            {
                if (!volume.IsDragging) volume.Value = v.Level;
                ((TextBlock)mute.Child).Text = v.Muted ? Glyphs.Mute : Glyphs.Volume;
            }
        });
        return root;
    }

    /// <summary>An island-styled menu button: current label and a chevron, opening a context menu.</summary>
    private static FrameworkElement Menu(string label, IReadOnlyList<(string Title, bool Checked, Action Run)> items)
    {
        var b = new Border
        {
            Child = IslandUi.Row(6, IslandUi.Text(label, 12.5, FontWeights.SemiBold), IslandUi.Icon(Glyphs.ChevronDown, 9, IslandPalette.Secondary)),
            Cursor = Cursors.Hand, Background = IslandPalette.Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 5),
        };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu();
            foreach (var (title, isChecked, run) in items)
            {
                var item = new MenuItem { Header = title, IsChecked = isChecked };
                item.Click += (_, _) => Ui.Guard(run);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        };
        return b;
    }

    private static FrameworkElement SourcePicker(IslandContext ctx, IReadOnlyList<MediaSnapshot> players)
    {
        var label = IslandUi.Text(ctx.ManualPlayer() is { } m ? MediaSelection.FriendlyName(m) : "Automatic", 10, FontWeights.Medium, IslandPalette.Secondary);
        label.MaxWidth = 110;
        var b = new Border { Child = IslandUi.Row(3, label, IslandUi.Icon(Glyphs.ChevronDown, 8, IslandPalette.Secondary)), Cursor = Cursors.Hand, ToolTip = "Playback source", Background = Brushes.Transparent };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu();
            var auto = new MenuItem { Header = "Automatic", IsChecked = ctx.ManualPlayer() is null };
            auto.Click += (_, _) => ctx.ChoosePlayer(null);
            menu.Items.Add(auto);
            foreach (var p in players)
            {
                var item = new MenuItem { Header = MediaSelection.FriendlyName(p.SourceApp), IsChecked = ctx.ManualPlayer() == p.SourceApp };
                item.Click += (_, _) => ctx.ChoosePlayer(p.SourceApp);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        };
        return b;
    }

    private static FrameworkElement LyricsPanel(IslandContext ctx)
    {
        var panel = new StackPanel();
        var scroll = new ScrollViewer { Content = panel, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 10, 0, 0) };
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.13));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.87));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        scroll.OpacityMask = fade;
        var offsetText = IslandUi.Text("+0.00", 11, FontWeights.Medium, IslandPalette.Secondary, mono: true);
        var footer = IslandUi.Row(6,
            IslandUi.Button(Glyphs.Folder, "Import lyrics…", () => ImportLyrics(ctx), 26, 12, IslandPalette.Secondary),
            IslandUi.Text("Timing", 11, brush: IslandPalette.Secondary),
            IslandUi.Button(Glyphs.Remove, "Earlier", () => { if (ctx.Lyrics() is { } l) l.Offset = LrcParser.StepOffset(l.Offset, -1); }, 24, 10, IslandPalette.Secondary),
            offsetText,
            IslandUi.Button(Glyphs.Add, "Later", () => { if (ctx.Lyrics() is { } l) l.Offset = LrcParser.StepOffset(l.Offset, 1); }, 24, 10, IslandPalette.Secondary));
        var root = Ui.Stack(4, scroll, footer);
        string? builtFor = null;
        var lineBlocks = new List<TextBlock>();
        var lastIndex = -2;
        IslandUi.Live(root, TimeSpan.FromMilliseconds(200), () =>
        {
            var service = ctx.Lyrics();
            var music = ctx.Music();
            if (service is null) return;
            if (!ctx.IsPreview) service.Follow(music);
            offsetText.Text = (service.Offset >= 0 ? "+" : "") + service.Offset.ToString("0.00");
            var key = service.TrackKey + "|" + service.Status + "|" + service.Loading + "|" + (service.Payload?.GetHashCode() ?? 0);
            if (key != builtFor)
            {
                builtFor = key;
                panel.Children.Clear();
                lineBlocks.Clear();
                lastIndex = -2;
                var payload = service.Payload;
                if (service.Loading) panel.Children.Add(IslandUi.Text("Loading lyrics…", 14, FontWeights.Medium, IslandPalette.Secondary));
                else if (payload is null || payload.IsEmpty)
                {
                    var message = service.Status switch
                    {
                        LyricsLookupStatus.Failed => "Could not load the lyrics.",
                        LyricsLookupStatus.Missing => "No matching lyrics for this recording.",
                        _ => LyricsService.Online ? "No matching lyrics for this recording." : "Turn on \"Find lyrics online\" in Settings to look lyrics up on lrclib.net.",
                    };
                    panel.Children.Add(IslandUi.Text(message, 14, FontWeights.Medium, IslandPalette.Secondary, wrap: true));
                    if (LyricsService.Online)
                    {
                        var again = IslandUi.Pill("Try again", () => service.TryAgain(ctx.Music()));
                        again.Margin = new Thickness(0, 8, 0, 0);
                        panel.Children.Add(again);
                    }
                }
                else if (payload.Instrumental) panel.Children.Add(IslandUi.Text("Instrumental", 17, FontWeights.Bold));
                else if (payload.IsSynced)
                {
                    foreach (var line in payload.Lines)
                    {
                        var tb = IslandUi.Text(line.Text.Length == 0 ? "♪" : line.Text, 17, FontWeights.Bold, IslandPalette.Tertiary, wrap: true);
                        tb.Margin = new Thickness(0, 0, 0, 12);
                        lineBlocks.Add(tb);
                        panel.Children.Add(tb);
                    }
                }
                else
                {
                    panel.Children.Add(IslandUi.Text(payload.Plain ?? string.Empty, 15, FontWeights.Medium, wrap: true));
                }
            }
            if (lineBlocks.Count > 0 && service.Payload is { } p && music is not null)
            {
                var index = LrcParser.CurrentIndex(p.Lines, music.PositionAt(ctx.Now()).TotalSeconds, service.Offset);
                if (index != lastIndex)
                {
                    lastIndex = index;
                    for (var i = 0; i < lineBlocks.Count; i++) lineBlocks[i].Foreground = i == index ? IslandPalette.Text : IslandPalette.Tertiary;
                    if (index >= 0)
                    {
                        lineBlocks[index].UpdateLayout();
                        var y = lineBlocks[index].TranslatePoint(new Point(0, 0), panel).Y;
                        scroll.ScrollToVerticalOffset(Math.Max(0, y - scroll.ActualHeight / 2 + 12));
                    }
                }
            }
        });
        root.Unloaded += (_, _) => ctx.Lyrics()?.Cancel();
        return root;
    }

    private static void ImportLyrics(IslandContext ctx)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import lyrics…", Filter = "Lyrics (*.lrc;*.txt)|*.lrc;*.txt|All files|*.*" };
        if (dialog.ShowDialog() == true) ctx.Lyrics()?.Import(dialog.FileName);
    }

    private static FrameworkElement QueuePanel()
    {
        var text = IslandUi.Text("This player does not share its upcoming songs.", 14, FontWeights.Medium, IslandPalette.Secondary, wrap: true);
        var caption = IslandUi.Text("Windows media sessions do not expose a player's queue.", 12, brush: IslandPalette.Tertiary, wrap: true);
        var stack = Ui.Stack(6, IslandUi.Text("Up next", 13, FontWeights.SemiBold), text, caption);
        stack.Margin = new Thickness(0, 12, 0, 0);
        stack.Height = 190;
        return stack;
    }

    // ------------------------------------------------------------------ files

    private static FrameworkElement Files(IslandContext ctx, IslandNav nav)
    {
        var items = ctx.Shelf();
        if (items.Count == 0)
        {
            var empty = Ui.Stack(8, IslandUi.Icon(Glyphs.Tray, 30, IslandPalette.Secondary), IslandUi.Text("Drop files here", 14, FontWeights.SemiBold, IslandPalette.Secondary),
                IslandUi.Text("Files you drop on the island stay here, ready to drag out or open.", 12, brush: IslandPalette.Tertiary));
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 30, 0, 0);
            foreach (var c in empty.Children.OfType<FrameworkElement>()) c.HorizontalAlignment = HorizontalAlignment.Center;
            return empty;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        ShelfItem? selected = null;
        var tilesByItem = new Dictionary<ShelfItem, Border>();
        foreach (var item in items)
        {
            var isDir = Directory.Exists(item.Path);
            var ext = Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
            var icon = IslandUi.Icon(isDir ? Glyphs.Folder : Glyphs.Document, 26, IslandPalette.Secondary);
            var badge = IslandUi.Text(ext, 9, FontWeights.SemiBold, IslandPalette.Tertiary);
            badge.HorizontalAlignment = HorizontalAlignment.Center;
            var name = IslandUi.Text(item.Name, 11, FontWeights.Medium, wrap: true);
            name.TextAlignment = TextAlignment.Center;
            name.MaxWidth = 84;
            name.MaxHeight = 30;
            var tile = new Border
            {
                Width = 92,
                Padding = new Thickness(4, 10, 4, 8),
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(12),
                Background = IslandPalette.Card,
                Child = Ui.Stack(4, icon, badge, name),
                ToolTip = item.Path,
                Cursor = Cursors.Hand,
            };
            tilesByItem[item] = tile;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (e.ClickCount == 2) { Open(item.Path); return; }
                selected = item;
                foreach (var (k, t) in tilesByItem) t.Background = k == item ? IslandPalette.CardHover : IslandPalette.Card;
                // Drag out to use the file elsewhere.
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            };
            row.Children.Add(tile);
        }
        var scroll = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var hint = IslandUi.Text("Click to select. Drag out to use or double-click to open.", 11, brush: IslandPalette.Tertiary);
        var buttons = IslandUi.Row(4,
            IslandUi.Button(Glyphs.Folder, "Show", () => { if (selected is { } s) Reveal(s.Path); }, 28, 13, IslandPalette.Secondary),
            IslandUi.Button(Glyphs.Remove, "Remove selected", () => { if (selected is { } s) { ctx.RemoveFromShelf(s); nav.Relayout(); } }, 28, 13, IslandPalette.Secondary),
            IslandUi.Button(Glyphs.Delete, "Clear all", () => { ctx.ClearShelf(); nav.Relayout(); }, 28, 13, IslandPalette.Secondary));
        var footer = Ui.Split(hint, buttons);
        footer.Margin = new Thickness(0, 8, 0, 0);
        return Ui.Stack(0, scroll, footer);
    }

    /// <summary>Opens a local file the user put on the shelf or that arrived in the watched folder.</summary>
    public static void Open(string path) => IslandLinks.OpenLocal(path);

    public static void Reveal(string path) => IslandLinks.RevealLocal(path);

    // ------------------------------------------------------------------ calendar

    private static FrameworkElement CalendarPage(IslandContext ctx, IslandNav nav)
    {
        var events = ctx.Events();
        if (events is null || CalendarService.Feeds().Count == 0 && !ctx.IsPreview)
        {
            var setup = Ui.Stack(10,
                IslandUi.Icon(Glyphs.Calendar, 28, IslandPalette.Secondary),
                IslandUi.Text("Add a calendar to see your upcoming appointments.", 14, FontWeights.SemiBold, wrap: true),
                IslandUi.Text("Windows keeps its calendar to its own apps, so the island reads iCalendar feeds: the secret iCal address Outlook or Google gives you, or an .ics file.", 12, brush: IslandPalette.Secondary, wrap: true),
                IslandUi.Pill("Open Settings", () => { nav.Collapse(); PanelHost.OpenSettings("island"); }, IslandPalette.Text));
            return setup;
        }
        var now = ctx.Now().LocalDateTime;
        var agenda = CalendarLogic.Agenda(events, now);
        var next = CalendarLogic.Next(events, now);
        var list = new StackPanel();
        if (agenda.Count == 0) list.Children.Add(IslandUi.Text("No upcoming appointments", 13, FontWeights.Medium, IslandPalette.Secondary));
        foreach (var (day, dayEvents) in agenda)
        {
            var label = IslandUi.Text((day == now.Date ? "Today" : day.ToString("ddd")) + " " + day.ToString("d MMM"), 11, FontWeights.Medium, IslandPalette.Secondary);
            label.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 8, 0, 4);
            list.Children.Add(label);
            foreach (var e in dayEvents) list.Children.Add(EventCard(e, now, e == next));
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = nav.Budget, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var header = IslandUi.Text("Next 7 days", 12, FontWeights.SemiBold, IslandPalette.Secondary);
        header.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Stack(0, header, scroll);
    }

    private static FrameworkElement EventCard(CalendarEvent e, DateTime now, bool upNext)
    {
        var ongoing = e.IsOngoing(now) && !e.IsAllDay;
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = IslandPalette.Pink, Margin = new Thickness(0, 0, 10, 0) };
        var title = IslandUi.Text(string.IsNullOrWhiteSpace(e.Title) ? "Untitled event" : e.Title, 13, FontWeights.SemiBold);
        if (e.End <= now) title.Opacity = 0.65;
        var meta = CalendarLogic.TimeLine(e) + (e.CalendarName is { Length: > 0 } c ? "  ·  " + c : string.Empty) + (e.Location is { Length: > 0 } l ? "  ·  " + l : string.Empty);
        var text = Ui.Stack(2,
            ongoing ? IslandUi.Text("Happening now", 9, FontWeights.SemiBold, IslandPalette.Mint) : upNext ? IslandUi.Text("Up next", 9, FontWeights.SemiBold, IslandPalette.Secondary) : null,
            title,
            IslandUi.Text(meta, 11, brush: IslandPalette.Secondary));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(bar);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        // Online meetings (upstream #2580): Join from 15 minutes before until the end; only known meeting services' https links.
        if (e.Meeting is { } meeting && e.End > now)
        {
            FrameworkElement side = MeetingLinks.IsJoinable(e, now)
                ? IslandUi.Pill("Join", () => IslandLinks.OpenWeb(meeting.Url), IslandPalette.Text, IslandPalette.Pink, Glyphs.Video)
                : IslandUi.Icon(Glyphs.Video, 13, IslandPalette.Secondary);
            side.ToolTip = $"{meeting.Provider} meeting";
            side.VerticalAlignment = VerticalAlignment.Center;
            side.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(side, 2);
            g.Children.Add(side);
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb((byte)(ongoing ? 51 : 26), 255, 55, 95)),
            Padding = new Thickness(8, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = g,
        };
        // Feed URLs are outside data: only http, https and mailto links open (never files, UNC paths or protocol handlers).
        if (IslandLinks.SafeWebUri(e.Url) is { } url)
        {
            card.Cursor = Cursors.Hand;
            card.ToolTip = url.AbsoluteUri;
            card.MouseLeftButtonUp += (_, _) => IslandLinks.OpenWeb(url.AbsoluteUri);
        }
        return card;
    }

    // ------------------------------------------------------------------ timer

    private static FrameworkElement TimerPage(IslandContext ctx, IslandNav nav)
    {
        var host = new ContentControl();
        var hadSession = (bool?)null;
        IslandUi.Live(host, TimeSpan.FromMilliseconds(250), () =>
        {
            var has = ctx.Timer() is { HasSession: true };
            if (has == hadSession) return;
            hadSession = has;
            host.Content = has ? TimerActive(ctx) : TimerSetup(ctx, nav);
        });
        return host;
    }

    private static FrameworkElement TimerSetup(IslandContext ctx, IslandNav nav)
    {
        var mode = Settings.GetString(IslandKeys.TimerMode, "timer");
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("timer", "Timer"), ("pomodoro", "Pomodoro"), ("stopwatch", "Stopwatch") })
        {
            var selected = id == mode;
            var text = IslandUi.Text(label, 12, FontWeights.Medium, selected ? IslandPalette.Text : IslandPalette.Secondary);
            var underline = new Border { Height = 2, Background = selected ? IslandPalette.Orange : Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0) };
            var b = new Border { Child = Ui.Stack(0, text, underline), Margin = new Thickness(0, 0, 18, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) => { Settings.Set(IslandKeys.TimerMode, id); nav.Relayout(); };
            modes.Children.Add(b);
        }
        var start = IslandUi.Pill("Start", () => ctx.TimerService()?.Start(), IslandPalette.Orange, new SolidColorBrush(Color.FromArgb(46, 255, 159, 10)));
        var top = Ui.Split(modes, start);

        var minutes = mode == "pomodoro" ? Settings.GetInt(IslandKeys.PomodoroFocus, 25) : Settings.GetInt(IslandKeys.TimerMinutes, IslandDefaults.TimerMinutes);
        var clock = IslandUi.Text(mode == "stopwatch" ? "00:00" : TimerFormat.Clock(minutes * 60), 30, FontWeights.Light, IslandPalette.Orange, mono: true);
        clock.MinWidth = 96;
        clock.TextAlignment = TextAlignment.Right;
        FrameworkElement middle;
        if (mode == "stopwatch")
        {
            middle = Ui.Split(IslandUi.Text("Counts up from zero until you stop it.", 13, brush: IslandPalette.Secondary), clock);
        }
        else
        {
            var ruler = new TimerRuler(minutes);
            ruler.ValueChanged += v =>
            {
                Settings.Set(mode == "pomodoro" ? IslandKeys.PomodoroFocus : IslandKeys.TimerMinutes, v);
                clock.Text = TimerFormat.Clock(v * 60);
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(ruler);
            clock.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(clock, 1);
            g.Children.Add(clock);
            middle = g;
        }
        middle.Margin = new Thickness(0, 14, 0, 0);
        UIElement? pomodoro = null;
        if (mode == "pomodoro")
        {
            FrameworkElement Readout(string title, string key, int def, int min, int max, bool minutesUnit)
            {
                var value = IslandUi.Text(string.Empty, 12, FontWeights.SemiBold, IslandPalette.Orange);
                void Refresh() => value.Text = Settings.GetInt(key, def) + (minutesUnit ? "m" : string.Empty) + " ▾";
                Refresh();
                var b = new Border { Child = Ui.Stack(1, IslandUi.Text(title, 9.5, brush: IslandPalette.Tertiary), value), Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 16, 0) };
                b.MouseLeftButtonUp += (_, _) =>
                {
                    var menu = new ContextMenu();
                    for (var v = min; v <= max; v++)
                    {
                        var val = v;
                        var item = new MenuItem { Header = v + (minutesUnit ? "m" : string.Empty), IsChecked = Settings.GetInt(key, def) == v };
                        item.Click += (_, _) => { Settings.Set(key, val); Refresh(); };
                        menu.Items.Add(item);
                    }
                    menu.PlacementTarget = b;
                    menu.IsOpen = true;
                };
                return b;
            }
            var row = IslandUi.Row(0,
                Readout("Short break", IslandKeys.PomodoroShortBreak, 5, 1, 60, true),
                Readout("Long break", IslandKeys.PomodoroLongBreak, 15, 1, 60, true),
                Readout("Sessions between long breaks", IslandKeys.PomodoroInterval, 4, 1, 24, false),
                Readout("Total sessions", IslandKeys.PomodoroTotal, 4, 1, 24, false));
            row.Margin = new Thickness(0, 12, 0, 0);
            row.ToolTip = "Start each phase when you are ready. The cycle ends after the last focus session.";
            pomodoro = row;
        }
        return Ui.Stack(0, top, middle, pomodoro);
    }

    private static FrameworkElement TimerActive(IslandContext ctx)
    {
        var primary = IslandUi.Button(Glyphs.Pause, "Pause", () =>
        {
            var service = ctx.TimerService();
            if (service is null) return;
            if (service.Session.CanStartNext) service.StartNext();
            else service.PauseOrResume();
        }, 52, 22, IslandPalette.Orange, new SolidColorBrush(Color.FromArgb(72, 255, 159, 10)));
        var secondary = IslandUi.Button(Glyphs.Close, "Cancel", () => ctx.TimerService()?.Cancel(), 52, 20, IslandPalette.Text, new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)));
        var title = IslandUi.Text(string.Empty, 17, FontWeights.Medium);
        var clock = IslandUi.Text(string.Empty, 56, FontWeights.Light, IslandPalette.Orange, mono: true);
        var session = IslandUi.Text(string.Empty, 12, brush: IslandPalette.Secondary);
        var right = Ui.Stack(0, title, clock, session);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var buttons = IslandUi.Row(12, primary, secondary);
        var g = Ui.Split(buttons, right);
        g.Margin = new Thickness(0, 8, 0, 0);
        IslandUi.Live(g, TimeSpan.FromMilliseconds(250), () =>
        {
            if (ctx.Timer() is not { HasSession: true } s) return;
            var now = ctx.TimerClock();
            title.Text = s.Completed ? (s.CycleFinished ? "Pomodoro complete" : "Time is up") : TimerFormat.PhaseName(s.Phase);
            clock.Text = s.CountsUp ? TimerFormat.Stopwatch(s.Reading(now)) : TimerFormat.Clock(s.Reading(now));
            session.Text = s.Mode == TimerMode.Pomodoro ? $"Session {s.SessionNumber} of {s.Configuration.TotalSessions}" : string.Empty;
            session.Visibility = s.Mode == TimerMode.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
            var icon = (TextBlock)primary.Child;
            if (s.CanStartNext)
            {
                icon.Text = Glyphs.Play;
                primary.ToolTip = "Start " + TimerFormat.PhaseName(s.NextPhase);
            }
            else
            {
                icon.Text = s.IsPaused ? Glyphs.Play : Glyphs.Pause;
                primary.ToolTip = s.IsPaused ? "Resume" : "Pause";
                primary.Visibility = s.Completed ? Visibility.Collapsed : Visibility.Visible;
            }
            if (s.CanStartNext) primary.Visibility = Visibility.Visible;
            ((TextBlock)secondary.Child).Text = s.Completed ? Glyphs.Check : Glyphs.Close;
            secondary.ToolTip = s.Completed ? "Done" : "Cancel";
        });
        return g;
    }

    // ------------------------------------------------------------------ downloads

    private static FrameworkElement DownloadsPage(IslandContext ctx, IslandNav nav)
    {
        var folder = ctx.DownloadsFolder();
        var header = Ui.Split(
            IslandUi.Row(6, IslandUi.Icon(Glyphs.Folder, 13, IslandPalette.Secondary), IslandUi.Text(Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder, 12.5, FontWeights.SemiBold)),
            IslandUi.Button(Glyphs.Settings, "Choose Folder…", () => { nav.Collapse(); PanelHost.OpenSettings("island"); }, 26, 12, IslandPalette.Secondary));
        header.Height = 24;
        if (ctx.DownloadsUnavailable())
            return Ui.Stack(8, header, IslandUi.Text("This folder is unavailable. Choose it again to restore access.", 13, brush: IslandPalette.Orange, wrap: true));
        var items = ctx.Downloads() ?? [];
        if (items.Count == 0)
            return Ui.Stack(8, header, IslandUi.Row(8, IslandUi.Icon(Glyphs.Download, 16, IslandPalette.Secondary), IslandUi.Text("No files in this folder", 13, brush: IslandPalette.Secondary)));
        var list = new StackPanel();
        foreach (var item in items.Take(30))
        {
            FrameworkElement card;
            if (item.Completed)
            {
                var open = new Border
                {
                    Child = IslandUi.Row(8, IslandUi.Icon(Glyphs.Check, 13, IslandPalette.Green), IslandUi.Text(item.Name, 12.5, FontWeights.SemiBold)),
                    Cursor = Cursors.Hand, ToolTip = "Open", Background = Brushes.Transparent,
                };
                open.MouseLeftButtonUp += (_, _) => Open(item.Path);
                var reveal = IslandUi.Button(Glyphs.Folder, "Show", () => Reveal(item.Path), 26, 12, IslandPalette.Secondary);
                var shelf = IslandUi.Button(Glyphs.Tray, "Files", () => ctx.AddToShelf([item.Path]), 26, 12, IslandPalette.Secondary);
                card = Ui.Stack(2, Ui.Split(open, IslandUi.Row(2, shelf, reveal)), IslandUi.Text("Saved · " + DownloadTracker.Bytes(item.ReceivedBytes), 10.5, brush: IslandPalette.Tertiary));
            }
            else
            {
                var head = Ui.Split(IslandUi.Row(8, IslandUi.Icon(Glyphs.Download, 13), IslandUi.Text(item.Name, 12.5, FontWeights.SemiBold)),
                    item.Fraction is { } f ? IslandUi.Text(Agents.AgentFormat.Percent(f), 11, FontWeights.Medium, IslandPalette.Secondary, mono: true) : null);
                var footer = Ui.Split(IslandUi.Text(item.Active ? "Downloading" : string.Empty, 10.5, brush: IslandPalette.Tertiary),
                    IslandUi.Text(item.ReceivedBytes > 0 ? DownloadTracker.Bytes(item.ReceivedBytes) : "Total size unavailable", 10.5, brush: IslandPalette.Tertiary));
                var meter = IslandUi.Meter(item.Fraction ?? 0.35, 4, item.Fraction is null ? IslandPalette.Tertiary : IslandPalette.Text);
                meter.Margin = new Thickness(0, 6, 0, 4);
                card = Ui.Stack(0, head, meter, footer);
            }
            var border = IslandUi.Card(card, new Thickness(12, 8, 12, 8));
            border.Margin = new Thickness(0, 0, 0, 8);
            list.Children.Add(border);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = nav.Budget - 30, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        scroll.Margin = new Thickness(0, 8, 0, 0);
        return Ui.Stack(0, header, scroll);
    }

    // ------------------------------------------------------------------ watch

    private static FrameworkElement WatchPage(IslandContext ctx, IslandNav nav)
    {
        var host = new ContentControl();
        string? builtState = null;
        IslandUi.Live(host, TimeSpan.FromSeconds(1), () =>
        {
            var w = ctx.Watch();
            var state = w is null ? "none" : $"{w.Active}|{w.Outcome?.Text}|{w.Area}|{w.Headline}";
            if (state == builtState) return;
            builtState = state;
            host.Content = WatchContent(ctx, nav, w);
        });
        return host;
    }

    private static FrameworkElement WatchContent(IslandContext ctx, IslandNav nav, WatchView? w)
    {
        var service = ctx.WatchService();
        if (w is null || (!w.Active && w.Outcome is null && w.Preview is null))
        {
            var setup = Ui.Stack(10,
                IslandUi.Icon(Glyphs.Eye, 28, IslandPalette.Purple),
                IslandUi.Text("Choose any part of a window, like a progress bar, a build status or a score. The island reads it for you and tells you when it changes.", 13, brush: IslandPalette.Secondary, wrap: true),
                IslandUi.Pill("Choose Area", () => { nav.Collapse(); service?.ChooseArea(); }, IslandPalette.Text));
            return setup;
        }
        Border preview = new() { Height = Math.Min(110, nav.Budget - 110), CornerRadius = new CornerRadius(10), Background = IslandPalette.Card, ClipToBounds = true };
        if (w.Preview is not null) preview.Background = new ImageBrush(w.Preview) { Stretch = Stretch.Uniform };
        var headline = w.Outcome is { } o
            ? IslandUi.Row(6, IslandUi.Icon(o is WatchOutcome.Closed ? Glyphs.Close : Glyphs.Check, 13, o is WatchOutcome.Closed ? IslandPalette.Secondary : IslandPalette.Green), IslandUi.Text(o.Text, 15, FontWeights.SemiBold))
            : (UIElement)IslandUi.Text(w.Headline.Length > 0 ? w.Headline : "No text", 15, FontWeights.SemiBold);
        var conditions = Menu(WatchService.ConditionLabel(WatchService.Condition),
            new[] { WatchCondition.Changes, WatchCondition.Settles, WatchCondition.Contains, WatchCondition.Reaches }
                .Select(c => (WatchService.ConditionLabel(c), c == WatchService.Condition, (Action)(() =>
                {
                    Settings.Set(IslandKeys.WatchCondition, c.ToString().ToLowerInvariant());
                    service?.ResetRule();
                    nav.Relayout();
                }))).ToList());
        UIElement? field = null;
        if (WatchService.Condition is WatchCondition.Contains or WatchCondition.Reaches)
        {
            var box = new TextBox
            {
                MinWidth = 140, MaxWidth = 180, ToolTip = WatchService.Condition == WatchCondition.Contains ? "Text, like Done" : "Number, like 100",
                Background = IslandPalette.Card, Foreground = IslandPalette.Text, CaretBrush = IslandPalette.Text, BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center,
            };
            box.Text = WatchService.Condition == WatchCondition.Contains ? service?.MatchText ?? string.Empty : service?.MatchNumber?.ToString() ?? string.Empty;
            void Apply()
            {
                if (service is null) return;
                if (WatchService.Condition == WatchCondition.Contains) service.MatchText = box.Text;
                else service.MatchNumber = WatchLogic.ParseLocaleNumber(box.Text);
                service.ResetRule();
            }
            box.LostFocus += (_, _) => Apply();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            field = box;
        }
        var rule = IslandUi.Row(8, IslandUi.Text("Tell me", 12.5, brush: IslandPalette.Secondary), conditions, field);
        var footerLeft = w.Active ? IslandUi.Text($"Watching since {w.Since:t}", 11, brush: IslandPalette.Tertiary) : null;
        var buttons = IslandUi.Row(4,
            w.Active ? null : IslandUi.Pill("Watch Again", () => service?.WatchAgain()),
            IslandUi.Button(Glyphs.Refresh, "Choose Another Area", () => { nav.Collapse(); service?.ChooseArea(); }, 28, 13, IslandPalette.Secondary),
            w.Active ? IslandUi.Button(Glyphs.Close, "Stop Watching", () => service?.StopWatching(), 28, 13, IslandPalette.Secondary) : null);
        var footer = Ui.Split(footerLeft ?? (UIElement)new Border(), buttons);
        return Ui.Stack(8, preview, headline, rule, footer);
    }
}
