// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Sound;

namespace Fieldkit.Tests.Sound;

public class SoundLogicTests
{
    // ------------------------------------------------------------ arrangement

    [Fact]
    public void PinnedAppsComeFirstThenRememberedOrder()
    {
        var arrangement = new MixerArrangement(order: ["b", "a"], pinned: ["c"]);
        var ordered = arrangement.Ordered(["a", "b", "c", "d"], x => x);
        Assert.Equal(["c", "b", "a", "d"], ordered);
    }

    [Fact]
    public void MoveSwapsWithinTheVisibleGroupOnly()
    {
        var arrangement = new MixerArrangement(order: ["a", "hidden", "b", "c"], pinned: ["p"]);
        string[] visible = ["p", "a", "b", "c"];
        arrangement.Move("c", -1, visible);
        Assert.Equal(["a", "hidden", "c", "b", "p"], arrangement.Order);
        // A pinned app has no unpinned neighbour.
        Assert.Null(arrangement.Neighbor("p", 1, visible));
        Assert.Null(arrangement.Neighbor("a", -1, ["p", "a", "b"]));
    }

    [Fact]
    public void TogglePinAddsAndRemoves()
    {
        var arrangement = new MixerArrangement();
        arrangement.TogglePin("x");
        Assert.True(arrangement.IsPinned("x"));
        arrangement.TogglePin("x");
        Assert.False(arrangement.IsPinned("x"));
    }

    // ------------------------------------------------------------------ mixer

    [Theory]
    [InlineData("85", 200, 0.85)]
    [InlineData(" 150 % ", 200, 1.5)]
    [InlineData("250", 200, 2.0)]
    [InlineData("-5", 100, 0.0)]
    [InlineData("120", 100, 1.0)]
    public void VolumeTextParses(string text, int max, double expected) =>
        Assert.Equal(expected, SoundLogic.VolumeFraction(text, max, CultureInfo.InvariantCulture)!.Value, 6);

    [Fact]
    public void VolumeTextUsesTheCultureDecimalSeparator() =>
        Assert.Equal(0.425, SoundLogic.VolumeFraction("42,5", 200, new CultureInfo("de-DE"))!.Value, 6);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void InvalidVolumeTextIsRejected(string text) => Assert.Null(SoundLogic.VolumeFraction(text, 200, CultureInfo.InvariantCulture));

    [Fact]
    public void HideInactiveKeepsPlayingAdjustedAndRoutedApps()
    {
        Assert.True(SoundLogic.ShouldShowApp(false, 1, false, hideInactive: false));
        Assert.False(SoundLogic.ShouldShowApp(false, 1, false, hideInactive: true));
        Assert.True(SoundLogic.ShouldShowApp(true, 1, false, hideInactive: true));
        Assert.True(SoundLogic.ShouldShowApp(false, 0.5, false, hideInactive: true));
        Assert.True(SoundLogic.ShouldShowApp(false, 1, true, hideInactive: true));
    }

    [Fact]
    public void BoostOnlyShowsWhileTheSessionIsAtFullVolume()
    {
        Assert.Equal(1.5, SoundLogic.DisplayLevel(1f, 150), 6);
        Assert.Equal(0.5, SoundLogic.DisplayLevel(0.5f, 150), 6);
        Assert.Equal(1.0, SoundLogic.DisplayLevel(1f, null), 6);
        Assert.Equal(2.0, SoundLogic.DisplayLevel(1f, 900), 6);
    }

    [Fact]
    public void AppIdIsTheExecutableName()
    {
        Assert.Equal("chrome", SoundLogic.AppId(@"C:\Program Files\Google\Chrome\Application\chrome.exe", null));
        Assert.Equal("discord", SoundLogic.AppId(null, "Discord"));
        Assert.Equal(string.Empty, SoundLogic.AppId(null, null));
    }

    [Fact]
    public void PolicyDeviceIdsAreUnpacked()
    {
        const string token = @"\\?\SWD#MMDEVAPI#";
        const string render = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        const string capture = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var id = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
        Assert.Equal(id, SoundLogic.UnpackPolicyDeviceId(token + id + render, token, render, capture));
        Assert.Null(SoundLogic.UnpackPolicyDeviceId("", token, render, capture));
    }

    // ---------------------------------------------------------------- devices

    [Theory]
    [InlineData("Headphones (WH-1000XM4)", 10u, true)]
    [InlineData("Speakers (Realtek(R) Audio)", 1u, false)]
    [InlineData("Realtek Digital Output", 3u, true)]
    [InlineData("Headset Earphone (Jabra Evolve2)", 10u, true)]
    public void HeadphonesAreRecognised(string name, uint formFactor, bool expected) =>
        Assert.Equal(expected, SoundLogic.LooksLikeHeadphones(name, formFactor));

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", true)]
    [InlineData("Speakers (Realtek(R) Audio)", false)]
    [InlineData("Jobs speaker", false)]
    public void VirtualDevicesAreRecognised(string name, bool expected) => Assert.Equal(expected, SoundLogic.LooksVirtual(name));

    [Fact]
    public void SwitcherCyclesThroughAvailableSelection()
    {
        var available = new HashSet<string> { "a", "b", "c" };
        Assert.Equal("b", SoundLogic.NextSelectedOutput("a", ["a", "b", "x"], available));
        Assert.Equal("a", SoundLogic.NextSelectedOutput("b", ["a", "b", "x"], available));
        Assert.Equal("a", SoundLogic.NextSelectedOutput("c", ["a", "b"], available));
        Assert.Null(SoundLogic.NextSelectedOutput("a", ["a"], available));
        Assert.Null(SoundLogic.NextSelectedOutput("a", ["x"], available));
    }

    [Fact]
    public void PriorityPicksTheFirstConnectedDevice()
    {
        Assert.Equal("b", SoundLogic.FirstAvailablePriority(["x", "b", "a"], new HashSet<string> { "a", "b" }));
        Assert.Null(SoundLogic.FirstAvailablePriority(["x"], new HashSet<string> { "a" }));
    }

    [Fact]
    public void InitialPriorityListLeadsWithCurrentAndEndsWithVirtual()
    {
        PriorityTier Tier(string id) => id.StartsWith('v') ? PriorityTier.Virtual : PriorityTier.Hardware;
        Assert.Equal(["b", "a", "c", "v1"], SoundLogic.InitialPriorityList(["v1", "a", "b", "c"], "b", Tier));
    }

    [Fact]
    public void NewDevicesArePlacedAboveVirtualOrFirstWhenCurrent()
    {
        PriorityTier Tier(string id) => id.StartsWith('v') ? PriorityTier.Virtual : PriorityTier.Hardware;
        Assert.Equal(["a", "n", "v1"], SoundLogic.PlaceNewPriorityDevice("n", ["a", "v1"], false, Tier));
        Assert.Equal(["n", "a", "v1"], SoundLogic.PlaceNewPriorityDevice("n", ["a", "v1"], true, Tier));
        Assert.Equal(["a", "v1", "v2"], SoundLogic.PlaceNewPriorityDevice("v2", ["a", "v1"], false, Tier));
        Assert.Equal(["a", "v1"], SoundLogic.PlaceNewPriorityDevice("a", ["a", "v1"], true, Tier));
    }

    [Fact]
    public void OnlyArrivalsAndRemovalsCountAsAvailabilityChanges()
    {
        Assert.False(SoundLogic.AvailabilityChanged(null, new HashSet<string> { "a" }));
        Assert.False(SoundLogic.AvailabilityChanged(new HashSet<string> { "a", "b" }, new HashSet<string> { "b", "a" }));
        Assert.True(SoundLogic.AvailabilityChanged(new HashSet<string> { "a" }, new HashSet<string> { "a", "b" }));
    }

    [Fact]
    public void MoveInListSwapsNeighbours()
    {
        Assert.Equal(["b", "a", "c"], SoundLogic.MoveInList(["a", "b", "c"], "b", -1));
        Assert.Equal(["a", "b", "c"], SoundLogic.MoveInList(["a", "b", "c"], "a", -1));
    }

    // -------------------------------------------------------- headphone guard

    [Fact]
    public void HeadphoneGuardLowersOnlyAfterHeadphonesLeave()
    {
        Assert.Equal(HeadphoneGuardAction.Lower,
            SoundLogic.EvaluateHeadphoneGuard(true, "hp", true, false, "spk", false, null));
        // Headphones still connected (user switched manually): leave it.
        Assert.Equal(HeadphoneGuardAction.None,
            SoundLogic.EvaluateHeadphoneGuard(true, "hp", true, true, "spk", false, null));
        // Already lowered this device.
        Assert.Equal(HeadphoneGuardAction.None,
            SoundLogic.EvaluateHeadphoneGuard(true, "hp", true, false, "spk", false, "spk"));
        Assert.Equal(HeadphoneGuardAction.None,
            SoundLogic.EvaluateHeadphoneGuard(false, "hp", true, false, "spk", false, null));
        Assert.Equal(HeadphoneGuardAction.Restore,
            SoundLogic.EvaluateHeadphoneGuard(true, "spk", false, true, "hp", true, "spk"));
    }

    [Fact]
    public void LoweredVolumeIsRestoredOnlyIfUntouched()
    {
        Assert.True(SoundLogic.ShouldRestoreVolume(0.25, 0.252));
        Assert.False(SoundLogic.ShouldRestoreVolume(0.25, 0.4));
        Assert.False(SoundLogic.ShouldRestoreVolume(0.25, null));
        Assert.Equal(10, SoundLogic.SanitizedHeadphoneVolume(0));
        Assert.Equal(100, SoundLogic.SanitizedHeadphoneVolume(150));
    }

    // ------------------------------------------------------------ music block

    [Fact]
    public void ProcessNamesAreNormalised() =>
        Assert.Equal(new HashSet<string> { "music.ui", "wmplayer", "microsoft.media.player" },
            SoundLogic.ParseProcessNames("Music.UI.exe, wmplayer; Microsoft.Media.Player,,"));

    [Fact]
    public void LaunchIsBlockedOnlyWhenAMediaKeyExplainsIt()
    {
        Assert.True(SoundLogic.ShouldBlockLaunch(1500, 1000, null, false));
        Assert.True(SoundLogic.ShouldBlockLaunch(1500, 1000, 900, false));
        // A click or key after the media key: the user may have opened it.
        Assert.False(SoundLogic.ShouldBlockLaunch(1500, 1000, 1200, false));
        Assert.False(SoundLogic.ShouldBlockLaunch(1000 + SoundLogic.MusicLaunchWindowMs + 1, 1000, null, false));
        Assert.False(SoundLogic.ShouldBlockLaunch(1500, null, null, false));
        Assert.False(SoundLogic.ShouldBlockLaunch(1500, 1000, null, wasRunningAtTrigger: true));
    }

    [Theory]
    [InlineData(0xB3, true)]
    [InlineData(0xB0, true)]
    [InlineData(0xAF, false)] // volume up
    [InlineData(0x41, false)]
    public void MediaKeysAreRecognised(int vk, bool expected) => Assert.Equal(expected, SoundLogic.IsMusicLaunchKey(vk));
}
