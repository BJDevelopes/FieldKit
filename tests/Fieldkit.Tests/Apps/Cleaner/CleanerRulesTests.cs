// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Apps.Cleaner;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Tests.Apps.Cleaner;

public class CleanerRulesTests
{
    [Theory]
    [InlineData(CleanerCategory.Caches, true, DisplayGroup.SafeCaches)]
    [InlineData(CleanerCategory.Caches, false, DisplayGroup.OtherCaches)]
    [InlineData(CleanerCategory.Developer, true, DisplayGroup.Developer)]
    [InlineData(CleanerCategory.Developer, false, DisplayGroup.OtherCaches)]
    [InlineData(CleanerCategory.Leftovers, false, DisplayGroup.Leftovers)]
    [InlineData(CleanerCategory.Trash, false, DisplayGroup.Trash)]
    [InlineData(CleanerCategory.Temp, true, DisplayGroup.Temp)]
    public void GroupsFindsLikeUpstream(CleanerCategory category, bool recommended, DisplayGroup expected)
    {
        Assert.Equal(expected, CleanerRules.GroupOf(category, recommended));
    }

    [Fact]
    public void SafeSectionHoldsOnlyRebuildableGroups()
    {
        var safe = Enum.GetValues<DisplayGroup>().Where(CleanerRules.IsSafe).ToHashSet();
        Assert.Equal([DisplayGroup.LoginItems, DisplayGroup.Temp, DisplayGroup.SafeCaches, DisplayGroup.Logs, DisplayGroup.Developer], safe.Order());
        Assert.False(CleanerRules.PrecheckLeftovers);
        Assert.False(CleanerRules.PrecheckDeviceBackups);
        Assert.False(CleanerRules.PrecheckScreenshots);
        Assert.False(CleanerRules.PrecheckSystemCaches);
    }

    [Fact]
    public void AutomaticCleanupOnlyTakesEnabledSafeCacheAndTempGroups()
    {
        bool AllOn(string _) => true;
        Assert.True(CleanerRules.AutomaticMayClean(CleanerCategory.Temp, true, false, AllOn));
        Assert.True(CleanerRules.AutomaticMayClean(CleanerCategory.Caches, true, false, AllOn));
        Assert.True(CleanerRules.AutomaticMayClean(CleanerCategory.Logs, true, false, AllOn));
        Assert.True(CleanerRules.AutomaticMayClean(CleanerCategory.Developer, true, false, AllOn));
        // Never: leftovers, startup items, the Recycle Bin, backups, screenshots, system caches.
        foreach (var c in new[] { CleanerCategory.Leftovers, CleanerCategory.LoginItems, CleanerCategory.Trash, CleanerCategory.DeviceBackups,
                     CleanerCategory.Screenshots, CleanerCategory.SystemCaches })
            Assert.False(CleanerRules.AutomaticMayClean(c, true, false, AllOn), c.ToString());
        // Not pre-checked, blocked by a running browser, or switched off for the schedule.
        Assert.False(CleanerRules.AutomaticMayClean(CleanerCategory.Caches, false, false, AllOn));
        Assert.False(CleanerRules.AutomaticMayClean(CleanerCategory.Caches, true, true, AllOn));
        Assert.False(CleanerRules.AutomaticMayClean(CleanerCategory.Caches, true, false, k => k != CleanerKeys.ScheduleCaches));
    }

    [Theory]
    [InlineData("OldPhotoEditor", true)]
    [InlineData("Microsoft", false)]
    [InlineData("packages", false)]
    [InlineData("Temp", false)]
    [InlineData(".config", false)]
    [InlineData("{8A2F-11}", false)]
    [InlineData("ab", false)]
    [InlineData("Fieldkit", false)]
    [InlineData("npm-cache", false)]
    public void FiltersLeftoverCandidateNames(string name, bool expected)
    {
        Assert.Equal(expected, CleanerRules.IsLeftoverCandidateName(name));
    }

    [Fact]
    public void LeftoverOwnershipIsGenerous()
    {
        var apps = new[]
        {
            new InstalledApp { Id = "1", Name = "Visual Studio Code", Publisher = "Microsoft Corporation",
                InstallLocation = @"C:\Users\alex\AppData\Local\Programs\Microsoft VS Code" },
            new InstalledApp { Id = "2", Name = "Widget Pro 3", Publisher = "Acme Software, Inc.", InstallLocation = @"C:\Program Files\Acme\Widget" },
            new InstalledApp { Id = "3", Name = "Spotify Music", Source = InstalledAppSource.StorePackage,
                PackageFamilyName = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0" },
        };
        var tokens = CleanerRules.OwnerTokens(apps, ["discord"]);

        Assert.True(CleanerRules.IsOwned("Code", [], tokens));               // "visualstudiocode" contains "code"
        Assert.True(CleanerRules.IsOwned("Acme", [], tokens));               // publisher
        Assert.True(CleanerRules.IsOwned("Widget", [], tokens));             // install folder
        Assert.True(CleanerRules.IsOwned("SomeVendor", ["Widget Pro"], tokens)); // a living child keeps the vendor folder
        Assert.True(CleanerRules.IsOwned("SpotifyAB", [], tokens));          // Store identity
        Assert.True(CleanerRules.IsOwned("discord", [], tokens));            // running process
        Assert.True(CleanerRules.IsOwned("Microsoft VS Code Data", [], tokens)); // name contains a 4+ char token
        Assert.False(CleanerRules.IsOwned("OldPhotoEditor", ["Presets"], tokens));
        Assert.False(CleanerRules.IsOwned("RetiredGame", [], tokens));
    }

    [Fact]
    public void LeftoverMustBeIdle()
    {
        var now = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(CleanerRules.IsIdleFor(now.AddDays(-31), now, CleanerRules.LeftoverIdleDays));
        Assert.False(CleanerRules.IsIdleFor(now.AddDays(-3), now, CleanerRules.LeftoverIdleDays));
    }

    [Theory]
    [InlineData("Screenshot 2026-07-02 101533.png", "2026-07-02", true)]
    [InlineData("Screenshot 2026-07-02 101533 (2).png", "2026-07-02", true)]
    [InlineData("screenshot 2026-07-02 101533.JPG", "2026-07-02", true)]
    [InlineData("Screenshot 2026-07-02 101533.png", "2026-07-03", false)] // copied later: not the capture
    [InlineData("Screenshot 2026-07-02 101533 copy.png", "2026-07-02", false)]
    [InlineData("Screenshot (12).png", "2026-07-02", false)]               // no date in the name: skipped
    [InlineData("Screenshot 2026-07-02 251533.png", "2026-07-02", false)]
    [InlineData("holiday.png", "2026-07-02", false)]
    public void RecognizesUntouchedScreenshotNames(string name, string created, bool expected)
    {
        Assert.Equal(expected, CleanerRules.ScreenshotKeepsDefaultName(name, DateTime.Parse(created).AddHours(10)));
    }

    [Fact]
    public void ScreenshotIsForgottenOnlyWhenNothingHappenedForTheWholePeriod()
    {
        var now = new DateTime(2026, 10, 14);
        var created = now.AddDays(-60);
        Assert.True(CleanerRules.IsForgottenScreenshot(created, created, created, now, 30));
        Assert.False(CleanerRules.IsForgottenScreenshot(created, created, now.AddDays(-2), now, 30)); // opened recently
        Assert.False(CleanerRules.IsForgottenScreenshot(created, now.AddDays(-1), null, now, 30));   // edited recently
        Assert.False(CleanerRules.IsForgottenScreenshot(created, null, null, now, 0));                // off
        Assert.Equal(0, CleanerRules.SanitizedScreenshotAgeDays(-5));
        Assert.Equal(3650, CleanerRules.SanitizedScreenshotAgeDays(99999));
    }

    [Fact]
    public void StartupEntryIsOrphanedOnlyWhenItsAbsoluteExecutableIsGoneFromAFixedDrive()
    {
        bool Fixed(string _) => true;
        bool Missing(string _) => false;
        bool Present(string _) => true;
        Assert.True(CleanerRules.IsOrphanedCommand("\"C:\\Program Files\\Old\\old.exe\" --tray", Missing, Fixed));
        Assert.True(CleanerRules.IsOrphanedCommand("C:\\Tools\\updater.exe /background", Missing, Fixed));
        Assert.False(CleanerRules.IsOrphanedCommand("\"C:\\Program Files\\Old\\old.exe\"", Present, Fixed));
        Assert.False(CleanerRules.IsOrphanedCommand("updater.exe", Missing, Fixed));                 // runs from PATH
        Assert.False(CleanerRules.IsOrphanedCommand("\\\\server\\share\\tool.exe", Missing, Fixed));  // network
        Assert.False(CleanerRules.IsOrphanedCommand("E:\\Portable\\tool.exe", Missing, _ => false));  // unplugged drive
        Assert.False(CleanerRules.IsOrphanedCommand("C:\\Windows\\System32\\rundll32.exe gone.dll,Run", Missing, Fixed));
        Assert.False(CleanerRules.IsOrphanedCommand("", Missing, Fixed));
        Assert.True(CleanerRules.IsProtectedStartupName("SecurityHealth"));
        Assert.True(CleanerRules.IsProtectedStartupName("Fieldkit"));
        Assert.False(CleanerRules.IsProtectedStartupName("OldUpdater"));
    }

    [Fact]
    public void RemovalGuardRefusesRootsWellKnownFoldersAndShallowPaths()
    {
        var paths = new CleanerPaths(@"C:\Users\alex\AppData\Local", @"C:\Users\alex\AppData\Roaming", @"C:\Users\alex\AppData\LocalLow",
            @"C:\Users\alex\AppData\Local\Temp", @"C:\Users\alex", @"C:\Users\alex\Pictures\Screenshots", @"C:\Windows");
        var critical = CleanerRules.CriticalPaths(paths).ToList();
        Assert.False(CleanerRules.MayRemovePath(@"C:\", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\Users\alex", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\Users\alex\AppData\Local\", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\Users\alex\AppData\Local\Temp", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\Users\alex\Downloads", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\Users\alex\AppData\Local\Microsoft", critical));
        Assert.False(CleanerRules.MayRemovePath(@"C:\a\b\c", critical));
        Assert.False(CleanerRules.MayRemovePath("", critical));
        Assert.True(CleanerRules.MayRemovePath(@"C:\Users\alex\AppData\Local\OldApp", critical));
        Assert.True(CleanerRules.MayRemovePath(@"C:\Users\alex\AppData\Local\Temp\setup.log", critical));
    }

    [Fact]
    public void NeverTrustsARecycleBinThatWouldDeleteForGood()
    {
        Assert.False(RecycleBinPolicy.Fits(10, null, nukeOnDelete: true, 500L << 30));
        Assert.True(RecycleBinPolicy.Fits(10_000_000, null, false, 500L << 30));
        // Default bin is a share of the drive: a 100 GB folder cannot fit a 500 GB drive's bin.
        Assert.False(RecycleBinPolicy.Fits(100L << 30, null, false, 500L << 30));
        Assert.True(RecycleBinPolicy.Fits(100L * 1024 * 1024, 1000, false, 500L << 30));
        Assert.False(RecycleBinPolicy.Fits(950L * 1024 * 1024, 1000, false, 500L << 30));
        Assert.False(RecycleBinPolicy.Fits(1, 0, false, 500L << 30));
    }

    [Fact]
    public void ReadsDeviceBackupInfo()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>Build Version</key><string>21A329</string>
              <key>Device Name</key><string>Alex’s iPhone</string>
              <key>Last Backup Date</key><date>2025-03-04T18:22:10Z</date>
            </dict></plist>
            """;
        var (device, date) = CleanerRules.ParseBackupInfo(xml);
        Assert.Equal("Alex’s iPhone", device);
        Assert.Equal(new DateTime(2025, 3, 4, 18, 22, 10, DateTimeKind.Utc), date);
        var bad = CleanerRules.ParseBackupInfo("not xml");
        Assert.Null(bad.Device);
        Assert.Null(bad.Date);
    }

    [Fact]
    public void TempAgeIsSanitized()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(7, CleanerRules.SanitizedTempAgeDays(5));
        Assert.Equal(2, CleanerRules.SanitizedTempAgeDays(2));
        Assert.True(CleanerRules.TempIsOldEnough(now.AddDays(-8), now, 7));
        Assert.False(CleanerRules.TempIsOldEnough(now.AddDays(-6), now, 7));
        Assert.True(CleanerRules.TempIsOldEnough(now.AddDays(-1.5), now, 1));
    }

    [Fact]
    public void FormatsPathsAndDatesForPeople()
    {
        var paths = new CleanerPaths(@"C:\Users\alex\AppData\Local", @"C:\Users\alex\AppData\Roaming", @"C:\Users\alex\AppData\LocalLow",
            @"C:\Users\alex\AppData\Local\Temp", @"C:\Users\alex", @"C:\Users\alex\Pictures\Screenshots", @"C:\Windows");
        Assert.Equal(@"%TEMP%\x", CleanerRules.PrettyPath(@"C:\Users\alex\AppData\Local\Temp\x", paths));
        Assert.Equal(@"%LOCALAPPDATA%\Google", CleanerRules.PrettyPath(@"C:\Users\alex\AppData\Local\Google", paths));
        Assert.Equal(@"%APPDATA%\discord", CleanerRules.PrettyPath(@"C:\Users\alex\AppData\Roaming\discord", paths));
        Assert.Equal(@"D:\Other", CleanerRules.PrettyPath(@"D:\Other", paths));
        var now = new DateTime(2026, 10, 14, 8, 0, 0);
        Assert.StartsWith("today at", CleanerRules.RelativeDate(now.AddHours(1), now));
        Assert.StartsWith("tomorrow at", CleanerRules.RelativeDate(now.AddDays(1), now));
        Assert.StartsWith("yesterday at", CleanerRules.RelativeDate(now.AddDays(-1), now));
    }

    [Fact]
    public void CacheCatalogStaysInsideTheUserProfileAndGuardsBrowsers()
    {
        foreach (var rule in CleanerRules.CacheCatalog)
        {
            // Developer tools may keep their cache in a dot folder of the profile (Bun's ~/.bun).
            var devDotFolder = rule.Category == CleanerCategory.Developer && rule.Pattern.StartsWith("{Profile}\\.", StringComparison.Ordinal);
            Assert.True(rule.Pattern.StartsWith("{Local}", StringComparison.Ordinal) || rule.Pattern.StartsWith("{Roaming}", StringComparison.Ordinal)
                        || rule.Pattern.StartsWith("{LocalLow}", StringComparison.Ordinal) || devDotFolder, rule.Pattern);
            Assert.Contains(rule.Category, new[] { CleanerCategory.Caches, CleanerCategory.Developer });
        }
        foreach (var browser in new[] { "Google Chrome", "Microsoft Edge", "Firefox", "Brave", "Opera", "Vivaldi" })
            Assert.All(CleanerRules.CacheCatalog.Where(r => r.App == browser), r => Assert.NotEmpty(r.Processes));
        Assert.True(CleanerRules.IsExcludedCacheOwner("Spotify"));
        Assert.True(CleanerRules.IsExcludedCacheOwner("Fieldkit"));
        Assert.False(CleanerRules.IsExcludedCacheOwner("discord"));
    }
}
