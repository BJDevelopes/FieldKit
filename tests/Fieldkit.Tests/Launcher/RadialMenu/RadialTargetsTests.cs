// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Launcher.RadialMenu;

namespace Fieldkit.Tests.Launcher.RadialMenu;

public class RadialTargetsTests
{
    private static readonly ToolAction Shot = new("screenshot.capture", "screenshot", "Capture screen", "g", () => { });
    private static readonly ToolAction ShotWindow = new("screenshot.window", "screenshot", "Capture window", "g", () => { });
    private static readonly ToolAction Own = new("radialMenu.open", "radialMenu", "Open the radial menu", "g", () => { });
    private static readonly ToolAction Left = new("windowLayout.leftHalf", "windowLayout", "Left half", "g", () => { });
    private static readonly ToolAction Max = new("windowLayout.maximize", "windowLayout", "Maximize", "g", () => { });
    private static readonly QuickToggleDef Dark = new("quickToggles.darkMode", "Dark mode", "g", () => false, () => { });
    private static readonly QuickToggleDef Bin = new("emptyRecycleBin", "Empty Recycle Bin", "g", null, () => { });

    private static RadialCatalog Catalog(params string[] unavailable) =>
        new([Shot, ShotWindow, Own, Left, Max], [Dark, Bin], id => !unavailable.Contains(id));

    [Fact]
    public void ResolvesToolsByIdOrUpstreamFeatureName()
    {
        var catalog = Catalog();
        Assert.Same(ShotWindow, catalog.Tool("screenshot.window"));
        Assert.Same(Shot, catalog.Tool("screenshot"));          // upstream raw value → first action of the feature
        Assert.Null(catalog.Tool("colorPicker"));
        Assert.Null(catalog.Tool("radialMenu.open"));           // never its own action
        Assert.Null(catalog.Tool("windowLayout.leftHalf"));     // layouts have their own kind
        Assert.Null(Catalog("screenshot").Tool("screenshot"));  // uninstalled features vanish
    }

    [Fact]
    public void ResolvesLayoutsByUpstreamName()
    {
        var catalog = Catalog();
        Assert.Same(Left, catalog.Layout("leftHalf"));
        Assert.Same(Max, catalog.Layout("windowLayout.maximize"));
        Assert.Null(catalog.Layout("topHalf"));
        Assert.Null(Catalog("windowLayout").Layout("leftHalf"));
        Assert.Equal(2, catalog.LayoutChoices.Count());
    }

    [Fact]
    public void ResolvesQuickTogglesThroughAliases()
    {
        var catalog = Catalog();
        Assert.Same(Dark, catalog.Toggle("darkMode"));
        Assert.Same(Bin, catalog.Toggle("emptyTrash"));
        Assert.Same(Bin, catalog.Toggle("emptyRecycleBin"));
        Assert.Null(catalog.Toggle("screenSaver"));
        Assert.Null(Catalog("quickToggles").Toggle("darkMode"));
    }

    [Fact]
    public void RunnabilityFollowsResolution()
    {
        var catalog = Catalog();
        Assert.True(catalog.IsRunnable(new RadialMenuItem(RadialItemKind.Tool, "screenshot")));
        Assert.False(catalog.IsRunnable(new RadialMenuItem(RadialItemKind.Tool, "cleaner")));
        Assert.True(catalog.IsRunnable(new RadialMenuItem(RadialItemKind.App, "x.exe")));
        Assert.DoesNotContain(Own, catalog.ToolChoices);
    }

    [Fact]
    public void NamesSlices()
    {
        var catalog = Catalog();
        Assert.Equal("Custom", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Tool, "screenshot") { Name = "Custom" }));
        Assert.Equal("Capture screen", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Tool, "screenshot")));
        Assert.Equal("Color picker", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Tool, "colorPicker")));
        Assert.Equal("Left half", catalog.DisplayName(new RadialMenuItem(RadialItemKind.WindowLayout, "leftHalf")));
        Assert.Equal("Empty Recycle Bin", catalog.DisplayName(new RadialMenuItem(RadialItemKind.QuickToggle, "emptyTrash")));
        Assert.Equal("github.com", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Url, "github.com/x")));
        Assert.Equal("Ctrl+Shift+T", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Shortcut, "ctrl+shift+t")));
        Assert.Equal("Play or pause", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Media, "playPause")));
        Assert.Equal("Submenu", catalog.DisplayName(new RadialMenuItem(RadialItemKind.Submenu)));
    }

    [Fact]
    public void NamesNowPlayingByState()
    {
        var catalog = Catalog();
        var item = new RadialMenuItem(RadialItemKind.Media, "nowPlaying");
        Assert.Equal("Now Playing", catalog.DisplayName(item));
        Assert.Equal("Now Playing", catalog.DisplayName(item, RadialNowPlayingState.Loading));
        Assert.Equal("Nothing playing", catalog.DisplayName(item, RadialNowPlayingState.NothingPlaying));
        var playing = RadialNowPlayingState.Playing(new RadialNowPlayingSnapshot("Song", "Artist", null, null, null));
        Assert.Equal("Song\nArtist", catalog.DisplayName(item, playing));
        var anonymous = RadialNowPlayingState.Playing(new RadialNowPlayingSnapshot(null, null, null, null, "Spotify.exe"));
        Assert.Equal("Now Playing", catalog.DisplayName(item, anonymous));
    }

    [Theory]
    [InlineData("C:\\Windows\\notepad.exe", "notepad")]
    [InlineData("C:\\Users\\me\\Desktop\\Code.lnk", "Code")]
    [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
    [InlineData("C:\\Users\\me\\Projects\\", "Projects")]
    [InlineData("~\\Downloads", "Downloads")]
    [InlineData("~", "Home")]
    [InlineData("D:", "D:\\")]
    [InlineData("shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "WindowsCalculator")]
    [InlineData("shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!Terminal", "Terminal")]
    public void NamesFiles(string payload, string expected)
    {
        Assert.Equal(expected, RadialCatalog.FileDisplayName(payload));
    }
}
