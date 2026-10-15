// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Fieldkit.Features.Files;

public enum InstallerKind { Exe, Msi }

/// <summary>What <see cref="InstallerLocator"/> found inside a mounted image or extracted archive.</summary>
public abstract record InstallerLocation;

/// <summary>An installer to run. <paramref name="RelativePath"/> is relative to the image / archive root.</summary>
public sealed record InstallerFound(string RelativePath, InstallerKind Kind) : InstallerLocation;

/// <summary>
/// A ready-to-run app (ZIP only): <paramref name="Folder"/> is the folder to copy ("" = the archive root),
/// <paramref name="MainExe"/> its main executable; both relative to the archive root.
/// </summary>
public sealed record PortableAppFound(string Folder, string MainExe) : InstallerLocation;

public sealed record NoInstallerFound(string Reason) : InstallerLocation;

/// <summary>
/// Finds the installer in a mounted disk image or extracted ZIP. Looks at the root and one level down:
/// the <c>autorun.inf</c> target, then <c>setup.exe</c> / <c>install.exe</c> / <c>installer.exe</c>, then a
/// single <c>.msi</c>, then a single <c>.exe</c> named like an installer, then (disk images) the only
/// <c>.exe</c> in the root. A ZIP without an installer may hold a portable app instead.
/// Pure: works on relative paths so it is testable without disks.
/// </summary>
public static class InstallerLocator
{
    private static readonly string[] PreferredNames = ["setup.exe", "install.exe", "installer.exe"];
    private static readonly string[] HelperExeHints = ["unins", "uninst", "update", "crash", "report", "helper", "elevate", "vc_redist", "vcredist", "dotnet", "directx", "dxsetup"];

    /// <param name="files">Every file at the root and one level down, relative to the root ('\' or '/').</param>
    /// <param name="autorunContent">Text of the root <c>autorun.inf</c>, if there is one.</param>
    /// <param name="isArchive">True for an extracted ZIP (enables portable apps, disables the "only root .exe" rule).</param>
    /// <param name="prefer64Bit">Breaks ties between per-architecture folders (x64 vs x86).</param>
    public static InstallerLocation Locate(IEnumerable<string> files, string? autorunContent, bool isArchive, bool prefer64Bit = true)
    {
        var all = files.Select(Normalize).Where(f => f.Length > 0 && Depth(f) <= 1)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0) return new NoInstallerFound(isArchive ? "The archive is empty." : "The disk image is empty.");
        var byName = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);

        // 1. autorun.inf
        if (autorunContent is not null && ParseAutorunTarget(autorunContent) is { } target)
        {
            var normalized = Normalize(target);
            var match = all.FirstOrDefault(f => string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null && KindOf(match) is { } k) return new InstallerFound(match, k);
        }

        var root = all.Where(f => Depth(f) == 0).ToList();
        var nested = all.Where(f => Depth(f) == 1).ToList();

        InstallerLocation? Pick(List<string> level, Func<string, bool> filter, string what)
        {
            var hits = level.Where(filter).ToList();
            if (hits.Count == 0) return null;
            if (hits.Count == 1) return new InstallerFound(hits[0], KindOf(hits[0])!.Value);
            var arch = hits.Where(h => IsArchFolder(h, prefer64Bit)).ToList();
            if (arch.Count == 1) return new InstallerFound(arch[0], KindOf(arch[0])!.Value);
            return new NoInstallerFound($"It holds several {what} ({string.Join(", ", hits.Take(3).Select(Path.GetFileName))}" +
                                        $"{(hits.Count > 3 ? ", …" : string.Empty)}), so open the right one from File Explorer.");
        }

        // 2. well-known names, in name order, root before one level down.
        foreach (var name in PreferredNames)
        {
            bool Named(string f) => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase);
            var found = Pick(root, Named, "installers") ?? Pick(nested, Named, "installers");
            if (found is not null) return found;
        }

        // 3. a single .msi.
        bool IsMsi(string f) => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        if ((Pick(root, IsMsi, "installer packages") ?? Pick(nested, IsMsi, "installer packages")) is { } msi) return msi;

        // 4. a single .exe named like an installer.
        if ((Pick(root, LooksLikeInstallerExe, "installers") ?? Pick(nested, LooksLikeInstallerExe, "installers")) is { } named) return named;

        if (!isArchive)
        {
            // 5. the only .exe in the root.
            var rootExes = root.Where(IsExe).ToList();
            if (rootExes.Count == 1) return new InstallerFound(rootExes[0], InstallerKind.Exe);
            return new NoInstallerFound(rootExes.Count == 0
                ? "The disk image has no installer (no setup program or .msi package)."
                : "The disk image holds several programs and none is clearly the installer.");
        }

        return (InstallerLocation?)LocatePortable(root, nested) ?? new NoInstallerFound("The archive has no installer and no single app to copy.");
    }

    /// <summary>
    /// A ZIP holding an app that runs in place: the archive root, or its only top-level folder when the root has
    /// no files of its own, with one obvious .exe (the only one that is not a helper, or one named like the folder).
    /// </summary>
    private static PortableAppFound? LocatePortable(List<string> root, List<string> nested)
    {
        var topFolders = nested.Select(f => f[..f.IndexOf('\\')]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string folder;
        List<string> exes;
        string folderName;
        if (root.Count == 0 && topFolders.Count == 1)
        {
            folder = topFolders[0];
            folderName = folder;
            exes = nested.Where(IsExe).ToList();
        }
        else
        {
            folder = string.Empty;
            folderName = string.Empty;
            exes = root.Where(IsExe).ToList();
        }
        if (exes.Count == 0) return null;
        if (exes.Count == 1) return new PortableAppFound(folder, exes[0]);
        var main = exes.Where(e => !IsHelperExe(e)).ToList();
        if (main.Count == 1) return new PortableAppFound(folder, main[0]);
        if (folderName.Length > 0)
        {
            var named = exes.Where(e => folderName.StartsWith(Path.GetFileNameWithoutExtension(e), StringComparison.OrdinalIgnoreCase)
                                        && Path.GetFileNameWithoutExtension(e).Length >= 3).ToList();
            if (named.Count == 1) return new PortableAppFound(folder, named[0]);
        }
        return null;
    }

    /// <summary>The program an <c>autorun.inf</c> starts (<c>shellexecute=</c> first, then <c>open=</c>), without arguments.</summary>
    public static string? ParseAutorunTarget(string content)
    {
        string? open = null, shellExecute = null;
        var inAutorun = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                inAutorun = line.StartsWith("[autorun]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inAutorun) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            if (key.Equals("open", StringComparison.OrdinalIgnoreCase)) open ??= value;
            else if (key.Equals("shellexecute", StringComparison.OrdinalIgnoreCase)) shellExecute ??= value;
        }
        var command = shellExecute ?? open;
        if (command is null) return null;
        string program;
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            program = close > 0 ? command[1..close] : command[1..];
        }
        else
        {
            // Unquoted: everything up to the first ".exe"/".msi" (paths with spaces), else the first word.
            var ext = new[] { ".exe", ".msi" }
                .Select(e => command.IndexOf(e, StringComparison.OrdinalIgnoreCase))
                .Where(i => i > 0).DefaultIfEmpty(-1).Min();
            program = ext > 0 ? command[..(ext + 4)] : command.Split(' ', 2)[0];
        }
        program = Normalize(program);
        return program.Length == 0 ? null : program;
    }

    public static InstallerKind? KindOf(string path) =>
        path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Msi
        : path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Exe
        : null;

    /// <summary>Lists the root and one level down of <paramref name="root"/> on disk and runs <see cref="Locate"/>.</summary>
    public static InstallerLocation LocateIn(string root, bool isArchive, int maxFiles = 5000)
    {
        var files = new List<string>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
            foreach (var f in Directory.EnumerateFiles(root, "*", options))
            {
                files.Add(Path.GetFileName(f));
                if (files.Count >= maxFiles) break;
            }
            foreach (var dir in Directory.EnumerateDirectories(root, "*", options))
            {
                if (files.Count >= maxFiles) break;
                var name = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                {
                    files.Add(name + "\\" + Path.GetFileName(f));
                    if (files.Count >= maxFiles) break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NoInstallerFound("Its files could not be read.");
        }
        string? autorun = null;
        var autorunPath = Path.Combine(root, "autorun.inf");
        try
        {
            if (File.Exists(autorunPath) && new FileInfo(autorunPath).Length < 64 * 1024) autorun = File.ReadAllText(autorunPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore an unreadable autorun.inf
        }
        return Locate(files, autorun, isArchive, Environment.Is64BitOperatingSystem);
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        while (p.StartsWith(".\\", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('\\');
    }

    private static int Depth(string path) => path.Count(c => c == '\\');

    private static bool IsExe(string f) => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeInstallerExe(string f)
    {
        if (!IsExe(f)) return false;
        var name = Path.GetFileNameWithoutExtension(f);
        if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("install", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelperExe(string f)
    {
        var name = Path.GetFileNameWithoutExtension(f);
        return HelperExeHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArchFolder(string path, bool prefer64Bit)
    {
        var slash = path.IndexOf('\\');
        if (slash < 0) return false;
        var folder = path[..slash].ToLowerInvariant();
        string[] x64 = ["x64", "amd64", "win64", "64bit", "64-bit"];
        string[] x86 = ["x86", "i386", "win32", "32bit", "32-bit"];
        return (prefer64Bit ? x64 : x86).Any(a => folder == a || folder.EndsWith(a, StringComparison.Ordinal) || folder.StartsWith(a, StringComparison.Ordinal));
    }
}
