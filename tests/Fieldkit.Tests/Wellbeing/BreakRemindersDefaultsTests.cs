// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Wellbeing;

namespace Fieldkit.Tests.Wellbeing;

/// <summary>
/// Break reminders start switched off (installing every feature must not start nagging), while an install that
/// already had the feature keeps its reminders. These tests use the shared test settings folder, so they clean up.
/// </summary>
[Collection("BreakReminderSettings")]
public class BreakRemindersDefaultsTests : IDisposable
{
    private static readonly string[] Keys =
    [
        BreakRemindersService.EyesKey, BreakRemindersService.MoveKey, "breakReminders.defaultsMigrated",
        FeatureCatalog.Get(BreakRemindersService.Id).AvailabilityKey,
    ];

    public BreakRemindersDefaultsTests() => Clear();

    public void Dispose() => Clear();

    private static void Clear()
    {
        foreach (var key in Keys) Settings.Remove(key);
    }

    [Fact]
    public void ANewInstallStartsWithTheRemindersOff()
    {
        BreakRemindersService.MigrateDefaults();

        Assert.True(FeatureCatalog.Get(BreakRemindersService.Id).IsAvailable); // installed with everything else
        Assert.False(Settings.GetBool(BreakRemindersService.EyesKey, BreakRemindersService.OnByDefault));
        Assert.False(Settings.GetBool(BreakRemindersService.MoveKey, BreakRemindersService.OnByDefault));
        Assert.False(new BreakRemindersService().ShouldRun());
    }

    [Fact]
    public void AnInstallThatHadTheFeatureKeepsItsReminders()
    {
        FeatureCatalog.Get(BreakRemindersService.Id).SetAvailable(true);

        BreakRemindersService.MigrateDefaults();

        Assert.True(Settings.GetBool(BreakRemindersService.EyesKey, BreakRemindersService.OnByDefault));
        Assert.True(Settings.GetBool(BreakRemindersService.MoveKey, BreakRemindersService.OnByDefault));
        Assert.True(new BreakRemindersService().ShouldRun());
    }

    [Fact]
    public void AReminderTheUserSwitchedOffStaysOff()
    {
        FeatureCatalog.Get(BreakRemindersService.Id).SetAvailable(true);
        Settings.Set(BreakRemindersService.EyesKey, false);

        BreakRemindersService.MigrateDefaults();

        Assert.False(Settings.GetBool(BreakRemindersService.EyesKey, BreakRemindersService.OnByDefault));
        Assert.True(Settings.GetBool(BreakRemindersService.MoveKey, BreakRemindersService.OnByDefault));
    }

    [Fact]
    public void ItRunsOnlyOnce()
    {
        BreakRemindersService.MigrateDefaults();
        FeatureCatalog.Get(BreakRemindersService.Id).SetAvailable(true);

        BreakRemindersService.MigrateDefaults(); // installing later must not switch reminders on

        Assert.False(Settings.GetBool(BreakRemindersService.EyesKey, BreakRemindersService.OnByDefault));
    }
}
