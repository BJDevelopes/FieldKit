// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Common;

/// <summary>Where an installed app was found.</summary>
public enum InstalledAppSource
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall (64-bit view).</summary>
    MachineRegistry,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Uninstall (32-bit view).</summary>
    MachineRegistry32,
    /// <summary>HKCU\...\Uninstall (per-user installs).</summary>
    UserRegistry,
    /// <summary>An MSIX/AppX package installed for the current user (Microsoft Store and sideloaded).</summary>
    StorePackage,
}

/// <summary>
/// One app as Windows lists it in Settings → Apps → Installed apps. Shared by the
/// uninstaller, the cleaner (leftovers of removed apps) and app updates.
/// </summary>
public sealed record InstalledApp
{
    /// <summary>Stable key: registry "&lt;hive&gt;\&lt;view&gt;\&lt;subkey&gt;" or the package family name.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public InstalledAppSource Source { get; init; }
    /// <summary>Folder the app installed to (may be empty or stale).</summary>
    public string InstallLocation { get; init; } = string.Empty;
    /// <summary>"path,index" from DisplayIcon, or the Store logo file.</summary>
    public string IconPath { get; init; } = string.Empty;
    public string UninstallString { get; init; } = string.Empty;
    public string QuietUninstallString { get; init; } = string.Empty;
    /// <summary>EstimatedSize from the registry (bytes), or 0 when unknown.</summary>
    public long EstimatedSizeBytes { get; init; }
    public DateTime? InstallDate { get; init; }
    /// <summary>Full registry path of the uninstall entry (e.g. HKEY_CURRENT_USER\Software\...\Uninstall\Foo).</summary>
    public string RegistryKeyPath { get; init; } = string.Empty;
    /// <summary>Uninstall subkey name (often the MSI product code or a vendor id).</summary>
    public string RegistryKeyName { get; init; } = string.Empty;
    public bool IsWindowsInstaller { get; init; }
    public string PackageFullName { get; init; } = string.Empty;
    public string PackageFamilyName { get; init; } = string.Empty;
    /// <summary>Whether removing it needs administrator rights (machine-wide installs).</summary>
    public bool RequiresAdmin => Source is InstalledAppSource.MachineRegistry or InstalledAppSource.MachineRegistry32;
    public bool IsStore => Source == InstalledAppSource.StorePackage;
}

/// <summary>
/// Reads the installed-app list from the registry Uninstall keys (64/32-bit, machine and user)
/// and the current user's MSIX/AppX packages. Read-only: never changes anything.
/// </summary>
public static class InstalledAppsProvider
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Every user-visible installed app, sorted by name. Slow-ish (registry + package
    /// enumeration): call off the UI thread.</summary>
    public static List<InstalledApp> Load(bool includeStore = true)
    {
        var apps = new List<InstalledApp>();
        ReadHive(apps, RegistryHive.LocalMachine, RegistryView.Registry64, InstalledAppSource.MachineRegistry);
        ReadHive(apps, RegistryHive.LocalMachine, RegistryView.Registry32, InstalledAppSource.MachineRegistry32);
        ReadHive(apps, RegistryHive.CurrentUser, RegistryView.Default, InstalledAppSource.UserRegistry);
        if (includeStore) ReadPackages(apps);
        return InstalledAppRules.Deduplicate(apps)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void ReadHive(List<InstalledApp> apps, RegistryHive hive, RegistryView view, InstalledAppSource source)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            var hiveName = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
            var keyPrefix = view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine
                ? $@"{hiveName}\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                : $@"{hiveName}\{UninstallPath}";
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var entry = new UninstallEntry(
                        DisplayName: key.GetValue("DisplayName") as string,
                        SystemComponent: ToInt(key.GetValue("SystemComponent")),
                        ParentKeyName: key.GetValue("ParentKeyName") as string,
                        ReleaseType: key.GetValue("ReleaseType") as string,
                        UninstallString: key.GetValue("UninstallString") as string,
                        WindowsInstaller: ToInt(key.GetValue("WindowsInstaller")),
                        NoRemove: ToInt(key.GetValue("NoRemove")));
                    if (!InstalledAppRules.IsUserVisible(entry)) continue;
                    apps.Add(new InstalledApp
                    {
                        Id = $"{keyPrefix}\\{name}",
                        Name = entry.DisplayName!.Trim(),
                        Publisher = (key.GetValue("Publisher") as string ?? string.Empty).Trim(),
                        Version = (key.GetValue("DisplayVersion") as string ?? string.Empty).Trim(),
                        Source = source,
                        InstallLocation = InstalledAppRules.CleanPath(key.GetValue("InstallLocation") as string),
                        IconPath = (key.GetValue("DisplayIcon") as string ?? string.Empty).Trim(),
                        UninstallString = (entry.UninstallString ?? string.Empty).Trim(),
                        QuietUninstallString = (key.GetValue("QuietUninstallString") as string ?? string.Empty).Trim(),
                        EstimatedSizeBytes = InstalledAppRules.EstimatedSizeBytes(key.GetValue("EstimatedSize")),
                        InstallDate = InstalledAppRules.ParseInstallDate(key.GetValue("InstallDate") as string),
                        RegistryKeyPath = $"{keyPrefix}\\{name}",
                        RegistryKeyName = name,
                        IsWindowsInstaller = entry.WindowsInstaller == 1,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Reading uninstall entry {name} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading {hive}/{view} uninstall keys failed: {ex.Message}");
        }
    }

    private static void ReadPackages(List<InstalledApp> apps)
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    if (package.IsStub) continue;
                    var name = SafeDisplayName(package);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = package.Id.Version;
                    string location = string.Empty;
                    try { location = package.InstalledLocation?.Path ?? string.Empty; } catch { }
                    string logo = string.Empty;
                    try { logo = package.Logo?.LocalPath ?? string.Empty; } catch { }
                    DateTime? installed = null;
                    try { installed = package.InstalledDate.LocalDateTime; } catch { }
                    apps.Add(new InstalledApp
                    {
                        Id = package.Id.FamilyName,
                        Name = name.Trim(),
                        Publisher = SafePublisher(package),
                        Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        Source = InstalledAppSource.StorePackage,
                        InstallLocation = location,
                        IconPath = logo,
                        InstallDate = installed,
                        PackageFullName = package.Id.FullName,
                        PackageFamilyName = package.Id.FamilyName,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Reading package failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Enumerating Store packages failed: {ex.Message}");
        }
    }

    private static string SafeDisplayName(Windows.ApplicationModel.Package package)
    {
        try { return package.DisplayName; }
        catch { return package.Id.Name; }
    }

    private static string SafePublisher(Windows.ApplicationModel.Package package)
    {
        try { return package.PublisherDisplayName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var p) => p,
        _ => 0,
    };

    /// <summary>The app's icon (DisplayIcon / main executable / Store logo), or null.</summary>
    public static ImageSource? Icon(InstalledApp app)
    {
        try
        {
            if (app.IsStore)
            {
                var logo = InstalledAppRules.ResolveStoreLogo(app.IconPath);
                if (logo is null) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(logo);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            var path = InstalledAppRules.IconFile(app.IconPath);
            if (path is not null && path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                frame?.Freeze();
                return frame;
            }
            return AppIcons.ForPath(path)
                   ?? AppIcons.ForPath(InstalledAppRules.MainExecutableGuess(app.InstallLocation));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The registry values that decide whether an Uninstall entry is a user-visible app.</summary>
public sealed record UninstallEntry(
    string? DisplayName,
    int SystemComponent,
    string? ParentKeyName,
    string? ReleaseType,
    string? UninstallString,
    int WindowsInstaller,
    int NoRemove);

/// <summary>Pure rules behind <see cref="InstalledAppsProvider"/> (unit tested).</summary>
public static partial class InstalledAppRules
{
    /// <summary>Mirrors what Settings → Installed apps shows: named, not a system component,
    /// not an update/hotfix or child entry, and removable.</summary>
    public static bool IsUserVisible(UninstallEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.DisplayName)) return false;
        if (e.SystemComponent == 1) return false;
        if (!string.IsNullOrWhiteSpace(e.ParentKeyName)) return false;
        if (e.ReleaseType is { } rt && (rt.Contains("Update", StringComparison.OrdinalIgnoreCase)
                                        || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                                        || rt.Equals("Security Update", StringComparison.OrdinalIgnoreCase))) return false;
        if (string.IsNullOrWhiteSpace(e.UninstallString) && e.WindowsInstaller != 1) return false;
        if (KbUpdateRegex().IsMatch(e.DisplayName)) return false;
        return true;
    }

    /// <summary>The registry stores EstimatedSize in KiB as a DWORD.</summary>
    public static long EstimatedSizeBytes(object? value) => value switch
    {
        int i when i > 0 => i * 1024L,
        long l when l > 0 => l * 1024L,
        string s when long.TryParse(s, out var p) && p > 0 => p * 1024L,
        _ => 0,
    };

    /// <summary>InstallDate is "yyyyMMdd" (sometimes with separators).</summary>
    public static DateTime? ParseInstallDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 8 && DateTime.TryParseExact(digits, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    /// <summary>Strips quotes and trailing separators from a registry path value.</summary>
    public static string CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var p = value.Trim().Trim('"').Trim();
        p = Environment.ExpandEnvironmentVariables(p);
        return p.Length > 3 ? p.TrimEnd('\\', '/') : p;
    }

    /// <summary>The file part of a DisplayIcon value ("C:\x\app.exe,0" → "C:\x\app.exe").</summary>
    public static string? IconFile(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            s = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var comma = s.LastIndexOf(',');
            if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        }
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        return s.Length == 0 ? null : s;
    }

    /// <summary>A best guess at an app's main executable inside its install folder (first .exe at the top level).</summary>
    public static string? MainExecutableGuess(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation)) return null;
        try
        {
            return Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Store logos are declared as "Assets\Logo.png" but shipped as scaled variants
    /// ("Logo.scale-200.png", "Logo.targetsize-48.png"). Returns an existing file or null.
    /// </summary>
    public static string? ResolveStoreLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        if (File.Exists(logoPath)) return logoPath;
        try
        {
            var dir = Path.GetDirectoryName(logoPath);
            if (dir is null || !Directory.Exists(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(logoPath);
            var ext = Path.GetExtension(logoPath);
            var candidates = Directory.EnumerateFiles(dir, stem + ".*" + ext).ToList();
            return candidates
                .OrderByDescending(c => c.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Contains("contrast", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The same product registered in both registry views (or HKLM and HKCU) appears once.</summary>
    public static IEnumerable<InstalledApp> Deduplicate(IEnumerable<InstalledApp> apps) =>
        apps.GroupBy(a => (Normalize(a.Name), a.Version.Trim().ToLowerInvariant(), a.IsStore))
            .Select(g => g.OrderBy(a => a.Source).First());

    /// <summary>Lower-case letters and digits only — the key for fuzzy name comparisons.</summary>
    public static string Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Splits a command line into the executable and its arguments, honouring quotes
    /// ("\"C:\a b\unins000.exe\" /SILENT" → ("C:\a b\unins000.exe", "/SILENT")).
    /// Unquoted paths with spaces are resolved by growing the candidate until it ends in .exe.</summary>
    public static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return (string.Empty, string.Empty);
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return (s.Trim('"'), string.Empty);
            return (s[1..end], s[(end + 1)..].Trim());
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            var cut = exe + 4;
            if (cut == s.Length || s[cut] == ' ') return (s[..cut], s[cut..].Trim());
        }
        var space = s.IndexOf(' ');
        return space < 0 ? (s, string.Empty) : (s[..space], s[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"\(KB\d{6,}\)|^Security Update for|^Update for|^Hotfix for", RegexOptions.IgnoreCase)]
    private static partial Regex KbUpdateRegex();
}
