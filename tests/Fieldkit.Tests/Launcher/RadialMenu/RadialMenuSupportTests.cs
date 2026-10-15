// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json.Nodes;
using Fieldkit.Features.Launcher.RadialMenu;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Launcher.RadialMenu;

public class RadialMenuSupportTests
{
    private static RadialMenuItem Item(RadialItemKind kind, string payload = "x") => new(kind, payload);

    // -------------------------------------------------------------- payloads

    [Theory]
    [InlineData(RadialItemKind.App, "", false)]
    [InlineData(RadialItemKind.App, "C:\\x.exe", true)]
    [InlineData(RadialItemKind.File, "~\\Downloads", true)]
    [InlineData(RadialItemKind.Url, "example.com", true)]
    [InlineData(RadialItemKind.Url, "not a url", false)]
    [InlineData(RadialItemKind.Shortcut, "Ctrl+Shift+T", true)]
    [InlineData(RadialItemKind.Shortcut, "Ctrl+", false)]
    [InlineData(RadialItemKind.Tool, "screenshot", true)]
    [InlineData(RadialItemKind.Tool, "", false)]
    [InlineData(RadialItemKind.Media, "nowPlaying", true)]
    [InlineData(RadialItemKind.Media, "volumeUp", false)]
    [InlineData(RadialItemKind.Submenu, "", true)]
    public void ValidatesPayloads(RadialItemKind kind, string payload, bool valid)
    {
        Assert.Equal(valid, RadialMenuSupport.IsValidPayload(Item(kind, payload)));
    }

    [Fact]
    public void SanitizingDropsDeadSlicesAndClamps()
    {
        var duplicate = Item(RadialItemKind.App, "a.exe");
        var items = new List<RadialMenuItem>
        {
            duplicate,
            duplicate,                                                  // same id
            Item(RadialItemKind.Url, "  example.com/page "),
            Item(RadialItemKind.Shortcut, "ctrl + shift + t"),
            Item(RadialItemKind.Media, "bogus"),
            new(RadialItemKind.App, "b.exe") { Name = "  " + new string('n', 80) + " " },
            new(RadialItemKind.App, "c.exe") { Children = [Item(RadialItemKind.App)] },
        };
        var result = RadialMenuSupport.Sanitized(items);
        Assert.Equal(5, result.Count);
        Assert.Equal("https://example.com/page", result[1].Payload);
        Assert.Equal("Ctrl+Shift+T", result[2].Payload);
        Assert.Equal(60, result[3].Name.Length);
        Assert.Empty(result[4].Children);
    }

    [Fact]
    public void SanitizingCapsWheelsAtTwelve()
    {
        var items = Enumerable.Range(0, 20).Select(i => Item(RadialItemKind.App, $"{i}.exe"));
        Assert.Equal(12, RadialMenuSupport.Sanitized(items).Count);
    }

    [Fact]
    public void SubmenusNestOneLevelOnly()
    {
        var inner = new RadialMenuItem(RadialItemKind.Submenu) { Children = [Item(RadialItemKind.App)] };
        var outer = new RadialMenuItem(RadialItemKind.Submenu) { Children = [Item(RadialItemKind.App), inner] };
        var result = RadialMenuSupport.Sanitized([outer]);
        Assert.Single(result);
        Assert.Single(result[0].Children);
        Assert.Equal(RadialItemKind.App, result[0].Children[0].Kind);
    }

    [Fact]
    public void RejectsOversizedOrNonImageIcons()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        Assert.True(RadialMenuSupport.IsAcceptableIcon(png));
        Assert.False(RadialMenuSupport.IsAcceptableIcon([1, 2, 3, 4]));
        Assert.False(RadialMenuSupport.IsAcceptableIcon(new byte[RadialMenuSupport.MaxStoredIconBytes + 1]));
        var item = new RadialMenuItem(RadialItemKind.Url, "a.com") { CustomIconData = [1, 2, 3] };
        Assert.Null(RadialMenuSupport.Sanitized([item])[0].CustomIconData);
    }

    [Fact]
    public void UnavailableSlicesDisappearWithEmptySubmenus()
    {
        var tool = Item(RadialItemKind.Tool, "gone");
        var submenu = new RadialMenuItem(RadialItemKind.Submenu) { Children = [Item(RadialItemKind.Tool, "gone")] };
        var mixed = new RadialMenuItem(RadialItemKind.Submenu) { Children = [Item(RadialItemKind.Tool, "gone"), Item(RadialItemKind.App)] };
        var result = RadialMenuSupport.AvailableItems([tool, submenu, mixed, Item(RadialItemKind.App)], i => i.Payload != "gone");
        Assert.Equal(2, result.Count);
        Assert.Single(result[0].Children);
        Assert.Equal(2, mixed.Children.Count); // the source is untouched
    }

    [Fact]
    public void DetectsNowPlayingAndWindowLayoutAtAnyDepth()
    {
        var nested = new RadialMenuItem(RadialItemKind.Submenu) { Children = [Item(RadialItemKind.Media, "nowPlaying"), Item(RadialItemKind.WindowLayout, "leftHalf")] };
        Assert.True(RadialMenuSupport.ContainsNowPlaying([nested]));
        Assert.True(RadialMenuSupport.UsesWindowLayout([nested]));
        Assert.False(RadialMenuSupport.ContainsNowPlaying([Item(RadialItemKind.Media, "playPause")]));
    }

    [Fact]
    public void ExpandsHomeAndVariables()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Downloads"), RadialMenuSupport.ExpandPath("~\\Downloads"));
        Assert.Equal(home, RadialMenuSupport.ExpandPath("~"));
        Assert.Equal(Environment.GetEnvironmentVariable("WINDIR") + "\\notepad.exe", RadialMenuSupport.ExpandPath("%WINDIR%\\notepad.exe"));
    }

    // ------------------------------------------------------------------ URLs

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData(" example.com/page?x=1 ", "https://example.com/page?x=1")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("mailto:me@example.com", "mailto:me@example.com")]
    [InlineData("tel:5551234", "tel:5551234")]
    [InlineData("example.com:8080", "https://example.com:8080")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("obsidian://open?vault=x", "obsidian://open?vault=x")]
    [InlineData("", null)]
    [InlineData("two words", null)]
    [InlineData("https://", null)]
    public void NormalizesUrls(string raw, string? expected)
    {
        Assert.Equal(expected, RadialMenuSupport.NormalizedUrl(raw));
    }

    [Theory]
    [InlineData("tel:555", true)]
    [InlineData("example.com:8080", false)]
    [InlineData("localhost:3000", false)]
    [InlineData("a://b", true)]
    [InlineData("example.com", false)]
    [InlineData("1abc:x", false)]
    public void DetectsSchemes(string value, bool expected)
    {
        Assert.Equal(expected, RadialMenuSupport.HasExplicitScheme(value));
    }

    // -------------------------------------------------------------- profiles

    [Fact]
    public void RoundTripsProfilesThroughJson()
    {
        var profile = RadialPresets.Create(RadialMenuPreset.Media, "Music", RadialMenuColor.Pink, "Ctrl+Alt+M", "back");
        profile.MouseModifiers = "Shift";
        profile.Items.Add(new RadialMenuItem(RadialItemKind.Submenu)
        {
            Name = "More",
            SymbolName = "star.fill",
            Children = [new RadialMenuItem(RadialItemKind.Url, "example.com") { CustomIconData = [0x89, 0x50, 0x4E, 0x47, 9] }],
        });
        var json = RadialMenuSupport.EncodeProfiles([profile]);
        var decoded = RadialMenuSupport.DecodeProfiles(json);
        var p = Assert.Single(decoded);
        Assert.Equal(profile.Id, p.Id);
        Assert.Equal("Music", p.Name);
        Assert.Equal(RadialMenuColor.Pink, p.Color);
        Assert.Equal("Ctrl+Alt+M", p.Shortcut);
        Assert.Equal(RadialMouseButton.Back, p.Button);
        Assert.Equal(Modifiers.Shift, p.ButtonModifiers);
        Assert.Equal("media", p.Preset);
        Assert.Equal(5, p.Items.Count);
        var sub = p.Items[4];
        Assert.Equal("More", sub.Name);
        Assert.Equal("star.fill", sub.SymbolName);
        Assert.Equal("https://example.com", sub.Children[0].Payload);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 9 }, sub.Children[0].CustomIconData);
    }

    [Fact]
    public void WritesUpstreamCompatibleFields()
    {
        var json = RadialMenuSupport.EncodeProfiles([RadialPresets.Create(RadialMenuPreset.General)]);
        var obj = (JsonObject)JsonNode.Parse(json)![0]!;
        Assert.Equal("accent", (string?)obj["color"]);
        Assert.Equal("off", (string?)obj["mouseButton"]);
        Assert.Equal("general", (string?)obj["preset"]);
        var first = (JsonObject)obj["items"]![0]!;
        Assert.Equal("media", (string?)first["kind"]);
        Assert.Equal("playPause", (string?)first["payload"]);
        Assert.Matches("^[0-9A-F-]{36}$", (string?)first["id"]);
    }

    [Fact]
    public void ReadsUpstreamBlobsLossily()
    {
        const string json = """
            [
              {"id":"6F9619FF-8B86-D011-B42D-00CF4FC964FF","name":"Mac wheel","color":"indigo","shortcut":"",
               "mouseButton":"button:7","trackpadTap":true,
               "items":[
                 {"id":"11111111-1111-1111-1111-111111111111","kind":"tool","payload":"screenshot"},
                 {"kind":"hologram","payload":"x"},
                 {"kind":"media","payload":"nextTrack","children":[{"kind":"app","payload":"y"}]},
                 "garbage"
               ]},
              {"name":"No id","items":[]},
              42
            ]
            """;
        var profiles = RadialMenuSupport.DecodeProfiles(json);
        Assert.Equal(2, profiles.Count);
        var mac = profiles[0];
        Assert.Equal(RadialMenuColor.Indigo, mac.Color);
        Assert.Equal(RadialMouseButton.Off, mac.Button);   // button 7 is not visible on Windows
        Assert.True(mac.TrackpadTap);                      // kept for round trips
        Assert.Equal(2, mac.Items.Count);
        Assert.Empty(mac.Items[1].Children);               // non-submenus carry no children
        Assert.NotEqual(Guid.Empty, profiles[1].Id);
    }

    [Fact]
    public void CorruptOrMissingBlobsSeedTheStarterWheel()
    {
        var fresh = RadialMenuSupport.DecodeProfiles(null);
        var p = Assert.Single(fresh);
        Assert.Equal(RadialMenuSupport.DefaultShortcut, p.Shortcut);
        Assert.Equal(6, p.Items.Count);
        Assert.Equal("general", p.Preset);

        Assert.Single(RadialMenuSupport.DecodeProfiles("{not json"));
        Assert.Single(RadialMenuSupport.DecodeProfiles("[]")); // an empty list never leaves zero wheels
    }

    [Fact]
    public void MigratesTheLegacySingleWheel()
    {
        var legacyItems = RadialMenuSupport.EncodeItems([Item(RadialItemKind.Url, "example.com")]);
        var migrated = RadialMenuSupport.DecodeProfiles(null, legacyItems, "Ctrl+Shift+Space", "forward");
        var p = Assert.Single(migrated);
        Assert.Equal("Ctrl+Shift+Space", p.Shortcut);
        Assert.Equal(RadialMouseButton.Forward, p.Button);
        Assert.Equal("https://example.com", Assert.Single(p.Items).Payload);
        Assert.Equal("general", p.Preset);

        // A present but empty legacy list is the user's own (empty) wheel.
        Assert.Empty(RadialMenuSupport.DecodeProfiles(null, "[]").Single().Items);
    }

    [Fact]
    public void SanitizedProfilesKeepEachTriggerOnOneWheel()
    {
        var a = new RadialMenuProfile { MouseButton = "middle", MouseModifiers = "ctrl" };
        var b = new RadialMenuProfile { MouseButton = "middle", MouseModifiers = "Ctrl" };
        var c = new RadialMenuProfile { MouseButton = "middle" };
        var d = new RadialMenuProfile { Id = a.Id, Name = "dupe" };
        var e = new RadialMenuProfile { Shortcut = "nonsense+" };
        var result = RadialMenuSupport.SanitizedProfiles([a, b, c, d, e]);
        Assert.Equal(4, result.Count);
        Assert.Equal("Ctrl", result[0].MouseModifiers);
        Assert.Equal(RadialMouseButton.Off, result[1].Button);
        Assert.Equal(RadialMouseButton.Middle, result[2].Button);
        Assert.Equal(string.Empty, result[3].Shortcut);
    }

    [Fact]
    public void DuplicatesStartWithoutTriggers()
    {
        var original = RadialPresets.Create(RadialMenuPreset.Tools, "Tools", shortcut: "Ctrl+Alt+T", mouseButton: "back");
        var copy = original.Duplicate("Tools 2");
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Tools 2", copy.Name);
        Assert.Equal(string.Empty, copy.Shortcut);
        Assert.Equal(RadialMouseButton.Off, copy.Button);
        Assert.Equal(original.Items.Count, copy.Items.Count);
        Assert.False(RadialMenuSupport.HasTrigger(copy));
        Assert.True(RadialMenuSupport.HasTrigger(original));
    }

    [Fact]
    public void AssigningAMouseButtonReleasesItElsewhere()
    {
        var a = new RadialMenuProfile { MouseButton = "back" };
        var b = new RadialMenuProfile { MouseButton = "back", MouseModifiers = "Ctrl" };
        var c = new RadialMenuProfile();
        var result = RadialMenuSupport.AssigningMouseButton(RadialMouseButton.Back, Modifiers.None, c.Id, [a, b, c]);
        Assert.Equal(RadialMouseButton.Off, result[0].Button);
        Assert.Equal(RadialMouseButton.Back, result[1].Button);  // different keys, still its own
        Assert.Equal(RadialMouseButton.Back, result[2].Button);
        Assert.Equal(RadialMouseButton.Back, a.Button);           // inputs untouched

        var off = RadialMenuSupport.AssigningMouseButton(RadialMouseButton.Off, Modifiers.Ctrl, b.Id, result);
        Assert.Equal(RadialMouseButton.Off, off[1].Button);
        Assert.Equal(string.Empty, off[1].MouseModifiers);
        Assert.Equal(RadialMouseButton.Back, off[2].Button);
    }

    [Fact]
    public void MatchesMouseTriggersExactly()
    {
        var a = new RadialMenuProfile { MouseButton = "middle", MouseModifiers = "Ctrl" };
        var b = new RadialMenuProfile { MouseButton = "forward" };
        var c = new RadialMenuProfile { MouseButton = "forward" };
        var triggers = RadialMenuSupport.MouseTriggers([a, b, c]);
        Assert.Equal(2, triggers.Count);
        Assert.Equal(a.Id, RadialMenuSupport.MatchMouseTrigger(triggers, RadialMouseButton.Middle, Modifiers.Ctrl)?.ProfileId);
        Assert.Null(RadialMenuSupport.MatchMouseTrigger(triggers, RadialMouseButton.Middle, Modifiers.None));
        Assert.Null(RadialMenuSupport.MatchMouseTrigger(triggers, RadialMouseButton.Middle, Modifiers.Ctrl | Modifiers.Shift));
        Assert.Equal(b.Id, RadialMenuSupport.MatchMouseTrigger(triggers, RadialMouseButton.Forward, Modifiers.None)?.ProfileId);
    }

    [Theory]
    [InlineData(MouseAction.MiddleDown, 0, RadialMouseButton.Middle)]
    [InlineData(MouseAction.XUp, 1, RadialMouseButton.Back)]
    [InlineData(MouseAction.XDown, 2, RadialMouseButton.Forward)]
    [InlineData(MouseAction.LeftDown, 0, RadialMouseButton.Off)]
    public void MapsHookButtons(MouseAction action, int xButton, RadialMouseButton expected)
    {
        Assert.Equal(expected, RadialMenuSupport.ButtonOf(action, xButton));
    }

    [Fact]
    public void FindsShortcutConflictsBetweenWheels()
    {
        var a = new RadialMenuProfile { Shortcut = "Ctrl+Alt+Space" };
        var b = new RadialMenuProfile { Shortcut = "" };
        Assert.Same(a, RadialMenuSupport.ProfileUsing(Shortcut.Parse("ctrl+alt+space"), [a, b], b.Id));
        Assert.Null(RadialMenuSupport.ProfileUsing(Shortcut.Parse("Ctrl+Alt+Space"), [a, b], a.Id));
        Assert.Null(RadialMenuSupport.ProfileUsing(Shortcut.None, [a, b], a.Id));
    }

    [Theory]
    [InlineData("", Modifiers.None)]
    [InlineData("Ctrl+Shift", Modifiers.Ctrl | Modifiers.Shift)]
    [InlineData("alt + win", Modifiers.Alt | Modifiers.Win)]
    public void ParsesModifierLists(string text, Modifiers expected)
    {
        Assert.Equal(expected, RadialMenuSupport.ParseModifiers(text));
        Assert.Equal(expected, RadialMenuSupport.ParseModifiers(RadialMenuSupport.ModifiersText(expected)));
    }

    // ------------------------------------------------------------- activation

    [Theory]
    [InlineData(RadialActivationMode.PressOrHold, true, false, true, true)]
    [InlineData(RadialActivationMode.PressOrHold, true, false, false, false)]  // F-key alone never holds
    [InlineData(RadialActivationMode.PressOrHold, false, true, false, true)]   // held mouse button
    [InlineData(RadialActivationMode.Hold, true, false, true, true)]
    [InlineData(RadialActivationMode.Press, true, true, true, false)]
    [InlineData(RadialActivationMode.PressOrHold, false, false, true, false)]  // Try it / trackpad
    public void DecidesWhetherSessionsStartHeld(RadialActivationMode mode, bool requestedHold, bool heldButton, bool hasModifiers, bool expected)
    {
        Assert.Equal(expected, RadialMenuSupport.StartsHeld(mode, requestedHold, heldButton, hasModifiers));
    }

    [Theory]
    [InlineData(RadialActivationMode.PressOrHold, true, RadialReleaseAction.Select)]
    [InlineData(RadialActivationMode.PressOrHold, false, RadialReleaseAction.StayOpen)]
    [InlineData(RadialActivationMode.Hold, false, RadialReleaseAction.Dismiss)]
    [InlineData(RadialActivationMode.Hold, true, RadialReleaseAction.Select)]
    [InlineData(RadialActivationMode.Press, false, RadialReleaseAction.StayOpen)]
    public void ResolvesReleases(RadialActivationMode mode, bool hasSelection, RadialReleaseAction expected)
    {
        Assert.Equal(expected, RadialMenuSupport.ReleaseAction(mode, hasSelection));
    }

    [Fact]
    public void ParsesActivationModesLeniently()
    {
        Assert.Equal(RadialActivationMode.Hold, RadialRaw.ParseActivation("hold"));
        Assert.Equal(RadialActivationMode.PressOrHold, RadialRaw.ParseActivation(null));
        Assert.Equal(RadialActivationMode.PressOrHold, RadialRaw.ParseActivation("sideways"));
    }

    [Fact]
    public void RotatesTheHighlightAroundTheWheel()
    {
        Assert.Equal(0, RadialMenuSupport.RotateHighlight(null, 1, 5));
        Assert.Equal(4, RadialMenuSupport.RotateHighlight(null, -1, 5));
        Assert.Equal(0, RadialMenuSupport.RotateHighlight(4, 1, 5));
        Assert.Equal(4, RadialMenuSupport.RotateHighlight(0, -1, 5));
        Assert.Null(RadialMenuSupport.RotateHighlight(0, 1, 0));
    }

    [Theory]
    [InlineData(0x31, 5, 0)]
    [InlineData(0x35, 5, 4)]
    [InlineData(0x36, 5, null)]
    [InlineData(0x63, 5, 2)]   // numpad 3
    [InlineData(0x30, 5, null)]
    [InlineData(0x41, 5, null)]
    public void DigitsPickSlices(int vk, int count, int? expected)
    {
        Assert.Equal(expected, RadialMenuSupport.DigitIndex(vk, count));
    }

    // ---------------------------------------------------------------- presets

    [Fact]
    public void PresetsMatchUpstream()
    {
        Assert.Equal(6, RadialPresets.MakeItems(RadialMenuPreset.General).Count);
        Assert.Equal(4, RadialPresets.MakeItems(RadialMenuPreset.Media).Count);
        Assert.Equal(6, RadialPresets.MakeItems(RadialMenuPreset.Tools).Count);
        Assert.Equal(5, RadialPresets.MakeItems(RadialMenuPreset.WindowLayout).Count);
        Assert.Equal(5, RadialPresets.MakeItems(RadialMenuPreset.QuickToggles).Count);
        Assert.Empty(RadialPresets.MakeItems(RadialMenuPreset.Blank));
        Assert.Equal(RadialMenuColor.Purple, RadialPresets.Create(RadialMenuPreset.Media).Color);
        Assert.Equal(RadialMenuColor.Graphite, RadialPresets.Create(RadialMenuPreset.Blank).Color);
        Assert.All(Enum.GetValues<RadialMenuPreset>().SelectMany(RadialPresets.MakeItems), i => Assert.True(RadialMenuSupport.IsValidPayload(i)));
    }

    [Fact]
    public void NamesNewProfilesAndResolvesTheirPreset()
    {
        Assert.Equal("General 3", RadialMenuSupport.NewProfileName(RadialMenuPreset.General, 2));
        Assert.Equal("Media", RadialMenuSupport.NewProfileName(RadialMenuPreset.Media, 2));
        Assert.Equal(RadialMenuPreset.Tools, RadialMenuSupport.ResolvedPreset(new RadialMenuProfile { Preset = "tools", Name = "x" }));
        Assert.Equal(RadialMenuPreset.Media, RadialMenuSupport.ResolvedPreset(new RadialMenuProfile { Name = "media" }));
        Assert.Null(RadialMenuSupport.ResolvedPreset(new RadialMenuProfile { Name = "My stuff" }));
    }

    [Fact]
    public void ResolvesProfileColorsPerTheme()
    {
        var accent = System.Windows.Media.Color.FromRgb(1, 2, 3);
        Assert.Equal(accent, RadialColors.Resolve(RadialMenuColor.Accent, true, accent));
        Assert.NotEqual(RadialColors.Resolve(RadialMenuColor.Blue, true, accent), RadialColors.Resolve(RadialMenuColor.Blue, false, accent));
        Assert.Equal(12, Enum.GetValues<RadialMenuColor>().Select(c => RadialColors.Resolve(c, true, accent)).Distinct().Count());
    }
}
