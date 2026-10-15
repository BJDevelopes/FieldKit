// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Features.Island.Agents;
using Fieldkit.Features.Island.Calendar;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Features.Island.Views;
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>States <see cref="IslandPreview.Render"/> can draw.</summary>
public enum IslandPreviewState
{
    CollapsedIdle, CollapsedMusic, CollapsedTimer, CollapsedAgents, CollapsedNotice, CollapsedCompanion, CollapsedDownload, CollapsedWatch,
    ExpandedControls, ExpandedMusic, ExpandedLyrics, ExpandedTimer, ExpandedTimerSetup, ExpandedAgents, ExpandedCalendar, ExpandedDownloads,
    ExpandedWatch, ExpandedFiles,
}

/// <summary>
/// Renders the island with sample data to a PNG (the island window is not reachable by --render-ui).
/// Uses no services, hardware, audio or network. Call on an STA thread.
/// </summary>
public static class IslandPreview
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 30, 0, TimeSpan.FromHours(2));
    private const double Clock = 10_000;

    public static void Render(string path, IslandPreviewState state)
    {
        var pill = BuildPill(state);
        var stage = new Grid
        {
            Background = new LinearGradientBrush(Color.FromRgb(70, 92, 130), Color.FromRgb(150, 110, 150), 30),
            Width = pill.Width + 80,
            Height = pill.Height + 40,
        };
        stage.Children.Add(pill);
        stage.Measure(new Size(stage.Width, stage.Height));
        stage.Arrange(new Rect(0, 0, stage.Width, stage.Height));
        stage.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(stage.Width * 2), (int)Math.Ceiling(stage.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(stage);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The island in <paramref name="state"/> with sample data, not yet laid out (for previews and the site export).</summary>
    /// <param name="expandedWidth">560 as on the PC; 360 is the narrowest custom size (the website uses it on phones).</param>
    public static Border BuildPill(IslandPreviewState state, double expandedWidth = 560)
    {
        var ctx = SampleContext(state);
        FrameworkElement content;
        double width, height, radius;
        if (state.ToString().StartsWith("Collapsed", StringComparison.Ordinal))
        {
            IslandNotice? notice = state == IslandPreviewState.CollapsedNotice
                ? new IslandNotice(IslandNoticeEvent.Agents, "Claude finished", "4m 12s · $3.40", AgentGlyphs.Sparkle, "Orange")
                : null;
            var activity = state switch
            {
                IslandPreviewState.CollapsedMusic => IslandActivity.Music,
                IslandPreviewState.CollapsedTimer => IslandActivity.Timer,
                IslandPreviewState.CollapsedAgents => IslandActivity.Agents,
                IslandPreviewState.CollapsedDownload => IslandActivity.Downloads,
                IslandPreviewState.CollapsedWatch => IslandActivity.Watch,
                _ => (IslandActivity?)null,
            };
            var collapsed = new CollapsedState(notice, activity, IslandIdleContent.None, state == IslandPreviewState.CollapsedCompanion, Hovered: false);
            content = IslandCollapsed.Build(ctx, collapsed);
            content.Measure(new Size(double.PositiveInfinity, IslandCollapsed.Height));
            width = Math.Max(content.MinWidth, Math.Min(content.MaxWidth, content.DesiredSize.Width));
            height = IslandCollapsed.Height;
            radius = height / 2;
        }
        else
        {
            var section = state switch
            {
                IslandPreviewState.ExpandedMusic or IslandPreviewState.ExpandedLyrics => "music",
                IslandPreviewState.ExpandedTimer or IslandPreviewState.ExpandedTimerSetup => "timer",
                IslandPreviewState.ExpandedAgents => "agents",
                IslandPreviewState.ExpandedCalendar => "calendar",
                IslandPreviewState.ExpandedDownloads => "downloads",
                IslandPreviewState.ExpandedWatch => "watch",
                IslandPreviewState.ExpandedFiles => "files",
                _ => "controls",
            };
            var nav = new IslandNav { Section = section, Width = expandedWidth, Budget = 264, LyricsOpen = state == IslandPreviewState.ExpandedLyrics };
            content = IslandPages.Build(ctx, nav, IslandLogic.Sections);
            content.Width = expandedWidth;
            content.Measure(new Size(expandedWidth, double.PositiveInfinity));
            width = expandedWidth;
            height = Math.Max(120, content.DesiredSize.Height);
            radius = Math.Min(28, 0.34 * height);
        }

        return new Border
        {
            Background = IslandPalette.Background,
            CornerRadius = new CornerRadius(radius),
            Width = width,
            Height = height,
            Child = content,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    /// <summary>Renders every state into <paramref name="directory"/> as island-&lt;state&gt;.png.</summary>
    public static List<string> RenderAll(string directory)
    {
        var files = new List<string>();
        foreach (var state in Enum.GetValues<IslandPreviewState>())
        {
            var file = Path.Combine(directory, $"island-{state}.png");
            Render(file, state);
            files.Add(file);
        }
        return files;
    }

    private static BitmapSource SampleArt()
    {
        const int size = 96;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 94, 98), Color.FromRgb(70, 40, 160), 45), null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(140, 255, 220, 120)), null, new Point(62, 34), 18, 18);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static IslandContext SampleContext(IslandPreviewState state)
    {
        var music = new MediaSnapshot
        {
            SourceApp = "Spotify.exe",
            Title = "Midnight City",
            Artist = "M83",
            Album = "Hurry Up, We're Dreaming",
            IsPlaying = true,
            Duration = TimeSpan.FromSeconds(243),
            Position = TimeSpan.FromSeconds(83),
            PositionUpdatedAt = Now,
            CanSeek = true,
            Shuffle = false,
            Artwork = SampleArt(),
        };
        var timer = state == IslandPreviewState.ExpandedTimerSetup
            ? TimerSession.Default
            : TimerSession.Default.Start(TimerMode.Pomodoro, 25, Clock - 754, new PomodoroConfig());
        var agents = SampleAgents();
        var downloads = new List<DownloadItem>
        {
            new(@"C:\Users\me\Downloads\ubuntu-24.04.iso.crdownload", "ubuntu-24.04.iso", 1_830_000_000, null, false, true, DateTime.Now),
            new(@"C:\Users\me\Downloads\report.pdf", "report.pdf", 482_000, 1, true, false, DateTime.Now.AddMinutes(-3)),
            new(@"C:\Users\me\Downloads\photos.zip", "photos.zip", 52_400_000, 1, true, false, DateTime.Now.AddHours(-2)),
        };
        var local = Now.LocalDateTime;
        var events = new List<CalendarEvent>
        {
            new("Design review", local.AddMinutes(-20), local.AddMinutes(25), false, "Room 4", "Work",
                Meeting: new MeetingLink("Teams", "https://teams.microsoft.com/l/meetup-join/sample")),
            new("1:1 with Sam", local.AddHours(1), local.AddHours(1.5), false, null, "Work",
                Meeting: new MeetingLink("Zoom", "https://example.zoom.us/j/123456789")),
            new("Dentist", local.Date.AddDays(1).AddHours(9), local.Date.AddDays(1).AddHours(10), false, "Main St", "Personal"),
            new("Holiday", local.Date.AddDays(2), local.Date.AddDays(3), true, null, "Personal"),
        };
        var lyrics = new Lyrics.LyricsService();
        lyrics.ShowPreview(music.TrackKey, new Lyrics.LyricsPayload(
        [
            new(70, "Waiting in a car"), new(76, "Waiting for a ride in the dark"), new(82, "The night city grows"),
            new(88, "Look and see her eyes, they glow"), new(95, ""), new(110, "Waiting in a car"),
        ], null, false), offset: 0.25);
        return new IslandContext
        {
            IsPreview = true,
            Now = () => Now,
            AnimationTime = () => 1.234,
            Music = () => music,
            Players = () => [music],
            TimerClock = () => Clock,
            Timer = () => timer,
            Volume = () => (0.62, false),
            LyricsAvailable = () => true,
            QueueAvailable = () => true,
            Lyrics = () => lyrics,
            Agents = () => agents,
            AgentsOn = () => true,
            Downloads = () => downloads,
            DownloadsFolder = () => @"C:\Users\me\Downloads",
            Events = () => events,
            Countdown = () => CalendarLogic.Countdown(events, local, true, true),
            Watch = () => new WatchView(true, "73%", "Uploading 73%\n3 of 4 files", SampleArt(), null, local.AddMinutes(-4), "640×120"),
            MascotOn = () => true,
            Shelf = () => [new ShelfItem(@"C:\Users\me\Desktop\notes.txt"), new ShelfItem(@"C:\Users\me\Pictures\holiday.jpg"), new ShelfItem(@"C:\Users\me\Projects")],
            ChoosePlayer = _ => { },
        };
    }

    private static AgentUsageSnapshot SampleAgents()
    {
        var records = new List<AgentUsageRecord>();
        var rng = new Random(7);
        for (var d = 0; d < 91; d++)
        {
            if (rng.NextDouble() < 0.35) continue;
            var date = Now.AddDays(-d).AddHours(-rng.Next(0, 8));
            records.Add(new AgentUsageRecord(AgentProvider.Claude, date, "claude-opus-4-5-20251101", d % 3 == 0 ? "fieldkit" : "website", "s",
                new AgentTokens(2000, 30000, 400000 + rng.Next(0, 900000), 12000), 1.5 + rng.NextDouble() * 9, 0.8));
            if (d % 2 == 0)
                records.Add(new AgentUsageRecord(AgentProvider.Codex, date.AddHours(1), "gpt-5.5", "api", "c",
                    new AgentTokens(8000, 0, 90000, 5000), 0.4 + rng.NextDouble() * 3, 0.1));
        }
        var limits = new Dictionary<AgentProvider, AgentLimits>
        {
            [AgentProvider.Claude] = new(AgentProvider.Claude, [
                new AgentLimitWindow("claude.session", AgentLimitKind.Session, 300, null, 42, Now.AddHours(2.3)),
                new AgentLimitWindow("claude.weekly", AgentLimitKind.Weekly, 10080, null, 83, Now.AddDays(2.5)),
            ], Now.AddMinutes(-3), AgentLimitSource.ClaudeApp),
            [AgentProvider.Codex] = new(AgentProvider.Codex, [
                new AgentLimitWindow("codex.300", AgentLimitKind.Session, 300, null, 12.5, Now.AddHours(4)),
                new AgentLimitWindow("codex.10080", AgentLimitKind.Weekly, 10080, null, 40, Now.AddDays(5)),
            ], Now.AddMinutes(-1), AgentLimitSource.SessionLog),
        };
        var live = new List<AgentLiveSession>
        {
            new("f", AgentProvider.Claude, Now.AddMinutes(-12).AddSeconds(-34), Now, "claude-opus-4-5-20251101", "fieldkit", new AgentTokens(0, 0, 0, 20400), 4.56),
        };
        var plans = new Dictionary<AgentProvider, AgentPlan> { [AgentProvider.Claude] = new("Max 20×", 200), [AgentProvider.Codex] = new("Plus", 20) };
        return AgentSummary.Build(records, limits, live, plans, Now);
    }
}
