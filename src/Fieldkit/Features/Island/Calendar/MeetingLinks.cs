// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Island.Calendar;

/// <summary>An online meeting an event can be joined at.</summary>
public sealed record MeetingLink(string Provider, string Url);

/// <summary>
/// Finds the join link of an online meeting in calendar text (upstream #2580). Only https links on the known
/// meeting services count, so a link to an agenda document or a random site is never offered as "Join".
/// </summary>
public static partial class MeetingLinks
{
    private static readonly (string Provider, Func<Uri, bool> Matches)[] Services =
    [
        ("Teams", u => Host(u, "teams.microsoft.com") && (u.AbsolutePath.StartsWith("/l/meetup-join", StringComparison.OrdinalIgnoreCase)
                                                        || u.AbsolutePath.StartsWith("/meet/", StringComparison.OrdinalIgnoreCase))
                       || Host(u, "teams.live.com") && u.AbsolutePath.StartsWith("/meet/", StringComparison.OrdinalIgnoreCase)),
        ("Zoom", u => Host(u, "zoom.us") && Regex.IsMatch(u.AbsolutePath, @"^/(j|w|my|s|wc/join)/", RegexOptions.IgnoreCase)
                      || Host(u, "zoomgov.com") && u.AbsolutePath.StartsWith("/j/", StringComparison.OrdinalIgnoreCase)),
        ("Google Meet", u => Host(u, "meet.google.com") && u.AbsolutePath.Length > 4),
        ("Webex", u => Host(u, "webex.com") && Regex.IsMatch(u.AbsolutePath + u.Query, @"(/meet/|/join/|/j\.php|MTID=)", RegexOptions.IgnoreCase)),
        ("GoTo Meeting", u => (Host(u, "gotomeeting.com") || Host(u, "goto.com") || Host(u, "gotomeet.me")) && u.AbsolutePath.Length > 1),
        ("Whereby", u => Host(u, "whereby.com") && u.AbsolutePath.Length > 1),
        ("Jitsi", u => Host(u, "meet.jit.si") && u.AbsolutePath.Length > 1),
        ("Slack huddle", u => Host(u, "app.slack.com") && u.AbsolutePath.StartsWith("/huddle/", StringComparison.OrdinalIgnoreCase)),
    ];

    [GeneratedRegex(@"https://[^\s<>""'\)\]\|]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsLink();

    private static bool Host(Uri u, string domain) =>
        u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first meeting link in <paramref name="texts"/>, searched in order (pass the most specific field first:
    /// a conference property, then URL, location, description).
    /// </summary>
    public static MeetingLink? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match m in HttpsLink().Matches(text))
            {
                var candidate = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '>');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                foreach (var (provider, matches) in Services)
                {
                    if (matches(uri)) return new MeetingLink(provider, uri.AbsoluteUri);
                }
            }
        }
        return null;
    }

    /// <summary>Whether the island should offer joining: from 15 minutes before the start until the end.</summary>
    public static bool IsJoinable(CalendarEvent e, DateTime now) =>
        e.Meeting is not null && !e.IsAllDay && now < e.End && e.Start - now <= TimeSpan.FromMinutes(15);

    /// <summary>The "starting" notice fires once, in the minute before the start.</summary>
    public static bool IsStarting(CalendarEvent e, DateTime now)
    {
        var left = e.Start - now;
        return e.Meeting is not null && !e.IsAllDay && left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(1);
    }
}
