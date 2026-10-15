// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>Review sections, in display order (upstream's Application / Support / Caches / Preferences / Containers / Logs / Other,
/// plus Shortcuts for the Start menu and Desktop).</summary>
public enum LeftoverCategory { App, Support, Caches, Preferences, Containers, Logs, Shortcuts, Other }

public enum LeftoverKind
{
    /// <summary>The app's registered uninstaller (or Store package removal): runs, never recycled.</summary>
    Uninstaller,
    Folder,
    File,
    /// <summary>A registry key (full path such as HKEY_CURRENT_USER\Software\Foo). Exported to .reg before deletion.</summary>
    RegistryKey,
}

/// <summary>How sure the scan is that an entry belongs to the app (upstream's LeftoverMatch).</summary>
public enum LeftoverMatch
{
    None,
    /// <summary>Path-based or exact-name evidence. Starts checked.</summary>
    Exact,
    /// <summary>A normalized name, executable name or prefix. Starts unchecked ("Optional, review before").</summary>
    Related,
}

/// <summary>One thing the uninstaller found and may remove.</summary>
public sealed class Leftover
{
    public required string Path { get; init; }
    public required LeftoverKind Kind { get; init; }
    public required LeftoverCategory Category { get; init; }
    public LeftoverMatch Confidence { get; init; } = LeftoverMatch.Exact;
    public long Size { get; set; }
    /// <summary>Machine-wide location (ProgramData, Program Files, all-users Start menu, HKLM).</summary>
    public bool RequiresAdmin { get; init; }
    public bool Include { get; set; }
    /// <summary>Overrides the leaf name (the uninstaller row shows the app's name).</summary>
    public string? DisplayName { get; init; }
    /// <summary>Overrides the location line (the uninstaller row shows its command).</summary>
    public string? Detail { get; init; }

    /// <summary>Leaf name shown in the review list.</summary>
    public string Name => DisplayName ?? (Kind == LeftoverKind.RegistryKey
        ? Path[(Path.LastIndexOf('\\') + 1)..]
        : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : Path);

    /// <summary>Parent folder / key shown under the name.</summary>
    public string Location
    {
        get
        {
            if (Detail is not null) return Detail;
            var trimmed = Path.TrimEnd('\\');
            var cut = trimmed.LastIndexOf('\\');
            return cut > 0 ? trimmed[..cut] : trimmed;
        }
    }
}

/// <summary>
/// Tokens that identify one selected app. Exact names are compared raw (case-insensitive) after stripping
/// a known file suffix; tokens are lower-case letters and digits only and only ever produce a
/// <see cref="LeftoverMatch.Related"/> (unchecked) match. Nothing here is ever turned into a path.
/// </summary>
public sealed class AppIdentity
{
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Raw names that count as an exact match anywhere (display name, without version, install folder name).</summary>
    public HashSet<string> ExactNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Raw names that count as exact only inside a vendor folder ("Mozilla\Firefox").</summary>
    public HashSet<string> VendorScopedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Publisher folder names (never claimed themselves, only their matching children).</summary>
    public HashSet<string> VendorNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Normalized name tokens (Related matches).</summary>
    public HashSet<string> Tokens { get; } = new(StringComparer.Ordinal);
    /// <summary>Normalized executable names without ".exe" (crash dumps, dangling shortcuts).</summary>
    public HashSet<string> ExeNames { get; } = new(StringComparer.Ordinal);
    /// <summary>The install folder when it is specific to this app, else empty.</summary>
    public string InstallLocation { get; init; } = string.Empty;
    public string PackageFamilyName { get; init; } = string.Empty;
}

/// <summary>
/// What every other installed app claims, so a leftover scan never takes a folder, key or shortcut
/// that still belongs to something installed (two versions of one app, or a sibling product).
/// </summary>
public sealed class OtherApps
{
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly List<string> _locations = [];

    public static OtherApps Empty { get; } = new([]);

    public OtherApps(IEnumerable<InstalledApp> others)
    {
        foreach (var app in others)
        {
            var id = LeftoverRules.Identity(app, [], []);
            foreach (var n in id.ExactNames.Concat(id.VendorScopedNames)) AddToken(LeftoverRules.Normalize(n));
            foreach (var t in id.Tokens) AddToken(t);
            foreach (var e in id.ExeNames) AddToken(e);
            if (!string.IsNullOrEmpty(id.InstallLocation)) _locations.Add(id.InstallLocation);
            if (!string.IsNullOrEmpty(app.InstallLocation) && LeftoverRules.IsAppSpecificFolder(app.InstallLocation))
                _locations.Add(app.InstallLocation);
        }
    }

    private void AddToken(string token)
    {
        if (token.Length >= 3) _tokens.Add(token);
    }

    public IReadOnlyCollection<string> InstallLocations => _locations;

    /// <summary>Whether another installed app answers to this entry name.</summary>
    public bool ClaimsName(string entryName) => _tokens.Contains(LeftoverRules.Normalize(LeftoverRules.StripEntrySuffix(entryName)));

    /// <summary>Whether the name only matched the app through a prefix token that another installed app shares
    /// ("kritarunner" while two Krita versions are installed).</summary>
    public bool SharesPrefix(string entryName, AppIdentity identity)
    {
        var norm = LeftoverRules.Normalize(LeftoverRules.StripEntrySuffix(entryName));
        return identity.Tokens.Any(t => t.Length >= 5 && norm.StartsWith(t, StringComparison.Ordinal) && _tokens.Contains(t));
    }

    /// <summary>Whether a path is inside (or contains) another installed app's folder.</summary>
    public bool ClaimsPath(string path) =>
        _locations.Any(l => ProtectedPaths.IsInside(path, l) || ProtectedPaths.IsInside(l, path));
}

/// <summary>Pure ownership and leftover-matching rules (unit tested with fixtures).</summary>
public static partial class LeftoverRules
{
    /// <summary>Folder and key names that name a role or a platform, never one app. Matching these would
    /// claim data that belongs to everyone (normalized form).</summary>
    public static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        // platforms and big vendors (vendor folders are traversed, never claimed)
        "microsoft", "windows", "google", "apple", "intel", "nvidia", "nvidiacorporation", "amd", "realtek", "dell", "hp",
        "lenovo", "asus", "oracle", "mozilla", "adobe", "packages", "programs", "program", "programfiles", "commonfiles",
        "microsoftshared", "windowsapps", "systemapps",
        // roles
        "app", "apps", "application", "applications", "mac", "helper", "agent", "daemon", "service", "services", "desktop",
        "client", "launcher", "plugin", "plugins", "extension", "extensions", "web", "free", "pro", "lite", "plus", "ui",
        "login", "updater", "update", "updates", "installer", "installers", "setup", "uninstall", "uninstaller", "renderer",
        "gpu", "worker", "crashpad", "crashdumps", "crashreports", "crashes", "broker", "utility", "utilities", "network",
        "audio", "server", "shared", "core", "common", "framework", "frameworks", "bundle", "process", "handler", "tool",
        "tools", "runtime", "runtimes", "electron", "java", "python", "node", "nodejs", "mono", "wine", "dotnet", "net",
        // generic data folders
        "temp", "tmp", "cache", "caches", "log", "logs", "data", "local", "roaming", "locallow", "user", "users", "default",
        "config", "configs", "settings", "profile", "profiles", "documents", "downloads", "music", "pictures", "videos",
        "public", "backup", "backups", "bin", "lib", "share", "resources", "assets", "files", "storage", "state", "session",
        "sessions", "history", "license", "licenses", "fonts", "themes", "drivers", "driver", "system", "system32",
        "startup", "startmenu", "accessories", "administrativetools", "systemtools", "maintenance", "accessibility",
        "windowspowershell", "windowstools", "games", "software", "classes", "policies", "wow6432node", "clients",
        "registeredapplications", "odbc", "appdatalow", "volatileenvironment", "environment", "console", "keyboardlayout",
        "identities", "printers", "controlpanel", "appevents", "vendor", "company", "x64", "x86", "amd64",
        "arm64", "win32", "win64", "unins000", "unins001", "uninst", "msiexec", "rundll32", "cmd", "explorer", "fieldkit",
    };

    /// <summary>Registry keys under Software that are never claimed, whatever the app is called.</summary>
    public static readonly HashSet<string> ProtectedRegistryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Microsoft", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "ODBC", "Windows",
        "Intel", "NVIDIA Corporation", "AppDataLow", "Fieldkit",
    };

    private static readonly string[] EntrySuffixes =
    [
        ".lnk", ".url", ".exe", ".dmp", ".log", ".ini", ".json", ".xml", ".cfg", ".config", ".dat", ".db", ".sqlite",
        ".tmp", ".reg", ".txt",
    ];

    private static readonly string[] CompanySuffixes =
    [
        ", Inc.", ", Inc", " Inc.", " Inc", ", LLC", " LLC", ", Ltd.", " Ltd.", " Ltd", " Limited", " Corporation",
        " Corp.", " Corp", " GmbH", " AG", " S.A.", " SA", " s.r.o.", " B.V.", " BV", " AB", " Co.", " Co", " Pty",
        " Software", " Technologies", " Technology", " Labs", " Team", " Foundation", " Project", " Developers",
        " Contributors", " Community", " Studio", " Studios", " Games", " Group", " Systems",
    ];

    /// <summary>Lower-case letters and digits only — the key for fuzzy name comparisons.</summary>
    public static string Normalize(string? text) => InstalledAppRules.Normalize(text);

    /// <summary>Removes one well-known file extension so "Foo.lnk" and "Foo.exe.1234.dmp" compare by name.</summary>
    public static string StripEntrySuffix(string name)
    {
        var s = name.Trim();
        foreach (var suffix in EntrySuffixes)
        {
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return s[..^suffix.Length].Trim();
        }
        return s;
    }

    /// <summary>"Foo 1.2.3 (x64)" → "Foo". Only version numbers, bracketed suffixes and architecture words go:
    /// release channels stay (an Insiders build is not the stable app).</summary>
    public static string StripVersion(string raw)
    {
        var s = raw.Trim();
        while (true)
        {
            var next = TrailingBracketRegex().Replace(s, string.Empty).Trim();
            next = TrailingVersionRegex().Replace(next, string.Empty).Trim();
            next = TrailingArchRegex().Replace(next, string.Empty).Trim().TrimEnd('-', '–', ',').Trim();
            if (next.Length < 2 || next == s) return next.Length < 2 ? s : next;
            s = next;
        }
    }

    /// <summary>Also strips release channels ("Foo Beta" → "Foo"): used only for Related tokens.</summary>
    public static string StripChannel(string raw)
    {
        var s = StripVersion(raw);
        var next = TrailingChannelRegex().Replace(s, string.Empty).Trim().TrimEnd('-', '–').Trim();
        return next.Length < 2 ? s : StripVersion(next);
    }

    /// <summary>"Mozilla Corporation" → "Mozilla", "Spotify AB" → "Spotify".</summary>
    public static string StripCompanySuffix(string publisher)
    {
        var s = publisher.Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in CompanySuffixes)
            {
                if (s.Length > suffix.Length + 1 && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^suffix.Length].Trim().TrimEnd(',').Trim();
                    changed = true;
                }
            }
        }
        return s;
    }

    /// <summary>A name may identify an app: at least three letters/digits, one letter, and not a generic word.</summary>
    public static bool IsUsableName(string? raw)
    {
        var n = Normalize(raw);
        return n.Length >= 3 && n.Any(char.IsLetter) && !GenericNames.Contains(n);
    }

    /// <summary>Whether an install folder belongs to one app (not a drive root, Program Files, a well-known folder
    /// or a generic name such as "bin").</summary>
    public static bool IsAppSpecificFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return false; }
        if (ProtectedPaths.IsProtected(full)) return false;
        var leaf = Path.GetFileName(full);
        if (!IsUsableName(leaf)) return false;
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => SharedInstallSegments.Contains(s))) return false;
        return segments.Length >= 2;
    }

    /// <summary>Folders that hold many products' files (installer caches, shared components, Store packages).</summary>
    private static readonly HashSet<string> SharedInstallSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsApps", "Package Cache", "Installer", "InstallShield Installation Information", "Common Files",
        "Downloads", "Package Store",
    };

    /// <summary>
    /// The app's install folder: the registered InstallLocation, else the folder of its DisplayIcon or
    /// uninstaller executable (one level up from an "Uninstall" subfolder). Store apps have none: Windows owns
    /// WindowsApps. Empty when nothing app-specific is known.
    /// </summary>
    public static string InferInstallLocation(InstalledApp app)
    {
        if (app.IsStore) return string.Empty;
        if (IsAppSpecificFolder(app.InstallLocation)) return Path.GetFullPath(app.InstallLocation).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(app.InstallLocation)) return string.Empty; // registered but too generic: trust nothing else

        var candidates = new List<string?>();
        if (InstalledAppRules.IconFile(app.IconPath) is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            candidates.Add(SafeDirectory(icon));
        var command = !string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString : app.QuietUninstallString;
        if (!string.IsNullOrWhiteSpace(command))
        {
            var file = InstalledAppRules.SplitCommandLine(Environment.ExpandEnvironmentVariables(command)).FileName;
            if (Path.IsPathRooted(file) && !UninstallRules.IsMsiExec(file)
                && !Path.GetFileName(file).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dir = SafeDirectory(file);
                if (dir is not null && Normalize(Path.GetFileName(dir)) is "uninstall" or "uninst" or "uninstaller")
                    dir = Path.GetDirectoryName(dir);
                candidates.Add(dir);
            }
        }
        foreach (var c in candidates)
        {
            if (IsAppSpecificFolder(c)) return Path.GetFullPath(c!).TrimEnd('\\');
        }
        return string.Empty;
    }

    private static string? SafeDirectory(string file)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(file.Trim().Trim('"'))); }
        catch { return null; }
    }

    /// <summary>Builds the identity of the app being removed.</summary>
    /// <param name="exeNames">Executable file names known to belong to it (DisplayIcon, main exe).</param>
    /// <param name="programFolders">Folders whose direct children are install folders (Program Files, Local\Programs):
    /// their names are never taken as a vendor.</param>
    public static AppIdentity Identity(InstalledApp app, IEnumerable<string> exeNames, IEnumerable<string> programFolders)
    {
        var install = InferInstallLocation(app);
        // Store (MSIX) apps keep their data in their package folder; a name elsewhere is only a hint.
        var namesAreExact = !app.IsStore;
        var id = new AppIdentity
        {
            DisplayName = app.Name,
            InstallLocation = install,
            PackageFamilyName = app.PackageFamilyName,
        };

        // Vendors: the publisher (with and without its company suffix) and the install folder's parent
        // ("Program Files\VideoLAN\VLC" → VideoLAN) unless that parent is a program root.
        void AddVendor(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var t = v.Trim();
            if (Normalize(t).Length >= 2) id.VendorNames.Add(t);
        }
        AddVendor(app.Publisher);
        AddVendor(StripCompanySuffix(app.Publisher));
        if (install.Length > 0)
        {
            var parent = Path.GetDirectoryName(install);
            if (parent is not null && !ProtectedPaths.IsProtected(parent)
                && !programFolders.Any(p => string.Equals(p.TrimEnd('\\'), parent, StringComparison.OrdinalIgnoreCase))
                && Normalize(Path.GetFileName(parent)).Length >= 2)
                AddVendor(Path.GetFileName(parent));
        }

        void AddExact(string? n)
        {
            if (string.IsNullOrWhiteSpace(n) || !IsUsableName(n)) return;
            if (namesAreExact) id.ExactNames.Add(n.Trim());
            id.Tokens.Add(Normalize(n));
        }
        void AddToken(string? n)
        {
            if (IsUsableName(n)) id.Tokens.Add(Normalize(n));
        }

        AddExact(app.Name);
        AddExact(StripVersion(app.Name));
        AddToken(StripChannel(app.Name));
        if (install.Length > 0)
        {
            // "Program Files\VideoLAN\VLC" names VLC media player; "Proton\VPN" does not name every VPN folder.
            var leaf = Path.GetFileName(install);
            var n = Normalize(leaf);
            if (n.Length >= 6 || Normalize(StripVersion(app.Name)).StartsWith(n, StringComparison.Ordinal)) AddExact(leaf);
            else AddToken(leaf);
        }

        // "Mozilla Firefox" inside the "Mozilla" vendor folder is just "Firefox".
        foreach (var vendor in id.VendorNames.ToList())
        {
            foreach (var name in new[] { StripVersion(app.Name), StripChannel(app.Name) })
            {
                if (name.Length > vendor.Length + 1 && name.StartsWith(vendor + " ", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = name[(vendor.Length + 1)..].Trim();
                    if (IsUsableName(rest))
                    {
                        if (namesAreExact) id.VendorScopedNames.Add(rest);
                        AddToken(rest);
                    }
                }
            }
        }

        // Store packages: "Publisher.AppName_hash" → "AppName".
        if (!string.IsNullOrEmpty(app.PackageFamilyName))
        {
            var name = app.PackageFamilyName.Split('_')[0];
            var last = name.Split('.').Last();
            AddToken(last);
        }

        foreach (var exe in exeNames)
        {
            var stem = Path.GetFileNameWithoutExtension(exe ?? string.Empty);
            var n = Normalize(stem);
            if (IsUsableName(stem) && !n.StartsWith("unins", StringComparison.Ordinal)
                && !n.Contains("setup", StringComparison.Ordinal) && !n.Contains("uninstall", StringComparison.Ordinal))
                id.ExeNames.Add(n);
        }
        return id;
    }

    /// <summary>
    /// Whether a folder, file or key name belongs to the app. Raw exact names are <see cref="LeftoverMatch.Exact"/>;
    /// normalized names, executable names and long prefixes are only <see cref="LeftoverMatch.Related"/>.
    /// </summary>
    public static LeftoverMatch Match(string entryName, AppIdentity identity, bool insideVendor = false)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.StartsWith('.') || entryName.Contains("..")
            || entryName.Contains('\\') || entryName.Contains('/')) return LeftoverMatch.None;
        var stem = StripEntrySuffix(entryName);
        var norm = Normalize(stem);
        if (norm.Length < 3 || !norm.Any(char.IsLetter) || GenericNames.Contains(norm)) return LeftoverMatch.None;
        if (LooksLikeGuid(stem)) return LeftoverMatch.None;
        if (identity.ExactNames.Contains(stem)) return LeftoverMatch.Exact;
        if (insideVendor && identity.VendorScopedNames.Contains(stem)) return LeftoverMatch.Exact;
        if (identity.Tokens.Contains(norm)) return LeftoverMatch.Related;
        if (identity.ExeNames.Contains(norm)) return LeftoverMatch.Related;
        // "Foo" claims "FooData" only from a long, specific token.
        foreach (var token in identity.Tokens)
        {
            if (token.Length >= 5 && norm.StartsWith(token, StringComparison.Ordinal)) return LeftoverMatch.Related;
        }
        return LeftoverMatch.None;
    }

    /// <summary>Whether a folder or key is the app's publisher (searched one level deeper, never claimed).</summary>
    public static bool IsVendor(string entryName, AppIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        if (identity.VendorNames.Contains(entryName.Trim())) return true;
        var n = Normalize(entryName);
        return n.Length >= 2 && identity.VendorNames.Any(v => Normalize(v) == n);
    }

    /// <summary>Crash dumps are "&lt;exe&gt;.exe.&lt;pid&gt;.dmp": the executable they belong to, normalized.</summary>
    public static string? CrashDumpExecutable(string fileName)
    {
        var m = CrashDumpRegex().Match(fileName);
        return m.Success ? Normalize(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Drops duplicates and anything nested inside an entry already kept; a Related entry is dropped
    /// when an Exact one lives inside it (upstream's dedupe).
    /// </summary>
    public static List<Leftover> Dedupe(IEnumerable<Leftover> items)
    {
        var all = items.ToList();
        var exactPaths = all.Where(i => i.Confidence == LeftoverMatch.Exact).Select(i => i.Path).ToList();
        var kept = new List<Leftover>();
        foreach (var item in all.OrderBy(i => i.Path.Length))
        {
            if (item.Confidence == LeftoverMatch.Related
                && exactPaths.Any(p => p.Length > item.Path.Length && ProtectedPaths.IsInside(p, item.Path))) continue;
            var existing = kept.FindIndex(k => string.Equals(k.Path.TrimEnd('\\'), item.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (kept[existing].Confidence != LeftoverMatch.Exact && item.Confidence == LeftoverMatch.Exact) kept[existing] = item;
                continue;
            }
            if (kept.Any(k => k.Kind != LeftoverKind.Uninstaller && ProtectedPaths.IsInside(item.Path, k.Path))) continue;
            kept.Add(item);
        }
        return kept;
    }

    /// <summary>The checkbox state a leftover starts with: only exact matches, and machine-wide items only when
    /// running elevated (and never HKLM keys, which stay an explicit opt-in).</summary>
    public static bool DefaultInclude(LeftoverMatch confidence, bool requiresAdmin, LeftoverKind kind, bool isAdmin)
    {
        if (kind == LeftoverKind.Uninstaller) return true;
        if (confidence != LeftoverMatch.Exact) return false;
        if (!requiresAdmin) return true;
        return isAdmin && kind != LeftoverKind.RegistryKey;
    }

    /// <summary>Whether the item can be selected at all (machine-wide ones need an elevated Fieldkit).</summary>
    public static bool CanSelect(Leftover item, bool isAdmin) => !item.RequiresAdmin || isAdmin;

    /// <summary>Category for the review list, by which root the entry was found in.</summary>
    public static LeftoverCategory CategoryFor(LeftoverKind kind, string path, ScanRootKind root) => kind switch
    {
        LeftoverKind.Uninstaller => LeftoverCategory.App,
        LeftoverKind.RegistryKey => LeftoverCategory.Preferences,
        _ => root switch
        {
            ScanRootKind.Programs or ScanRootKind.InstallFolder => LeftoverCategory.App,
            ScanRootKind.Temp => LeftoverCategory.Caches,
            ScanRootKind.CrashDumps => LeftoverCategory.Logs,
            ScanRootKind.Packages => LeftoverCategory.Containers,
            ScanRootKind.StartMenu or ScanRootKind.Desktop => LeftoverCategory.Shortcuts,
            _ => LooksLikeCacheOrLog(path),
        },
    };

    private static LeftoverCategory LooksLikeCacheOrLog(string path)
    {
        var leaf = Normalize(Path.GetFileName(path.TrimEnd('\\')));
        if (leaf.EndsWith("cache", StringComparison.Ordinal) || leaf.EndsWith("caches", StringComparison.Ordinal)) return LeftoverCategory.Caches;
        if (leaf.EndsWith("logs", StringComparison.Ordinal) || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return LeftoverCategory.Logs;
        return LeftoverCategory.Support;
    }

    /// <summary>Registry keys that may be deleted: below HKCU\Software or HKLM\SOFTWARE (incl. WOW6432Node),
    /// at least one level under it, and not one of the shared roots.</summary>
    public static bool IsDeletableRegistryKey(string keyPath)
    {
        var parts = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        var hive = parts[0].ToUpperInvariant();
        if (hive is not ("HKEY_CURRENT_USER" or "HKCU" or "HKEY_LOCAL_MACHINE" or "HKLM")) return false;
        if (!parts[1].Equals("Software", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = parts.Skip(2).ToArray();
        if (rest[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) rest = rest.Skip(1).ToArray();
        if (rest.Length == 0) return false;
        if (ProtectedRegistryNames.Contains(rest[0])) return false;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is strictly below one of <paramref name="roots"/> (never a root itself).</summary>
    public static bool IsInsideAnyRoot(string path, IEnumerable<string> roots)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return false; }
        return roots.Any(r => !string.IsNullOrWhiteSpace(r)
                              && !string.Equals(full, r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                              && ProtectedPaths.IsInside(full, r));
    }

    public static bool IsMachineRegistryKey(string keyPath) =>
        keyPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGuid(string s) => Guid.TryParse(s.Trim('{', '}'), out _);

    [GeneratedRegex(@"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$")]
    private static partial Regex TrailingBracketRegex();

    [GeneratedRegex(@"\s+(v|version\s*)?\d+([\.\-_]\d+)*[a-z]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionRegex();

    [GeneratedRegex(@"\s*[-–]?\s*\b(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|en-us)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArchRegex();

    [GeneratedRegex(@"\s*[-–]?\s*\b(nightly|beta|alpha|dev|canary|preview|insider|insiders|stable|release|rc|lts|developer edition|community edition)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingChannelRegex();

    [GeneratedRegex(@"^(.+?)\.exe\.\d+\.dmp$", RegexOptions.IgnoreCase)]
    private static partial Regex CrashDumpRegex();
}
