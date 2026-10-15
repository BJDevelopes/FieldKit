// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Calendar;

/// <summary>
/// Upcoming events for the island. Upstream reads EventKit; an unpackaged Windows app cannot read the
/// Windows calendar (AppointmentManager needs package identity), so events come from iCalendar feeds the user
/// adds: a "secret address in iCal format" from Outlook or Google, or a local .ics file. Feeds refresh every 15
/// minutes; nothing else is sent anywhere.
/// </summary>
public sealed class CalendarService : IslandExtensionService
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    public const int MaximumFeedBytes = 20 << 20;
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = RefreshEvery };
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private HttpClient? _http;
    private IDisposable? _observer;
    private CancellationTokenSource? _cts;
    private string? _lastBegins;
    private readonly HashSet<string> _meetingNotified = [];

    public override string FeatureId => "notchCalendar";
    protected override string EnableKey => IslandKeys.CalendarEnabled;

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public CalendarCountdown? Countdown { get; private set; }

    public static List<string> Feeds() => Settings.GetString(IslandKeys.CalendarFeeds, string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public CalendarService()
    {
        _refresh.Tick += (_, _) => _ = LoadAsync();
        _tick.Tick += (_, _) => UpdateCountdown();
    }

    protected override void OnStart()
    {
        IslandHub.Calendar = this;
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = MaximumFeedBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        _observer = Settings.Observe(() => _ = LoadAsync(), IslandKeys.CalendarFeeds);
        _refresh.Start();
        _tick.Start();
        _ = LoadAsync();
    }

    protected override void OnStop()
    {
        _observer?.Dispose();
        _observer = null;
        _cts?.Cancel();
        _refresh.Stop();
        _tick.Stop();
        _http?.Dispose();
        _http = null;
        Events = [];
        Countdown = null;
        if (IslandHub.Calendar == this) IslandHub.Calendar = null;
        IslandHub.RaiseChanged();
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var feeds = Feeds();
        Loading = true;
        IslandHub.RaiseChanged();
        var from = DateTime.Today;
        var to = from.AddDays(42);
        var all = new List<CalendarEvent>();
        string? error = null;
        foreach (var feed in feeds)
        {
            try
            {
                string text;
                var uri = IslandLinks.SafeWebUri(feed.Replace("webcal://", "https://", StringComparison.OrdinalIgnoreCase));
                if (uri is not null && uri.Scheme is "https" or "http")
                {
                    if (_http is null) return;
                    using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    response.EnsureSuccessStatusCode();
                    // Content-Length can be missing or wrong: the body is counted as it arrives and abandoned past the limit.
                    text = await IslandLinks.ReadLimitedAsync(response, MaximumFeedBytes, cts.Token)
                           ?? throw new InvalidDataException("Calendar too large");
                }
                else if (IslandLinks.IsLocalPath(feed) && File.Exists(feed))
                {
                    if (new FileInfo(feed).Length > MaximumFeedBytes) throw new InvalidDataException("Calendar too large");
                    text = await File.ReadAllTextAsync(feed, cts.Token);
                }
                else
                {
                    throw new InvalidDataException("Not an http(s) address or a local .ics file");
                }
                var name = CalendarName(text) ?? (uri?.Host ?? Path.GetFileNameWithoutExtension(feed));
                all.AddRange(await Task.Run(() => IcsParser.Parse(text, from, to, name), cts.Token));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = "Could not read a calendar.";
                Log.Warn($"Reading calendar feed failed: {ex.Message}");
            }
        }
        if (cts.IsCancellationRequested || !IsRunning) return;
        Events = CalendarLogic.Ordered(all);
        Error = error;
        Loading = false;
        UpdateCountdown();
        IslandHub.RaiseChanged();
    }

    private static string? CalendarName(string ics)
    {
        foreach (var line in IcsParser.Unfold(ics).Take(50))
            if (line.StartsWith("X-WR-CALNAME:", StringComparison.OrdinalIgnoreCase)) return line[13..].Trim();
        return null;
    }

    private void UpdateCountdown()
    {
        var now = DateTime.Now;
        var c = CalendarLogic.Countdown(Events, now, Settings.GetBool(IslandKeys.CalendarCountdown, false), Settings.GetBool(IslandKeys.CalendarTimeLeft, false));
        var begins = Events.FirstOrDefault(e => !e.IsAllDay && (now - e.Start).TotalSeconds is >= -2 and <= 60);
        if (begins is not null && _lastBegins != begins.Title + begins.Start)
        {
            _lastBegins = begins.Title + begins.Start;
            IslandHub.React(MascotEvent.CalendarEventBegins);
        }
        if (Settings.GetBool(IslandKeys.CalendarMeetingNotice, true))
        {
            foreach (var e in Events)
            {
                if (!MeetingLinks.IsStarting(e, now) || !_meetingNotified.Add(e.Title + e.Start.Ticks)) continue;
                IslandHub.Post(new IslandNotice(IslandNoticeEvent.Meeting, e.Title, $"{e.Meeting!.Provider} · starting · click to join",
                    Glyphs.Video, "Pink", OpenSection: "calendar"));
            }
        }
        if (c != Countdown || c is not null)
        {
            Countdown = c;
            IslandHub.RaiseChanged();
        }
    }
}
