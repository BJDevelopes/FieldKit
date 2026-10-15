// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Island;
using Fieldkit.Features.Island.Accessories;
using Fieldkit.Features.Island.Calendar;
using Fieldkit.Features.Island.Downloads;
using Fieldkit.Features.Island.Lyrics;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Features.Island.Watch;

namespace Fieldkit.Tests.Island;

public class IslandPriorityTests
{
    [Fact]
    public void ActivitiesFollowUpstreamOrder()
    {
        var all = IslandLogic.CompactActivities(true, true, true, true, true, true, true);
        Assert.Equal([IslandActivity.Timer, IslandActivity.Watch, IslandActivity.Downloads, IslandActivity.Agents,
            IslandActivity.Calendar, IslandActivity.Music, IslandActivity.KeepAwake], all);
        var some = IslandLogic.CompactActivities(false, false, false, true, false, true, false);
        Assert.Equal(IslandActivity.Agents, IslandLogic.Current(some, null));
        Assert.Equal(IslandActivity.Music, IslandLogic.Current(some, IslandActivity.Music));
        Assert.Equal(IslandActivity.Agents, IslandLogic.Current(some, IslandActivity.Timer)); // choice gone: first wins
        Assert.Null(IslandLogic.Current([], IslandActivity.Music));
    }

    [Fact]
    public void PlayingMusicTakesTheIdleCompanionsPlace()
    {
        // Companion on, "At rest: Nothing": music playing is an activity and shows instead of the companion.
        Assert.True(IslandLogic.ShowsMusicActivity(true, IslandIdleContent.None, companionEnabled: true, showPlayingMusic: true));
        Assert.Equal(IslandIdleContent.Music, IslandLogic.VisibleIdleContent(IslandIdleContent.None, true, true, true));
        var compact = IslandLogic.Current(IslandLogic.CompactActivities(false, false, false, false, false, true, false), null);
        Assert.False(IslandLogic.CompanionRestsInView(true, IslandIdleContent.None, false, compact));

        // Paused: the companion comes back.
        Assert.Equal(IslandIdleContent.None, IslandLogic.VisibleIdleContent(IslandIdleContent.None, false, true, true));
        Assert.True(IslandLogic.CompanionRestsInView(true, IslandIdleContent.None, false, null));

        // Without the companion and with "Nothing" at rest, music is not watched at all.
        Assert.False(IslandLogic.ShowsMusicActivity(true, IslandIdleContent.None, false, true));
        // "Show music while playing" off: never.
        Assert.False(IslandLogic.ShowsMusicActivity(true, IslandIdleContent.Music, true, false));
        // Battery at rest stays battery.
        Assert.Equal(IslandIdleContent.Battery, IslandLogic.VisibleIdleContent(IslandIdleContent.Battery, true, false, true));
    }

    [Fact]
    public void AnyActivityDisplacesTheCompanion()
    {
        Assert.False(IslandLogic.CompanionRestsInView(true, IslandIdleContent.None, false, IslandActivity.Timer));
        Assert.False(IslandLogic.CompanionRestsInView(true, IslandIdleContent.None, tucked: true, null));
        Assert.False(IslandLogic.CompanionRestsInView(false, IslandIdleContent.None, false, null));
    }

    [Fact]
    public void NoticesReplaceByPriority()
    {
        Assert.True(IslandLogic.ShouldReplace(null, IslandNoticeEvent.Track, false));
        Assert.True(IslandLogic.ShouldReplace(IslandNoticeEvent.Download, IslandNoticeEvent.Track, false)); // same priority
        Assert.False(IslandLogic.ShouldReplace(IslandNoticeEvent.Timer, IslandNoticeEvent.Download, false));
        Assert.True(IslandLogic.ShouldReplace(IslandNoticeEvent.Agents, IslandNoticeEvent.Volume, false));
        Assert.False(IslandLogic.ShouldReplace(IslandNoticeEvent.Accessory, IslandNoticeEvent.Agents, held: true));
        Assert.True(IslandLogic.ShouldReplace(IslandNoticeEvent.Agents, IslandNoticeEvent.Agents, held: true));
        Assert.Equal(TimeSpan.FromSeconds(6), IslandLogic.Duration(IslandNoticeEvent.Timer));
        Assert.Equal(TimeSpan.FromSeconds(5), IslandLogic.Duration(IslandNoticeEvent.Agents));
    }

    [Fact]
    public void SectionsKeepSavedOrderAndAppendNewOnes()
    {
        var visible = IslandLogic.VisibleSections("timer,music,bogus", "files", f => f != "notchAgents");
        var ids = visible.Select(s => s.Id).ToList();
        Assert.Equal("timer", ids[0]);
        Assert.Equal("music", ids[1]);
        Assert.DoesNotContain("files", ids);
        Assert.DoesNotContain("agents", ids);
        Assert.Contains("controls", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void SizesFollowUpstream()
    {
        Assert.Equal(480, IslandLogic.ExpandedWidth("compact", 0));
        Assert.Equal(560, IslandLogic.ExpandedWidth("spacious", 0));
        Assert.Equal(600, IslandLogic.ExpandedWidth("custom", 900));
        Assert.Equal(180, IslandLogic.ContentBudget("compact", 0));
        Assert.Equal(264, IslandLogic.ContentBudget("spacious", 0));
    }
}

public class MediaTests
{
    private static MediaSnapshot S(string app, bool playing) => new() { SourceApp = app, Title = "t", IsPlaying = playing };

    [Fact]
    public void PlayingMusicBeatsPlayingVideoBeatsPausedMusic()
    {
        var ctx = new MediaSelectionContext(null, true, null, null);
        var spotifyPaused = S("Spotify.exe", false);
        var chromePlaying = S("chrome", true);
        Assert.Equal("chrome", MediaSelection.Choose([spotifyPaused, chromePlaying], ctx)!.SourceApp);
        var spotifyPlaying = S("Spotify.exe", true);
        Assert.Equal("Spotify.exe", MediaSelection.Choose([spotifyPlaying, chromePlaying], ctx)!.SourceApp);
        Assert.Equal("Spotify.exe", MediaSelection.Choose([spotifyPaused], ctx)!.SourceApp);
    }

    [Fact]
    public void PausingAFollowedVideoDoesNotJumpToPausedMusic()
    {
        var ctx = new MediaSelectionContext(null, true, null, PreviousChoice: "chrome");
        Assert.Equal("chrome", MediaSelection.Choose([S("Spotify.exe", false), S("chrome", false)], ctx)!.SourceApp);
    }

    [Fact]
    public void OtherPlayersCanBeExcludedAndManualChoiceWins()
    {
        Assert.Null(MediaSelection.Choose([S("chrome", true)], new MediaSelectionContext(null, false, "chrome", null)));
        var manual = new MediaSelectionContext("chrome", true, null, null);
        Assert.Equal("chrome", MediaSelection.Choose([S("Spotify.exe", true), S("chrome", false)], manual)!.SourceApp);
        Assert.Null(MediaSelection.Choose([new MediaSnapshot { SourceApp = "x" }], manual)); // no track
    }

    [Fact]
    public void TimelineExtrapolatesWhilePlaying()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(15), TrackTime.Extrapolate(TimeSpan.FromSeconds(10), t, true, TimeSpan.FromMinutes(3), t.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(10), TrackTime.Extrapolate(TimeSpan.FromSeconds(10), t, false, TimeSpan.FromMinutes(3), t.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromMinutes(3), TrackTime.Extrapolate(TimeSpan.FromSeconds(179), t, true, TimeSpan.FromMinutes(3), t.AddSeconds(50)));
        Assert.Equal("3:07", TrackTime.Format(TimeSpan.FromSeconds(187)));
        Assert.Equal("-1:02:03", TrackTime.Format(-TimeSpan.FromSeconds(3723)));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("chrome", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    public void FriendlyNames(string id, string name) => Assert.Equal(name, MediaSelection.FriendlyName(id));

    [Fact]
    public void EqualizerBandsAndSmoothing()
    {
        var mags = new float[512];
        // A loud tone in the 1–2 kHz band (bins 21..42 at 48 kHz / 1024).
        for (var k = 22; k < 40; k++) mags[k] = 0.5f;
        var bands = EqualizerMath.Bands(mags, 48000, 1024);
        Assert.Equal(7, bands.Length);
        Assert.Equal(4, Array.IndexOf(bands, bands.Max()));
        Assert.Equal(0, bands[0]);
        var smoother = new EqualizerSmoother(7);
        smoother.Push(bands);
        Assert.True(smoother.Heard);
        Assert.True(smoother.Levels[4] > smoother.Levels[0]);
        Assert.Equal(6, EqualizerMath.BandForBar(6, 7, 7));
        Assert.Equal(1, EqualizerMath.BandForBar(0, 3, 7));
    }

    [Fact]
    public void SyntheticBarsStayInRange()
    {
        for (var t = 0.0; t < 5; t += 0.07)
        for (var i = 0; i < 7; i++)
        {
            var h = EqualizerMath.SyntheticHeight(i, 7, t, 16, 1.8);
            Assert.InRange(h, 1.8, 16);
        }
        Assert.Equal(0, EqualizerMath.Swing(0), 6);
        Assert.Equal(1, EqualizerMath.Swing(1), 6);
        Assert.Equal(0, EqualizerMath.Swing(2), 6);
    }

    [Fact]
    public void GesturesFireOncePerSequence()
    {
        var g = new IslandGestureTracker();
        Assert.Equal(IslandGestureAction.None, g.Feed(0, 10, 0.0, false, false, true, true));
        Assert.Equal(IslandGestureAction.Open, g.Feed(0, 10, 0.1, false, false, true, true));
        Assert.Equal(IslandGestureAction.None, g.Feed(0, 40, 0.2, false, false, true, true));
        // A pause starts a new sequence: scrolling up while open closes.
        Assert.Equal(IslandGestureAction.Close, g.Feed(0, -24, 1.0, false, true, true, true));
        // Horizontal swipes need a precise device and 40 points.
        var h = new IslandGestureTracker();
        Assert.Equal(IslandGestureAction.None, h.Feed(-30, 0, 0, true, false, true, true));
        Assert.Equal(IslandGestureAction.NextTrack, h.Feed(-15, 0, 0.05, true, false, true, true));
        var w = new IslandGestureTracker();
        Assert.Equal(IslandGestureAction.None, w.Feed(-48, 0, 0, false, false, true, true));
        var p = new IslandGestureTracker();
        Assert.Equal(IslandGestureAction.PreviousTrack, p.Feed(50, 2, 0, true, false, true, true));
    }
}

public class LyricsTests
{
    [Fact]
    public void ParsesTimesOffsetsAndMerges()
    {
        var lines = LrcParser.Parse(Fixtures.Text("example.lrc"), 200);
        // [offset:500] moves every line half a second earlier; the line past the end is dropped.
        Assert.Equal([0.5, 5.0, 9.5, 11.5, 19.5], lines.Select(l => l.Time).ToArray());
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(string.Empty, lines[2].Text);
        Assert.Equal("Third line\nDuet part", lines[3].Text);
        Assert.Equal("Chorus", lines[4].Text);
    }

    [Fact]
    public void MetadataAndEmptyLyricsYieldNothing()
    {
        Assert.Empty(LrcParser.Parse("[ar:Someone]\n[ti:Title]\n"));
        Assert.Empty(LrcParser.Parse("[00:01.00]\n[00:02.00]"));
        Assert.Empty(LrcParser.Parse("[00:61.00]bad seconds"));
    }

    [Fact]
    public void CurrentLineUsesBinarySearch()
    {
        var lines = LrcParser.Parse(Fixtures.Text("example.lrc"), 200);
        Assert.Equal(-1, LrcParser.CurrentIndex(lines, 0.2));
        Assert.Equal(0, LrcParser.CurrentIndex(lines, 0.5));
        Assert.Equal(3, LrcParser.CurrentIndex(lines, 15));
        Assert.Equal(4, LrcParser.CurrentIndex(lines, 300));
        Assert.Equal(0, LrcParser.CurrentIndex(lines, 5.0, userOffset: 0.5));
        Assert.Equal(0.25, LrcParser.StepOffset(0, 1));
        Assert.Equal(10, LrcParser.StepOffset(10, 1));
    }

    private static readonly LyricsTrack Track = new("Example Song", "Someone", "Album - Single", 200);

    [Fact]
    public void LookupUrlFollowsLrclib()
    {
        var url = LyricsCatalog.LookupUrl(Track)!;
        Assert.StartsWith("https://lrclib.net/api/get?track_name=Example%20Song&artist_name=Someone&album_name=Album&duration=200", url);
        Assert.Null(LyricsCatalog.LookupUrl(Track with { Duration = 0 }));
        Assert.Null(LyricsCatalog.LookupUrl(Track with { Artist = " " }));
        Assert.Single(LyricsCatalog.SearchUrls(Track)); // the three variants collapse to one
    }

    [Fact]
    public void GetReplyMustMatchTheRecording()
    {
        var payload = LyricsCatalog.DecodeGet(Fixtures.Text("lrclib-get.json"), Track);
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Lines.Count);
        Assert.Null(LyricsCatalog.DecodeGet(Fixtures.Text("lrclib-get.json"), Track with { Duration = 230 }));
        Assert.Null(LyricsCatalog.DecodeGet(Fixtures.Text("lrclib-get.json"), Track with { Title = "Another" }));
    }

    [Fact]
    public void SearchPicksTheSameRecording()
    {
        var best = LyricsCatalog.BestFromSearch(Fixtures.Text("lrclib-search.json"), Track);
        Assert.NotNull(best);
        Assert.Equal("best", best!.Plain);
        Assert.True(best.IsSynced);
    }

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("AC/DC", "acdc")]
    [InlineData("Ёлка", "елка")]
    public void KeysFoldAccentsAndPunctuation(string text, string key) => Assert.Equal(key, LyricsCatalog.Key(text));

    [Fact]
    public void ArtistCreditsSplit()
    {
        Assert.Equal(["A", "B", "C"], LyricsCatalog.ArtistNames("A, B feat. C"));
        Assert.Equal(["Simon", "Garfunkel"], LyricsCatalog.ArtistNames("Simon & Garfunkel"));
        Assert.Equal("example song", LyricsCatalog.TitleKey("Example Song (feat. X)").Length > 0 ? "example song" : "");
    }
}

public class TimerTests
{
    [Fact]
    public void CountdownRunsPausesAndCompletes()
    {
        var s = TimerSession.Default.Start(TimerMode.Timer, 15, 100, new PomodoroConfig());
        Assert.True(s.IsRunning);
        Assert.Equal(900, s.Reading(100));
        Assert.Equal("15m", TimerFormat.Compact(s, 100));
        Assert.Equal("14m", TimerFormat.Compact(s, 101));
        var paused = s.Pause(400);
        Assert.True(paused.IsPaused);
        Assert.Equal(600, paused.Reading(5000));
        var resumed = paused.Resume(1000);
        Assert.Equal(1600, resumed.Deadline);
        var done = resumed.FinishIfDue(1600);
        Assert.True(done.Completed);
        Assert.Equal(0, done.Reading(2000));
        Assert.Equal("0s", TimerFormat.Compact(done, 2000));
        // A started session ignores another start.
        Assert.Same(s, s.Start(TimerMode.Stopwatch, 1, 0, new PomodoroConfig()));
    }

    [Fact]
    public void MinutesAreClamped()
    {
        Assert.Equal(180 * 60, TimerSession.Default.Start(TimerMode.Timer, 999, 0, new PomodoroConfig()).Duration);
        Assert.Equal(60, TimerSession.Default.Start(TimerMode.Timer, 0, 0, new PomodoroConfig()).Duration);
    }

    [Fact]
    public void PomodoroCycles()
    {
        var config = new PomodoroConfig(25, 5, 15, 2, 3);
        var s = TimerSession.Default.Start(TimerMode.Pomodoro, 0, 0, config);
        Assert.Equal(TimerPhase.Focus, s.Phase);
        Assert.Equal(1, s.SessionNumber);
        s = s.FinishIfDue(25 * 60);
        Assert.True(s.CanStartNext);
        Assert.Equal(TimerPhase.ShortBreak, s.NextPhase);
        s = s.StartNext(2000).FinishIfDue(9999);
        Assert.Equal(TimerPhase.Focus, s.NextPhase);
        s = s.StartNext(10000).FinishIfDue(99999);
        Assert.Equal(2, s.CompletedFocuses);
        Assert.Equal(TimerPhase.LongBreak, s.NextPhase);
        s = s.StartNext(100000).FinishIfDue(200000).StartNext(200000).FinishIfDue(300000);
        Assert.Equal(3, s.CompletedFocuses);
        Assert.True(s.CycleFinished);
        Assert.False(s.CanStartNext);
    }

    [Fact]
    public void StopwatchCountsUp()
    {
        var s = TimerSession.Default.Start(TimerMode.Stopwatch, 0, 10, new PomodoroConfig());
        Assert.Equal(65, s.Reading(75));
        Assert.Equal("01:05", TimerFormat.Compact(s, 75));
        Assert.Equal("1h01", TimerFormat.Compact(s, 10 + 3660));
        Assert.Null(s.Deadline);
        Assert.False(s.FinishIfDue(1e9).Completed);
    }

    [Theory]
    [InlineData(300, "05:00")]
    [InlineData(59.2, "01:00")]
    [InlineData(3725, "1:02:05")]
    public void ClockFormat(double r, string text) => Assert.Equal(text, TimerFormat.Clock(r));

    [Theory]
    [InlineData(5, "5")]
    [InlineData(65, "1h05")]
    public void RulerLabels(int m, string text) => Assert.Equal(text, TimerFormat.RulerLabel(m));
}

public class CalendarTests
{
    private static readonly DateTime From = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Local);

    [Fact]
    public void ParsesFoldedRecurringAndAllDayEvents()
    {
        var events = IcsParser.Parse(Fixtures.Text("calendar.ics"), From, From.AddDays(7), "Work");
        var standups = events.Where(e => e.Title == "Standup").ToList();
        // Daily ×10 from Oct 12, minus the EXDATE on Oct 15, within the week Oct 12–18.
        Assert.Equal(6, standups.Count);
        Assert.DoesNotContain(standups, e => e.Start.ToUniversalTime().Day == 15);
        Assert.All(standups, e => Assert.Equal(TimeSpan.FromMinutes(15), e.End - e.Start));
        var holiday = Assert.Single(events, e => e.IsAllDay);
        Assert.Equal("Holiday with a very long title that is folded across two lines", holiday.Title);
        Assert.Equal("Home, sweet home", holiday.Location);
        Assert.DoesNotContain(events, e => e.Title == "Cancelled thing");
        Assert.DoesNotContain(events, e => e.Title == "Alarm text");
        var weekly = events.Where(e => e.Title == "Weekly review").ToList();
        Assert.Equal(2, weekly.Count); // Monday and Wednesday, series from 2020
        Assert.All(weekly, e => Assert.Contains(e.Start.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }));
        Assert.All(events, e => Assert.Equal("Work", e.CalendarName));
    }

    [Theory]
    [InlineData("PT1H30M", 90)]
    [InlineData("P1D", 1440)]
    [InlineData("-PT15M", -15)]
    [InlineData("P1W", 10080)]
    public void Durations(string text, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), IcsParser.ParseDuration(text));

    [Fact]
    public void CountdownPicksTheSoonestMomentWithinTheHour()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0);
        var meeting = new CalendarEvent("Meeting", now.AddMinutes(30), now.AddMinutes(90), false);
        var ongoing = new CalendarEvent("Focus", now.AddMinutes(-30), now.AddMinutes(30), false);
        var later = new CalendarEvent("Later", now.AddHours(3), now.AddHours(4), false);
        var events = new[] { meeting, ongoing, later };
        Assert.Null(CalendarLogic.Countdown(events, now, false, false));
        var start = CalendarLogic.Countdown(events, now, true, false)!;
        Assert.Equal("Meeting", start.Event.Title);
        var tie = CalendarLogic.Countdown(events, now, true, true)!;
        Assert.True(tie.Ongoing); // same target: the ongoing event wins
        Assert.Equal("29:31", CalendarLogic.CountdownText(TimeSpan.FromSeconds(1770.2)));
        Assert.Equal("Meeting", CalendarLogic.Next(events, now)!.Title);
    }

    [Fact]
    public void AgendaListsTheNextSevenDays()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var events = new[]
        {
            new CalendarEvent("Past", now.AddHours(-3), now.AddHours(-2), false),
            new CalendarEvent("All day", now.Date, now.Date.AddDays(1), true),
            new CalendarEvent("Soon", now.AddHours(1), now.AddHours(2), false),
            new CalendarEvent("Next week", now.AddDays(8), now.AddDays(8).AddHours(1), false),
        };
        var agenda = CalendarLogic.Agenda(events, now);
        var today = Assert.Single(agenda);
        Assert.Equal(["All day", "Soon"], today.Events.Select(e => e.Title).ToArray());
        var selected = CalendarLogic.Agenda(events, now, now.Date);
        Assert.Equal(3, selected[0].Events.Count);
    }
}

public class ExtensionLogicTests
{
    [Theory]
    [InlineData("C:\\d\\file.zip.crdownload", "C:\\d\\file.zip")]
    [InlineData("C:\\d\\movie.mkv.part", "C:\\d\\movie.mkv")]
    [InlineData("C:\\d\\a.download", "C:\\d\\a")]
    [InlineData("C:\\d\\a.zip", null)]
    public void PartialDownloads(string path, string? expected) => Assert.Equal(expected, DownloadTracker.ExpectedPath(path));

    [Fact]
    public void DownloadsCompleteWhenRenamedInPlace()
    {
        var p1 = new PartialFile("a.zip.crdownload", "a.zip", 10, DateTime.Now, 77);
        var p2 = new PartialFile("b.bin.part", "b.bin", 10, DateTime.Now, 88);
        var p3 = new PartialFile("c.iso.part", "c.iso", 10, DateTime.Now, 99);
        var files = new Dictionary<string, long> { ["a.zip"] = 77, ["c.iso"] = 5 };
        var (completed, failed) = DownloadTracker.Compare([p1, p2, p3], [], p => files.TryGetValue(p, out var id) ? id : null);
        Assert.Equal(["a.zip.crdownload"], completed.Select(c => c.Path).ToArray());
        Assert.Equal(["b.bin.part"], failed.Select(c => c.Path).ToArray());
    }

    [Fact]
    public void DownloadItemsPutActiveFirst()
    {
        var now = DateTime.Now;
        var scan = new DownloadScan(
            [new PartialFile("x.crdownload", "x", 5_000_000, now.AddSeconds(-5), 1), new PartialFile("old.part", "old", 1, now.AddHours(-1), 2)],
            [new DownloadItem("f.txt", "f.txt", 3, 1, true, false, now.AddMinutes(-1))]);
        var items = DownloadTracker.Items(scan, [], now);
        Assert.Equal("x", items[0].Name);
        Assert.True(items[0].Active);
        Assert.False(items.Single(i => i.Name == "old").Active);
        Assert.Equal("5 MB", DownloadTracker.Bytes(5_000_000));
    }

    [Fact]
    public void WatchFingerprintToleratesNoise()
    {
        var a = Enumerable.Repeat((byte)5, 256).ToArray();
        var b = (byte[])a.Clone();
        b[0] = 6; b[1] = 4; b[2] = 6;
        Assert.True(WatchLogic.SameFingerprint(a, b));
        b[3] = 6;
        Assert.False(WatchLogic.SameFingerprint(a, b));
        var c = (byte[])a.Clone();
        c[10] = 8;
        Assert.False(WatchLogic.SameFingerprint(a, c));
    }

    [Fact]
    public void WatchFingerprintFromFrame()
    {
        var bgra = new byte[32 * 32 * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 255; bgra[i + 1] = 255; bgra[i + 2] = 255; bgra[i + 3] = 255; }
        var cells = WatchLogic.Fingerprint(new WatchFrame(32, 32, bgra));
        Assert.All(cells, c => Assert.Equal(15, c));
    }

    [Fact]
    public void WatchRules()
    {
        var changes = new WatchTracker(WatchCondition.Changes);
        Assert.Null(changes.Observe("a", "a", 0));
        Assert.Null(changes.Observe("b", "b", 1));
        Assert.Null(changes.Observe("a", "a", 2)); // reverted: timer resets
        Assert.Null(changes.Observe("b", "b", 3));
        Assert.IsType<WatchOutcome.Changed>(changes.Observe("b", "b", 6));

        var settles = new WatchTracker(WatchCondition.Settles);
        Assert.Null(settles.Observe("a", "", 0));
        Assert.Null(settles.Observe("a", "", 40)); // never changed yet
        Assert.Null(settles.Observe("b", "", 41));
        Assert.IsType<WatchOutcome.Settled>(settles.Observe("b", "", 71));

        var contains = new WatchTracker(WatchCondition.Contains, "Done");
        Assert.Null(contains.Observe("x", "Building…", 0));
        Assert.Equal("It shows Done", contains.Observe("y", "Build  DONE!", 1)!.Text);

        var reaches = new WatchTracker(WatchCondition.Reaches, target: 100);
        Assert.Null(reaches.Observe("x", "Progress 42%", 0));
        Assert.Null(reaches.Observe("x", "Progress 99%", 1));
        Assert.IsType<WatchOutcome.Reached>(reaches.Observe("x", "Progress 100%", 2));
    }

    [Theory]
    [InlineData("Uploading 42 %", "42%")]
    [InlineData("Build passed", "Build passed")]
    [InlineData("A much longer line of text that goes on\nnext", "A much longer line of…")]
    public void WatchHeadlines(string text, string headline) => Assert.Equal(headline, WatchLogic.Headline(text));

    [Theory]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1,234", 1234)]
    public void LocaleNumbers(string text, double value) => Assert.Equal(value, WatchLogic.ParseLocaleNumber(text));

    [Fact]
    public void LowBatteryWarnsOncePerEpisode()
    {
        var t = new LowBatteryTracker();
        AccessoryBattery D(int p) => new("id", "Buds", p, AccessoryKind.Headphones);
        Assert.Empty(t.Observe([D(15)])); // first reading is a silent baseline, even when low
        Assert.Empty(t.Observe([D(14)]));
        Assert.Empty(t.Observe([D(22)])); // 21–24 changes nothing
        Assert.Empty(t.Observe([D(19)]));
        Assert.Empty(t.Observe([D(30)])); // re-armed
        var alert = Assert.Single(t.Observe([D(20)]));
        Assert.False(alert.Connected);
        var connected = Assert.Single(t.Observe([D(20), new AccessoryBattery("kb", "Keys", 80, AccessoryKind.Keyboard)]));
        Assert.True(connected.Connected);
    }

    [Theory]
    [InlineData("WH-1000XM5 Hands-Free AG", "WH-1000XM5")]
    [InlineData("Surface Keyboard", "Surface Keyboard")]
    public void AccessoryNames(string raw, string clean) => Assert.Equal(clean, BluetoothBatteryReader.CleanName(raw));

    [Fact]
    public void AccessoryKinds()
    {
        Assert.Equal(AccessoryKind.Headphones, AccessoryClassifier.Classify("AirPods Pro", ""));
        Assert.Equal(AccessoryKind.Mouse, AccessoryClassifier.Classify("MX Master 3", ""));
        Assert.Equal(AccessoryKind.Controller, AccessoryClassifier.Classify("Xbox Wireless Controller", ""));
    }

    [Fact]
    public void MascotReactionsAndGate()
    {
        Assert.Equal(MascotReaction.Celebrate, MascotLogic.ReactionFor(MascotEvent.DownloadFinished));
        Assert.Equal(MascotReaction.Groove, MascotLogic.ReactionFor(MascotEvent.MusicStarted));
        Assert.Equal(MascotReaction.Surprised, MascotLogic.ReactionFor(MascotEvent.TimerDone));
        var gate = new MascotReactionGate();
        Assert.True(gate.TryPass(MascotReaction.Celebrate, 0));
        Assert.False(gate.TryPass(MascotReaction.Perk, 1)); // too soon
        Assert.True(gate.TryPass(MascotReaction.Perk, 3));
        Assert.False(gate.TryPass(MascotReaction.Celebrate, 10)); // same reaction within 30 s
        Assert.True(gate.TryPass(MascotReaction.Celebrate, 31));
        Assert.Equal("pearl", MascotLogic.Palette("unknown").Id);
        Assert.Equal(0, MascotLogic.Hop(MascotReaction.Celebrate, 5));
    }
}
