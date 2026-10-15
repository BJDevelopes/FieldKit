// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public class InstallerLogicTests
{
    // ---------------------------------------------------------------- files

    [Theory]
    [InlineData(@"C:\d\a.iso", InstallerSourceKind.DiskImage)]
    [InlineData(@"C:\d\A.IMG", InstallerSourceKind.DiskImage)]
    [InlineData(@"C:\d\a.vhd", InstallerSourceKind.VirtualDisk)]
    [InlineData(@"C:\d\a.VHDX", InstallerSourceKind.VirtualDisk)]
    [InlineData(@"C:\d\a.zip", InstallerSourceKind.Zip)]
    [InlineData(@"C:\d\a.dmg", InstallerSourceKind.None)]
    [InlineData(@"C:\d\a.iso.part", InstallerSourceKind.None)]
    [InlineData(@"C:\d\zip", InstallerSourceKind.None)]
    [InlineData("", InstallerSourceKind.None)]
    [InlineData(null, InstallerSourceKind.None)]
    public void ClassifiesExtensions(string? path, InstallerSourceKind kind)
    {
        Assert.Equal(kind, InstallerFiles.KindOf(path));
        Assert.Equal(kind != InstallerSourceKind.None, InstallerFiles.IsSupported(path));
    }

    [Fact]
    public void NewestFiltersSortsAndCaps()
    {
        var t = new DateTime(2026, 10, 1, 12, 0, 0);
        var files = new[]
        {
            new InstallerDownload(@"C:\D\old.iso", t.AddDays(-3), 10),
            new InstallerDownload(@"C:\D\new.zip", t, 10),
            new InstallerDownload(@"C:\D\notes.txt", t.AddDays(1), 10),
            new InstallerDownload(@"C:\D\empty.iso", t.AddDays(1), 0),
            new InstallerDownload(@"C:\D\b.vhdx", t.AddDays(-1), 10),
            new InstallerDownload(@"C:\D\a.vhd", t.AddDays(-1), 10),
            new InstallerDownload(@"C:\D\~$lock.zip", t.AddDays(2), 10),
        };
        var newest = InstallerFiles.Newest(files);
        Assert.Equal(["new.zip", "a.vhd", "b.vhdx", "old.iso"], newest.Select(f => System.IO.Path.GetFileName(f.Path)));
        Assert.Equal(2, InstallerFiles.Newest(files, 2).Count);
        Assert.Empty(InstallerFiles.Newest(files, 0));
    }

    [Fact]
    public void NewestCapsAtTwelveByDefault()
    {
        var t = DateTime.Now;
        var files = Enumerable.Range(0, 30).Select(i => new InstallerDownload($@"C:\D\f{i}.iso", t.AddMinutes(-i), 1));
        var newest = InstallerFiles.Newest(files);
        Assert.Equal(InstallerFiles.MaxListed, newest.Count);
        Assert.EndsWith("f0.iso", newest[0].Path);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(6_114_656_256, "5.7 GB")]
    [InlineData(300L * 1024 * 1024 * 1024, "300 GB")]
    public void FormatsSizes(long bytes, string expected)
    {
        Assert.Equal(expected, InstallerFiles.FormatSize(bytes).Replace(',', '.'));
    }

    [Fact]
    public void FormatsAges()
    {
        var now = new DateTime(2026, 10, 14, 15, 0, 0);
        Assert.Equal("Just now", InstallerFiles.FormatAge(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", InstallerFiles.FormatAge(now.AddMinutes(-5), now));
        Assert.StartsWith("Today", InstallerFiles.FormatAge(now.AddHours(-3), now));
        Assert.Equal("Yesterday", InstallerFiles.FormatAge(now.AddDays(-1), now));
        Assert.Equal("3 days ago", InstallerFiles.FormatAge(now.AddDays(-3), now));
        Assert.DoesNotContain("ago", InstallerFiles.FormatAge(now.AddDays(-30), now));
    }

    // ---------------------------------------------------------------- PowerShell

    [Theory]
    [InlineData(@"C:\a.iso", @"'C:\a.iso'")]
    [InlineData(@"C:\it's.iso", @"'C:\it''s.iso'")]
    [InlineData("C:\\a\u2019b.iso", "'C:\\a\u2019\u2019b.iso'")]
    [InlineData("C:\\\u2018x\u201A\u201B.iso", "'C:\\\u2018\u2018x\u201A\u201A\u201B\u201B.iso'")]
    [InlineData("$(evil) `n \"q\".iso", "'$(evil) `n \"q\".iso'")]
    [InlineData("", "''")]
    public void QuotesPowerShellLiterals(string value, string expected)
    {
        Assert.Equal(expected, InstallerPowerShell.Quote(value));
    }

    [Fact]
    public void MountScriptQuotesPathAndPicksStorageType()
    {
        var iso = InstallerPowerShell.MountScript(@"C:\Down loads\it's.iso");
        Assert.Contains(@"-ImagePath 'C:\Down loads\it''s.iso'", iso);
        Assert.Contains("-StorageType ISO", iso);
        Assert.Contains(InstallerPowerShell.MountedMarker, iso);
        Assert.Contains("-StorageType VHDX", InstallerPowerShell.MountScript(@"C:\a.vhdx"));
        Assert.Contains("-StorageType VHD ", InstallerPowerShell.MountScript(@"C:\a.vhd"));
        Assert.Contains("Mount-DiskImage", iso);
        Assert.Equal("$ErrorActionPreference = 'Stop'; Dismount-DiskImage -ImagePath 'C:\\a.iso' | Out-Null",
            InstallerPowerShell.DismountScript(@"C:\a.iso"));
    }

    [Fact]
    public void EncodesAsUtf16Base64()
    {
        var encoded = InstallerPowerShell.Encode("'ä'");
        Assert.Equal("'ä'", Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Theory]
    [InlineData("FIELDKIT-MOUNTED\r\nE\r\n", true, 'E')]
    [InlineData("f\r\n", false, 'F')]
    [InlineData("FIELDKIT-MOUNTED\r\n", true, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    [InlineData("Warning: something\r\nEE\r\n", false, null)]
    public void ParsesMountOutput(string? stdout, bool mounted, char? letter)
    {
        var (m, l) = InstallerPowerShell.ParseMountOutput(stdout);
        Assert.Equal(mounted, m);
        Assert.Equal(letter, l);
    }

    [Theory]
    [InlineData("Mount-DiskImage : Access is denied.", true)]
    [InlineData("HRESULT 0x80070005", true)]
    [InlineData("CategoryInfo : PermissionDenied: (MSFT_DiskImage)", true)]
    [InlineData("The file is in use", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DetectsAccessDenied(string? stderr, bool expected)
    {
        Assert.Equal(expected, InstallerPowerShell.LooksLikeAccessDenied(stderr));
    }

    // ---------------------------------------------------------------- zip safety

    [Theory]
    [InlineData("setup.exe", @"C:\T\x\setup.exe")]
    [InlineData("dir/sub/file.txt", @"C:\T\x\dir\sub\file.txt")]
    [InlineData("dir/", @"C:\T\x\dir\")]
    [InlineData("../evil.exe", null)]
    [InlineData("dir/../../evil.exe", null)]
    [InlineData("dir\\..\\..\\evil.exe", null)]
    [InlineData("/abs.exe", null)]
    [InlineData("\\abs.exe", null)]
    [InlineData("C:/Windows/evil.dll", null)]
    [InlineData("file.txt:stream", null)]
    [InlineData("a<b.txt", null)]
    [InlineData("", null)]
    [InlineData("./", null)]
    public void ResolvesEntriesInsideTheFolder(string entry, string? expected)
    {
        Assert.Equal(expected, InstallerZipSafety.ResolveEntry(@"C:\T\x", entry));
    }

    [Fact]
    public void SiblingFolderWithSamePrefixIsOutside()
    {
        Assert.Null(InstallerZipSafety.ResolveEntry(@"C:\T\x", "..\\x2\\a.txt"));
    }

    [Fact]
    public void ChecksArchiveLimits()
    {
        Assert.Null(InstallerZipSafety.CheckLimits([10, 20, 30], maxTotalBytes: 60, maxEntries: 3));
        Assert.Contains("more than", InstallerZipSafety.CheckLimits([10, 20, 31], maxTotalBytes: 60, maxEntries: 3));
        Assert.Contains("files", InstallerZipSafety.CheckLimits([1, 1, 1, 1], maxTotalBytes: 60, maxEntries: 3));
        Assert.Contains("damaged", InstallerZipSafety.CheckLimits([-1]));
        Assert.Null(InstallerZipSafety.CheckLimits([]));
        Assert.Contains("8 GB", InstallerZipSafety.CheckLimits([InstallerZipSafety.MaxTotalBytes, 1]));
    }

    // ---------------------------------------------------------------- paths

    [Theory]
    [InlineData(@"E:\", @"E:\setup.exe", true)]
    [InlineData(@"E:", @"e:\sub\setup.exe", true)]
    [InlineData(@"C:\T\abc", @"C:\T\abc\x.exe", true)]
    [InlineData(@"C:\T\abc\", @"C:\t\ABC\x.exe", true)]
    [InlineData(@"C:\T\abc", @"C:\T\abcd\x.exe", false)]
    [InlineData(@"C:\T\abc", @"C:\T\abc", false)]
    [InlineData(@"E:\", null, false)]
    [InlineData("", @"C:\x", false)]
    public void ChecksContainment(string root, string? path, bool expected)
    {
        Assert.Equal(expected, InstallerPaths.IsUnder(root, path));
    }

    [Theory]
    [InlineData("WinMerge", "WinMerge")]
    [InlineData("a/b:c*d", "abcd")]
    [InlineData("  name.  ", "name")]
    [InlineData("CON", "CON app")]
    [InlineData("nul.txt", "nul.txt app")]
    [InlineData("...", "App")]
    [InlineData("<>", "App")]
    public void MakesSafeFolderNames(string name, string expected)
    {
        Assert.Equal(expected, InstallerPaths.SafeFolderName(name));
    }

    [Fact]
    public void PortableDestinationIsDirectChild()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\WinMerge",
            InstallerPaths.PortableDestination(@"C:\Users\u\AppData\Local\Programs\", "WinMerge"));
        Assert.Equal(@"C:\P\....evil", InstallerPaths.PortableDestination(@"C:\P", @"..\..\evil"));
        Assert.Equal(@"C:\P\App", InstallerPaths.PortableDestination(@"C:\P", ".."));
        Assert.Null(InstallerPaths.PortableDestination("", "x"));
    }

    // ---------------------------------------------------------------- waiting

    [Fact]
    public void WaitTracksRootAndAddsChildren()
    {
        var previous = new Dictionary<int, int> { [100] = InstallerWait.AnyParent };
        var snapshot = new List<InstallerProcess>
        {
            new(100, 1, @"E:\setup.exe"),
            new(200, 100, @"C:\Users\u\AppData\Local\Temp\is-123\setup.tmp"),
            new(300, 200, @"C:\Users\u\AppData\Local\Temp\is-123\helper.exe"),
            new(400, 1, @"C:\Windows\explorer.exe"),
        };
        var next = InstallerWait.Step(previous, snapshot);
        Assert.Equal([100, 200, 300], next.Keys.Order());
        Assert.Equal(InstallerWait.AnyParent, next[100]);
    }

    [Fact]
    public void WaitFollowsOrphansOfAnExitedBootstrapper()
    {
        var previous = new Dictionary<int, int> { [100] = InstallerWait.AnyParent };
        var snapshot = new List<InstallerProcess> { new(200, 100, @"C:\Temp\real-setup.exe") };
        Assert.Equal([200], InstallerWait.Step(previous, snapshot).Keys);
        // One poll later the bootstrapper's PID is gone from the tracked set: a newcomer reusing
        // that parent PID is no longer adopted, but the tracked child stays.
        var later = InstallerWait.Step(new Dictionary<int, int> { [200] = 100 },
            [new(200, 100, @"C:\Temp\real-setup.exe"), new(500, 100, @"C:\x\unrelated.exe")]);
        Assert.Equal([200], later.Keys);
    }

    [Fact]
    public void WaitDropsReusedPids()
    {
        var previous = new Dictionary<int, int> { [200] = 100 };
        Assert.Empty(InstallerWait.Step(previous, [new(200, 999, @"C:\x\other.exe")]));
    }

    [Fact]
    public void WaitIgnoresTheAppAnInstallerLaunchesAtTheEnd()
    {
        var previous = new Dictionary<int, int> { [100] = InstallerWait.AnyParent };
        var snapshot = new List<InstallerProcess>
        {
            new(100, 1, @"E:\setup.exe"),
            new(200, 100, @"C:\Program Files\App\app.exe"),
            new(300, 100, @"C:\Users\u\AppData\Local\Temp\x\setup.tmp"),
            new(400, 100, null),
        };
        var next = InstallerWait.Step(previous, snapshot, p => InstallerWait.IsInstallerWork(p, @"E:\", @"C:\Users\u\AppData\Local\Temp\"));
        Assert.Equal([100, 300, 400], next.Keys.Order());
    }

    [Theory]
    [InlineData(@"E:\bin\x.exe", true)]
    [InlineData(@"C:\Temp\x.exe", true)]
    [InlineData(@"C:\Windows\System32\msiexec.exe", true)]
    [InlineData(@"C:\Program Files\Thing\ThingInstallHelper.exe", true)]
    [InlineData(@"C:\Program Files\Thing\thing.exe", false)]
    [InlineData(null, true)]
    public void ClassifiesInstallerWork(string? image, bool expected)
    {
        Assert.Equal(expected, InstallerWait.IsInstallerWork(new InstallerProcess(5, 1, image), @"E:\", @"C:\Temp"));
    }

    [Fact]
    public void FindsProcessesRunningFromRoot()
    {
        var snapshot = new List<InstallerProcess> { new(1, 0, @"E:\a.exe"), new(2, 0, @"C:\b.exe"), new(3, 0, @"E:\self.exe"), new(4, 0, null) };
        Assert.Equal([1], InstallerWait.RunningFrom(@"E:\", snapshot, selfPid: 3).Select(p => p.Pid));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(1602, false)]
    [InlineData(1, false)]
    public void InterpretsExitCodes(int? code, bool succeeded)
    {
        Assert.Equal(succeeded, InstallerWait.Succeeded(code));
        Assert.Equal(succeeded, InstallerWait.ExitCodeDetail(InstallerKind.Exe, code) is null);
    }

    [Fact]
    public void ExplainsMsiCancel()
    {
        Assert.Equal("The installation was cancelled.", InstallerWait.ExitCodeDetail(InstallerKind.Msi, 1602));
        Assert.Equal("It exited with code 1602.", InstallerWait.ExitCodeDetail(InstallerKind.Exe, 1602));
    }

    // ---------------------------------------------------------------- text

    [Theory]
    [InlineData(null, "VeraCrypt Setup 1.26.iso", "VeraCrypt Setup 1.26")]
    [InlineData("  My\t\n  App  ", "x.zip", "My App")]
    [InlineData("   ", "Tool.zip", "Tool")]
    [InlineData("A\u0007B\u200BC", "x.iso", "ABC")]
    [InlineData(null, "noext", "noext")]
    public void MakesDisplayNames(string? preferred, string fileName, string expected)
    {
        Assert.Equal(expected, InstallerText.DisplayName(preferred, fileName));
    }

    [Fact]
    public void DisplayNameIsCappedAtEighty()
    {
        var name = InstallerText.DisplayName(new string('x', 200), "a.iso");
        Assert.Equal(80, name.Length);
        var emoji = InstallerText.DisplayName(new string('x', 79) + "\U0001F600", "a.iso");
        Assert.Equal(79, emoji.Length);
    }

    [Fact]
    public void PromptMentionsCleanup()
    {
        Assert.Equal("Tool will run its installer. When it closes, the disk image is ejected and the download moved to the Recycle Bin.",
            InstallerText.PromptBody("Tool", InstallerMethod.Installer, InstallerSourceKind.DiskImage, null, true));
        Assert.Equal("Tool will run its installer. When it closes, the extracted files are removed.",
            InstallerText.PromptBody("Tool", InstallerMethod.Installer, InstallerSourceKind.Zip, null, false));
        Assert.Equal(@"Tool will be copied to C:\P and the download moved to the Recycle Bin.",
            InstallerText.PromptBody("Tool", InstallerMethod.Portable, InstallerSourceKind.Zip, @"C:\P", true));
    }

    private static InstallerResult R(InstallerOutcome o, InstallerSourceKind s = InstallerSourceKind.DiskImage,
        InstallerMethod m = InstallerMethod.Installer, bool trashed = false, bool cleaned = true, string? detail = null) =>
        new(o, s, m, "Tool", @"C:\P", trashed, cleaned, detail);

    [Fact]
    public void DescribesInstalledOutcomes()
    {
        var a = InstallerText.Describe(R(InstallerOutcome.Installed, trashed: true));
        Assert.Equal("Installer finished", a.Title);
        Assert.Equal("The Tool installer finished. The disk image was ejected and its download moved to the Recycle Bin.", a.Body);
        Assert.False(a.Warning);
        Assert.True(a.Notify);

        Assert.Equal("The Tool installer finished. The disk image was ejected and its download kept.",
            InstallerText.Describe(R(InstallerOutcome.Installed)).Body);
        Assert.Equal("The Tool installer finished. The extracted files were removed and its download moved to the Recycle Bin.",
            InstallerText.Describe(R(InstallerOutcome.Installed, InstallerSourceKind.Zip, trashed: true)).Body);

        var portable = InstallerText.Describe(R(InstallerOutcome.Installed, InstallerSourceKind.Zip, InstallerMethod.Portable, trashed: true));
        Assert.Equal("App installed", portable.Title);
        Assert.Equal(@"Tool is ready in C:\P. Its download was moved to the Recycle Bin.", portable.Body);
    }

    [Fact]
    public void DescribesPartialOutcomes()
    {
        var mount = InstallerText.Describe(R(InstallerOutcome.InstalledKeepingMount));
        Assert.True(mount.Warning);
        Assert.Equal("The Tool installer finished, but the disk image could not be ejected. Its download was kept.", mount.Body);

        Assert.Equal("The Tool installer finished. The disk image was ejected, but its download could not be moved to the Recycle Bin.",
            InstallerText.Describe(R(InstallerOutcome.InstalledKeepingDownload)).Body);
        Assert.Equal(@"Tool is ready in C:\P. Its download could not be moved to the Recycle Bin.",
            InstallerText.Describe(R(InstallerOutcome.InstalledKeepingDownload, InstallerSourceKind.Zip, InstallerMethod.Portable)).Body);
    }

    [Fact]
    public void DescribesFailures()
    {
        Assert.Equal(@"Tool is already installed in C:\P.", InstallerText.Describe(R(InstallerOutcome.AlreadyInstalled)).Body);
        Assert.Equal("The archive is damaged. " + InstallerText.FailedBody,
            InstallerText.Describe(R(InstallerOutcome.Failed, detail: "The archive is damaged.")).Body);
        Assert.Equal(InstallerText.FailedBody, InstallerText.Describe(R(InstallerOutcome.Failed)).Body);
        Assert.Contains("administrator", InstallerText.Describe(R(InstallerOutcome.NeedsAdmin, InstallerSourceKind.VirtualDisk)).Body);
        var none = InstallerText.Describe(R(InstallerOutcome.NoInstaller, detail: "No setup here."));
        Assert.Equal("No installer found", none.Title);
        Assert.Equal("No setup here. Nothing was installed and the download was kept.", none.Body);
        Assert.Equal("The Tool installer was cancelled or failed. It exited with code 2. The disk image was ejected and its download kept.",
            InstallerText.Describe(R(InstallerOutcome.InstallerFailed, detail: "It exited with code 2.")).Body);
        Assert.Equal("The Tool installer was cancelled or failed. The disk image could not be ejected and its download was kept.",
            InstallerText.Describe(R(InstallerOutcome.InstallerFailed, cleaned: false)).Body);
        Assert.Contains("left mounted", InstallerText.Describe(R(InstallerOutcome.StillRunning)).Body);
    }

    [Fact]
    public void CancelIsQuiet()
    {
        var c = InstallerText.Describe(R(InstallerOutcome.Cancelled));
        Assert.False(c.Notify);
        Assert.False(c.Warning);
        Assert.Equal("Nothing was installed. The disk image was ejected and its download kept.", c.Body);
    }

    [Fact]
    public void EveryOutcomeHasText()
    {
        foreach (var outcome in Enum.GetValues<InstallerOutcome>())
        foreach (var source in new[] { InstallerSourceKind.DiskImage, InstallerSourceKind.Zip })
        foreach (var method in Enum.GetValues<InstallerMethod>())
        {
            var (title, body, _, _) = InstallerText.Describe(R(outcome, source, method));
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.DoesNotContain("Trash", body);
            Assert.DoesNotContain("Mac", body);
            Assert.DoesNotContain("  ", body);
        }
    }
}
