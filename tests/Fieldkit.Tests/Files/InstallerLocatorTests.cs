// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public class InstallerLocatorTests
{
    private static InstallerLocation Image(string? autorun, params string[] files) =>
        InstallerLocator.Locate(files, autorun, isArchive: false);

    private static InstallerLocation Zip(params string[] files) =>
        InstallerLocator.Locate(files, null, isArchive: true);

    private static void AssertInstaller(InstallerLocation location, string path, InstallerKind kind = InstallerKind.Exe)
    {
        var found = Assert.IsType<InstallerFound>(location);
        Assert.Equal(path, found.RelativePath, ignoreCase: true);
        Assert.Equal(kind, found.Kind);
    }

    [Fact]
    public void AutorunTargetWinsOverSetupExe()
    {
        var autorun = "[AutoRun]\r\nopen=bin\\launcher.exe /auto\r\nicon=app.ico\r\n";
        AssertInstaller(Image(autorun, "setup.exe", "autorun.inf", "bin/launcher.exe"), "bin\\launcher.exe");
    }

    [Fact]
    public void AutorunShellExecuteIsPreferredOverOpen()
    {
        var autorun = "[autorun]\nopen=setup.exe\nshellexecute=\"Install Me.msi\"\n";
        AssertInstaller(Image(autorun, "setup.exe", "Install Me.msi", "autorun.inf"), "Install Me.msi", InstallerKind.Msi);
    }

    [Fact]
    public void AutorunPointingAtMissingFileFallsBack()
    {
        AssertInstaller(Image("[autorun]\nopen=gone.exe\n", "setup.exe", "autorun.inf"), "setup.exe");
    }

    [Fact]
    public void AutorunPointingAtNonProgramIsIgnored()
    {
        AssertInstaller(Image("[autorun]\nopen=readme.txt\n", "readme.txt", "installer.exe"), "installer.exe");
    }

    [Theory]
    [InlineData("[autorun]\nopen=setup.exe", "setup.exe")]
    [InlineData("[AUTORUN]\r\nOPEN = .\\Setup.exe -x\r\n", "Setup.exe")]
    [InlineData("﻿[autorun]\nopen=\"Program Files\\My Setup.exe\" /s", "Program Files\\My Setup.exe")]
    [InlineData("[autorun]\nopen=My App\\setup.exe /quiet", "My App\\setup.exe")]
    [InlineData("[autorun]\n; comment\nshellexecute=x64/inst.msi", "x64\\inst.msi")]
    [InlineData("[other]\nopen=setup.exe", null)]
    [InlineData("[autorun]\nicon=a.ico", null)]
    [InlineData("", null)]
    public void ParsesAutorunTargets(string content, string? expected)
    {
        Assert.Equal(expected, InstallerLocator.ParseAutorunTarget(content));
    }

    [Fact]
    public void PrefersSetupThenInstallThenInstaller()
    {
        AssertInstaller(Image(null, "installer.exe", "install.exe", "Setup.EXE"), "Setup.EXE");
        AssertInstaller(Image(null, "installer.exe", "install.exe"), "install.exe");
        AssertInstaller(Image(null, "installer.exe", "app.exe"), "installer.exe");
    }

    [Fact]
    public void RootBeatsOneLevelDown()
    {
        AssertInstaller(Image(null, "sub\\setup.exe", "setup.exe"), "setup.exe");
    }

    [Fact]
    public void FindsSetupOneLevelDown()
    {
        AssertInstaller(Image(null, "readme.txt", "Disk1/setup.exe"), "Disk1\\setup.exe");
    }

    [Fact]
    public void IgnoresFilesDeeperThanOneLevel()
    {
        var result = Image(null, "a\\b\\setup.exe", "readme.txt");
        Assert.IsType<NoInstallerFound>(result);
    }

    [Fact]
    public void PicksArchitectureFolderWhenSetupIsInSeveral()
    {
        AssertInstaller(InstallerLocator.Locate(["x86\\setup.exe", "x64\\setup.exe"], null, false, prefer64Bit: true), "x64\\setup.exe");
        AssertInstaller(InstallerLocator.Locate(["x86\\setup.exe", "x64\\setup.exe"], null, false, prefer64Bit: false), "x86\\setup.exe");
    }

    [Fact]
    public void AmbiguousSetupsGiveReason()
    {
        var none = Assert.IsType<NoInstallerFound>(Image(null, "Office\\setup.exe", "Visio\\setup.exe"));
        Assert.Contains("several", none.Reason);
    }

    [Fact]
    public void SingleMsiIsUsed()
    {
        AssertInstaller(Image(null, "Product.msi", "readme.txt", "data.cab"), "Product.msi", InstallerKind.Msi);
    }

    [Fact]
    public void MsiBeatsNamedExe()
    {
        AssertInstaller(Image(null, "Product.msi", "ProductSetupHelper.exe"), "Product.msi", InstallerKind.Msi);
    }

    [Fact]
    public void SeveralMsisAreAmbiguous()
    {
        Assert.IsType<NoInstallerFound>(Image(null, "a.msi", "b.msi"));
    }

    [Fact]
    public void ExeNamedLikeInstallerIsUsed()
    {
        AssertInstaller(Image(null, "app.exe", "tool.exe", "Firefox Setup 130.0.exe"), "Firefox Setup 130.0.exe");
        AssertInstaller(Image(null, "app.exe", "tool.exe", "VeraCrypt_Installer_x64.exe"), "VeraCrypt_Installer_x64.exe");
    }

    [Fact]
    public void UninstallerIsNeverTheInstaller()
    {
        AssertInstaller(Image(null, "unins000.exe", "uninstall.exe", "app-setup.exe"), "app-setup.exe");
        var result = Image(null, "unins000.exe", "uninstall.exe", "app.exe");
        Assert.IsType<NoInstallerFound>(result);
    }

    [Fact]
    public void OnlyRootExeOfDiskImageIsUsed()
    {
        AssertInstaller(Image(null, "Driver.exe", "docs\\manual.pdf", "docs\\viewer.exe"), "Driver.exe");
    }

    [Fact]
    public void SeveralRootExesOfDiskImageGiveNone()
    {
        Assert.IsType<NoInstallerFound>(Image(null, "a.exe", "b.exe"));
    }

    [Fact]
    public void EmptyImageGivesNone()
    {
        var none = Assert.IsType<NoInstallerFound>(Image(null));
        Assert.Contains("empty", none.Reason);
    }

    [Fact]
    public void ZipWithInstallerRunsIt()
    {
        AssertInstaller(Zip("Tool-1.2\\setup.exe", "Tool-1.2\\readme.txt"), "Tool-1.2\\setup.exe");
    }

    [Fact]
    public void ZipWithSingleExeInRootIsPortable()
    {
        var p = Assert.IsType<PortableAppFound>(Zip("putty.exe"));
        Assert.Equal(string.Empty, p.Folder);
        Assert.Equal("putty.exe", p.MainExe);
    }

    [Fact]
    public void ZipWithOneTopFolderIsPortableFromThatFolder()
    {
        var p = Assert.IsType<PortableAppFound>(Zip("WinMerge\\WinMergeU.exe", "WinMerge\\Docs\\a.txt", "WinMerge\\lang.dll", "WinMerge\\unins000.exe"));
        Assert.Equal("WinMerge", p.Folder);
        Assert.Equal("WinMerge\\WinMergeU.exe", p.MainExe);
    }

    [Fact]
    public void ZipPortableIgnoresHelperExes()
    {
        var p = Assert.IsType<PortableAppFound>(Zip("app.exe", "updater.exe", "crashreporter.exe", "app.dll"));
        Assert.Equal("app.exe", p.MainExe);
    }

    [Fact]
    public void ZipPortablePicksExeNamedLikeFolder()
    {
        var p = Assert.IsType<PortableAppFound>(Zip("Notepad3\\Notepad3.exe", "Notepad3\\MiniPath.exe"));
        Assert.Equal("Notepad3\\Notepad3.exe", p.MainExe);
    }

    [Fact]
    public void ZipWithoutAnyExeGivesNone()
    {
        Assert.IsType<NoInstallerFound>(Zip("docs\\readme.txt", "photo.png"));
    }

    [Fact]
    public void ZipWithManyUnrelatedExesGivesNone()
    {
        Assert.IsType<NoInstallerFound>(Zip("a.exe", "b.exe", "c.exe"));
    }

    [Fact]
    public void ZipRootFilesPlusFolderUseRoot()
    {
        var p = Assert.IsType<PortableAppFound>(Zip("app.exe", "plugins\\plugin.exe", "plugins\\other.exe"));
        Assert.Equal(string.Empty, p.Folder);
        Assert.Equal("app.exe", p.MainExe);
    }

    [Fact]
    public void LocateInReadsRealFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "fieldkit-tests", "locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "x64"));
        try
        {
            File.WriteAllText(Path.Combine(root, "autorun.inf"), "[autorun]\nopen=x64\\run.exe\n");
            File.WriteAllText(Path.Combine(root, "x64", "run.exe"), "MZ");
            File.WriteAllText(Path.Combine(root, "setup.exe"), "MZ");
            AssertInstaller(InstallerLocator.LocateIn(root, isArchive: false), "x64\\run.exe");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
