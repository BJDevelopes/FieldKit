// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>
/// Links a winget row to the app Windows lists as installed, for its icon, publisher, install
/// folder and size, and to open it. Exact links first (MSIX package name, ARP registry key),
/// then the display name; an ambiguous name links to nothing rather than to the wrong app.
/// </summary>
public static class InstalledAppMatcher
{
    public static InstalledApp? Match(string name, string id, string version, IReadOnlyList<InstalledApp> apps)
    {
        if (apps.Count == 0) return null;
        if (id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
        {
            var full = id[5..];
            return apps.FirstOrDefault(a => string.Equals(a.PackageFullName, full, StringComparison.OrdinalIgnoreCase));
        }
        if (id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
        {
            // ARP\<Machine|User>\<X64|X86|Arm64>\<uninstall subkey>
            var parts = id.Split('\\', 4);
            if (parts.Length == 4)
            {
                var user = parts[1].Equals("User", StringComparison.OrdinalIgnoreCase);
                var keyMatches = apps.Where(a => !a.IsStore
                                                 && string.Equals(a.RegistryKeyName, parts[3], StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var scoped = keyMatches.Where(a => (a.Source == InstalledAppSource.UserRegistry) == user).ToList();
                if (scoped.Count == 1) return scoped[0];
                if (keyMatches.Count == 1) return keyMatches[0];
            }
        }

        var key = InstalledAppRules.Normalize(name);
        if (key.Length == 0) return null;
        var exact = apps.Where(a => InstalledAppRules.Normalize(a.Name) == key).ToList();
        var picked = Pick(exact, version);
        if (picked is not null || exact.Count > 1) return picked;
        if (key.Length < 5) return null;
        var prefix = apps.Where(a =>
        {
            var n = InstalledAppRules.Normalize(a.Name);
            return n.Length >= 5 && (n.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(n, StringComparison.Ordinal));
        }).ToList();
        return Pick(prefix, version);
    }

    private static InstalledApp? Pick(List<InstalledApp> candidates, string version)
    {
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0 || version.Length == 0) return null;
        var sameVersion = candidates.Where(a => string.Equals(a.Version.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return sameVersion.Count == 1 ? sameVersion[0] : null;
    }

    /// <summary>The executable that starts the app: the DisplayIcon target when it is an .exe that
    /// is not an uninstaller, otherwise a guess inside the install folder.</summary>
    public static string? LaunchTarget(InstalledApp app)
    {
        var icon = InstalledAppRules.IconFile(app.IconPath);
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(icon).Contains("unins", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(icon).Contains("setup", StringComparison.OrdinalIgnoreCase))
            return icon;
        return InstalledAppRules.MainExecutableGuess(app.InstallLocation);
    }

    /// <summary>Starts the app (Store apps through their app list entry). Falls back to opening the
    /// install folder in File Explorer. Returns false when there is nothing to open.</summary>
    public static async Task<bool> OpenAsync(InstalledApp app)
    {
        try
        {
            if (app.IsStore && app.PackageFullName.Length > 0)
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                var package = manager.FindPackageForUser(string.Empty, app.PackageFullName);
                if (package is not null)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    if (entries.Count > 0) return await entries[0].LaunchAsync();
                }
                return false;
            }
            var exe = LaunchTarget(app);
            if (exe is not null && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty })?.Dispose();
                return true;
            }
            if (app.InstallLocation.Length > 0 && Directory.Exists(app.InstallLocation))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{app.InstallLocation}\"") { UseShellExecute = true })?.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening {app.Name} failed: {ex.Message}");
        }
        return false;
    }
}
