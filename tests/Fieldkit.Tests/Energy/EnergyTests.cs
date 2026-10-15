// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Energy;

namespace Fieldkit.Tests.Energy;

public class KeepAwakeLogicTests
{
    [Theory]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(727, "12 min 07 s")]
    [InlineData(42, "42 s")]
    [InlineData(0, "0 s")]
    public void RemainingFormats(int seconds, string expected) =>
        Assert.Equal(expected, KeepAwakeLogic.Remaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(3492, "58:12")]
    [InlineData(3912, "1:05:12")]
    [InlineData(59, "0:59")]
    public void CountdownFormats(int seconds, string expected) =>
        Assert.Equal(expected, KeepAwakeLogic.Countdown(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(15, "15m")]
    [InlineData(60, "1h")]
    [InlineData(480, "8h")]
    [InlineData(0, "∞")]
    public void ChipLabels(int minutes, string expected) => Assert.Equal(expected, KeepAwakeLogic.ChipLabel(minutes));

    [Fact]
    public void LongLabels()
    {
        Assert.Equal("15 minutes", KeepAwakeLogic.LongLabel(15));
        Assert.Equal("1 hour", KeepAwakeLogic.LongLabel(60));
        Assert.Equal("2 hours", KeepAwakeLogic.LongLabel(120));
        Assert.Equal("Indefinite", KeepAwakeLogic.LongLabel(0));
        Assert.Equal("+1h", KeepAwakeLogic.ExtendLabel(60));
    }

    [Fact]
    public void UntilRollsToTomorrowWhenPast()
    {
        var now = new DateTime(2026, 10, 14, 15, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 14, 17, 30, 0), KeepAwakeLogic.ResolvedUntil(now, 17, 30));
        Assert.Equal(new DateTime(2026, 10, 15, 9, 0, 0), KeepAwakeLogic.ResolvedUntil(now, 9, 0));
        Assert.Equal(new DateTime(2026, 10, 15, 15, 0, 0), KeepAwakeLogic.ResolvedUntil(now, 15, 0));
        Assert.Equal("Today · 2h 30m", KeepAwakeLogic.UntilSummary(now, new DateTime(2026, 10, 14, 17, 30, 0)));
        Assert.Equal("Tomorrow · 18h", KeepAwakeLogic.UntilSummary(now, new DateTime(2026, 10, 15, 9, 0, 0)));
    }

    [Fact]
    public void ExtendAddsToTheLaterOfEndAndNow()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.Equal(now.AddMinutes(45), KeepAwakeLogic.Extended(now.AddMinutes(15), now, 30));
        Assert.Equal(now.AddMinutes(30), KeepAwakeLogic.Extended(now.AddMinutes(-5), now, 30));
        Assert.Equal(now.AddMinutes(60), KeepAwakeLogic.Extended(null, now, 60));
    }

    [Fact]
    public void SanitizesStoredChoices()
    {
        Assert.Equal(0, KeepAwakeLogic.SanitizedDuration(17));
        Assert.Equal(240, KeepAwakeLogic.SanitizedDuration(240));
        Assert.Equal(10, KeepAwakeLogic.SanitizedBatteryLimit(7));
        Assert.Equal(0, KeepAwakeLogic.SanitizedBatteryLimit(0));
        Assert.Equal(5, KeepAwakeLogic.SanitizedJiggleInterval(3));
    }

    [Fact]
    public void BatteryProtection()
    {
        Assert.True(KeepAwakeLogic.BatteryCutoff(10, onBattery: true, 10));
        Assert.False(KeepAwakeLogic.BatteryCutoff(10, onBattery: true, 11));
        Assert.False(KeepAwakeLogic.BatteryCutoff(10, onBattery: false, 3));
        Assert.False(KeepAwakeLogic.BatteryCutoff(0, onBattery: true, 3));
        Assert.False(KeepAwakeLogic.BatteryCutoff(10, onBattery: true, null));
        Assert.True(KeepAwakeLogic.AutomaticStartAllowed(10, onBattery: true, 11));
        Assert.False(KeepAwakeLogic.AutomaticStartAllowed(10, onBattery: true, 10));
        Assert.True(KeepAwakeLogic.AutomaticStartAllowed(10, onBattery: false, 2));
    }

    [Fact]
    public void SatisfiedHonoursAnyAndAll()
    {
        var enabled = AwakeCondition.Power | AwakeCondition.ExternalDisplay;
        Assert.False(KeepAwakeLogic.Satisfied(AwakeCondition.None, enabled, false));
        Assert.True(KeepAwakeLogic.Satisfied(AwakeCondition.Power, enabled, false));
        Assert.False(KeepAwakeLogic.Satisfied(AwakeCondition.Power, enabled, true));
        Assert.True(KeepAwakeLogic.Satisfied(enabled, enabled, true));
        // Conditions that are not enabled never count.
        Assert.False(KeepAwakeLogic.Satisfied(AwakeCondition.RunningApps, enabled, false));
    }

    [Fact]
    public void AutomationDecisions()
    {
        var on = AwakeCondition.Power;
        Assert.Equal(AutomationAction.Activate, KeepAwakeLogic.Decide(true, on, on, false, sessionActive: false, automaticSessionActive: false));
        Assert.Equal(AutomationAction.None, KeepAwakeLogic.Decide(true, on, on, false, sessionActive: true, automaticSessionActive: false));
        Assert.Equal(AutomationAction.Deactivate, KeepAwakeLogic.Decide(true, AwakeCondition.None, on, false, true, true));
        // A manual session is never ended by automation.
        Assert.Equal(AutomationAction.None, KeepAwakeLogic.Decide(true, AwakeCondition.None, on, false, true, false));
        Assert.Equal(AutomationAction.Deactivate, KeepAwakeLogic.Decide(false, on, on, false, true, true));
    }

    [Fact]
    public void AutomationStatusText()
    {
        Assert.Equal("Active while connected to power", KeepAwakeLogic.AutomationStatus(AwakeCondition.Power));
        Assert.Equal("Active because an automatic condition is met",
            KeepAwakeLogic.AutomationStatus(AwakeCondition.Power | AwakeCondition.RunningApps));
    }

    [Fact]
    public void AppMatchingIgnoresPathCaseAndExtension()
    {
        Assert.Equal("obs64", KeepAwakeLogic.ProcessKey(@"C:\Program Files\obs-studio\bin\64bit\OBS64.EXE"));
        Assert.True(KeepAwakeLogic.AnyAppRunning([@"C:\Apps\Zoom.exe"], ["explorer", "zoom"]));
        Assert.False(KeepAwakeLogic.AnyAppRunning(["zoom.exe"], ["explorer"]));
        Assert.False(KeepAwakeLogic.AnyAppRunning([], ["explorer"]));
    }
}

public class DisplayMathTests
{
    [Fact]
    public void MatchesWmiInstanceToDevicePath()
    {
        const string path = @"\\?\DISPLAY#BOE0867#4&2a8d2c9a&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        Assert.Equal(@"DISPLAY\BOE0867\4&2A8D2C9A&0&UID8388688", DisplayMath.NormalizeDevicePath(path));
        Assert.Equal(@"DISPLAY\BOE0867\4&2A8D2C9A&0&UID8388688", DisplayMath.NormalizeWmiInstance(@"DISPLAY\BOE0867\4&2a8d2c9a&0&UID8388688_0"));
        var instances = new[] { @"DISPLAY\DEL1234\5&1&0&UID1_0", @"DISPLAY\BOE0867\4&2a8d2c9a&0&UID8388688_0" };
        Assert.Equal(instances[1], DisplayMath.MatchWmiInstance(path, true, instances, 1));
    }

    [Fact]
    public void SingleInternalPanelFallsBackToTheOnlyInstance()
    {
        Assert.Equal("X_0", DisplayMath.MatchWmiInstance(string.Empty, true, ["X_0"], 1));
        Assert.Null(DisplayMath.MatchWmiInstance(string.Empty, false, ["X_0"], 1));
        Assert.Null(DisplayMath.MatchWmiInstance(string.Empty, true, ["X_0"], 2));
        Assert.Null(DisplayMath.MatchWmiInstance("p", true, [], 1));
    }

    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(11u, true)]
    [InlineData(5u, false)] // HDMI
    [InlineData(10u, false)] // DisplayPort external
    public void InternalTechnologies(uint tech, bool expected) => Assert.Equal(expected, DisplayMath.IsInternalTechnology(tech));

    [Fact]
    public void DdcValueScaling()
    {
        Assert.Equal(0.5, DisplayMath.Normalized(50, 100));
        Assert.Equal(1.0, DisplayMath.Normalized(120, 100));
        Assert.Equal(0.5, DisplayMath.Normalized(50, 0)); // unknown maximum means 100
        Assert.Equal(38u, DisplayMath.DeviceValue(0.5, 75));
        Assert.Equal(0u, DisplayMath.DeviceValue(-1, 100));
    }

    [Fact]
    public void ExtendedDimmingSplitsTheSlider()
    {
        Assert.Equal((0.0, 0.4), DisplayMath.ExtendedDimmingComponents(0.1));
        Assert.Equal((0.0, 1.0), DisplayMath.ExtendedDimmingComponents(0.25));
        Assert.Equal((1.0, 1.0), DisplayMath.ExtendedDimmingComponents(1));
        var (hw, pic) = DisplayMath.ExtendedDimmingComponents(0.625);
        Assert.Equal(0.5, hw, 6);
        Assert.Equal(1.0, pic);
    }

    [Fact]
    public void ExtendedDimmingLevelReversesTheMapping()
    {
        Assert.Equal(0.625, DisplayMath.ExtendedDimmingLevel(0.5, null, false), 6);
        Assert.Equal(0.1, DisplayMath.ExtendedDimmingLevel(0, 0.1, pictureDimmed: true), 6);
        Assert.Equal(0.25, DisplayMath.ExtendedDimmingLevel(0, 0.1, pictureDimmed: false), 6);
    }

    [Fact]
    public void OverlayOpacityKeepsAFloor()
    {
        Assert.Equal(0, DisplayMath.OverlayOpacity(1));
        Assert.Equal(0.5, DisplayMath.OverlayOpacity(0.5), 6);
        Assert.Equal(0.9, DisplayMath.OverlayOpacity(0), 6);
    }

    [Fact]
    public void KeySteps()
    {
        Assert.Equal(BrightnessKeyStep.Half, DisplayMath.ParseStep("half"));
        Assert.Equal(BrightnessKeyStep.Standard, DisplayMath.ParseStep("bogus"));
        Assert.Equal(1.0 / 64, DisplayMath.Limited(BrightnessKeyStep.Quarter, 1.0 / 16));
        Assert.Equal(-1.0 / 32, DisplayMath.Limited(BrightnessKeyStep.Half, -1.0 / 16));
        Assert.Equal(1.0 / 64, DisplayMath.Limited(BrightnessKeyStep.Standard, 1.0 / 64)); // finer steps are kept
        Assert.Equal(1.0, DisplayMath.Stepped(0.98, 1.0 / 16));
        Assert.Equal(0.0, DisplayMath.Stepped(0.02, -1.0 / 16));
    }

    [Fact]
    public void OsdMaths()
    {
        Assert.Equal(0, DisplayMath.FilledSegments(0));
        Assert.Equal(1, DisplayMath.FilledSegments(0.01));
        Assert.Equal(16, DisplayMath.FilledSegments(1));
        Assert.Equal(63, DisplayMath.WholePercent(0.625));
        Assert.Equal(0, DisplayMath.WholePercent(double.NaN));
    }

    [Fact]
    public void PathListsKeepTheNewest16()
    {
        var list = Enumerable.Range(0, 16).Select(i => $"p{i}").ToList();
        var updated = DisplayMath.UpdatedPathList(list, "new", include: true);
        Assert.Equal(16, updated.Count);
        Assert.Equal("new", updated[^1]);
        Assert.DoesNotContain("p0", updated);
        Assert.DoesNotContain("p3", DisplayMath.UpdatedPathList(["p3", "", "p4"], "P3", include: false));
        Assert.Equal(["p4"], DisplayMath.UpdatedPathList(["p3", "", "p4"], "p3", include: false));
    }

    [Fact]
    public void SdrWhiteBoost()
    {
        Assert.Equal(6000u, DisplayMath.BoostedWhiteLevel(2000, 100));
        Assert.Equal(2000u, DisplayMath.BoostedWhiteLevel(2000, 0));
        Assert.Equal(4000u, DisplayMath.BoostedWhiteLevel(2000, 50));
        Assert.Equal(6000u, DisplayMath.BoostedWhiteLevel(9000, 30)); // clamped to Windows' range
        Assert.Equal(80, DisplayMath.WhiteLevelNits(1000));
    }

    [Fact]
    public void ConnectionKeys()
    {
        Assert.Equal(@"\\.\DISPLAY2", DisplayMath.ConnectionKey(null, @"\\.\display2"));
        Assert.Equal(@"\\?\DISPLAY#ABC", DisplayMath.ConnectionKey(@"\\?\display#abc", @"\\.\DISPLAY1"));
    }
}

public class BluetoothSleepLogicTests
{
    [Fact]
    public void SleepPlan()
    {
        Assert.Equal((true, true), BluetoothSleepLogic.SleepPlan(isPoweredOn: true, restoresOnWake: true));
        Assert.Equal((true, false), BluetoothSleepLogic.SleepPlan(isPoweredOn: true, restoresOnWake: false));
        Assert.Equal((false, false), BluetoothSleepLogic.SleepPlan(isPoweredOn: false, restoresOnWake: true));
    }

    [Fact]
    public void RestoresOnlyWhenOwedAndOff()
    {
        Assert.True(BluetoothSleepLogic.Restores(owes: true, isOn: false));
        Assert.False(BluetoothSleepLogic.Restores(owes: true, isOn: true));
        Assert.False(BluetoothSleepLogic.Restores(owes: false, isOn: false));
    }
}

public class DisplayInventoryTests
{
    [Fact]
    public void EnumeratesWithoutChangingAnything()
    {
        // Read-only query: every machine running the tests has at least one active display (or none in CI).
        var displays = DisplayInventory.Enumerate();
        Assert.All(displays, d => Assert.False(string.IsNullOrEmpty(d.Key)));
        Assert.True(displays.Count(d => d.IsPrimary) <= 1);
    }
}
