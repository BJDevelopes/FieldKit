// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Uninstaller;

namespace Fieldkit.Tests.Apps.Uninstaller;

public class LeftoverRulesTests
{
    private static InstalledApp App(string name, string publisher = "", string install = "", string family = "") => new()
    {
        Id = name,
        Name = name,
        Publisher = publisher,
        InstallLocation = install,
        PackageFamilyName = family,
        Source = family.Length > 0 ? InstalledAppSource.StorePackage : InstalledAppSource.UserRegistry,
    };

    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("Python 3.12.4 (64-bit)", "Python")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Git version 2.46.0", "Git")]
    [InlineData("VLC media player", "VLC media player")]
    [InlineData("Foo 2 x64", "Foo")]
    [InlineData("Visual Studio Code Insiders", "Visual Studio Code Insiders")]
    public void StripsVersionsButNotChannels(string raw, string expected) =>
        Assert.Equal(expected, LeftoverRules.StripVersion(raw));

    [Theory]
    [InlineData("Visual Studio Code Insiders", "Visual Studio Code")]
    [InlineData("Firefox Developer Edition 131.0", "Firefox")]
    [InlineData("Discord", "Discord")]
    public void StripsChannelsForTokens(string raw, string expected) =>
        Assert.Equal(expected, LeftoverRules.StripChannel(raw));

    [Theory]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("Spotify AB", "Spotify")]
    [InlineData("Discord Inc.", "Discord")]
    [InlineData("Google LLC", "Google")]
    [InlineData("JetBrains s.r.o.", "JetBrains")]
    [InlineData("Igor Pavlov", "Igor Pavlov")]
    public void StripsCompanySuffixes(string raw, string expected) =>
        Assert.Equal(expected, LeftoverRules.StripCompanySuffix(raw));

    [Fact]
    public void BuildsIdentityFromNamesPublisherFolderAndExecutables()
    {
        var install = Path.Combine(Path.GetTempPath(), "fieldkit-uninst-id", "VideoLAN", "VLC");
        var id = LeftoverRules.Identity(App("VLC media player 3.0.21", "VideoLAN", install),
            [Path.Combine(install, "vlc.exe"), "unins000.exe", "setup.exe"], []);
        Assert.Contains("VLC media player 3.0.21", id.ExactNames);
        Assert.Contains("VLC media player", id.ExactNames);
        Assert.Contains("VLC", id.ExactNames); // install folder name
        Assert.Contains("VideoLAN", id.VendorNames);
        Assert.Contains("vlcmediaplayer", id.Tokens);
        Assert.Contains("vlc", id.ExeNames);
        Assert.DoesNotContain("unins000", id.ExeNames);
        Assert.DoesNotContain("setup", id.ExeNames);
    }

    [Fact]
    public void VendorPrefixedNameIsExactOnlyInsideTheVendorFolder()
    {
        var id = LeftoverRules.Identity(App("Mozilla Firefox (x64 en-US)", "Mozilla"), [], []);
        Assert.Contains("Firefox", id.VendorScopedNames);
        Assert.Equal(LeftoverMatch.Exact, LeftoverRules.Match("Firefox", id, insideVendor: true));
        Assert.Equal(LeftoverMatch.Related, LeftoverRules.Match("Firefox", id));
        Assert.Equal(LeftoverMatch.Exact, LeftoverRules.Match("Mozilla Firefox", id));
        Assert.True(LeftoverRules.IsVendor("Mozilla", id));
        // The vendor folder itself is never a match: "mozilla" is generic.
        Assert.Equal(LeftoverMatch.None, LeftoverRules.Match("Mozilla", id));
        Assert.Equal(LeftoverMatch.None, LeftoverRules.Match("Thunderbird", id, insideVendor: true));
    }

    [Theory]
    [InlineData("Spotify", LeftoverMatch.Exact)]
    [InlineData("spotify", LeftoverMatch.Exact)]
    [InlineData("Spotify.lnk", LeftoverMatch.Exact)]
    [InlineData("SpotifyWebHelper", LeftoverMatch.Related)]
    [InlineData("Spotify-Data", LeftoverMatch.Related)]
    [InlineData("Microsoft", LeftoverMatch.None)]
    [InlineData("Temp", LeftoverMatch.None)]
    [InlineData("Packages", LeftoverMatch.None)]
    [InlineData(".spotify", LeftoverMatch.None)]
    [InlineData("Spot", LeftoverMatch.None)]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}", LeftoverMatch.None)]
    [InlineData("..", LeftoverMatch.None)]
    public void MatchesEntryNamesConservatively(string entry, LeftoverMatch expected)
    {
        var id = LeftoverRules.Identity(App("Spotify", "Spotify AB"), ["Spotify.exe"], []);
        Assert.Equal(expected, LeftoverRules.Match(entry, id));
    }

    [Fact]
    public void ShortNamesAndGenericWordsNeverMatch()
    {
        var id = LeftoverRules.Identity(App("Go", "Google LLC"), [], []);
        Assert.Empty(id.ExactNames);
        Assert.Equal(LeftoverMatch.None, LeftoverRules.Match("Go", id));
        var updater = LeftoverRules.Identity(App("Updater"), [], []);
        Assert.Equal(LeftoverMatch.None, LeftoverRules.Match("Updater", updater));
    }

    [Fact]
    public void ExecutableNamesAreRelatedOnly()
    {
        var id = LeftoverRules.Identity(App("Visual Studio Code", "Microsoft Corporation"), ["Code.exe"], []);
        Assert.Equal(LeftoverMatch.Related, LeftoverRules.Match("Code", id));
        Assert.Equal(LeftoverMatch.Exact, LeftoverRules.Match("Visual Studio Code", id));
    }

    [Fact]
    public void StorePackagesContributeTheirName()
    {
        var id = LeftoverRules.Identity(App("Windows Terminal", "Microsoft Corporation", family: "Microsoft.WindowsTerminal_8wekyb3d8bbwe"), [], []);
        Assert.Contains("windowsterminal", id.Tokens);
        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe", id.PackageFamilyName);
    }

    [Fact]
    public void ShortGenericInstallFolderNamesAreOnlyHints()
    {
        var id = LeftoverRules.Identity(App("Proton VPN", "Proton AG", @"C:\Program Files\Proton\VPN"), [], []);
        Assert.DoesNotContain("VPN", id.ExactNames);
        Assert.Equal(LeftoverMatch.Related, LeftoverRules.Match("VPN", id));
        Assert.Equal(LeftoverMatch.Exact, LeftoverRules.Match("Proton VPN", id, insideVendor: true));
    }

    [Fact]
    public void StoreAppNamesAreOnlyHints()
    {
        var app = App("Claude", "Anthropic, PBC", @"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__abc", "Claude_abc");
        var id = LeftoverRules.Identity(app, [], []);
        Assert.Empty(id.ExactNames);
        Assert.Equal(string.Empty, id.InstallLocation);
        Assert.Equal(LeftoverMatch.Related, LeftoverRules.Match("Claude", id));
    }

    [Fact]
    public void InfersInstallFoldersConservatively()
    {
        InstalledApp Reg(string install = "", string icon = "", string uninstall = "") => new()
        {
            Id = "x", Name = "X", InstallLocation = install, IconPath = icon, UninstallString = uninstall, Source = InstalledAppSource.MachineRegistry,
        };
        Assert.Equal(@"C:\Program Files\Bambu Studio", LeftoverRules.InferInstallLocation(Reg(uninstall: "\"C:\\Program Files\\Bambu Studio\\Uninstall.exe\"")));
        Assert.Equal(@"C:\Program Files (x86)\Graphtec\Cutting Master 4",
            LeftoverRules.InferInstallLocation(Reg(uninstall: "\"C:\\Program Files (x86)\\Graphtec\\Cutting Master 4\\Uninstall\\unins000.exe\"")));
        Assert.Equal(@"C:\Apps\Foo", LeftoverRules.InferInstallLocation(Reg(icon: @"C:\Apps\Foo\foo.exe,0", uninstall: "MsiExec.exe /X{1}")));
        Assert.Equal(string.Empty, LeftoverRules.InferInstallLocation(Reg(uninstall: "MsiExec.exe /X{23170F69-40C1-2702-2301-000001000000}")));
        Assert.Equal(string.Empty, LeftoverRules.InferInstallLocation(Reg(uninstall: "\"C:\\ProgramData\\Package Cache\\{a}\\setup.exe\" /uninstall")));
        Assert.Equal(string.Empty, LeftoverRules.InferInstallLocation(Reg(uninstall: "\"C:\\Program Files\\Google\\Chrome\\Application\\1.0\\Installer\\setup.exe\" --uninstall")));
        Assert.Equal(string.Empty, LeftoverRules.InferInstallLocation(Reg(uninstall: "C:\\WINDOWS\\SysWOW64\\RunDll32.EXE \"C:\\x\\NVI2.DLL\",UninstallPackage x")));
        // A registered but generic location is not second-guessed.
        Assert.Equal(string.Empty, LeftoverRules.InferInstallLocation(Reg(install: @"C:\Program Files", uninstall: "\"C:\\Program Files\\Foo\\u.exe\"")));
        Assert.Equal(@"C:\Program Files\7-Zip", LeftoverRules.InferInstallLocation(Reg(install: @"C:\Program Files\7-Zip\")));
    }

    [Fact]
    public void OtherInstalledAppsClaimTheirNamesAndFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fieldkit-uninst-other", "Krita 5.2");
        var others = new OtherApps([App("Krita 5.2.6 (x64)", "Krita Foundation", folder)]);
        Assert.True(others.ClaimsName("Krita"));
        Assert.True(others.ClaimsName("krita.lnk"));
        Assert.False(others.ClaimsName("Spotify"));
        Assert.True(others.ClaimsPath(Path.Combine(folder, "Lib")));
        Assert.True(others.ClaimsPath(Path.GetDirectoryName(folder)!)); // contains another app's folder
        Assert.False(others.ClaimsPath(Path.Combine(Path.GetTempPath(), "fieldkit-uninst-other2")));
    }

    [Theory]
    [InlineData("Foo.exe.10244.dmp", "foo")]
    [InlineData("My App.exe.1.dmp", "myapp")]
    [InlineData("foo.dmp", null)]
    [InlineData("foo.exe.log", null)]
    public void ParsesCrashDumpNames(string file, string? exe) =>
        Assert.Equal(exe, LeftoverRules.CrashDumpExecutable(file));

    [Fact]
    public void DedupesNestedAndPrefersExact()
    {
        Leftover L(string p, LeftoverMatch m) => new() { Path = p, Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support, Confidence = m };
        var result = LeftoverRules.Dedupe(
        [
            L(@"C:\u\Roaming\Foo", LeftoverMatch.Exact),
            L(@"C:\u\Roaming\Foo\Cache", LeftoverMatch.Exact),
            L(@"C:\u\Roaming\Foo", LeftoverMatch.Related),
            L(@"C:\u\Local\Vendor", LeftoverMatch.Related),
            L(@"C:\u\Local\Vendor\Foo", LeftoverMatch.Exact),
            L(@"C:\u\Local\FooBar", LeftoverMatch.Related),
        ]);
        Assert.Equal([@"C:\u\Roaming\Foo", @"C:\u\Local\FooBar", @"C:\u\Local\Vendor\Foo"], result.Select(r => r.Path).ToArray());
        Assert.All(result.Where(r => r.Path.EndsWith("Foo")), r => Assert.Equal(LeftoverMatch.Exact, r.Confidence));
    }

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Spotify", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Spotify AB\Spotify", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\VideoLAN\VLC", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Office", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\foo", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Foo", false)]
    [InlineData(@"HKEY_CLASSES_ROOT\Foo\Bar", false)]
    public void OnlyAppKeysBelowSoftwareAreDeletable(string key, bool expected) =>
        Assert.Equal(expected, LeftoverRules.IsDeletableRegistryKey(key));

    [Theory]
    [InlineData(LeftoverMatch.Exact, false, LeftoverKind.Folder, false, true)]
    [InlineData(LeftoverMatch.Related, false, LeftoverKind.Folder, false, false)]
    [InlineData(LeftoverMatch.Exact, true, LeftoverKind.Folder, false, false)]
    [InlineData(LeftoverMatch.Exact, true, LeftoverKind.Folder, true, true)]
    [InlineData(LeftoverMatch.Exact, true, LeftoverKind.RegistryKey, true, false)]
    [InlineData(LeftoverMatch.Exact, false, LeftoverKind.RegistryKey, false, true)]
    public void DefaultSelectionIsConservative(LeftoverMatch match, bool machine, LeftoverKind kind, bool admin, bool expected) =>
        Assert.Equal(expected, LeftoverRules.DefaultInclude(match, machine, kind, admin));

    [Fact]
    public void RootsThemselvesAreNeverInside()
    {
        string[] roots = [@"C:\Users\me\AppData\Roaming", @"C:\ProgramData"];
        Assert.True(LeftoverRules.IsInsideAnyRoot(@"C:\Users\me\AppData\Roaming\Foo", roots));
        Assert.False(LeftoverRules.IsInsideAnyRoot(@"C:\Users\me\AppData\Roaming", roots));
        Assert.False(LeftoverRules.IsInsideAnyRoot(@"C:\Users\me\AppData\RoamingX\Foo", roots));
        Assert.False(LeftoverRules.IsInsideAnyRoot(@"C:\Windows\System32", roots));
    }

    [Fact]
    public void InstallFoldersMustBeAppSpecific()
    {
        Assert.False(LeftoverRules.IsAppSpecificFolder(@"C:\"));
        Assert.False(LeftoverRules.IsAppSpecificFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(LeftoverRules.IsAppSpecificFolder(@"C:\Tools"));
        Assert.False(LeftoverRules.IsAppSpecificFolder(@"C:\Apps\bin"));
        Assert.True(LeftoverRules.IsAppSpecificFolder(@"C:\Program Files\7-Zip"));
        Assert.False(LeftoverRules.IsAppSpecificFolder(""));
    }

    [Fact]
    public void LeftoverDisplayNames()
    {
        var key = new Leftover { Path = @"HKEY_CURRENT_USER\Software\Spotify", Kind = LeftoverKind.RegistryKey, Category = LeftoverCategory.Preferences };
        Assert.Equal("Spotify", key.Name);
        Assert.Equal(@"HKEY_CURRENT_USER\Software", key.Location);
        var folder = new Leftover { Path = @"C:\Users\me\AppData\Roaming\Spotify", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support };
        Assert.Equal("Spotify", folder.Name);
        Assert.Equal(@"C:\Users\me\AppData\Roaming", folder.Location);
    }
}
