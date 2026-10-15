// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Uninstaller;

public enum UninstallOutcome { Finished, Declined, Failed }

public sealed record UninstallResult(UninstallOutcome Outcome, int ExitCode = 0, string? Error = null);

/// <summary>Starts an app's own uninstaller (elevated through UAC for machine-wide installs) and waits for it.</summary>
public static class UninstallRunner
{
    private const int ErrorCancelled = 1223; // the UAC prompt was declined

    public static async Task<UninstallResult> RunAsync(UninstallPlan plan, CancellationToken ct)
    {
        switch (plan.Method)
        {
            case UninstallMethod.StorePackage:
                return await RemovePackageAsync(plan.FileName, ct).ConfigureAwait(false);
            case UninstallMethod.Command:
            case UninstallMethod.Msi:
                break;
            default:
                return new UninstallResult(UninstallOutcome.Failed, Error: plan.Problem ?? "There is no uninstaller to run.");
        }

        var psi = new ProcessStartInfo(plan.FileName, plan.Arguments) { UseShellExecute = true };
        if (plan.Elevate && !Elevation.IsAdministrator) psi.Verb = "runas";
        if (Path.IsPathRooted(plan.FileName) && Path.GetDirectoryName(plan.FileName) is { } dir && Directory.Exists(dir))
            psi.WorkingDirectory = dir;
        try
        {
            Log.Info($"Uninstaller: running {plan.CommandText}{(psi.Verb == "runas" ? " (elevated)" : string.Empty)}");
            using var process = Process.Start(psi);
            if (process is null) return new UninstallResult(UninstallOutcome.Finished);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new UninstallResult(UninstallOutcome.Finished, SafeExitCode(process));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new UninstallResult(UninstallOutcome.Declined);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstaller: starting {plan.FileName} failed: {ex.Message}");
            return new UninstallResult(UninstallOutcome.Failed, Error: ex.Message);
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch { return 0; }
    }

    private static async Task<UninstallResult> RemovePackageAsync(string fullName, CancellationToken ct)
    {
        try
        {
            Log.Info($"Uninstaller: removing package {fullName}");
            var manager = new Windows.Management.Deployment.PackageManager();
            var result = await manager.RemovePackageAsync(fullName).AsTask(ct).ConfigureAwait(false);
            if (result.ExtendedErrorCode is { } error)
                return new UninstallResult(UninstallOutcome.Failed, error.HResult, string.IsNullOrWhiteSpace(result.ErrorText) ? error.Message : result.ErrorText);
            return new UninstallResult(UninstallOutcome.Finished);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UninstallResult(UninstallOutcome.Failed, ex.HResult, ex.Message);
        }
    }

    /// <summary>Whether the app is still registered (its uninstall entry or Store package still exists).</summary>
    public static bool IsStillInstalled(InstalledApp app)
    {
        try
        {
            if (app.IsStore)
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                return manager.FindPackagesForUser(string.Empty).Any(p =>
                    string.Equals(p.Id.FullName, app.PackageFullName, StringComparison.OrdinalIgnoreCase));
            }
            return !string.IsNullOrEmpty(app.RegistryKeyPath) && WindowsRegistryBrowser.Instance.Exists(app.RegistryKeyPath);
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>Outcome of moving the chosen leftovers away.</summary>
public sealed record RemovalResult(long Freed, IReadOnlyList<(Leftover Item, string Reason)> Failed, IReadOnlyList<string> Backups);

/// <summary>
/// Moves the chosen files and folders to the Recycle Bin and deletes the chosen registry keys after a .reg
/// backup. Every item is re-checked right before it goes: still inside a scanned root, not a junction or
/// symlink, not a protected location, and machine-wide items only when running elevated.
/// </summary>
public static class LeftoverRemover
{
    public static async Task<RemovalResult> RemoveAsync(IReadOnlyList<Leftover> chosen, ScanEnvironment env, string installLocation,
        CancellationToken ct)
    {
        long freed = 0;
        var failed = new List<(Leftover, string)>();
        var backups = new List<string>();
        // The install folder itself may be a leftover wherever it lives, so its parent counts as a root.
        var installParent = LeftoverRules.IsAppSpecificFolder(installLocation) ? Path.GetDirectoryName(installLocation) : null;
        var roots = env.FileRoots().Append(installParent ?? string.Empty).Where(r => !string.IsNullOrEmpty(r)).ToList();
        foreach (var item in chosen)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind == LeftoverKind.Uninstaller) continue;
            if (item.RequiresAdmin && !env.IsAdmin)
            {
                failed.Add((item, UninstallerText.AdminRequired));
                continue;
            }
            if (item.Kind == LeftoverKind.RegistryKey)
            {
                if (!LeftoverRules.IsDeletableRegistryKey(item.Path))
                {
                    failed.Add((item, "Protected registry key"));
                    continue;
                }
                if (!env.Registry.Exists(item.Path)) continue; // already gone
                var backup = await RegistryBackup.ExportAsync(item.Path, $"Uninstaller {item.Name}", ct).ConfigureAwait(false);
                if (backup is null)
                {
                    failed.Add((item, "Couldn't back up the key, so it was kept"));
                    continue;
                }
                backups.Add(backup);
                if (DeleteKey(item.Path) is { } error) failed.Add((item, error));
                continue;
            }

            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                freed += item.Size; // already gone (the uninstaller took it)
                continue;
            }
            if (!IsStillSafe(item.Path, roots, env.ExcludedPaths))
            {
                failed.Add((item, "No longer safe to remove"));
                continue;
            }
            var result = RecycleBin.Send([item.Path]);
            if (result.Recycled.Count > 0) freed += item.Size;
            else failed.Add((item, result.Failed.FirstOrDefault().Error ?? "Couldn’t be moved"));
        }
        return new RemovalResult(freed, failed, backups);
    }

    /// <summary>Strictly inside a scanned root, not excluded, not protected, and no junction/symlink on the way.</summary>
    public static bool IsStillSafe(string path, IReadOnlyList<string> roots, IReadOnlyList<string> excluded)
    {
        if (!LeftoverRules.IsInsideAnyRoot(path, roots)) return false;
        if (excluded.Any(e => ProtectedPaths.IsInside(path, e) || ProtectedPaths.IsInside(e, path))) return false;
        if (ProtectedPaths.IsProtected(path)) return false;
        try
        {
            var root = roots.Where(r => ProtectedPaths.IsInside(path, r)).OrderByDescending(r => r.Length).First();
            var current = Path.GetFullPath(path).TrimEnd('\\');
            var stop = Path.GetFullPath(root).TrimEnd('\\');
            while (current.Length > stop.Length)
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
                current = Path.GetDirectoryName(current) ?? stop;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? DeleteKey(string keyPath)
    {
        try
        {
            var (hive, sub) = WindowsRegistryBrowser.Split(keyPath);
            if (hive is null) return "Unknown registry hive";
            var cut = sub.LastIndexOf('\\');
            if (cut <= 0) return "Protected registry key";
            using var root = RegistryKey.OpenBaseKey(hive.Value, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
            using var parent = root.OpenSubKey(sub[..cut], writable: true);
            if (parent is null) return null;
            parent.DeleteSubKeyTree(sub[(cut + 1)..], throwOnMissingSubKey: false);
            Log.Info($"Uninstaller: deleted registry key {keyPath}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstaller: deleting {keyPath} failed: {ex.Message}");
            return ex is UnauthorizedAccessException or System.Security.SecurityException ? UninstallerText.AdminRequired : ex.Message;
        }
    }
}
