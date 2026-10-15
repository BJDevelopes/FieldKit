// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;

namespace Fieldkit.Features.Files;

/// <summary>The "Install this app?" question.</summary>
public sealed record InstallerPrompt(string Title, string Body, string ConfirmText);

/// <summary>
/// Runs one installation end to end: mount the image (PowerShell Storage cmdlets) or extract the ZIP into
/// Fieldkit's own temp folder, find the installer, ask, run it and wait for it (and anything it started from
/// the image) to finish, or copy a portable app; then eject / remove the temp files and move the download
/// to the Recycle Bin. Call on the UI thread: prompts and progress come back there, slow work runs on the pool.
/// </summary>
public static class InstallerRunner
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Fieldkit's own extraction area: %TEMP%\Fieldkit-installer.</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "Fieldkit-installer");

    public static bool IsBusy { get; private set; }

    /// <param name="confirm">Shows the prompt; true to go ahead.</param>
    /// <param name="progress">Progress lines for the window.</param>
    public static async Task<InstallerResult> InstallAsync(string path, Func<InstallerPrompt, bool> confirm, Action<string> progress)
    {
        var source = InstallerFiles.KindOf(path);
        var name = InstallerText.DisplayName(null, Path.GetFileName(path));
        if (IsBusy) return new InstallerResult(InstallerOutcome.Failed, source, InstallerMethod.Installer, name, Detail: "Another installation is still running.");
        IsBusy = true;
        try
        {
            return await RunAsync(path, source, name, confirm, progress);
        }
        catch (Exception ex)
        {
            Log.Error($"Installing from {path} failed", ex);
            return new InstallerResult(InstallerOutcome.Failed, source, InstallerMethod.Installer, name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<InstallerResult> RunAsync(string path, InstallerSourceKind source, string name,
        Func<InstallerPrompt, bool> confirm, Action<string> progress)
    {
        InstallerResult Result(InstallerOutcome o, InstallerMethod m = InstallerMethod.Installer, string? location = null,
            bool trashed = false, bool cleaned = true, string? detail = null, string? installed = null) =>
            new(o, source, m, name, location, trashed, cleaned, detail, installed);

        if (source == InstallerSourceKind.None) return Result(InstallerOutcome.Failed, detail: "This is not a disk image or ZIP archive.");
        if (!File.Exists(path)) return Result(InstallerOutcome.Failed, detail: "The file no longer exists.");
        var fileName = Path.GetFileName(path);
        var isZip = source == InstallerSourceKind.Zip;

        // ---- 1. make the contents reachable
        string root;
        string? tempDir = null;
        var mountedByUs = false;
        if (isZip)
        {
            progress($"Extracting {fileName}…");
            tempDir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..12]);
            var error = await Task.Run(() => ExtractZip(path, tempDir));
            if (error is not null)
            {
                DeleteTemp(tempDir);
                return Result(InstallerOutcome.Failed, detail: error);
            }
            root = tempDir;
        }
        else
        {
            progress($"Mounting {fileName}…");
            var mount = await MountAsync(path, source);
            if (mount.NeedsAdmin) return Result(InstallerOutcome.NeedsAdmin);
            if (mount.Letter is not { } letter) return Result(InstallerOutcome.Failed, detail: mount.Error);
            root = $"{letter}:\\";
            mountedByUs = mount.MountedByUs;
        }

        // Eject (only what we mounted, unless the install went ahead) or remove our extracted files.
        async Task<bool> CleanUp(bool installed)
        {
            if (tempDir is not null) return DeleteTemp(tempDir);
            if (!mountedByUs && !installed) return true;
            progress($"Ejecting {fileName}…");
            return await DismountAsync(path);
        }

        // ---- 2. find what to run
        var location = await Task.Run(() => InstallerLocator.LocateIn(root, isZip));
        var trash = InstallerSettings.TrashDownload;

        switch (location)
        {
            case PortableAppFound portable:
            {
                var portableRoot = InstallerSettings.PortableFolder;
                var appName = portable.Folder.Length > 0 ? InstallerText.DisplayName(portable.Folder, portable.Folder) : name;
                var destination = InstallerPaths.PortableDestination(portableRoot, appName);
                if (destination is null)
                {
                    await CleanUp(false);
                    return Result(InstallerOutcome.Failed, InstallerMethod.Portable, detail: "The portable apps folder is not valid.");
                }
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    await CleanUp(false);
                    return Result(InstallerOutcome.AlreadyInstalled, InstallerMethod.Portable, portableRoot) with { DisplayName = appName };
                }
                var prompt = new InstallerPrompt(InstallerText.PromptTitle,
                    InstallerText.PromptBody(appName, InstallerMethod.Portable, source, portableRoot, trash), InstallerText.InstallButton);
                if (!confirm(prompt))
                {
                    var cleanedUp = await CleanUp(false);
                    return Result(InstallerOutcome.Cancelled, InstallerMethod.Portable, cleaned: cleanedUp) with { DisplayName = appName };
                }
                progress(InstallerText.Installing(appName));
                var sourceFolder = portable.Folder.Length == 0 ? root : Path.Combine(root, portable.Folder);
                var copyError = await Task.Run(() => CopyPortable(sourceFolder, destination));
                await CleanUp(true);
                if (copyError is not null)
                    return Result(InstallerOutcome.Failed, InstallerMethod.Portable, detail: copyError) with { DisplayName = appName };
                var mainExe = Path.Combine(destination, portable.Folder.Length == 0
                    ? portable.MainExe
                    : portable.MainExe[(portable.Folder.Length + 1)..]);
                var result = await TrashDownloadAsync(path, trash,
                    Result(InstallerOutcome.Installed, InstallerMethod.Portable, portableRoot, installed: mainExe) with { DisplayName = appName });
                if (InstallerSettings.RevealApp) Reveal(mainExe);
                return result;
            }

            case InstallerFound installer:
            {
                var prompt = new InstallerPrompt(InstallerText.PromptTitle,
                    InstallerText.PromptBody(name, InstallerMethod.Installer, source, null, trash), InstallerText.InstallButton);
                if (!confirm(prompt))
                    return Result(InstallerOutcome.Cancelled, cleaned: await CleanUp(false));

                progress(InstallerText.Installing(name));
                var full = Path.Combine(root, installer.RelativePath);
                var (process, launchError, userCancelled) = Launch(full, installer.Kind);
                if (userCancelled) return Result(InstallerOutcome.Cancelled, cleaned: await CleanUp(false));
                if (launchError is not null)
                {
                    await CleanUp(false);
                    return Result(InstallerOutcome.Failed, detail: launchError);
                }

                progress($"Waiting for the {name} installer to finish…");
                var (finished, exitCode) = await WaitAsync(process, root);
                process?.Dispose();
                if (!finished) return Result(InstallerOutcome.StillRunning, cleaned: false);

                var succeeded = InstallerWait.Succeeded(exitCode);
                var cleaned = await CleanUp(succeeded);
                if (!succeeded)
                    return Result(InstallerOutcome.InstallerFailed, cleaned: cleaned, detail: InstallerWait.ExitCodeDetail(installer.Kind, exitCode));
                if (!cleaned) return Result(InstallerOutcome.InstalledKeepingMount);
                return await TrashDownloadAsync(path, trash, Result(InstallerOutcome.Installed));
            }

            default:
            {
                await CleanUp(false);
                return Result(InstallerOutcome.NoInstaller, detail: (location as NoInstallerFound)?.Reason);
            }
        }
    }

    private static async Task<InstallerResult> TrashDownloadAsync(string path, bool trash, InstallerResult installed)
    {
        if (!trash) return installed;
        var ok = await Task.Run(() =>
        {
            try { return ClipboardNative.MoveToRecycleBin(path) && !File.Exists(path); }
            catch (Exception ex)
            {
                Log.Warn($"Moving {path} to the Recycle Bin failed: {ex.Message}");
                return false;
            }
        });
        return ok
            ? installed with { DownloadTrashed = true }
            : installed with { Outcome = InstallerOutcome.InstalledKeepingDownload };
    }

    // ---------------------------------------------------------------- disk images

    private sealed record MountResult(char? Letter, bool MountedByUs, bool NeedsAdmin, string? Error);

    private static async Task<MountResult> MountAsync(string path, InstallerSourceKind source)
    {
        var run = await RunPowerShellAsync(InstallerPowerShell.MountScript(path), MountTimeout);
        var (mounted, letter) = InstallerPowerShell.ParseMountOutput(run.StdOut);
        if (letter is not null && run.ExitCode == 0) return new MountResult(letter, mounted, false, null);
        Log.Warn($"Mounting {path} failed (exit {run.ExitCode}): {run.StdErr.Trim()}");
        if (mounted) await DismountAsync(path);
        if (source == InstallerSourceKind.VirtualDisk &&
            (!InstallerNative.IsElevated() || InstallerPowerShell.LooksLikeAccessDenied(run.StdErr)))
            return new MountResult(null, false, true, null);
        return new MountResult(null, false, false, run.TimedOut
            ? "Windows took too long to mount the disk image."
            : mounted ? "The disk image has no volume Windows can read." : "Windows could not mount the disk image.");
    }

    private static async Task<bool> DismountAsync(string path)
    {
        var run = await RunPowerShellAsync(InstallerPowerShell.DismountScript(path), DismountTimeout);
        if (run.ExitCode == 0) return true;
        Log.Warn($"Ejecting {path} failed (exit {run.ExitCode}): {run.StdErr.Trim()}");
        return false;
    }

    private sealed record PowerShellRun(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    private static async Task<PowerShellRun> RunPowerShellAsync(string script, TimeSpan timeout)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe, $"-NoProfile -NonInteractive -EncodedCommand {InstallerPowerShell.Encode(script)}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new PowerShellRun(-1, string.Empty, "PowerShell did not start.", false);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new PowerShellRun(-1, string.Empty, "timed out", true);
            }
            return new PowerShellRun(process.ExitCode, await stdout, await stderr, false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new PowerShellRun(-1, string.Empty, ex.Message, false);
        }
    }

    // ---------------------------------------------------------------- archives

    /// <summary>Extracts into <paramref name="destination"/> with zip-slip and size guards. Null on success, else why not.</summary>
    public static string? ExtractZip(string zipPath, string destination,
        long maxTotalBytes = InstallerZipSafety.MaxTotalBytes, int maxEntries = InstallerZipSafety.MaxEntries)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (InstallerZipSafety.CheckLimits(archive.Entries.Select(e => e.Length), maxTotalBytes, maxEntries) is { } limit)
                return limit;
            if (archive.Entries.Any(e => InstallerZipSafety.ResolveEntry(destination, e.FullName) is null))
                return "The archive contains unsafe file names, so it was not extracted.";
            Directory.CreateDirectory(destination);
            // Carry the download's Mark of the Web to EVERY extracted file, as File Explorer does, so
            // SmartScreen, Office Protected View and script hosts still treat them as downloaded.
            // An extension allowlist would miss .lnk, .hta, .chm, Office macros and many more.
            var motw = ReadZone(zipPath);
            long written = 0;
            var buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                var target = InstallerZipSafety.ResolveEntry(destination, entry.FullName);
                if (target is null) return "The archive contains unsafe file names, so it was not extracted.";
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    // The declared sizes can lie; count what actually comes out.
                    if (written > maxTotalBytes) return $"The archive unpacks to more than {InstallerFiles.FormatSize(maxTotalBytes)}.";
                    output.Write(buffer, 0, read);
                }
                output.Dispose();
                if (motw is not null) WriteZone(target, motw);
            }
            return null;
        }
        catch (InvalidDataException)
        {
            return "The archive is damaged or not a ZIP file.";
        }
        catch (NotSupportedException)
        {
            return "The archive is encrypted or uses an unsupported format.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The archive could not be extracted: {ex.Message}";
        }
    }

    private static string? ReadZone(string path)
    {
        try
        {
            var stream = path + ":Zone.Identifier";
            return File.Exists(stream) ? File.ReadAllText(stream) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteZone(string path, string zone)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", zone); }
        catch { /* not NTFS: nothing to carry */ }
    }

    /// <summary>Deletes one of Fieldkit's own extraction folders (never anything outside <see cref="TempRoot"/>).</summary>
    public static bool DeleteTemp(string folder) => DeleteOwnedFolder(folder, TempRoot);

    public static bool DeleteOwnedFolder(string folder, string ownerRoot)
    {
        if (!InstallerPaths.IsUnder(ownerRoot, Path.GetFullPath(folder))) return false;
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(folder, recursive: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Removing {folder} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Copies a portable app into a sibling staging folder, then renames it into place. Null on success.</summary>
    public static string? CopyPortable(string sourceFolder, string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var staging = Path.Combine(parent, "." + Path.GetFileName(destination) + ".fieldkit-partial-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(staging);
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var dir in Directory.EnumerateDirectories(sourceFolder, "*", options))
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(sourceFolder, dir)));
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", options))
                File.Copy(file, Path.Combine(staging, Path.GetRelativePath(sourceFolder, file)), overwrite: false);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                DeleteOwnedFolder(staging, parent);
                return "A folder with that name appeared while copying.";
            }
            Directory.Move(staging, destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteOwnedFolder(staging, parent);
            return $"The app could not be copied: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- running

    private static (Process? Process, string? Error, bool UserCancelled) Launch(string path, InstallerKind kind)
    {
        try
        {
            var psi = kind == InstallerKind.Msi
                ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{path}\"")
                : new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty };
            psi.UseShellExecute = true;
            return (Process.Start(psi), null, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was declined
        {
            return (null, null, true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Starting {path} failed: {ex.Message}");
            return (null, "The installer could not be started.", false);
        }
    }

    /// <summary>
    /// Waits (polling every 2 s, off the UI thread) until the installer, the installer work it started and
    /// anything running from <paramref name="root"/> have exited, or <see cref="MaxWait"/> passes.
    /// </summary>
    private static async Task<(bool Finished, int? ExitCode)> WaitAsync(Process? process, string root)
    {
        var self = Environment.ProcessId;
        var temp = Path.GetTempPath();
        var tracked = new Dictionary<int, int>();
        if (process is not null)
        {
            try { tracked[process.Id] = InstallerWait.AnyParent; }
            catch (InvalidOperationException) { /* already gone */ }
        }
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < MaxWait)
        {
            await Task.Delay(PollInterval);
            var running = await Task.Run(() =>
            {
                try
                {
                    var snapshot = InstallerNative.Snapshot(withImagePaths: true);
                    tracked = InstallerWait.Step(tracked, snapshot, p => InstallerWait.IsInstallerWork(p, root, temp));
                    tracked.Remove(self);
                    return tracked.Count > 0 || InstallerWait.RunningFrom(root, snapshot, self).Any();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Checking installer processes failed: {ex.Message}");
                    return true;
                }
            });
            if (running) continue;
            if (process is not null && !HasExited(process)) continue;
            return (true, ExitCode(process));
        }
        return (false, null);
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int? ExitCode(Process? process)
    {
        if (process is null) return null;
        try { return process.ExitCode; }
        catch { return null; }
    }

    /// <summary>Shows a file selected in File Explorer.</summary>
    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Revealing {path} failed: {ex.Message}");
        }
    }
}
