// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Cleaner;

namespace Fieldkit.Tests.Apps.Cleaner;

/// <summary>A throwaway folder tree under %TEMP% shaped like a user profile. Deleted on dispose.</summary>
public sealed class CleanerFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fieldkit-cleaner-tests", Guid.NewGuid().ToString("N"));
    public CleanerPaths Paths { get; }

    public CleanerFixture()
    {
        var profile = Path.Combine(Root, "alex");
        Paths = new CleanerPaths(
            Path.Combine(profile, "AppData", "Local"),
            Path.Combine(profile, "AppData", "Roaming"),
            Path.Combine(profile, "AppData", "LocalLow"),
            Path.Combine(profile, "AppData", "Local", "Temp"),
            profile,
            Path.Combine(profile, "Pictures", "Screenshots"),
            Path.Combine(Root, "Windows"));
        foreach (var d in new[] { Paths.LocalAppData, Paths.RoamingAppData, Paths.LocalLow, Paths.Temp, Paths.Screenshots })
            Directory.CreateDirectory(d);
    }

    /// <summary>Creates a file of <paramref name="bytes"/> bytes, optionally dated in the past
    /// (its write time and every parent folder up to the root of the tree).</summary>
    public string File(string path, int bytes = 100, int? ageDays = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (ageDays is { } days)
        {
            var when = DateTime.UtcNow.AddDays(-days);
            System.IO.File.SetLastWriteTimeUtc(path, when);
            System.IO.File.SetCreationTimeUtc(path, when);
        }
        return path;
    }

    /// <summary>Back-dates a folder and everything in it (folders get modified when files are created).</summary>
    public void Age(string dir, int days)
    {
        var when = DateTime.UtcNow.AddDays(-days);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) System.IO.File.SetLastWriteTimeUtc(f, when);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Reverse()) Directory.SetLastWriteTimeUtc(d, when);
        Directory.SetLastWriteTimeUtc(dir, when);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { }
    }
}

/// <summary>Records what the clean pass asked for instead of touching the Recycle Bin or registry.</summary>
public sealed class FakeDeleter : ICleanerDeleter
{
    public List<string> Recycled { get; } = [];
    public List<string> Calls { get; } = [];
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RemovedRunValues { get; } = [];

    public bool Recycle(string path, long size, bool allowProtected)
    {
        Recycled.Add(path);
        Calls.Add("recycle:" + path);
        return true;
    }

    public bool RemoveRunValue(string name, string expectedCommand)
    {
        RemovedRunValues.Add(name);
        Calls.Add("run:" + name);
        return true;
    }

    public bool EmptyRecycleBin()
    {
        Calls.Add("empty");
        return true;
    }

    public bool IsAnyRunning(IReadOnlyCollection<string> processNames) => processNames.Any(Running.Contains);
}
