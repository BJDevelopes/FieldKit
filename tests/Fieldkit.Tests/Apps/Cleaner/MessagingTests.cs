// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Cleaner.Messaging;

namespace Fieldkit.Tests.Apps.Cleaner;

public class MessagingRulesTests
{
    [Theory]
    [InlineData(".jpg", MessagingCategory.Image)]
    [InlineData("JPEG", MessagingCategory.Image)]
    [InlineData(".mp4", MessagingCategory.Video)]
    [InlineData(".opus", MessagingCategory.Audio)]
    [InlineData(".pdf", MessagingCategory.Document)]
    [InlineData(".zip", MessagingCategory.Archive)]
    [InlineData(".exe", MessagingCategory.Other)]
    [InlineData("", MessagingCategory.Other)]
    public void ClassifiesByExtension(string ext, MessagingCategory expected)
    {
        Assert.Equal(expected, MessagingRules.Category(ext));
    }

    [Fact]
    public void SkipsIncompleteDownloads()
    {
        Assert.True(MessagingRules.IsIncompleteFile(".crdownload"));
        Assert.True(MessagingRules.IsIncompleteFile("PART"));
        Assert.False(MessagingRules.IsIncompleteFile(".jpg"));
    }

    [Fact]
    public void RetentionNeedsBothDatesPastTheWindow()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        Assert.True(MessagingRules.IsOldEnough(now.AddDays(-8), now.AddDays(-8), now, 7));
        Assert.False(MessagingRules.IsOldEnough(now.AddDays(-8), now.AddDays(-1), now, 7)); // edited recently
        Assert.False(MessagingRules.IsOldEnough(now.AddDays(-3), now.AddDays(-3), now, 7));
        Assert.Equal(7, MessagingRules.SanitizedRetentionDays(3));
        Assert.Equal(30, MessagingRules.SanitizedRetentionDays(30));
        Assert.Equal(5, MessagingRules.SanitizedOrganizerDelayMinutes(7));
    }

    [Fact]
    public void AutomationRespectsTheExistingFilesChoice()
    {
        var now = new DateTime(2026, 10, 14);
        var enabled = MessagingRules.DefaultCategories;
        var old = now.AddDays(-20);
        var start = now.AddDays(-30);
        Assert.True(MessagingRules.IsEligibleForAutomaticCleanup(MessagingCategory.Image, old, old, now, 7, enabled, true, null));
        Assert.False(MessagingRules.IsEligibleForAutomaticCleanup(MessagingCategory.Image, old, old, now, 7, enabled, false, null));
        Assert.True(MessagingRules.IsEligibleForAutomaticCleanup(MessagingCategory.Image, old, old, now, 7, enabled, false, start));
        Assert.False(MessagingRules.IsEligibleForAutomaticCleanup(MessagingCategory.Image, old, old, now, 7, enabled, false, now.AddDays(-10)));
        Assert.False(MessagingRules.IsEligibleForAutomaticCleanup(MessagingCategory.Document, old, old, now, 7, enabled, true, null));
    }

    [Fact]
    public void ChecksOnceADayAtNine()
    {
        var morning = new DateTime(2026, 10, 14, 8, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 0, 0), MessagingRules.NextAutomaticCheck(morning));
        Assert.Equal(new DateTime(2026, 10, 15, 9, 0, 0), MessagingRules.NextAutomaticCheck(morning.AddHours(2)));
        Assert.True(MessagingRules.MissedAutomaticCheck(morning.AddHours(3), new DateTime(2026, 10, 13, 9, 0, 0)));
        Assert.False(MessagingRules.MissedAutomaticCheck(morning, new DateTime(2026, 10, 13, 9, 0, 0)));
        Assert.False(MessagingRules.MissedAutomaticCheck(morning.AddHours(3), null));
    }

    [Fact]
    public void OrganizerTimingAndLayout()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        Assert.True(MessagingRules.IsStableForOrganization(now.AddMinutes(-6), now.AddMinutes(-6), now, 5));
        Assert.False(MessagingRules.IsStableForOrganization(now.AddMinutes(-6), now.AddMinutes(-1), now, 5));
        Assert.True(MessagingRules.OrganizerUndoIsValid(now.AddDays(-6), now));
        Assert.False(MessagingRules.OrganizerUndoIsValid(now.AddDays(-8), now));
        Assert.False(MessagingRules.OrganizerUndoIsValid(now.AddDays(1), now));
        Assert.Empty(MessagingRules.OrganizerRelativeComponents(OrganizerLayout.Flat, MessagingCategory.Image, now));
        Assert.Equal(["Images"], MessagingRules.OrganizerRelativeComponents(OrganizerLayout.Category, MessagingCategory.Image, now));
        Assert.Equal(["2026", "10"], MessagingRules.OrganizerRelativeComponents(OrganizerLayout.Month, MessagingCategory.Video, now));
        Assert.Equal(OrganizerLayout.Month, MessagingRules.ParseLayout("month"));
        Assert.Equal(OrganizerLayout.Flat, MessagingRules.ParseLayout("bogus"));
        Assert.Equal(DuplicateAction.ReplaceExisting, MessagingRules.ParseDuplicateAction("replaceExisting"));
        Assert.Equal(DuplicateAction.TrashNew, MessagingRules.ParseDuplicateAction(null));
    }

    [Fact]
    public void CategoriesRoundTrip()
    {
        Assert.Equal("image,video,audio", MessagingRules.EncodeCategories(MessagingRules.DefaultCategories));
        Assert.Equal(MessagingRules.DefaultCategories, MessagingRules.DecodeCategories(null));
        Assert.Empty(MessagingRules.DecodeCategories(""));
        Assert.Equal([MessagingCategory.Document, MessagingCategory.Other], MessagingRules.DecodeCategories("other, document,unknown,5").Order());
    }

    [Theory]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://web.whatsapp.com/\r\nHostUrl=blob:https://web.whatsapp.com/1f2e", MessagingApp.WhatsApp)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://mmg.whatsapp.net/v/t62/abc.enc", MessagingApp.WhatsApp)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nReferrerUrl=https://web.telegram.org/k/\nHostUrl=blob:https://web.telegram.org/x", MessagingApp.Telegram)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://t.me/file", MessagingApp.Telegram)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://whatsapp.com.evil.example/x", null)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://notwhatsapp.com/x", null)]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=file:///C:/x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AttributesDownloadsByZoneIdentifier(string? zone, MessagingApp? expected)
    {
        Assert.Equal(expected, MessagingRules.AttributedApp(zone));
    }

    [Fact]
    public void FindsAFreeNameForMovedFiles()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\M\photo.jpg", @"D:\M\photo (2).jpg" };
        Assert.Equal(@"D:\M\photo (3).jpg", MessagingRules.UniqueName(@"D:\M", "photo.jpg", taken.Contains));
        Assert.Equal(@"D:\M\new.jpg", MessagingRules.UniqueName(@"D:\M", "new.jpg", taken.Contains));
    }
}

/// <summary>Scan, clean and organize against fixture folders under %TEMP%.</summary>
public class MessagingScanTests
{
    private const string WhatsAppZone = "[ZoneTransfer]\nZoneId=3\nReferrerUrl=https://web.whatsapp.com/";
    private const string TelegramZone = "[ZoneTransfer]\nZoneId=3\nReferrerUrl=https://web.telegram.org/";

    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fieldkit-cleaner-tests", "msg-" + Guid.NewGuid().ToString("N"));
        public MessagingPaths Paths { get; }
        public Dictionary<string, string> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Env()
        {
            var downloads = Path.Combine(Root, "Downloads");
            Paths = new MessagingPaths(downloads, Path.Combine(downloads, "Telegram Desktop"), Path.Combine(Root, "WhatsAppPkg", "transfers"));
            Directory.CreateDirectory(downloads);
        }

        public string File(string path, int days, string? zone = null, int bytes = 64)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)(path.Length % 251), bytes).ToArray());
            var when = DateTime.Now.AddDays(-days);
            System.IO.File.SetCreationTime(path, when);
            System.IO.File.SetLastWriteTime(path, when);
            if (zone is not null) Zones[path] = zone;
            return path;
        }

        public string? Zone(string path) => Zones.GetValueOrDefault(path);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch { }
        }
    }

    private static MessagingSnapshot Snapshot(bool includeExisting = true, DateTime? start = null, bool whatsApp = true, bool telegram = true) =>
        new(7, MessagingRules.DefaultCategories, includeExisting, start, whatsApp, telegram);

    [Fact]
    public void FindsOnlyConfirmedMessagingFiles()
    {
        using var env = new Env();
        var d = env.Paths.Downloads;
        var photo = env.File(Path.Combine(d, "WhatsApp Image.jpeg"), 10, WhatsAppZone);
        var fresh = env.File(Path.Combine(d, "fresh.jpg"), 1, WhatsAppZone);
        var pdf = env.File(Path.Combine(d, "Invoice.pdf"), 10, TelegramZone);
        env.File(Path.Combine(d, "other.jpg"), 10);                         // no zone: not ours
        env.File(Path.Combine(d, "browser.jpg"), 10, "[ZoneTransfer]\nHostUrl=https://example.com/a.jpg");
        env.File(Path.Combine(d, "partial.jpg.crdownload"), 10, WhatsAppZone);
        env.File(Path.Combine(d, "sub", "nested.jpg"), 10, WhatsAppZone);    // only the top level of Downloads
        var tg = env.File(Path.Combine(env.Paths.TelegramFolder, "clip.mp4"), 10);
        var media = env.File(Path.Combine(env.Paths.WhatsAppMedia, "ab", "x.jpg"), 10);

        var (found, skipped) = MessagingDownloads.ScanCore(env.Paths, Snapshot(), new HashSet<string>(), [], DateTime.Now, env.Zone, whatsAppRunning: false);
        Assert.False(skipped);
        Assert.Equal(new[] { photo, fresh, pdf, tg, media }.Order(), found.Select(c => c.Path).Order());
        var p = found.Single(c => c.Path == photo);
        Assert.Equal(MessagingApp.WhatsApp, p.App);
        Assert.True(p.EligibleForRules);
        Assert.True(p.Include);
        Assert.False(found.Single(c => c.Path == fresh).EligibleForRules);
        Assert.False(found.Single(c => c.Path == pdf).EligibleForRules);     // documents are not in the default types
        Assert.Equal(MessagingSource.TelegramFolder, found.Single(c => c.Path == tg).Source);
        Assert.Equal(MessagingSource.WhatsAppMedia, found.Single(c => c.Path == media).Source);

        // WhatsApp running: its media store is skipped, and saying so.
        var (whileRunning, wasSkipped) = MessagingDownloads.ScanCore(env.Paths, Snapshot(), new HashSet<string>(), [], DateTime.Now, env.Zone, true);
        Assert.True(wasSkipped);
        Assert.DoesNotContain(whileRunning, c => c.Path == media);

        // Sources can be switched off.
        var (noTelegram, _) = MessagingDownloads.ScanCore(env.Paths, Snapshot(telegram: false), new HashSet<string>(), [], DateTime.Now, env.Zone, false);
        Assert.DoesNotContain(noTelegram, c => c.App == MessagingApp.Telegram);
    }

    [Fact]
    public void KeptFilesAreNeverSelected()
    {
        using var env = new Env();
        var photo = env.File(Path.Combine(env.Paths.Downloads, "a.jpg"), 10, WhatsAppZone);
        var (first, _) = MessagingDownloads.ScanCore(env.Paths, Snapshot(), new HashSet<string>(), [], DateTime.Now, env.Zone, false);
        var id = Assert.Single(first).Id;
        var (again, _) = MessagingDownloads.ScanCore(env.Paths, Snapshot(), new HashSet<string> { id }, [], DateTime.Now, env.Zone, false);
        var c = Assert.Single(again);
        Assert.Equal(photo, c.Path);
        Assert.True(c.Excluded);
        Assert.False(c.Include);
    }

    [Fact]
    public void OrganizedFilesAreReviewableButNeverAutomatic()
    {
        using var env = new Env();
        var organized = env.File(Path.Combine(env.Paths.Downloads, "Messaging", "old.jpg"), 30);
        var (found, _) = MessagingDownloads.ScanCore(env.Paths, Snapshot(), new HashSet<string>(), [organized], DateTime.Now, env.Zone, false);
        var c = Assert.Single(found);
        Assert.True(c.Organized);
        Assert.True(c.EligibleForRules);
        Assert.False(c.EligibleForAutomaticCleanup);
    }

    [Fact]
    public void CleanRechecksEveryFile()
    {
        using var env = new Env();
        var d = env.Paths.Downloads;
        var keep = env.File(Path.Combine(d, "a.jpg"), 10, WhatsAppZone);
        var replaced = env.File(Path.Combine(d, "b.jpg"), 10, WhatsAppZone);
        var notAuto = env.File(Path.Combine(d, "c.jpg"), 10, WhatsAppZone);
        var snapshot = Snapshot(includeExisting: false, start: DateTime.Now.AddDays(-30));
        var (found, _) = MessagingDownloads.ScanCore(env.Paths, snapshot, new HashSet<string>(), [], DateTime.Now, env.Zone, false);
        File.Delete(replaced);
        env.File(replaced, 10, WhatsAppZone); // a different file now carries the name
        var recycled = new List<string>();
        bool Recycle(string path, long _)
        {
            recycled.Add(path);
            return true;
        }

        var manual = MessagingDownloads.CleanCore(found.Where(c => c.Path != notAuto).ToList(), false, snapshot, DateTime.Now, env.Zone, Recycle);
        Assert.Equal([keep], recycled);
        Assert.Equal(1, manual.Failed);
        Assert.Single(manual.MovedIds);
        Assert.Equal(64, manual.Bytes);

        // Automatic: a file downloaded before automation started is skipped (not failed).
        recycled.Clear();
        var later = Snapshot(includeExisting: false, start: DateTime.Now.AddDays(-1));
        var auto = MessagingDownloads.CleanCore(found.Where(c => c.Path == notAuto).ToList(), true, later, DateTime.Now, env.Zone, Recycle);
        Assert.Empty(recycled);
        Assert.Equal(0, auto.Failed);
    }

    // -------------------------------------------------------------- organizer

    /// <summary>Real moves inside the fixture, but "Recycle Bin" moves go to a fixture folder.</summary>
    private sealed class FixtureOps(string bin) : IOrganizerFileOps
    {
        public List<string> Recycled { get; } = [];
        public bool Exists(string path) => SystemOrganizerFileOps.Instance.Exists(path);
        public bool IsLocked(string path) => SystemOrganizerFileOps.Instance.IsLocked(path);
        public string? Hash(string path) => SystemOrganizerFileOps.Instance.Hash(path);
        public bool Move(string source, string destination) => SystemOrganizerFileOps.Instance.Move(source, destination);

        public bool Recycle(string path, long size)
        {
            Directory.CreateDirectory(bin);
            File.Move(path, Path.Combine(bin, Guid.NewGuid().ToString("N")));
            Recycled.Add(path);
            return true;
        }
    }

    private static OrganizerSettings OrgSettings(Env env, OrganizerLayout layout = OrganizerLayout.Flat, DuplicateAction dup = DuplicateAction.TrashNew) =>
        new(Path.Combine(env.Paths.Downloads, "Messaging"), 5, new HashSet<MessagingCategory>(Enum.GetValues<MessagingCategory>()), layout, dup);

    private static OrganizerSource Src(string path, int minutesAgo) =>
        new(path, new FileInfo(path).Length, DateTime.Now.AddMinutes(-minutesAgo), DateTime.Now.AddMinutes(-minutesAgo), MessagingRules.Category(Path.GetExtension(path)));

    [Fact]
    public void OrganizesStableFilesAndWaitsForFreshOnes()
    {
        using var env = new Env();
        var ops = new FixtureOps(Path.Combine(env.Root, "bin"));
        var stable = env.File(Path.Combine(env.Paths.Downloads, "a.jpg"), 1);
        var fresh = env.File(Path.Combine(env.Paths.Downloads, "b.jpg"), 0);
        var result = MessagingOrganizer.RunCore([Src(stable, 30), Src(fresh, 1)], OrgSettings(env, OrganizerLayout.Category), [], DateTime.Now, ops);
        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(env.Paths.Downloads, "Messaging", "Images", "a.jpg")));
        Assert.False(File.Exists(stable));
        Assert.True(File.Exists(fresh));
        Assert.NotNull(result.NextEligible);
        Assert.Single(result.Records);

        // Undo moves it back.
        Assert.Equal(0, MessagingOrganizer.PerformUndo(result.Undo!, ops));
        Assert.True(File.Exists(stable));
        Assert.Empty(MessagingOrganizer.RecordsAfterUndo(result.Undo!, result.Records));
    }

    [Fact]
    public void DetectsExactRepeatDownloads()
    {
        using var env = new Env();
        var ops = new FixtureOps(Path.Combine(env.Root, "bin"));
        var first = env.File(Path.Combine(env.Paths.Downloads, "photo.jpg"), 1, bytes: 300);
        var r1 = MessagingOrganizer.RunCore([Src(first, 30)], OrgSettings(env), [], DateTime.Now, ops);
        Assert.Equal(1, r1.Moved);

        // WhatsApp downloads the same bytes again under the same name.
        var again = env.File(Path.Combine(env.Paths.Downloads, "photo.jpg"), 1, bytes: 300);
        var r2 = MessagingOrganizer.RunCore([Src(again, 30)], OrgSettings(env), r1.Records, DateTime.Now, ops);
        Assert.Equal(0, r2.Moved);
        Assert.Equal(1, r2.Duplicates);
        Assert.Equal([again], ops.Recycled);

        // Keep both: moved under a free name.
        var third = env.File(Path.Combine(env.Paths.Downloads, "photo.jpg"), 1, bytes: 300);
        var r3 = MessagingOrganizer.RunCore([Src(third, 30)], OrgSettings(env, dup: DuplicateAction.KeepBoth), r2.Records, DateTime.Now, ops);
        Assert.Equal(1, r3.Moved);
        Assert.Equal(1, r3.Duplicates);
        Assert.True(File.Exists(Path.Combine(env.Paths.Downloads, "Messaging", "photo (2).jpg")));

        // A different file with the same name is not a duplicate.
        var different = env.File(Path.Combine(env.Paths.Downloads, "photo.jpg"), 1, bytes: 301);
        var r4 = MessagingOrganizer.RunCore([Src(different, 30)], OrgSettings(env), r3.Records, DateTime.Now, ops);
        Assert.Equal(1, r4.Moved);
        Assert.Equal(0, r4.Duplicates);
    }

    [Fact]
    public void DestinationIsNeverDownloadsItself()
    {
        Assert.Equal(@"C:\U\Downloads\Messaging", MessagingOrganizer.ResolveDestination("", @"C:\U\Downloads"));
        Assert.Equal(@"C:\U\Downloads\Messaging", MessagingOrganizer.ResolveDestination(@"C:\U\Downloads\", @"C:\U\Downloads"));
        Assert.Equal(@"D:\Sorted", MessagingOrganizer.ResolveDestination(@"D:\Sorted\", @"C:\U\Downloads"));
    }

    [Fact]
    public void ServiceRunsOnlyWhenSomethingIsAutomated()
    {
        Assert.False(MessagingService.ShouldRunFor(false, true, true));
        Assert.False(MessagingService.ShouldRunFor(true, false, false));
        Assert.True(MessagingService.ShouldRunFor(true, true, false));
        Assert.True(MessagingService.ShouldRunFor(true, false, true));
    }
}
