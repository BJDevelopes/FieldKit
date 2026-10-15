// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.IO.Compression;
using Fieldkit.Features.Files;

namespace Fieldkit.Tests.Files;

/// <summary>Extraction, copying and clean-up against temp folders this test creates (never Downloads).</summary>
public sealed class InstallerExtractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fieldkit-tests", "installer-" + Guid.NewGuid().ToString("N"));

    public InstallerExtractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void ExtractsNormalArchive()
    {
        var zip = MakeZip(("App/", ""), ("App/app.exe", "MZ"), ("App/docs/readme.txt", "hello"));
        var dest = Path.Combine(_root, "out");
        Assert.Null(InstallerRunner.ExtractZip(zip, dest));
        Assert.Equal("MZ", File.ReadAllText(Path.Combine(dest, "App", "app.exe")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "App", "docs", "readme.txt")));
        var found = Assert.IsType<PortableAppFound>(InstallerLocator.LocateIn(dest, isArchive: true));
        Assert.Equal(@"App\app.exe", found.MainExe);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("C:/escape.txt")]
    public void RefusesZipSlip(string evil)
    {
        var zip = MakeZip(("ok.txt", "fine"), (evil, "bad"));
        var dest = Path.Combine(_root, "out");
        Assert.Contains("unsafe", InstallerRunner.ExtractZip(zip, dest));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public void RefusesOversizedArchive()
    {
        var zip = MakeZip(("big.bin", new string('x', 5000)));
        var dest = Path.Combine(_root, "out");
        Assert.Contains("more than", InstallerRunner.ExtractZip(zip, dest, maxTotalBytes: 1000));
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
    }

    [Fact]
    public void RefusesTooManyEntries()
    {
        var zip = MakeZip(("a", "1"), ("b", "2"), ("c", "3"));
        Assert.Contains("files", InstallerRunner.ExtractZip(zip, Path.Combine(_root, "out"), maxEntries: 2));
    }

    [Fact]
    public void ReportsDamagedArchive()
    {
        var notZip = Path.Combine(_root, "fake.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.Contains("damaged", InstallerRunner.ExtractZip(notZip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void CopiesPortableAppAndRefusesNothingElse()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "lang"));
        File.WriteAllText(Path.Combine(source, "app.exe"), "MZ");
        File.WriteAllText(Path.Combine(source, "lang", "de.txt"), "de");
        var dest = Path.Combine(_root, "Programs", "App");

        Assert.Null(InstallerRunner.CopyPortable(source, dest));
        Assert.Equal("MZ", File.ReadAllText(Path.Combine(dest, "app.exe")));
        Assert.Equal("de", File.ReadAllText(Path.Combine(dest, "lang", "de.txt")));
        // No staging folder is left behind.
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Programs")));
        // The source is untouched.
        Assert.True(File.Exists(Path.Combine(source, "app.exe")));
    }

    [Fact]
    public void DeletesOnlyOwnedFolders()
    {
        var owner = Path.Combine(_root, "owner");
        var inside = Path.Combine(owner, "abc");
        var outside = Path.Combine(_root, "keep");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(inside, "f.txt"), "x");
        File.SetAttributes(Path.Combine(inside, "f.txt"), FileAttributes.ReadOnly);

        Assert.False(InstallerRunner.DeleteOwnedFolder(outside, owner));
        Assert.True(Directory.Exists(outside));
        Assert.False(InstallerRunner.DeleteOwnedFolder(owner, owner));
        Assert.True(InstallerRunner.DeleteOwnedFolder(inside, owner));
        Assert.False(Directory.Exists(inside));
        Assert.False(InstallerRunner.DeleteTemp(outside));
        Assert.True(Directory.Exists(outside));
    }
}
