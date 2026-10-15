// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>Which kind of location a leftover was found in (decides its review section).</summary>
public enum ScanRootKind { AppData, Programs, Temp, CrashDumps, Packages, StartMenu, Desktop, InstallFolder, Registry }

/// <summary>Read-only registry access, so scans can be tested against a fake tree.</summary>
public interface IRegistryBrowser
{
    /// <summary>Subkey names of a full key path ("HKEY_CURRENT_USER\Software"); empty when missing or unreadable.</summary>
    IReadOnlyList<string> SubKeyNames(string keyPath);
    bool Exists(string keyPath);
}

/// <summary>The real registry (HKLM through the 64-bit view, so WOW6432Node paths stay explicit).</summary>
public sealed class WindowsRegistryBrowser : IRegistryBrowser
{
    public static WindowsRegistryBrowser Instance { get; } = new();

    public IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public bool Exists(string keyPath)
    {
        try
        {
            using var key = Open(keyPath);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a full key path read-only (or writable for deletion of a child).</summary>
    public static RegistryKey? Open(string keyPath, bool writable = false)
    {
        var (hive, sub) = Split(keyPath);
        if (hive is null) return null;
        var root = RegistryKey.OpenBaseKey(hive.Value, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
        if (sub.Length == 0) return root;
        try { return root.OpenSubKey(sub, writable); }
        finally { root.Dispose(); }
    }

    public static (RegistryHive? Hive, string SubKey) Split(string keyPath)
    {
        var cut = keyPath.IndexOf('\\');
        var head = (cut < 0 ? keyPath : keyPath[..cut]).ToUpperInvariant();
        var rest = cut < 0 ? string.Empty : keyPath[(cut + 1)..].Trim('\\');
        RegistryHive? hive = head switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            _ => null,
        };
        return (hive, rest);
    }
}

/// <summary>Every place a leftover scan looks, as plain paths so tests can point it at temp fixtures.</summary>
public sealed record ScanEnvironment
{
    public required string UserProfile { get; init; }
    public string RoamingAppData { get; init; } = string.Empty;
    public string LocalAppData { get; init; } = string.Empty;
    public string LocalLow { get; init; } = string.Empty;
    public string ProgramData { get; init; } = string.Empty;
    public string Temp { get; init; } = string.Empty;
    public string UserStartMenu { get; init; } = string.Empty;
    public string CommonStartMenu { get; init; } = string.Empty;
    public string UserDesktop { get; init; } = string.Empty;
    public string PublicDesktop { get; init; } = string.Empty;
    /// <summary>Folders whose children are install folders (never taken as vendor folders).</summary>
    public IReadOnlyList<string> ProgramFolders { get; init; } = [];
    /// <summary>Never touched, nor anything inside or containing them (Fieldkit's own data and program folder).</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public IReadOnlyList<string> RegistryRoots { get; init; } = [];
    public IRegistryBrowser Registry { get; init; } = WindowsRegistryBrowser.Instance;
    public Func<string, string?> ResolveShortcut { get; init; } = ShellLink.ResolveTarget;
    public bool IsAdmin { get; init; }

    public string Programs => Combine(LocalAppData, "Programs");
    public string CrashDumps => Combine(LocalAppData, "CrashDumps");
    public string Packages => Combine(LocalAppData, "Packages");

    private static string Combine(string root, string child) => string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, child);

    /// <summary>Anything outside the user profile (ProgramData, Program Files, all-users Start menu, Public desktop).</summary>
    public bool IsMachineWide(string path) => !ProtectedPaths.IsInside(path, UserProfile);

    /// <summary>Roots a file leftover must still be strictly inside when it is removed.</summary>
    public IEnumerable<string> FileRoots() =>
        new[] { RoamingAppData, LocalAppData, LocalLow, ProgramData, Temp, UserStartMenu, CommonStartMenu, UserDesktop, PublicDesktop }
            .Concat(ProgramFolders)
            .Where(r => !string.IsNullOrWhiteSpace(r));

    public static ScanEnvironment Current()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        return new ScanEnvironment
        {
            UserProfile = profile,
            RoamingAppData = F(Environment.SpecialFolder.ApplicationData),
            LocalAppData = local,
            LocalLow = Path.Combine(profile, "AppData", "LocalLow"),
            ProgramData = F(Environment.SpecialFolder.CommonApplicationData),
            Temp = Path.GetTempPath().TrimEnd('\\'),
            UserStartMenu = F(Environment.SpecialFolder.Programs),
            CommonStartMenu = F(Environment.SpecialFolder.CommonPrograms),
            UserDesktop = F(Environment.SpecialFolder.DesktopDirectory),
            PublicDesktop = F(Environment.SpecialFolder.CommonDesktopDirectory),
            ProgramFolders = new[] { F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs") }
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ExcludedPaths = new[] { AppInfo.DataDirectory, Path.GetDirectoryName(AppInfo.ExecutablePath) ?? string.Empty }
                .Where(p => !string.IsNullOrEmpty(p)).ToList(),
            RegistryRoots = [@"HKEY_CURRENT_USER\Software", @"HKEY_LOCAL_MACHINE\SOFTWARE", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node"],
            IsAdmin = Elevation.IsAdministrator,
        };
    }
}

/// <summary>
/// Finds what an app leaves behind: AppData (Roaming, Local, LocalLow), ProgramData, %TEMP%, crash dumps,
/// its Store package data, Start menu and Desktop shortcuts, the install folder (after its uninstaller ran)
/// and its HKCU/HKLM Software keys. Read-only: it never changes anything.
/// </summary>
public static class LeftoverScanner
{
    /// <param name="afterUninstall">True once the app's uninstaller ran: the install folder itself may then be a leftover.
    /// Before that, everything inside the install folder belongs to the uninstaller and is left out.</param>
    public static List<Leftover> Scan(AppIdentity id, OtherApps others, ScanEnvironment env, bool afterUninstall,
        CancellationToken ct = default)
    {
        var found = new List<Leftover>();

        bool Excluded(string path) =>
            env.ExcludedPaths.Any(e => ProtectedPaths.IsInside(path, e) || ProtectedPaths.IsInside(e, path));

        void AddFile(string path, ScanRootKind root, LeftoverMatch match, bool pathEvidence = false)
        {
            if (match == LeftoverMatch.None) return;
            if (Excluded(path) || ProtectedPaths.IsProtected(path)) return;
            if (!afterUninstall && id.InstallLocation.Length > 0 && root != ScanRootKind.StartMenu && root != ScanRootKind.Desktop
                && ProtectedPaths.IsInside(path, id.InstallLocation)) return;
            if (others.ClaimsPath(path)) return;
            if (!pathEvidence && (others.ClaimsName(Path.GetFileName(path)) || others.SharesPrefix(Path.GetFileName(path), id))) return;
            bool isDir;
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return; // never follow junctions/symlinks
                isDir = attributes.HasFlag(FileAttributes.Directory);
            }
            catch
            {
                return;
            }
            var kind = isDir ? LeftoverKind.Folder : LeftoverKind.File;
            var machine = env.IsMachineWide(path);
            found.Add(new Leftover
            {
                Path = Path.GetFullPath(path).TrimEnd('\\'),
                Kind = kind,
                Category = LeftoverRules.CategoryFor(kind, path, root),
                Confidence = match,
                RequiresAdmin = machine,
                Include = LeftoverRules.DefaultInclude(match, machine, kind, env.IsAdmin),
            });
        }

        void ScanFolder(string root, ScanRootKind kind, bool exactOnly = false)
        {
            foreach (var entry in Entries(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var match = Filter(LeftoverRules.Match(name, id), exactOnly);
                // A publisher folder ("Spotify AB", "VideoLAN") is searched one level deeper and only claimed
                // whole when it is exactly the app's own name too.
                if (match != LeftoverMatch.Exact && Directory.Exists(entry) && LeftoverRules.IsVendor(name, id))
                {
                    foreach (var child in Entries(entry))
                        AddFile(child, kind, Filter(LeftoverRules.Match(Path.GetFileName(child), id, insideVendor: true), exactOnly));
                    continue;
                }
                AddFile(entry, kind, match);
            }
        }

        // Install folder (once the uninstaller is done) and per-user program folders.
        if (afterUninstall && id.InstallLocation.Length > 0 && Directory.Exists(id.InstallLocation))
            AddFile(id.InstallLocation, ScanRootKind.InstallFolder, LeftoverMatch.Exact, pathEvidence: true);

        ScanFolder(env.RoamingAppData, ScanRootKind.AppData);
        ScanFolder(env.LocalAppData, ScanRootKind.AppData);
        ScanFolder(env.LocalLow, ScanRootKind.AppData);
        ScanFolder(env.ProgramData, ScanRootKind.AppData);
        ScanFolder(env.Programs, ScanRootKind.Programs);
        ScanFolder(env.Temp, ScanRootKind.Temp, exactOnly: true);

        foreach (var dump in Entries(env.CrashDumps))
        {
            ct.ThrowIfCancellationRequested();
            var exe = LeftoverRules.CrashDumpExecutable(Path.GetFileName(dump));
            if (exe is null) continue;
            if (id.ExeNames.Contains(exe)) AddFile(dump, ScanRootKind.CrashDumps, LeftoverMatch.Exact, pathEvidence: true);
            else if (id.Tokens.Contains(exe)) AddFile(dump, ScanRootKind.CrashDumps, LeftoverMatch.Related);
        }

        if (!string.IsNullOrEmpty(id.PackageFamilyName) && !string.IsNullOrEmpty(env.Packages))
        {
            var package = Path.Combine(env.Packages, id.PackageFamilyName);
            if (Directory.Exists(package)) AddFile(package, ScanRootKind.Packages, LeftoverMatch.Exact, pathEvidence: true);
        }

        ScanStartMenu(env.UserStartMenu);
        ScanStartMenu(env.CommonStartMenu);
        foreach (var desktop in new[] { env.UserDesktop, env.PublicDesktop })
        {
            foreach (var entry in Entries(desktop))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) CheckShortcut(entry, ScanRootKind.Desktop, nameOnlyAllowed: false);
            }
        }

        ScanRegistry();

        var result = LeftoverRules.Dedupe(found);
        foreach (var item in result)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind is LeftoverKind.File or LeftoverKind.Folder) item.Size = DiskUsage.SizeOf(item.Path, ct);
        }
        return result.OrderBy(i => i.Category).ThenByDescending(i => i.Size).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();

        // ------------------------------------------------------------ local helpers

        void ScanStartMenu(string programs)
        {
            foreach (var entry in Entries(programs))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    var match = LeftoverRules.Match(name, id);
                    var links = Entries(entry).Where(e => e.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
                    var allOurs = id.InstallLocation.Length > 0 && links.Count > 0 && links.All(l =>
                        env.ResolveShortcut(l) is { } t && ProtectedPaths.IsInside(t, id.InstallLocation));
                    if (allOurs) AddFile(entry, ScanRootKind.StartMenu, LeftoverMatch.Exact, pathEvidence: true);
                    else if (match != LeftoverMatch.None && !links.Any(l => PointsToOtherApp(l))) AddFile(entry, ScanRootKind.StartMenu, match);
                    else foreach (var link in links) CheckShortcut(link, ScanRootKind.StartMenu, nameOnlyAllowed: true);
                }
                else if (entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    CheckShortcut(entry, ScanRootKind.StartMenu, nameOnlyAllowed: true);
                }
            }
        }

        bool PointsToOtherApp(string link) =>
            env.ResolveShortcut(link) is { } target && others.ClaimsPath(target)
            && !(id.InstallLocation.Length > 0 && ProtectedPaths.IsInside(target, id.InstallLocation));

        void CheckShortcut(string link, ScanRootKind root, bool nameOnlyAllowed)
        {
            var target = env.ResolveShortcut(link);
            if (target is not null && id.InstallLocation.Length > 0 && ProtectedPaths.IsInside(target, id.InstallLocation))
            {
                AddFile(link, root, LeftoverMatch.Exact, pathEvidence: true);
                return;
            }
            if (target is not null && others.ClaimsPath(target)) return;
            var targetExists = target is not null && (File.Exists(target) || Directory.Exists(target));
            var exe = target is null ? null : LeftoverRules.Normalize(Path.GetFileNameWithoutExtension(target));
            if (!targetExists && exe is not null && id.ExeNames.Contains(exe))
            {
                // A dangling shortcut to the app's own executable.
                AddFile(link, root, LeftoverMatch.Exact, pathEvidence: true);
                return;
            }
            var match = LeftoverRules.Match(Path.GetFileName(link), id);
            if (match == LeftoverMatch.None) return;
            if (!targetExists && match == LeftoverMatch.Exact && nameOnlyAllowed) AddFile(link, root, LeftoverMatch.Exact);
            else if (!targetExists || nameOnlyAllowed) AddFile(link, root, LeftoverMatch.Related);
        }

        void ScanRegistry()
        {
            foreach (var root in env.RegistryRoots)
            {
                foreach (var name in env.Registry.SubKeyNames(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (LeftoverRules.ProtectedRegistryNames.Contains(name)) continue;
                    var match = LeftoverRules.Match(name, id);
                    if (match != LeftoverMatch.Exact && LeftoverRules.IsVendor(name, id))
                    {
                        foreach (var child in env.Registry.SubKeyNames($@"{root}\{name}"))
                            AddKey($@"{root}\{name}\{child}", child, LeftoverRules.Match(child, id, insideVendor: true));
                        continue;
                    }
                    AddKey($@"{root}\{name}", name, match);
                }
            }
        }

        void AddKey(string path, string leaf, LeftoverMatch match)
        {
            if (match == LeftoverMatch.None || !LeftoverRules.IsDeletableRegistryKey(path)) return;
            if (others.ClaimsName(leaf) || others.SharesPrefix(leaf, id)) return;
            var machine = LeftoverRules.IsMachineRegistryKey(path);
            found.Add(new Leftover
            {
                Path = path,
                Kind = LeftoverKind.RegistryKey,
                Category = LeftoverCategory.Preferences,
                Confidence = match,
                RequiresAdmin = machine,
                Include = LeftoverRules.DefaultInclude(match, machine, LeftoverKind.RegistryKey, env.IsAdmin),
            });
        }
    }

    private static LeftoverMatch Filter(LeftoverMatch match, bool exactOnly) =>
        exactOnly && match != LeftoverMatch.Exact ? LeftoverMatch.None : match;

    /// <summary>Visible entries of a folder (no hidden dot-names); empty when missing or unreadable.</summary>
    private static List<string> Entries(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(e => !Path.GetFileName(e).StartsWith('.'))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
