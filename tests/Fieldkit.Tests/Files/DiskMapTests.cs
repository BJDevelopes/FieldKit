// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Files.DiskMap;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Files;

public class SunburstLayoutTests
{
    private static DiskNode Folder(string name, DiskNode? parent, params DiskNode[] children)
    {
        var node = new DiskNode(name, DiskNodeKind.Folder, parent);
        foreach (var child in children) node.Children.Add(child);
        node.Size = children.Sum(c => c.Size);
        node.FileCount = children.Sum(c => c.FileCount);
        return node;
    }

    private static DiskNode File(string name, long size) => new(name, DiskNodeKind.File, null) { Size = size, FileCount = 1 };

    [Fact]
    public void FirstRingSharesTheCircleInProportionLargestFirst()
    {
        var root = Folder("C:\\", null, File("small", 100), File("big", 300));
        var arcs = SunburstLayout.Layout(root);
        Assert.Equal(["big", "small"], arcs.Select(a => a.Node.Name));
        Assert.Equal(270, arcs[0].Sweep, 6);
        Assert.Equal(270, arcs[1].Start, 6);
        Assert.Equal(360, arcs.Where(a => a.Ring == 0).Sum(a => a.Sweep), 6);
    }

    [Fact]
    public void ChildrenSitInsideTheirParentsAngleAndKeepItsBranch()
    {
        var inner = Folder("Videos", null, File("a.mp4", 600), File("b.mp4", 200));
        var root = Folder("C:\\", null, inner, File("x", 200));
        var arcs = SunburstLayout.Layout(root);
        var children = arcs.Where(a => a.Ring == 1).ToList();
        Assert.Equal(2, children.Count);
        Assert.Equal(0, children[0].Start, 6);
        Assert.Equal(288 * 600 / 800.0, children[0].Sweep, 6);
        Assert.All(children, c => Assert.Equal(arcs.First(a => a.Node == inner).Branch, c.Branch));
    }

    [Fact]
    public void TinyWedgesAndDeepRingsAreLeftOut()
    {
        var deep = Folder("1", null, Folder("2", null, Folder("3", null, Folder("4", null, Folder("5", null, File("f", 1000))))));
        var root = Folder("C:\\", null, deep, File("crumb", 1));
        var arcs = SunburstLayout.Layout(root, rings: 4);
        Assert.Equal(4, arcs.Max(a => a.Ring) + 1);
        Assert.DoesNotContain(arcs, a => a.Node.Name == "crumb");
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(90, 200, 100)]
    [InlineData(180, 100, 200)]
    public void AnglesStartAtTheTopAndRunClockwise(double degrees, double x, double y)
    {
        var (px, py) = SunburstLayout.PointAt(100, 100, 100, degrees);
        Assert.Equal(x, px, 6);
        Assert.Equal(y, py, 6);
    }

    [Fact]
    public void SharesRoundAndFlagTinyOnes()
    {
        Assert.Equal("25%", SunburstLayout.Share(25, 100));
        Assert.Equal("<1%", SunburstLayout.Share(1, 1000));
        Assert.Equal(string.Empty, SunburstLayout.Share(1, 0));
    }
}

public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fieldkit-disk-" + Guid.NewGuid().ToString("N"));

    public DiskScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void LargeFilesKeepTheirOwnNodeAndSmallOnesAreSummed()
    {
        var folder = new DiskNode("x", DiskNodeKind.Folder, null);
        DiskScanner.AddFiles(folder, [("big.iso", 5_000_000), ("a.txt", 10), ("b.txt", 20), ("empty", 0)]);
        Assert.Equal(5_000_030, folder.Size);
        Assert.Equal(4, folder.FileCount);
        Assert.Contains(folder.Children, c => c.Name == "big.iso" && c.Kind == DiskNodeKind.File);
        var rest = Assert.Single(folder.Children, c => c.Kind == DiskNodeKind.OtherFiles);
        Assert.Equal(30, rest.Size);
        Assert.Equal(3, rest.FileCount);
    }

    [Fact]
    public void MeasuresARealFolderTree()
    {
        Write(@"Videos\film.bin", 2_000_000);
        Write(@"Videos\clips\one.bin", 1_500_000);
        Write(@"Docs\a.txt", 1_000);
        Write(@"Docs\b.txt", 2_000);
        var result = DiskScanner.Scan(_root, null, CancellationToken.None);
        var root = result.Root;
        Assert.Equal(3_503_000, root.Size);
        Assert.Equal(4, root.FileCount);
        var videos = root.Children.Single(c => c.Name == "Videos");
        Assert.Equal(3_500_000, videos.Size);
        Assert.Equal(Path.Combine(_root, "Videos", "clips", "one.bin"),
            videos.Children.Single(c => c.Name == "clips").Children.Single().Path);
        Assert.Equal(0, result.Unreadable);
    }

    [Fact]
    public void RemovingAnItemShrinksEveryFolderAbove()
    {
        Write(@"Videos\clips\one.bin", 1_500_000);
        Write(@"Videos\two.bin", 1_200_000);
        var root = DiskScanner.Scan(_root, null, CancellationToken.None).Root;
        var clips = root.Children.Single().Children.Single(c => c.Name == "clips");
        var one = clips.Children.Single();
        clips.Remove(one);
        Assert.Equal(0, clips.Size);
        Assert.Equal(1_200_000, root.Size);
        Assert.Equal(1, root.FileCount);
        Assert.Null(one.Parent);
    }

    [Fact]
    public void RendersThePreview()
    {
        var path = StaRunner.PreviewPath("diskusage.png");
        var root = new DiskNode("C:\\", DiskNodeKind.Folder, null);
        DiskNode Folder(DiskNode parent, string name, params (string Name, long Size)[] files)
        {
            var f = new DiskNode(name, DiskNodeKind.Folder, parent);
            DiskScanner.AddFiles(f, files);
            parent.Children.Add(f);
            return f;
        }
        void Total(DiskNode n)
        {
            foreach (var c in n.Children.Where(c => c.IsFolder)) Total(c);
            n.Size = n.Children.Sum(c => c.Size);
            n.FileCount = n.Children.Sum(c => c.FileCount);
        }
        var users = Folder(root, "Users");
        var me = Folder(users, "me");
        Folder(me, "Videos", ("Holiday 2025.mp4", 14_000_000_000), ("Wedding.mov", 9_000_000_000), ("clip.mp4", 400_000_000));
        Folder(me, "Downloads", ("installer.exe", 3_200_000_000), ("ubuntu.iso", 6_100_000_000), ("notes.txt", 20_000));
        Folder(me, "Pictures", ("IMG_2041.jpg", 4_000_000), ("IMG_2042.jpg", 4_100_000), ("tiny.png", 90_000));
        Folder(root, "Windows", ("memory.dmp", 2_000_000_000), ("small.log", 900_000));
        Folder(root, "Program Files", ("game.pak", 31_000_000_000), ("app.dll", 300_000_000));
        Folder(root, "ProgramData", ("cache.db", 4_500_000_000));
        Total(root);
        StaRunner.Run(() => PreviewRenderer.Render(DiskMapWindow.BuildPreview(new DiskScanResult(root, 3, 512_000_000_000, 140_000_000_000)), path, 980, 700));
        Assert.True(new FileInfo(path).Length > 20_000);
    }
}
