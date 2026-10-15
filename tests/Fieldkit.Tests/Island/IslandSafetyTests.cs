// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using Fieldkit.Core;
using Fieldkit.Features.Island;
using Fieldkit.Features.Island.Calendar;

namespace Fieldkit.Tests.Island;

public class IslandSafetyTests
{
    [Theory]
    [InlineData("https://teams.example.com/meet/123")]
    [InlineData("http://example.com/event")]
    [InlineData("mailto:someone@example.com")]
    public void WebLinksAreAllowed(string url) => Assert.NotNull(IslandLinks.SafeWebUri(url));

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData("//server/share/x.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-word:ofe|u|https://x/y.docx")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search-ms:query=x")]
    [InlineData("calc.exe")]
    [InlineData("relative/path")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsRefused(string? url) => Assert.Null(IslandLinks.SafeWebUri(url));

    [Fact]
    public void RefusedLinksAreNotOpened()
    {
        Assert.False(IslandLinks.OpenWeb(@"\\server\share\x.exe"));
        Assert.False(IslandLinks.OpenWeb("file:///C:/Windows/System32/calc.exe"));
        Assert.False(IslandLinks.OpenLocal(@"\\server\share\x.exe"));
        Assert.False(IslandLinks.OpenLocal("https://example.com/x.exe"));
        Assert.False(IslandLinks.OpenLocal(@"C:\definitely\missing\file.exe"));
    }

    [Fact]
    public void LocalPathsMustExistAndNotBeNetworkPaths()
    {
        var temp = Path.GetTempPath();
        Assert.True(IslandLinks.IsLocalPath(temp));
        Assert.False(IslandLinks.IsLocalPath(@"\\localhost\c$\Windows"));
        Assert.False(IslandLinks.IsLocalPath("relative.txt"));
    }

    [Fact]
    public void IcsParserDropsUnsafeEventUrls()
    {
        static string Event(string uid, string url) => string.Join("\r\n",
            "BEGIN:VEVENT", "UID:" + uid, "SUMMARY:" + uid, "DTSTART:20261014T100000Z", "DTEND:20261014T110000Z", "URL:" + url, "END:VEVENT");
        var ics = string.Join("\r\n", "BEGIN:VCALENDAR",
            Event("web", "https://example.com/join"),
            Event("unc", @"\\attacker\share\payload.exe"),
            Event("file", "file:///C:/Windows/System32/calc.exe"),
            Event("proto", "ms-msdt:/id PCWDiagnostic"),
            "END:VCALENDAR");
        var from = new DateTime(2026, 10, 13);
        var events = IcsParser.Parse(ics, from, from.AddDays(3));
        Assert.Equal(4, events.Count);
        Assert.Equal("https://example.com/join", events.Single(e => e.Title == "web").Url);
        Assert.All(events.Where(e => e.Title != "web"), e => Assert.Null(e.Url));
    }

    private sealed class EndlessStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Fill(buffer, (byte)'A', offset, n);
            _position += n;
            return n;
        }
    }

    [Fact]
    public async Task BodiesWithoutContentLengthAreCappedWhileReading()
    {
        // No Content-Length header and a body larger than the limit: reading stops at the limit.
        using var big = new HttpResponseMessage { Content = new StreamContent(new EndlessStream(5_000_000)) };
        Assert.Null(big.Content.Headers.ContentLength);
        Assert.Null(await IslandLinks.ReadLimitedAsync(big, 1_000_000, CancellationToken.None));

        using var small = new HttpResponseMessage { Content = new StreamContent(new EndlessStream(1000)) };
        Assert.Equal(1000, (await IslandLinks.ReadLimitedAsync(small, 1_000_000, CancellationToken.None))!.Length);

        // A declared length over the limit is refused before reading.
        using var declared = new HttpResponseMessage { Content = new ByteArrayContent(new byte[10]) };
        declared.Content.Headers.ContentLength = 50_000_000;
        Assert.Null(await IslandLinks.ReadLimitedAsync(declared, 1_000_000, CancellationToken.None));
    }

    [Fact]
    public void CalendarFeedLimitIsTwentyMegabytes() => Assert.Equal(20 * 1024 * 1024, CalendarService.MaximumFeedBytes);
}

public class IslandModuleTests
{
    private static readonly string[] Ids =
    [
        "notch", "notchCalendar", "notchNotifications", "notchGestures", "notchTimer", "notchAccessories", "notchLyrics",
        "notchQueue", "notchLiveEqualizer", "notchDownloads", "notchAgents", "notchWatch", "notchMascot",
    ];

    [Fact]
    public void EveryIslandFeatureIsClaimedWithANote()
    {
        var r = new ModuleRegistry();
        new IslandModule().Register(r);
        foreach (var id in Ids)
        {
            Assert.True(r.Parity.TryGetValue(id, out var claim), id);
            Assert.False(string.IsNullOrWhiteSpace(claim!.Note), id);
        }
        Assert.Contains(r.SettingsPages, p => p.Id == "island" && p.Category == SettingsCategory.DynamicIsland && p.Order == 0);
        Assert.Contains(r.SettingsPages, p => p.Id == "agents" && p.FeatureGate.SequenceEqual(["notchAgents"]) && p.Order == 10);
        Assert.All(r.Actions, a => Assert.StartsWith(a.FeatureId + ".", a.Id));
        Assert.All(r.Actions.Where(a => a.ShortcutKey is not null), a => Assert.StartsWith(a.FeatureId + ".", a.ShortcutKey!));
    }

    [Fact]
    public void ExtensionsAreChildrenOfTheIsland()
    {
        foreach (var id in Ids.Skip(1))
            Assert.Equal("notch", FeatureCatalog.Get(id).Parent);
    }

    [Fact]
    public void SettingsKeysUseTheirFeaturePrefix()
    {
        var keys = typeof(IslandKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.All(keys, k => Assert.Contains(k.Split('.')[0], Ids));
    }
}
