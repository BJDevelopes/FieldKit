// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Launcher.CommandBar;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarAppsTests
{
    [Theory]
    [InlineData("Notepad", "C:\\Windows\\notepad.exe", true)]
    [InlineData("Microsoft.WindowsCalculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("Uninstall Foo", "C:\\Foo\\unins000.exe", false)]
    [InlineData("Foo Help", "C:\\Foo\\help.chm", false)]
    [InlineData("Read me", "C:\\Foo\\readme.txt", false)]
    [InlineData("Website", "https://example.com", false)]
    [InlineData("", "C:\\x.exe", false)]
    public void KeepsOnlyApps(string name, string id, bool expected) => Assert.Equal(expected, CommandBarApps.IsApp(name, id));

    [Fact]
    public void ReadsTheInstalledAppsReadOnly()
    {
        List<CommandBarApp> apps = [];
        StaRunner.Run(() => apps = CommandBarApps.Load());
        Assert.NotEmpty(apps);
        Assert.DoesNotContain(apps, a => a.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(apps.Count, apps.Select(a => a.Name + "|" + a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
