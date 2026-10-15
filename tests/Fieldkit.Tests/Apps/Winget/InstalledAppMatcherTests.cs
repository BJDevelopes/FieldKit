// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Tests.Apps.Winget;

public class InstalledAppMatcherTests
{
    private static readonly List<InstalledApp> Apps =
    [
        new() { Id = "1", Name = "Git", Version = "2.53.0", Source = InstalledAppSource.MachineRegistry, RegistryKeyName = "Git_is1" },
        new() { Id = "2", Name = "Microsoft Visual Studio Code (User)", Version = "1.141.0", Source = InstalledAppSource.UserRegistry, RegistryKeyName = "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1" },
        new() { Id = "3", Name = "Discord", Version = "1.0.9261", Source = InstalledAppSource.UserRegistry, RegistryKeyName = "Discord" },
        new() { Id = "4", Name = "Paint", Version = "11.2508.361.0", Source = InstalledAppSource.StorePackage, PackageFullName = "Microsoft.Paint_11.2508.361.0_x64__8wekyb3d8bbwe" },
        new() { Id = "5", Name = "Python 3.12.4 (64-bit)", Version = "3.12.4150.0", Source = InstalledAppSource.MachineRegistry },
        new() { Id = "6", Name = "Python 3.12.4 (64-bit)", Version = "3.12.4150.0", Source = InstalledAppSource.UserRegistry },
        new() { Id = "7", Name = "Notepad++", Version = "8.8.5", Source = InstalledAppSource.MachineRegistry },
        new() { Id = "8", Name = "Notepad++", Version = "8.7.0", Source = InstalledAppSource.UserRegistry },
    ];

    [Fact]
    public void MatchesMsixByPackageFullName() =>
        Assert.Equal("4", InstalledAppMatcher.Match("Paint", @"MSIX\Microsoft.Paint_11.2508.361.0_x64__8wekyb3d8bbwe", "11.2508.361.0", Apps)?.Id);

    [Fact]
    public void MatchesArpByRegistryKey() =>
        Assert.Equal("3", InstalledAppMatcher.Match("Something else", @"ARP\User\X64\Discord", "1.0", Apps)?.Id);

    [Fact]
    public void MatchesByNormalizedName() =>
        Assert.Equal("2", InstalledAppMatcher.Match("Microsoft Visual Studio Code (User)", "Microsoft.VisualStudioCode", "1.141.0", Apps)?.Id);

    [Fact]
    public void AmbiguousNamesMatchNothing() =>
        Assert.Null(InstalledAppMatcher.Match("Python 3.12.4 (64-bit)", "Python.Python.3.12", "3.12.4150.0", Apps));

    [Fact]
    public void VersionBreaksTies() =>
        Assert.Equal("8", InstalledAppMatcher.Match("Notepad++", "Notepad++.Notepad++", "8.7.0", Apps)?.Id);

    [Fact]
    public void MatchesByPrefixWhenUnique() =>
        Assert.Equal("2", InstalledAppMatcher.Match("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode", "1.141.0", Apps)?.Id);

    [Fact]
    public void ShortNamesDoNotPrefixMatch() =>
        Assert.Null(InstalledAppMatcher.Match("Gi", "Gi.Gi", "1", Apps));

    [Fact]
    public void UnknownMsixMatchesNothing() =>
        Assert.Null(InstalledAppMatcher.Match("Paint", @"MSIX\Other_1.0_x64__abc", "1.0", Apps));
}
