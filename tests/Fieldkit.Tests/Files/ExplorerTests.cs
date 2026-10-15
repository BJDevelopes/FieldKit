// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public class ExplorerTests
{
    [Fact]
    public void PastedImagesGetSortableNames() =>
        Assert.Equal("Pasted image 2026-10-14 093005.png", PastedImageNaming.FileName(new DateTime(2026, 10, 14, 9, 30, 5)));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void OnlyAPictureWithoutFilesIsSaved(bool image, bool files, bool expected) =>
        Assert.Equal(expected, PastedImageNaming.ShouldSaveImage(image, files));

    [Theory]
    [InlineData("DirectUIHWND", true)]
    [InlineData("SysListView32", true)]
    [InlineData("Edit", false)]
    [InlineData("", false)]
    public void OnlyFileViewsSaveImages(string focusClass, bool expected) =>
        Assert.Equal(expected, PastedImageNaming.IsFileViewFocus(focusClass));

    [Fact]
    public void UniquePathNeverOverwrites()
    {
        var dir = Directory.CreateTempSubdirectory("fieldkit-explorer-").FullName;
        try
        {
            Assert.Equal(Path.Combine(dir, "a.png"), ExplorerShell.UniquePath(dir, "a.png"));
            File.WriteAllText(Path.Combine(dir, "a.png"), "x");
            File.WriteAllText(Path.Combine(dir, "a 2.png"), "x");
            Assert.Equal(Path.Combine(dir, "a 3.png"), ExplorerShell.UniquePath(dir, "a.png"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
