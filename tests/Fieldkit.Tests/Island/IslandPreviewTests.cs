// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.ExceptionServices;
using Fieldkit.Features.Island;

namespace Fieldkit.Tests.Island;

public class IslandPreviewTests
{
    /// <summary>Folder the previews are written to (kept for visual review).</summary>
    public static string Output => Path.Combine(Path.GetTempPath(), "fieldkit-island-preview");

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    [Fact]
    public void RendersEveryStateWithSampleData()
    {
        List<string> files = [];
        OnSta(() => files = IslandPreview.RenderAll(Output));
        Assert.Equal(Enum.GetValues<IslandPreviewState>().Length, files.Count);
        foreach (var f in files)
        {
            Assert.True(File.Exists(f), f);
            Assert.True(new FileInfo(f).Length > 1000, f);
        }
    }
}
