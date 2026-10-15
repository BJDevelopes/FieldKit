// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Controls;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Tests.Apps;

public class CommonTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Foo Bar\\unins000.exe\" /SILENT", "C:\\Program Files\\Foo Bar\\unins000.exe", "/SILENT")]
    [InlineData("MsiExec.exe /X{1234-ABCD}", "MsiExec.exe", "/X{1234-ABCD}")]
    [InlineData("C:\\Program Files\\Foo\\uninstall.exe --remove", "C:\\Program Files\\Foo\\uninstall.exe", "--remove")]
    [InlineData("C:\\Tools\\x.exe", "C:\\Tools\\x.exe", "")]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain app.application", "rundll32.exe", "dfshim.dll,ShArpMaintain app.application")]
    public void SplitsCommandLines(string input, string file, string args)
    {
        var (f, a) = InstalledAppRules.SplitCommandLine(input);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }

    [Fact]
    public void FiltersSystemComponentsUpdatesAndChildren()
    {
        Assert.True(InstalledAppRules.IsUserVisible(new UninstallEntry("7-Zip", 0, null, null, "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry(null, 0, null, null, "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry("VC Runtime", 1, null, null, "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry("Child", 0, "Parent", null, "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry("Office fix", 0, null, "Security Update", "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry("Update for Foo (KB1234567)", 0, null, null, "x.exe", 0, 0)));
        Assert.False(InstalledAppRules.IsUserVisible(new UninstallEntry("No uninstaller", 0, null, null, null, 0, 0)));
        Assert.True(InstalledAppRules.IsUserVisible(new UninstallEntry("MSI app", 0, null, null, null, 1, 0)));
    }

    [Fact]
    public void ParsesRegistryValues()
    {
        Assert.Equal(2048L, InstalledAppRules.EstimatedSizeBytes(2));
        Assert.Equal(0L, InstalledAppRules.EstimatedSizeBytes(null));
        Assert.Equal(new DateTime(2024, 3, 9), InstalledAppRules.ParseInstallDate("20240309"));
        Assert.Null(InstalledAppRules.ParseInstallDate(""));
        Assert.Equal("C:\\x\\app.exe", InstalledAppRules.IconFile("C:\\x\\app.exe,0"));
        Assert.Equal("C:\\x y\\app.exe", InstalledAppRules.IconFile("\"C:\\x y\\app.exe\",-101"));
        Assert.Equal("C:\\Apps\\Foo", InstalledAppRules.CleanPath("\"C:\\Apps\\Foo\\\""));
        Assert.Equal("visualstudiocode", InstalledAppRules.Normalize("Visual Studio Code"));
    }

    [Fact]
    public void DeduplicatesAcrossViews()
    {
        var a = new InstalledApp { Id = "1", Name = "Foo", Version = "1.0", Source = InstalledAppSource.MachineRegistry32 };
        var b = new InstalledApp { Id = "2", Name = "foo", Version = "1.0", Source = InstalledAppSource.MachineRegistry };
        var c = new InstalledApp { Id = "3", Name = "Foo", Version = "2.0", Source = InstalledAppSource.UserRegistry };
        var result = InstalledAppRules.Deduplicate([a, b, c]).ToList();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == "2");
    }

    [Fact]
    public void ProtectsRootsAndWellKnownFolders()
    {
        string[] known = ["C:\\Users\\me", "C:\\Users\\me\\AppData\\Local"];
        Assert.True(ProtectedPaths.IsProtected("C:\\", known, "C:\\Windows"));
        Assert.True(ProtectedPaths.IsProtected("C:\\Users\\me\\", known, "C:\\Windows"));
        Assert.True(ProtectedPaths.IsProtected("C:\\Windows\\SoftwareDistribution\\Download", known, "C:\\Windows"));
        Assert.True(ProtectedPaths.IsProtected("", known, "C:\\Windows"));
        Assert.False(ProtectedPaths.IsProtected("C:\\Users\\me\\AppData\\Local\\Foo", known, "C:\\Windows"));
        Assert.False(ProtectedPaths.IsProtected("C:\\Windowsill\\x", known, "C:\\Windows"));
    }

    [Theory]
    [InlineData(0, "Zero KB")]
    [InlineData(512, "512 bytes")]
    [InlineData(1_500, "2 KB")]
    [InlineData(1_400_000_000, "1.4 GB")]
    public void FormatsBytes(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Format(bytes).Replace(',', '.'));
    }

    [Fact]
    public void RendersThemedPreviewOffscreen()
    {
        var path = StaRunner.PreviewPath("common-preview.png");
        StaRunner.Run(() =>
        {
            var content = Ui.Stack(8, Ui.Title("Preview"), Ui.Caption("Rendered without an Application."),
                Ui.Button("Accent", () => { }, accent: true));
            PreviewRenderer.Render(new Border { Child = content, Padding = new System.Windows.Thickness(16) }, path, 320, 160);
        });
        Assert.True(new FileInfo(path).Length > 1000);
    }
}

public class CloudFilesTests
{
    [Theory]
    [InlineData(0x00400000, true)]  // OneDrive "online-only"
    [InlineData(0x00040000, true)]  // recall on open
    [InlineData(0x00001000, true)]  // offline
    [InlineData(0x00000020, false)] // archive (a normal file)
    [InlineData(0x00080000, false)] // pinned "always keep on this device"
    public void RecognisesCloudOnlyAttributes(int attributes, bool cloudOnly) =>
        Assert.Equal(cloudOnly, Fieldkit.Features.Apps.Common.CloudFiles.IsCloudOnly((System.IO.FileAttributes)attributes));
}
