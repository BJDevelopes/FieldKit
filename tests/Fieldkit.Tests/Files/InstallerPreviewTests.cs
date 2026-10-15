// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.ExceptionServices;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

/// <summary>Renders the installer helper window to %TEMP%\fieldkit-previews for visual review.</summary>
public class InstallerPreviewTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "fieldkit-previews");

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void RendersWindowPreview()
    {
        var path = Path.Combine(Folder, "installer.png");
        var light = Path.Combine(Folder, "installer-light.png");
        var outcome = Path.Combine(Folder, "installer-outcome.png");
        OnSta(() =>
        {
            InstallerWindow.RenderPreview(path);
            InstallerWindow.RenderPreview(light, light: true);
            InstallerWindow.RenderOutcomePreview(outcome, new InstallerResult(InstallerOutcome.InstalledKeepingDownload,
                InstallerSourceKind.DiskImage, InstallerMethod.Installer, "VeraCrypt_Setup_x64_1.26"));
        });
        Assert.True(new FileInfo(path).Length > 0);
        Assert.True(new FileInfo(light).Length > 0);
        Assert.True(new FileInfo(outcome).Length > 0);
    }
}
