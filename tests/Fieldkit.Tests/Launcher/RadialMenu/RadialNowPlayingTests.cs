// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Launcher.RadialMenu;

namespace Fieldkit.Tests.Launcher.RadialMenu;

public class RadialNowPlayingTests
{
    [Fact]
    public void SanitizesText()
    {
        Assert.Null(RadialNowPlayingSupport.SanitizedText(null));
        Assert.Null(RadialNowPlayingSupport.SanitizedText("  \n "));
        Assert.Equal("Song", RadialNowPlayingSupport.SanitizedText("  So\u0007ng\n"));
        Assert.Equal("AB", RadialNowPlayingSupport.SanitizedText("A\u2028B"));
        Assert.Equal(300, RadialNowPlayingSupport.SanitizedText(new string('x', 500))!.Length);
    }

    [Fact]
    public void SnapshotsOnlyWhilePlayingAndIdentified()
    {
        Assert.Null(RadialNowPlayingSupport.Snapshot("Song", "A", null, null, "Spotify.exe", isPlaying: false));
        Assert.Null(RadialNowPlayingSupport.Snapshot(" ", null, null, null, null, isPlaying: true));
        var byApp = RadialNowPlayingSupport.Snapshot(null, null, null, null, "Spotify.exe", isPlaying: true);
        Assert.NotNull(byApp);
        Assert.Null(byApp!.RadialLabel);
        var full = RadialNowPlayingSupport.Snapshot("Song", "Artist", "Album", [1, 2], "x\u0001", isPlaying: true)!;
        Assert.Equal("Song\nArtist", full.RadialLabel);
        Assert.Null(full.AppId); // control characters reject the id
        Assert.Equal(new byte[] { 1, 2 }, full.ArtworkData);
        var huge = RadialNowPlayingSupport.Snapshot("Song", null, null, new byte[RadialNowPlayingSupport.MaximumArtworkBytes + 1], null, true)!;
        Assert.Null(huge.ArtworkData);
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome", "chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FindsDesktopProcessNames(string? appId, string? expected)
    {
        Assert.Equal(expected, RadialNowPlayingSupport.ProcessNameOf(appId));
    }

    [Theory]
    [InlineData("example.com/page?x=1#top", "https://example.com/favicon.ico")]
    [InlineData("http://user:pw@example.com:8080/a", "http://example.com:8080/favicon.ico")]
    [InlineData("mailto:me@example.com", null)]
    [InlineData("not a url", null)]
    public void BuildsFaviconUrls(string raw, string? expected)
    {
        Assert.Equal(expected, RadialFavicon.FaviconUrl(raw)?.ToString());
    }

    [Fact]
    public void RedirectsStayOnOneOrigin()
    {
        var origin = new Uri("https://example.com/favicon.ico");
        Assert.True(RadialFavicon.SameOrigin(origin, new Uri("https://EXAMPLE.com/static/icon.png")));
        Assert.False(RadialFavicon.SameOrigin(origin, new Uri("http://example.com/favicon.ico")));
        Assert.False(RadialFavicon.SameOrigin(origin, new Uri("https://cdn.example.com/favicon.ico")));
        Assert.False(RadialFavicon.SameOrigin(origin, new Uri("https://example.com:8443/favicon.ico")));
    }

    [Theory]
    [InlineData(16, 16, true)]
    [InlineData(4096, 4096, true)]
    [InlineData(4097, 10, false)]
    [InlineData(0, 10, false)]
    public void ChecksIconDimensions(int w, int h, bool expected)
    {
        Assert.Equal(expected, RadialFavicon.DimensionsAreSafe(w, h));
    }

    [Fact]
    public void ConvertsIconsToSmallPngs()
    {
        byte[]? png = null;
        Fieldkit.Tests.Apps.StaRunner.Run(() =>
        {
            // A 2x2 BMP decodes and scales to a 64 px PNG.
            byte[] bmp =
            [
                0x42, 0x4D, 0x46, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0x18, 0,
                0, 0, 0, 0, 0x10, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0xFF, 0, 0xFF, 0xFF, 0xFF, 0, 0,
            ];
            png = RadialFavicon.ToPng(bmp);
        });
        Assert.NotNull(png);
        Assert.True(RadialMenuSupport.IsAcceptableIcon(png!));
        Assert.Null(RadialFavicon.ToPng([1, 2, 3]));
    }
}
