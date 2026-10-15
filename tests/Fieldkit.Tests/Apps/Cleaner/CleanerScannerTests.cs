// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Cleaner;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Tests.Apps.Cleaner;

/// <summary>Scans and cleans a fixture profile under %TEMP%; the clean pass goes through a fake deleter.</summary>
public class CleanerScannerTests
{
    private static CleanerScanOptions Options(bool attended = true, params string[] running) => new()
    {
        Attended = attended,
        InstalledApps = [new InstalledApp { Id = "1", Name = "Installed Thing", Publisher = "Thing Corp" }],
        RunningProcesses = running,
        IncludeRecycleBin = false,
        IncludeStartup = false,
        TempAgeDays = 7,
        ScreenshotAgeDays = 30,
    };

    private static void BuildProfile(CleanerFixture fx)
    {
        var p = fx.Paths;
        fx.File(Path.Combine(p.LocalAppData, @"Google\Chrome\User Data\Default\Cache\data_0"), 4000);
        fx.File(Path.Combine(p.LocalAppData, @"Google\Chrome\User Data\Profile 1\Code Cache\js\x"), 2000);
        fx.File(Path.Combine(p.RoamingAppData, @"discord\Cache\Cache_Data\f_000001"), 3000);
        fx.File(Path.Combine(p.RoamingAppData, @"discord\settings.json"), 10);
        fx.File(Path.Combine(p.LocalAppData, @"npm-cache\_cacache\index-v5\aa"), 5000);
        fx.File(Path.Combine(p.LocalAppData, @"D3DSCache\abc\cache.idx"), 700);
        fx.File(Path.Combine(p.LocalAppData, @"CrashDumps\app.exe.1234.dmp"), 9000);
        fx.File(Path.Combine(p.RoamingAppData, @"SomeEditor\logs\main.log"), 600);
        fx.File(Path.Combine(p.Temp, "old.tmp"), 300, ageDays: 20);
        fx.File(Path.Combine(p.Temp, "fresh.tmp"), 300);
        fx.File(Path.Combine(p.Temp, @"olddir\inner.bin"), 800);
        fx.Age(Path.Combine(p.Temp, "olddir"), 20);
        fx.File(Path.Combine(p.Temp, @"mixeddir\old.bin"), 100, ageDays: 40);
        fx.File(Path.Combine(p.Temp, @"mixeddir\new.bin"), 100); // a fresh file keeps the folder
        fx.File(Path.Combine(p.RoamingAppData, @"OldPhotoEditor\presets.db"), 6000);
        fx.File(Path.Combine(p.RoamingAppData, @"OldPhotoEditor\Cache\thumb"), 100);
        fx.Age(Path.Combine(p.RoamingAppData, "OldPhotoEditor"), 90);
        fx.File(Path.Combine(p.RoamingAppData, @"Installed Thing\state.json"), 400);
        fx.Age(Path.Combine(p.RoamingAppData, "Installed Thing"), 90);
        fx.File(Path.Combine(p.LocalAppData, @"RecentlyUsedApp\state.json"), 400); // fresh: alive
        var shot = fx.File(Path.Combine(p.Screenshots, "Screenshot 2020-01-02 101010.png"), 2500);
        File.SetCreationTime(shot, new DateTime(2020, 1, 2, 10, 10, 10));
        File.SetLastWriteTime(shot, new DateTime(2020, 1, 2, 10, 10, 10));
        File.SetLastAccessTime(shot, new DateTime(2020, 1, 2, 10, 10, 10));
        var renamed = fx.File(Path.Combine(p.Screenshots, "receipt.png"), 2500);
        File.SetCreationTime(renamed, new DateTime(2020, 1, 2));
    }

    [Fact]
    public void FindsEachCategoryAndPrechecksOnlySafeOnes()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var items = CleanerScanner.ScanAll(fx.Paths, Options(true, "chrome"));
        CleanerItem Find(string end) => Assert.Single(items, i => i.Path.EndsWith(end, StringComparison.OrdinalIgnoreCase));

        var chrome = Find(@"Default\Cache");
        Assert.Equal(CleanerCategory.Caches, chrome.Category);
        Assert.True(chrome.IsBlocked);            // Chrome is running: shown, never checked
        Assert.False(chrome.Include);
        Assert.Equal("Google Chrome (Default) · Cache", chrome.Name);
        Assert.Equal(4000, chrome.Size);
        Assert.True(Find(@"Profile 1\Code Cache").IsBlocked);

        var discord = Find(@"discord\Cache");
        Assert.True(discord.Include);
        Assert.Equal(DisplayGroup.SafeCaches, discord.Group);

        var shader = Find("D3DSCache");
        Assert.Equal(DisplayGroup.OtherCaches, shader.Group);
        Assert.False(shader.Include);

        Assert.Equal(CleanerCategory.Developer, Find(@"npm-cache\_cacache").Category);
        Assert.Equal(CleanerCategory.Logs, Find("CrashDumps").Category);
        Assert.Equal(CleanerCategory.Logs, Find(@"SomeEditor\logs").Category);

        Assert.True(Find("old.tmp").Include);
        Assert.Equal(CleanerCategory.Temp, Find("olddir").Category);
        Assert.DoesNotContain(items, i => i.Path.EndsWith("fresh.tmp"));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("mixeddir"));

        var leftover = Find("OldPhotoEditor");
        Assert.Equal(CleanerCategory.Leftovers, leftover.Category);
        Assert.False(leftover.Include);           // a guess: always waits for the user's eye
        Assert.Equal(6100, leftover.Size);
        // Claimed by the leftover: its cache folder is not listed a second time.
        Assert.DoesNotContain(items, i => i.Path.EndsWith(@"OldPhotoEditor\Cache"));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("Installed Thing"));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("RecentlyUsedApp"));
        Assert.DoesNotContain(items, i => i.Path.EndsWith(@"Local\Temp") || i.Name == "Temp");

        var screenshot = Find("Screenshot 2020-01-02 101010.png");
        Assert.False(screenshot.Include);
        Assert.DoesNotContain(items, i => i.Path.EndsWith("receipt.png"));
        Assert.All(items, i => Assert.NotNull(i.Identity));
    }

    [Fact]
    public void AFolderStillUsedUnderAnotherRootKeepsItsSiblingsAlive()
    {
        using var fx = new CleanerFixture();
        fx.File(Path.Combine(fx.Paths.RoamingAppData, @"PortableTool\config.json"), 200);
        fx.Age(Path.Combine(fx.Paths.RoamingAppData, "PortableTool"), 120);
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"PortableTool\state.db"), 200); // used today
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"GoneTool\state.db"), 200);
        fx.Age(Path.Combine(fx.Paths.LocalAppData, "GoneTool"), 120);
        var leftovers = CleanerScanner.ScanAll(fx.Paths, Options()).Where(i => i.Category == CleanerCategory.Leftovers).ToList();
        Assert.Equal(["GoneTool"], leftovers.Select(i => i.Name));
    }

    [Fact]
    public void UnattendedPassSkipsScreenshots()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var items = CleanerScanner.ScanAll(fx.Paths, Options(attended: false));
        Assert.DoesNotContain(items, i => i.Category == CleanerCategory.Screenshots);
        Assert.False(Assert.Single(items, i => i.Path.EndsWith(@"Default\Cache")).IsBlocked);
    }

    [Fact]
    public void ReportsProgressInScanOrderAndHonoursCancellation()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var started = new List<CleanerCategory>();
        CleanerScanner.Scan(fx.Paths, Options(), CancellationToken.None, started.Add);
        Assert.Equal(CleanerScanner.Order, started);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CleanerScanner.ScanAll(fx.Paths, Options(), cts.Token));
    }

    [Fact]
    public void ExpandsWildcardPatterns()
    {
        using var fx = new CleanerFixture();
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"Mozilla\Firefox\Profiles\abc.default\cache2\entries\1"));
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"Mozilla\Firefox\Profiles\xyz.dev\cache2\entries\2"));
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"Microsoft\Windows\Explorer\thumbcache_96.db"));
        fx.File(Path.Combine(fx.Paths.LocalAppData, @"Microsoft\Windows\Explorer\iconcache_16.db"));
        var firefox = CleanerScanner.ExpandPattern(fx.Paths.Expand(@"{Local}\Mozilla\Firefox\Profiles\*\cache2")).ToList();
        Assert.Equal(2, firefox.Count);
        var thumbs = CleanerScanner.ExpandPattern(fx.Paths.Expand(@"{Local}\Microsoft\Windows\Explorer\thumbcache_*.db")).ToList();
        Assert.Single(thumbs);
        Assert.Empty(CleanerScanner.ExpandPattern(fx.Paths.Expand(@"{Local}\Nope\*\cache")));
    }

    [Fact]
    public void FindsOrphanedStartupEntries()
    {
        using var fx = new CleanerFixture();
        var existing = fx.File(Path.Combine(fx.Root, "Tools", "alive.exe"));
        var options = new CleanerScanOptions
        {
            InstalledApps = [], RunningProcesses = [], IncludeRecycleBin = false, IncludeStartup = true,
            StartupEntries = () =>
            [
                new StartupEntry("GoneUpdater", $"\"{Path.Combine(fx.Root, "Gone", "updater.exe")}\" /tray", true, @"HKEY_CURRENT_USER\...\Run\GoneUpdater"),
                new StartupEntry("Alive", $"\"{existing}\"", true, @"HKEY_CURRENT_USER\...\Run\Alive"),
                new StartupEntry("SecurityHealth", @"C:\Nope\gone.exe", true, "x"),
            ],
        };
        var items = CleanerScanner.ScanAll(fx.Paths, options).Where(i => i.Category == CleanerCategory.LoginItems).ToList();
        var orphan = Assert.Single(items);
        Assert.Equal("GoneUpdater", orphan.RunValueName);
        Assert.Equal(CleanerItemKind.RunValue, orphan.Kind);
        Assert.True(orphan.Include);
    }

    // ------------------------------------------------------------------ clean

    [Fact]
    public void CleansOnlyCheckedItemsThroughTheDeleter()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var items = CleanerScanner.ScanAll(fx.Paths, Options());
        var chosen = items.Where(i => i.Include).ToList();
        var deleter = new FakeDeleter();
        var outcome = CleanerEngine.Clean(chosen, deleter, fx.Paths, ownerTokens: null);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(chosen.Sum(i => i.Size), outcome.Freed);
        Assert.Equal(chosen.Select(i => i.Path).Order(), deleter.Recycled.Order());
        Assert.DoesNotContain(deleter.Recycled, p => p.EndsWith("OldPhotoEditor")); // unchecked leftover untouched
        // Nothing real was removed.
        Assert.All(chosen, i => Assert.True(File.Exists(i.Path) || Directory.Exists(i.Path)));
    }

    [Fact]
    public void EmptiesTheRecycleBinFirst()
    {
        using var fx = new CleanerFixture();
        var file = fx.File(Path.Combine(fx.Paths.Temp, "old.log"), 50, ageDays: 30);
        var temp = CleanerScanner.ScanAll(fx.Paths, Options()).Single(i => i.Path == file);
        var trash = new CleanerItem { Path = "Recycle Bin", Name = "Recycle Bin", Category = CleanerCategory.Trash, Size = 999, Kind = CleanerItemKind.RecycleBin };
        var deleter = new FakeDeleter();
        var outcome = CleanerEngine.Clean([temp, trash], deleter, fx.Paths, null);
        Assert.Equal(["empty", "recycle:" + file], deleter.Calls);
        Assert.Equal(1049, outcome.Freed);
    }

    [Fact]
    public void RefusesItemsThatChangedSinceTheScan()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var items = CleanerScanner.ScanAll(fx.Paths, Options());
        var old = items.Single(i => i.Path.EndsWith("old.tmp"));
        // Replaced by another file under the same name: a different identity.
        File.Delete(old.Path);
        fx.File(old.Path, 300, ageDays: 20);
        var discord = items.Single(i => i.Path.EndsWith(@"discord\Cache"));
        var deleter = new FakeDeleter();
        var outcome = CleanerEngine.Clean([old, discord], deleter, fx.Paths, null);
        Assert.Equal(1, outcome.Failed);
        Assert.Equal([discord.Path], deleter.Recycled);
    }

    [Fact]
    public void RefusesBrowserCachesWhileTheBrowserRuns()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var chrome = CleanerScanner.ScanAll(fx.Paths, Options()).Single(i => i.Path.EndsWith(@"Default\Cache"));
        var deleter = new FakeDeleter();
        deleter.Running.Add("chrome");
        var outcome = CleanerEngine.Clean([chrome], deleter, fx.Paths, null);
        Assert.Equal(1, outcome.Failed);
        Assert.Empty(deleter.Recycled);
    }

    [Fact]
    public void RechecksLeftoversAgainstLivingOwners()
    {
        using var fx = new CleanerFixture();
        BuildProfile(fx);
        var leftover = CleanerScanner.ScanAll(fx.Paths, Options()).Single(i => i.Category == CleanerCategory.Leftovers);
        var deleter = new FakeDeleter();
        // The app was reinstalled in the meantime.
        var alive = CleanerRules.OwnerTokens([new InstalledApp { Id = "x", Name = "Old Photo Editor" }], []);
        Assert.Equal(1, CleanerEngine.Clean([leftover], deleter, fx.Paths, alive).Failed);
        // No oracle at all: refused too.
        Assert.Equal(1, CleanerEngine.Clean([leftover], deleter, fx.Paths, null).Failed);
        Assert.Empty(deleter.Recycled);
        var none = CleanerRules.OwnerTokens([], []);
        Assert.Equal(0, CleanerEngine.Clean([leftover], deleter, fx.Paths, none).Failed);
        Assert.Equal([leftover.Path], deleter.Recycled);
    }

    [Fact]
    public void RefusesForgedItems()
    {
        using var fx = new CleanerFixture();
        var outside = fx.File(Path.Combine(fx.Root, "elsewhere", "deep", "deeper", "file.bin"));
        var deleter = new FakeDeleter();
        CleanerItem Forge(string path, CleanerCategory c) => new() { Path = path, Name = Path.GetFileName(path), Category = c, Size = 1 };
        var outcome = CleanerEngine.Clean(
        [
            Forge(outside, CleanerCategory.Temp),                    // not in the temp folder, no identity
            Forge(fx.Paths.LocalAppData, CleanerCategory.Caches),    // a critical root
            Forge(fx.Paths.Screenshots, CleanerCategory.Screenshots),
        ], deleter, fx.Paths, null);
        Assert.Equal(3, outcome.Failed);
        Assert.Empty(deleter.Recycled);
    }

    [Fact]
    public void RemovesStartupValuesOnlyThroughTheDeleter()
    {
        using var fx = new CleanerFixture();
        var item = new CleanerItem
        {
            Path = @"HKEY_CURRENT_USER\x\Run\Gone", Name = "Gone", Category = CleanerCategory.LoginItems, Kind = CleanerItemKind.RunValue,
            RunValueName = "Gone", RunCommand = @"C:\Gone\gone.exe", Recommended = true,
        };
        var deleter = new FakeDeleter();
        Assert.Equal(0, CleanerEngine.Clean([item], deleter, fx.Paths, null).Failed);
        Assert.Equal(["Gone"], deleter.RemovedRunValues);
    }

    [Fact]
    public void MeasuresTreesWithoutFollowingLinks()
    {
        using var fx = new CleanerFixture();
        var dir = Path.Combine(fx.Root, "tree");
        fx.File(Path.Combine(dir, "a"), 10);
        fx.File(Path.Combine(dir, "b", "c"), 20);
        var (size, newest) = TreeStats.Measure(dir);
        Assert.Equal(30, size);
        Assert.True(newest > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(0, TreeStats.Measure(Path.Combine(fx.Root, "missing")).Size);
    }

    [Fact]
    public void NotificationTextMatchesUpstream()
    {
        Assert.Equal("Nothing to clean. Your PC is tidy.", CleanerScheduleService.NotificationText(0, 0));
        Assert.Equal($"{ByteFormat.Format(2_000_000)} freed and sent to the Recycle Bin.", CleanerScheduleService.NotificationText(2_000_000, 0));
        Assert.EndsWith("Some items couldn’t be moved to the Recycle Bin.", CleanerScheduleService.NotificationText(5, 1));
    }
}
