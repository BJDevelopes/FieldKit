// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Uninstaller;

namespace Fieldkit.Tests.Apps.Uninstaller;

public class UninstallRulesTests
{
    private const string ProductCode = "{23170F69-40C1-2702-2301-000001000000}";

    private static readonly Func<string, bool> AllExist = _ => true;

    [Fact]
    public void WindowsInstallerProductsUseMsiexecByProductCode()
    {
        var app = new InstalledApp { Id = "x", Name = "7-Zip", IsWindowsInstaller = true, RegistryKeyName = ProductCode, UninstallString = "MsiExec.exe /I" + ProductCode, Source = InstalledAppSource.MachineRegistry };
        var plan = UninstallRules.Plan(app, preferQuiet: false, AllExist);
        Assert.Equal(UninstallMethod.Msi, plan.Method);
        Assert.Equal("msiexec.exe", plan.FileName);
        Assert.Equal("/x " + ProductCode, plan.Arguments);
        Assert.True(plan.Elevate);
        Assert.EndsWith("/passive /norestart", UninstallRules.Plan(app, preferQuiet: true, AllExist).Arguments);
    }

    [Fact]
    public void MsiexecInstallSwitchBecomesRemove()
    {
        var app = new InstalledApp { Id = "x", Name = "Tool", RegistryKeyName = "Tool", UninstallString = "MsiExec.exe /I" + ProductCode, Source = InstalledAppSource.UserRegistry };
        var plan = UninstallRules.Plan(app, false, AllExist);
        Assert.Equal(UninstallMethod.Msi, plan.Method);
        Assert.Equal("/X" + ProductCode, plan.Arguments);
        Assert.False(plan.Elevate);
    }

    [Fact]
    public void PrefersTheInteractiveUninstallerUnlessAskedForSilence()
    {
        var app = new InstalledApp
        {
            Id = "x",
            Name = "Foo",
            UninstallString = "\"C:\\Program Files\\Foo\\unins000.exe\"",
            QuietUninstallString = "\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT",
            Source = InstalledAppSource.MachineRegistry32,
        };
        var normal = UninstallRules.Plan(app, false, AllExist);
        Assert.Equal(UninstallMethod.Command, normal.Method);
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", normal.FileName);
        Assert.Equal(string.Empty, normal.Arguments);
        Assert.True(normal.Elevate);
        Assert.Equal("\"C:\\Program Files\\Foo\\unins000.exe\"", normal.CommandText);
        Assert.Equal("/SILENT", UninstallRules.Plan(app, true, AllExist).Arguments);
        // Only a quiet command registered: use it even when not preferred.
        var quietOnly = app with { UninstallString = string.Empty };
        Assert.Equal("/SILENT", UninstallRules.Plan(quietOnly, false, AllExist).Arguments);
    }

    [Fact]
    public void MissingOrUnregisteredUninstallersCannotRun()
    {
        var missing = new InstalledApp { Id = "x", Name = "Foo", UninstallString = @"C:\Gone\uninstall.exe /S", Source = InstalledAppSource.UserRegistry };
        var plan = UninstallRules.Plan(missing, false, _ => false);
        Assert.False(plan.CanRun);
        Assert.Contains("missing", plan.Problem);
        var none = new InstalledApp { Id = "y", Name = "Bar", Source = InstalledAppSource.UserRegistry };
        Assert.False(UninstallRules.Plan(none, false, AllExist).CanRun);
        // Not rooted (resolved through PATH): left to the shell.
        var bare = new InstalledApp { Id = "z", Name = "Baz", UninstallString = "rundll32.exe dfshim.dll,ShArpMaintain baz.application", Source = InstalledAppSource.UserRegistry };
        Assert.True(UninstallRules.Plan(bare, false, _ => false).CanRun);
    }

    [Fact]
    public void StorePackagesUseThePackageManager()
    {
        var app = new InstalledApp { Id = "f", Name = "Terminal", PackageFullName = "Microsoft.WindowsTerminal_1.21.2911.0_x64__8wekyb3d8bbwe", Source = InstalledAppSource.StorePackage };
        var plan = UninstallRules.Plan(app, false, AllExist);
        Assert.Equal(UninstallMethod.StorePackage, plan.Method);
        Assert.False(plan.Elevate);
        Assert.False(UninstallRules.Plan(app with { PackageFullName = string.Empty }, false, AllExist).CanRun);
    }

    [Theory]
    [InlineData(ProductCode, true)]
    [InlineData("{23170F69-40C1-2702-2301-00000100000}", false)]
    [InlineData("7-Zip", false)]
    [InlineData("23170F69-40C1-2702-2301-000001000000", false)]
    public void RecognisesProductCodes(string key, bool expected) =>
        Assert.Equal(expected, UninstallRules.TryProductCode(key, out _));

    [Fact]
    public void ResolvesDroppedProgramsToTheirApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "fieldkit-uninst-resolve");
        var vlc = new InstalledApp { Id = "vlc", Name = "VLC media player", InstallLocation = Path.Combine(root, "VideoLAN", "VLC"), IconPath = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe") + ",0" };
        var videolan = new InstalledApp { Id = "vl", Name = "VideoLAN tools", InstallLocation = Path.Combine(root, "VideoLAN") };
        var code = new InstalledApp { Id = "code", Name = "Visual Studio Code", InstallLocation = Path.Combine(root, "Microsoft VS Code"), UninstallString = "\"" + Path.Combine(root, "Microsoft VS Code", "unins000.exe") + "\"" };
        var named = new InstalledApp { Id = "n", Name = "Portable Thing 2.0" };
        InstalledApp[] apps = [videolan, vlc, code, named];

        Assert.Same(vlc, UninstallRules.Resolve(Path.Combine(root, "VideoLAN", "VLC", "vlc.exe"), apps));
        // Most specific install folder wins over the vendor's.
        Assert.Same(vlc, UninstallRules.Resolve(Path.Combine(root, "VideoLAN", "VLC", "plugins", "x.exe"), apps));
        Assert.Same(videolan, UninstallRules.Resolve(Path.Combine(root, "VideoLAN", "other.exe"), apps));
        Assert.Same(code, UninstallRules.Resolve(Path.Combine(root, "Microsoft VS Code", "bin", "code.exe"), apps));
        Assert.Same(named, UninstallRules.Resolve(@"D:\Portable\thing.exe", apps, productName: "Portable Thing"));
        Assert.Null(UninstallRules.Resolve(@"D:\Portable\thing.exe", apps, productName: null));
        Assert.Null(UninstallRules.Resolve(string.Empty, apps));
    }

    [Fact]
    public void NeverOffersItself()
    {
        var self = Path.Combine(Path.GetTempPath(), "fieldkit-uninst-self", "Fieldkit", "Fieldkit.exe");
        Assert.True(UninstallRules.IsSelf(new InstalledApp { Id = "t", Name = "Fieldkit 0.1" }, self));
        Assert.True(UninstallRules.IsSelf(new InstalledApp { Id = "t", Name = "Something", InstallLocation = Path.GetDirectoryName(self)! }, self));
        Assert.False(UninstallRules.IsSelf(new InstalledApp { Id = "s", Name = "Spotify" }, self));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("spot", true)]
    [InlineData("AB", true)]
    [InlineData("1.2", true)]
    [InlineData("discord", false)]
    public void SearchMatchesNamePublisherAndVersion(string query, bool expected)
    {
        var app = new InstalledApp { Id = "s", Name = "Spotify", Publisher = "Spotify AB", Version = "1.2.47" };
        Assert.Equal(expected, UninstallRules.MatchesQuery(app, query));
    }
}
