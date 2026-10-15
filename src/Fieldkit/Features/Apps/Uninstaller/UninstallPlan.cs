// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Uninstaller;

public enum UninstallMethod
{
    /// <summary>The registered UninstallString / QuietUninstallString.</summary>
    Command,
    /// <summary>msiexec /x {product code}.</summary>
    Msi,
    /// <summary>PackageManager.RemovePackageAsync for the current user.</summary>
    StorePackage,
    /// <summary>Nothing can be run (missing or unregistered uninstaller).</summary>
    Unavailable,
}

/// <summary>How an app's own uninstaller is started.</summary>
public sealed record UninstallPlan(UninstallMethod Method, string FileName, string Arguments, bool Elevate, string? Problem = null)
{
    public bool CanRun => Method != UninstallMethod.Unavailable;

    /// <summary>"msiexec.exe /x {…}" as shown on the review screen.</summary>
    public string CommandText => Method switch
    {
        UninstallMethod.StorePackage => FileName,
        UninstallMethod.Unavailable => string.Empty,
        _ => string.IsNullOrEmpty(Arguments) ? Quote(FileName) : $"{Quote(FileName)} {Arguments}",
    };

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

/// <summary>Pure decisions about running an uninstaller and recognising an app (unit tested).</summary>
public static partial class UninstallRules
{
    /// <summary>
    /// The command that removes <paramref name="app"/>: the interactive UninstallString unless
    /// <paramref name="preferQuiet"/> and a QuietUninstallString exists; Windows Installer products by
    /// product code; Store packages through the package manager. Machine-wide entries run elevated.
    /// </summary>
    public static UninstallPlan Plan(InstalledApp app, bool preferQuiet, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (app.IsStore)
        {
            return string.IsNullOrEmpty(app.PackageFullName)
                ? new UninstallPlan(UninstallMethod.Unavailable, string.Empty, string.Empty, false, "This package can't be removed for your account.")
                : new UninstallPlan(UninstallMethod.StorePackage, app.PackageFullName, string.Empty, false);
        }

        var elevate = app.RequiresAdmin;
        if (app.IsWindowsInstaller && TryProductCode(app.RegistryKeyName, out var code))
        {
            var args = $"/x {code}" + (preferQuiet ? " /passive /norestart" : string.Empty);
            return new UninstallPlan(UninstallMethod.Msi, "msiexec.exe", args, elevate);
        }

        var command = preferQuiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString
            : !string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString
            : app.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(command))
            return new UninstallPlan(UninstallMethod.Unavailable, string.Empty, string.Empty, elevate, $"{app.Name} doesn't register an uninstaller.");

        var (file, arguments) = InstalledAppRules.SplitCommandLine(Environment.ExpandEnvironmentVariables(command));
        if (IsMsiExec(file))
        {
            // "MsiExec.exe /I{code}" opens the repair/change dialog; /X removes.
            arguments = MsiInstallSwitch().Replace(arguments, "/X$1");
            return new UninstallPlan(UninstallMethod.Msi, file, arguments, elevate);
        }
        if (Path.IsPathRooted(file) && !fileExists(file))
            return new UninstallPlan(UninstallMethod.Unavailable, file, arguments, elevate, $"The app's uninstaller is missing: {file}");
        return new UninstallPlan(UninstallMethod.Command, file, arguments, elevate);
    }

    public static bool IsMsiExec(string file)
    {
        var name = Path.GetFileName(file.Trim('"'));
        return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MSI uninstall keys are named by product code ("{8C1A…}").</summary>
    public static bool TryProductCode(string keyName, out string code)
    {
        code = string.Empty;
        var k = keyName.Trim();
        if (k.Length != 38 || k[0] != '{' || k[^1] != '}' || !Guid.TryParse(k, out _)) return false;
        code = k.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// Finds the installed app a dropped or chosen program belongs to: its DisplayIcon executable,
    /// then the most specific install folder containing it, then a matching product name.
    /// </summary>
    public static InstalledApp? Resolve(string executablePath, IEnumerable<InstalledApp> apps, string? productName = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        string full;
        try { full = Path.GetFullPath(executablePath.Trim().Trim('"')); }
        catch { return null; }
        var list = apps.ToList();

        var byIcon = list.FirstOrDefault(a => InstalledAppRules.IconFile(a.IconPath) is { } icon
                                              && string.Equals(SafeFull(icon), full, StringComparison.OrdinalIgnoreCase));
        if (byIcon is not null) return byIcon;

        var byFolder = list
            .Select(a => (App: a, Folder: a.IsStore ? a.InstallLocation : LeftoverRules.InferInstallLocation(a)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Folder) && ProtectedPaths.IsInside(full, x.Folder))
            .OrderByDescending(x => x.Folder.TrimEnd('\\').Length)
            .Select(x => x.App)
            .FirstOrDefault();
        if (byFolder is not null) return byFolder;

        var byUninstaller = list.FirstOrDefault(a => !string.IsNullOrEmpty(a.UninstallString)
            && string.Equals(SafeFull(InstalledAppRules.SplitCommandLine(a.UninstallString).FileName), full, StringComparison.OrdinalIgnoreCase));
        if (byUninstaller is not null) return byUninstaller;

        var product = LeftoverRules.Normalize(productName);
        if (product.Length >= 3)
        {
            var named = list.Where(a => LeftoverRules.Normalize(a.Name) == product
                                        || LeftoverRules.Normalize(LeftoverRules.StripVersion(a.Name)) == product).ToList();
            if (named.Count == 1) return named[0];
        }
        return null;
    }

    /// <summary>Fieldkit never offers to uninstall itself.</summary>
    public static bool IsSelf(InstalledApp app, string selfExecutable)
    {
        if (app.Name.StartsWith("Fieldkit", StringComparison.OrdinalIgnoreCase)) return true;
        var dir = Path.GetDirectoryName(selfExecutable);
        return !string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(app.InstallLocation)
               && !ProtectedPaths.IsProtected(app.InstallLocation) && ProtectedPaths.IsInside(dir, app.InstallLocation);
    }

    /// <summary>Picker search: name, publisher or version contains the query (case-insensitive).</summary>
    public static bool MatchesQuery(InstalledApp app, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return app.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || app.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || app.Version.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { return path; }
    }

    [GeneratedRegex(@"/I\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();
}
