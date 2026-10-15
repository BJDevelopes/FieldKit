// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

public class ShelfPreviewTests
{
    private static void RunSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void RendersTheShelfWithSamplesAndEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldkit-previews");
        RunSta(() =>
        {
            ShelfWindow.RenderPreview(Path.Combine(dir, "shelf.png"));
            ShelfWindow.RenderEmptyPreview(Path.Combine(dir, "shelf-empty.png"));
            ShelfWindow.RenderPreview(Path.Combine(dir, "shelf-light.png"), light: true);
            ShelfWindow.RenderEmptyPreview(Path.Combine(dir, "shelf-empty-light.png"), light: true);
        });
        foreach (var name in new[] { "shelf.png", "shelf-empty.png", "shelf-light.png", "shelf-empty-light.png" })
            Assert.True(new FileInfo(Path.Combine(dir, name)).Length > 1000, name);
    }

    [Fact]
    public void DragOutDataCarriesFilesTextAndLinks()
    {
        RunSta(() =>
        {
            var file = Path.Combine(Path.GetTempPath(), "fieldkit-shelf-drag.txt");
            File.WriteAllText(file, "x");
            ShelfItem[] items =
            [
                new() { Kind = ShelfItemKind.File, Path = file, Title = "f" },
                new() { Kind = ShelfItemKind.Text, Text = "note", Title = "note" },
            ];
            var data = ShelfWindow.BuildDataObject(items);
            Assert.Equal(new[] { file }, (string[])data.GetData(DataFormats.FileDrop));
            Assert.Equal("note", data.GetData(DataFormats.UnicodeText));

            var link = ShelfWindow.BuildDataObject([new ShelfItem { Kind = ShelfItemKind.Link, Text = "https://a.b", Title = "a.b" }]);
            Assert.False(link.GetDataPresent(DataFormats.FileDrop));
            Assert.Equal("https://a.b", link.GetData(DataFormats.UnicodeText));
            Assert.True(link.GetDataPresent("UniformResourceLocatorW"));
        });
    }
}
