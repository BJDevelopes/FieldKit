// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Island;
using Fieldkit.Features.Island.Calendar;

namespace Fieldkit.Tests.Island;

public class MeetingLinksTests
{
    [Theory]
    [InlineData("Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=%7b%7d", "Teams")]
    [InlineData("https://teams.live.com/meet/9876543210", "Teams")]
    [InlineData("Zoom: https://us02web.zoom.us/j/81234567890?pwd=abc123.", "Zoom")]
    [InlineData("<https://meet.google.com/abc-defg-hij>", "Google Meet")]
    [InlineData("https://acme.webex.com/acme/j.php?MTID=m1234", "Webex")]
    [InlineData("https://acme.webex.com/meet/sam", "Webex")]
    [InlineData("https://global.gotomeeting.com/join/123456789", "GoTo Meeting")]
    [InlineData("https://whereby.com/team-standup", "Whereby")]
    [InlineData("https://meet.jit.si/OurDailyCall", "Jitsi")]
    public void RecognisesTheMeetingServices(string text, string provider)
    {
        var link = MeetingLinks.Find(text);
        Assert.NotNull(link);
        Assert.Equal(provider, link.Provider);
        Assert.StartsWith("https://", link.Url);
        Assert.DoesNotMatch(@"[.>]$", link.Url);
    }

    [Theory]
    [InlineData("Agenda: https://docs.google.com/document/d/123")]
    [InlineData("http://meet.google.com/abc-defg-hij")] // not https
    [InlineData("https://zoom.us/pricing")]
    [InlineData("https://zoom.us.evil.example/j/123")] // look-alike host
    [InlineData("https://teams.microsoft.com/downloads")]
    [InlineData("Room 4, second floor")]
    public void IgnoresEverythingElse(string text) => Assert.Null(MeetingLinks.Find(text));

    [Fact]
    public void TheMostSpecificFieldWins()
    {
        var link = MeetingLinks.Find(null, "https://meet.google.com/aaa-bbbb-ccc", "Room 4", "Backup: https://zoom.us/j/1");
        Assert.Equal("Google Meet", link?.Provider);
    }

    [Fact]
    public void ADocumentLinkBeforeTheMeetingLinkIsSkipped()
    {
        var link = MeetingLinks.Find("Notes https://docs.example.com/x then join https://zoom.us/j/99");
        Assert.Equal("https://zoom.us/j/99", link?.Url);
    }

    [Fact]
    public void JoinIsOfferedFromAQuarterHourBeforeUntilTheEnd()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var meeting = new MeetingLink("Zoom", "https://zoom.us/j/1");
        CalendarEvent At(int startMin, int endMin) => new("x", now.AddMinutes(startMin), now.AddMinutes(endMin), false, Meeting: meeting);
        Assert.False(MeetingLinks.IsJoinable(At(20, 50), now));
        Assert.True(MeetingLinks.IsJoinable(At(15, 45), now));
        Assert.True(MeetingLinks.IsJoinable(At(-30, 5), now));
        Assert.False(MeetingLinks.IsJoinable(At(-60, 0), now));
        Assert.False(MeetingLinks.IsJoinable(At(5, 30) with { Meeting = null }, now));
    }

    [Fact]
    public void TheStartingNoticeIsForTheLastMinuteOnly()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var e = new CalendarEvent("x", now.AddSeconds(40), now.AddMinutes(30), false, Meeting: new MeetingLink("Teams", "https://teams.microsoft.com/meet/1"));
        Assert.True(MeetingLinks.IsStarting(e, now));
        Assert.False(MeetingLinks.IsStarting(e, now.AddMinutes(-2)));
        Assert.False(MeetingLinks.IsStarting(e, now.AddMinutes(1)));
    }

    [Fact]
    public void TheParserFindsLinksInDescriptionsAndConferenceFields()
    {
        const string ics = "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Standup\r\nDTSTART:20261015T090000\r\nDTEND:20261015T091500\r\n" +
            "DESCRIPTION:Join Microsoft Teams Meeting\\nhttps://teams.microsoft.com/l/meetup-join/19%3a\r\n abc%40thread.v2/0\\nMeeting ID: 1\r\n" +
            "END:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:b\r\nSUMMARY:Review\r\nDTSTART:20261015T110000\r\nDTEND:20261015T120000\r\n" +
            "X-GOOGLE-CONFERENCE:https://meet.google.com/xyz-abcd-efg\r\nLOCATION:Room 2\r\n" +
            "END:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:c\r\nSUMMARY:Lunch\r\nDTSTART:20261015T130000\r\nDTEND:20261015T140000\r\nLOCATION:Cafe\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";
        var events = IcsParser.Parse(ics, new DateTime(2026, 10, 15), new DateTime(2026, 10, 16));
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/19%3aabc%40thread.v2/0", events.Single(e => e.Title == "Standup").Meeting?.Url);
        Assert.Equal("Google Meet", events.Single(e => e.Title == "Review").Meeting?.Provider);
        Assert.Null(events.Single(e => e.Title == "Lunch").Meeting);
    }

    [Fact]
    public void MeetingNoticesOpenTheCalendar() =>
        Assert.Equal("calendar", IslandLogic.NoticeSection(IslandNoticeEvent.Meeting));
}
