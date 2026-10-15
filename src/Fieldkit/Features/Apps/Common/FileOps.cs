// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Fieldkit.Core;

namespace Fieldkit.Features.Apps.Common;

/// <summary>Byte counts as people read them ("1.4 GB", "820 KB"), decimal units like Explorer's "size on disk" dialogs on macOS.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "Zero KB";
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var format = value >= 100 || unit == 1 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}

/// <summary>Outcome of moving a set of paths to the Recycle Bin.</summary>
public sealed record RecycleResult(IReadOnlyList<string> Recycled, IReadOnlyList<(string Path, string Error)> Failed, long FreedBytes);

/// <summary>
/// Moves files and folders to the Recycle Bin (never deletes permanently). Paths that
/// are protected (Windows, Program Files, the user profile root, drive roots) are refused
/// unless the caller explicitly allows protected locations.
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// Recycles each path. Items the Recycle Bin cannot keep are refused (never deleted for good);
    /// see <see cref="RecycleBinPolicy"/>.
    /// </summary>
    public static RecycleResult Send(IEnumerable<string> paths, Func<string, long>? sizeOf = null, bool allowProtected = false)
    {
        var ok = new List<string>();
        var failed = new List<(string, string)>();
        long freed = 0;
        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\', '/');
            try
            {
                if (!allowProtected && ProtectedPaths.IsProtected(path))
                {
                    failed.Add((path, "Protected location"));
                    continue;
                }
                if (!Directory.Exists(path) && !File.Exists(path)) continue;
                var size = sizeOf?.Invoke(path) ?? DiskUsage.SizeOf(path);
                if (!RecycleBinPolicy.CanHold(path, size))
                {
                    // The shell would delete it permanently instead; leave it in place.
                    failed.Add((path, "The Recycle Bin can't hold this item, so it was left in place"));
                    continue;
                }
                if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else if (File.Exists(path))
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    continue;
                ok.Add(path);
                freed += size;
            }
            catch (Exception ex)
            {
                failed.Add((path, ex.Message));
                Log.Warn($"Recycling {path} failed: {ex.Message}");
            }
        }
        return new RecycleResult(ok, failed, freed);
    }
}

/// <summary>
/// OneDrive (and other cloud filter) placeholders: reading a cloud-only file downloads it, so anything
/// that reads contents (hashing, thumbnails, previews) must skip them (upstream #877, iCloud's version).
/// </summary>
public static class CloudFiles
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Whether reading this file would download it first.</summary>
    public static bool IsCloudOnly(string path)
    {
        try { return IsCloudOnly(File.GetAttributes(path)); }
        catch { return false; }
    }

    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;
}

/// <summary>Locations no cleanup may touch on its own (rules are pure and unit tested).</summary>
public static class ProtectedPaths
{
    private static IEnumerable<string> Roots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>True for a drive root, a well-known folder itself, or anything inside Windows.</summary>
    public static bool IsProtected(string path) => IsProtected(path, Roots(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    public static bool IsProtected(string path, IEnumerable<string> wellKnown, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return true; }
        if (full.Length <= 3) return true; // "C:" / "C:\"
        if (Path.GetPathRoot(full)?.TrimEnd('\\') is { } root && string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var r in wellKnown)
        {
            if (string.IsNullOrEmpty(r)) continue;
            if (string.Equals(full, r.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrEmpty(windowsDir) && IsInside(full, windowsDir)) return true;
        return false;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or below it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = path.TrimEnd('\\', '/') + "\\";
        var f = folder.TrimEnd('\\', '/') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Folder size measurement that tolerates access errors and reparse points.</summary>
public static class DiskUsage
{
    public static long SizeOf(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += file.Length; } catch { }
            }
            return total;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>Result of a finished child process.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);

/// <summary>Runs a console program without a window, streaming its output lines.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, Action<string>? onLine = null,
        CancellationToken ct = default, TimeSpan? timeout = null, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeoutCts = timeout is null ? new CancellationTokenSource() : new CancellationTokenSource(timeout.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, output.ToString(), error.ToString(), TimedOut: true);
        }
        process.WaitForExit(); // flush async readers
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString(), TimedOut: false);
    }
}

/// <summary>Elevation helpers.</summary>
public static class Elevation
{
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>Exports registry keys to .reg files before anything removes them.</summary>
public static class RegistryBackup
{
    /// <summary>Folder for backups: %APPDATA%\Fieldkit\Registry backups.</summary>
    public static string Folder
    {
        get
        {
            var dir = Path.Combine(AppInfo.DataDirectory, "Registry backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Runs <c>reg.exe export</c> for a key (full path such as HKEY_CURRENT_USER\Software\Foo).
    /// Returns the .reg file path, or null if the export failed (callers must then NOT delete the key).</summary>
    public static async Task<string?> ExportAsync(string keyPath, string label, CancellationToken ct = default)
    {
        var safe = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe}.reg");
        var result = await ProcessRunner.RunAsync("reg.exe", $"export \"{keyPath}\" \"{file}\" /y", ct: ct,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return result.ExitCode == 0 && File.Exists(file) ? file : null;
    }
}
