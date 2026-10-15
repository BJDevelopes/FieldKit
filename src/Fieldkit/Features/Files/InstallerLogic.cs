// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text;
using Fieldkit.Core;

namespace Fieldkit.Features.Files;

/// <summary>What kind of download the installer helper can open.</summary>
public enum InstallerSourceKind { None, DiskImage, VirtualDisk, Zip }

/// <summary>A file in the Downloads folder (path, last write time, size in bytes).</summary>
public sealed record InstallerDownload(string Path, DateTime Modified, long Size);

/// <summary>Settings keys and their defaults.</summary>
public static class InstallerSettings
{
    public const string TrashDownloadKey = "diskImageInstaller.trashDownload";
    public const string RevealAppKey = "diskImageInstaller.revealApp";
    public const string PortableFolderKey = "diskImageInstaller.portableFolder";

    public const bool TrashDownloadDefault = true;
    public const bool RevealAppDefault = false;
    public const string PortableFolderDefault = @"%LOCALAPPDATA%\Programs";

    public static bool TrashDownload => Settings.GetBool(TrashDownloadKey, TrashDownloadDefault);
    public static bool RevealApp => Settings.GetBool(RevealAppKey, RevealAppDefault);

    /// <summary>The folder portable apps are copied to, with environment variables expanded.</summary>
    public static string PortableFolder
    {
        get
        {
            var raw = Settings.GetString(PortableFolderKey, PortableFolderDefault);
            if (string.IsNullOrWhiteSpace(raw)) raw = PortableFolderDefault;
            return Environment.ExpandEnvironmentVariables(raw.Trim());
        }
    }
}

/// <summary>Which files the helper lists and opens.</summary>
public static class InstallerFiles
{
    public static readonly IReadOnlyList<string> Extensions = [".iso", ".img", ".vhd", ".vhdx", ".zip"];

    /// <summary>Most rows the Downloads list shows.</summary>
    public const int MaxListed = 12;

    public static InstallerSourceKind KindOf(string? path)
    {
        var ext = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".iso" or ".img" => InstallerSourceKind.DiskImage,
            ".vhd" or ".vhdx" => InstallerSourceKind.VirtualDisk,
            ".zip" => InstallerSourceKind.Zip,
            _ => InstallerSourceKind.None,
        };
    }

    public static bool IsSupported(string? path) => KindOf(path) != InstallerSourceKind.None;

    /// <summary>"Disk image", "Virtual disk", "ZIP archive".</summary>
    public static string KindLabel(string path) => KindOf(path) switch
    {
        InstallerSourceKind.DiskImage => "Disk image",
        InstallerSourceKind.VirtualDisk => "Virtual disk",
        InstallerSourceKind.Zip => "ZIP archive",
        _ => "File",
    };

    /// <summary>File dialog filter for the supported types.</summary>
    public static string DialogFilter =>
        "Disk images and archives (*.iso;*.img;*.vhd;*.vhdx;*.zip)|*.iso;*.img;*.vhd;*.vhdx;*.zip";

    /// <summary>Supported, non-empty files, newest first (ties by name), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<InstallerDownload> Newest(IEnumerable<InstallerDownload> files, int max = MaxListed) =>
        files.Where(f => f.Size > 0 && IsSupported(f.Path) && !Path.GetFileName(f.Path).StartsWith("~$", StringComparison.Ordinal))
            .OrderByDescending(f => f.Modified)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, max))
            .ToList();

    /// <summary>"512 bytes", "1.4 MB", "4.7 GB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>"Just now", "5 min ago", "Today 14:05", "Yesterday", "3 days ago", or the date.</summary>
    public static string FormatAge(DateTime modified, DateTime now)
    {
        var age = now - modified;
        if (age < TimeSpan.FromMinutes(1)) return "Just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (modified.Date == now.Date) return "Today " + modified.ToString("t", CultureInfo.CurrentCulture);
        if (modified.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)Math.Ceiling((now.Date - modified.Date).TotalDays)} days ago";
        return modified.ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>Builds the PowerShell used to mount and eject disk images.</summary>
public static class InstallerPowerShell
{
    /// <summary>Line printed by <see cref="MountScript"/> when it attached the image itself.</summary>
    public const string MountedMarker = "FIELDKIT-MOUNTED";

    /// <summary>
    /// A single-quoted PowerShell literal. PowerShell treats the typographic quotes
    /// (U+2018–U+201B) as single quotes too, so every one of them is doubled.
    /// </summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// Mounts the image unless it is already attached, then prints the drive letter (and
    /// <see cref="MountedMarker"/> first when this script did the mounting).
    /// </summary>
    public static string MountScript(string imagePath)
    {
        var p = Quote(imagePath);
        return "$ErrorActionPreference = 'Stop'; " +
               $"$i = Get-DiskImage -ImagePath {p}; " +
               $"if (-not $i.Attached) {{ Mount-DiskImage -ImagePath {p} -StorageType {StorageType(imagePath)} | Out-Null; '{MountedMarker}' }}; " +
               "$d = $null; " +
               "for ($n = 0; $n -lt 40 -and -not $d; $n++) { " +
               $"$d = (Get-DiskImage -ImagePath {p} | Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | Select-Object -First 1 -ExpandProperty DriveLetter); " +
               "if (-not $d) { Start-Sleep -Milliseconds 250 } }; " +
               "$d";
    }

    public static string DismountScript(string imagePath) =>
        $"$ErrorActionPreference = 'Stop'; Dismount-DiskImage -ImagePath {Quote(imagePath)} | Out-Null";

    private static string StorageType(string path) => InstallerFiles.KindOf(path) switch
    {
        InstallerSourceKind.VirtualDisk => Path.GetExtension(path).Equals(".vhdx", StringComparison.OrdinalIgnoreCase) ? "VHDX" : "VHD",
        _ => "ISO",
    };

    /// <summary>The <c>-EncodedCommand</c> argument (UTF-16LE, base64) so no outer quoting is needed.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Reads the mount script's output: whether it mounted, and the drive letter (if any).</summary>
    public static (bool MountedByScript, char? DriveLetter) ParseMountOutput(string? stdout)
    {
        var mounted = false;
        char? letter = null;
        foreach (var raw in (stdout ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line == MountedMarker) mounted = true;
            else if (line.Length == 1 && char.IsAsciiLetter(line[0])) letter = char.ToUpperInvariant(line[0]);
        }
        return (mounted, letter);
    }

    /// <summary>Whether PowerShell's error output reads like a missing-elevation failure.</summary>
    public static bool LooksLikeAccessDenied(string? stderr) =>
        !string.IsNullOrEmpty(stderr) &&
        (stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("elevat", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("PermissionDenied", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Zip-slip and zip-bomb guards for archive extraction.</summary>
public static class InstallerZipSafety
{
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20_000;

    /// <summary>
    /// The full path an entry extracts to, or null when it would land outside
    /// <paramref name="destinationRoot"/> (absolute names, "..", drive letters, streams).
    /// </summary>
    public static string? ResolveEntry(string destinationRoot, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        var name = entryName.Replace('/', '\\');
        if (name.StartsWith('\\') || name.Contains(':') || name.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0) return null;
        if (name.Split('\\').Any(part => part.Trim() == "..")) return null;
        var root = Path.GetFullPath(destinationRoot).TrimEnd('\\') + "\\";
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, name)); }
        catch { return null; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return full.Length > root.Length ? full : null;
    }

    /// <summary>Null when the archive is within limits, otherwise why not.</summary>
    public static string? CheckLimits(IEnumerable<long> entryLengths, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
    {
        long total = 0;
        var count = 0;
        foreach (var length in entryLengths)
        {
            if (++count > maxEntries) return $"The archive has more than {maxEntries:N0} files.";
            if (length < 0) return "The archive is damaged.";
            total += length;
            if (total > maxTotalBytes) return $"The archive unpacks to more than {InstallerFiles.FormatSize(maxTotalBytes)}.";
        }
        return null;
    }
}

/// <summary>Path checks shared by the runner.</summary>
public static class InstallerPaths
{
    /// <summary>Whether <paramref name="path"/> is inside <paramref name="root"/> (case-insensitive, separator-aware).</summary>
    public static bool IsUnder(string root, string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        var r = root.Replace('/', '\\').TrimEnd('\\') + "\\";
        var p = path.Replace('/', '\\');
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A folder name safe for File Explorer: invalid characters dropped, reserved device names avoided.</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) return "App";
        var stem = cleaned.Split('.')[0].Trim().ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(stem) ? cleaned + " app" : cleaned;
    }

    /// <summary>
    /// Where a portable app goes: a direct child of <paramref name="portableRoot"/> named after the app,
    /// or null if the name cannot make one (the counterpart of upstream <c>destinationURL</c>).
    /// </summary>
    public static string? PortableDestination(string portableRoot, string appName)
    {
        if (string.IsNullOrWhiteSpace(portableRoot)) return null;
        var root = Path.GetFullPath(portableRoot).TrimEnd('\\');
        var destination = Path.GetFullPath(Path.Combine(root, SafeFolderName(appName)));
        return string.Equals(Path.GetDirectoryName(destination)?.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
            ? destination
            : null;
    }
}

/// <summary>A process seen by the installer wait loop.</summary>
public readonly record struct InstallerProcess(int Pid, int ParentPid, string? ImagePath);

/// <summary>Decides which processes still belong to a running installation.</summary>
public static class InstallerWait
{
    /// <summary>Recorded parent value meaning "any parent" (the root installer we launched).</summary>
    public const int AnyParent = -1;

    /// <summary>
    /// One poll: keeps tracked processes that are still alive with the same parent (so a reused PID
    /// does not count) and adds every process whose parent was tracked at the previous poll (bootstrappers
    /// that start a child and exit), transitively. Returns pid → parent pid.
    /// </summary>
    /// <param name="counts">Whether a descendant is installer work (see <see cref="IsInstallerWork"/>); the app an
    /// installer launches when it finishes is not, so waiting does not last until the user quits it.</param>
    public static Dictionary<int, int> Step(IReadOnlyDictionary<int, int> previous, IReadOnlyList<InstallerProcess> snapshot,
        Func<InstallerProcess, bool>? counts = null)
    {
        var next = new Dictionary<int, int>();
        foreach (var p in snapshot)
        {
            if (!previous.TryGetValue(p.Pid, out var parent)) continue;
            if (parent == AnyParent) next[p.Pid] = AnyParent;
            else if (parent == p.ParentPid) next[p.Pid] = p.ParentPid;
        }
        bool added;
        do
        {
            added = false;
            foreach (var p in snapshot)
            {
                if (next.ContainsKey(p.Pid) || p.Pid == p.ParentPid || p.Pid == 0) continue;
                if (!previous.ContainsKey(p.ParentPid) && !next.ContainsKey(p.ParentPid)) continue;
                if (counts is not null && !counts(p)) continue;
                next[p.Pid] = p.ParentPid;
                added = true;
            }
        } while (added);
        return next;
    }

    /// <summary>
    /// Whether a process an installer started is still part of the installation: it runs from the image /
    /// extracted folder or the temp folder (bootstrappers unpack there), is named like an installer, or its
    /// path cannot be read (assume it matters).
    /// </summary>
    public static bool IsInstallerWork(InstallerProcess p, string root, string tempFolder)
    {
        if (string.IsNullOrEmpty(p.ImagePath)) return true;
        if (InstallerPaths.IsUnder(root, p.ImagePath) || InstallerPaths.IsUnder(tempFolder, p.ImagePath)) return true;
        var name = Path.GetFileNameWithoutExtension(p.ImagePath);
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("install", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Processes (other than <paramref name="selfPid"/>) whose executable lives under <paramref name="root"/>.</summary>
    public static IEnumerable<InstallerProcess> RunningFrom(string root, IEnumerable<InstallerProcess> snapshot, int selfPid) =>
        snapshot.Where(p => p.Pid != selfPid && InstallerPaths.IsUnder(root, p.ImagePath));

    /// <summary>Whether an installer's exit code means it finished (0, or "restart required" 3010 / 1641). Unknown counts as finished.</summary>
    public static bool Succeeded(int? exitCode) => exitCode is null or 0 or 3010 or 1641;

    /// <summary>A short explanation of a failing exit code, or null.</summary>
    public static string? ExitCodeDetail(InstallerKind kind, int? exitCode) => exitCode switch
    {
        null or 0 or 3010 or 1641 => null,
        1602 when kind == InstallerKind.Msi => "The installation was cancelled.",
        1618 when kind == InstallerKind.Msi => "Another installation is already in progress.",
        _ => $"It exited with code {exitCode}.",
    };
}

/// <summary>How an installation ended.</summary>
public enum InstallerOutcome
{
    Installed,
    InstalledKeepingMount,
    InstalledKeepingDownload,
    AlreadyInstalled,
    NoInstaller,
    NeedsAdmin,
    InstallerFailed,
    StillRunning,
    Cancelled,
    Failed,
}

public enum InstallerMethod { Installer, Portable }

/// <summary>The result of one installation, turned into words by <see cref="InstallerText.Describe"/>.</summary>
/// <param name="Location">Portable apps: the folder they went to.</param>
/// <param name="CleanedUp">The image was ejected / the extracted files removed.</param>
/// <param name="Detail">Extra reason (no installer found, archive too large, exit code, …).</param>
/// <param name="InstalledPath">Portable apps: the main executable in its new place.</param>
public sealed record InstallerResult(
    InstallerOutcome Outcome,
    InstallerSourceKind Source,
    InstallerMethod Method,
    string DisplayName,
    string? Location = null,
    bool DownloadTrashed = false,
    bool CleanedUp = true,
    string? Detail = null,
    string? InstalledPath = null)
{
    public bool IsInstalled => Outcome is InstallerOutcome.Installed or InstallerOutcome.InstalledKeepingMount
        or InstallerOutcome.InstalledKeepingDownload;
}

/// <summary>User-facing strings (upstream DiskImageInstallerStrings.enUS, adapted to Windows).</summary>
public static class InstallerText
{
    public const string Title = "Installer helper";
    public const string PromptTitle = "Install this app?";
    public const string InstallButton = "Install";
    public const string TrashDownloadOption = "Move the download to the Recycle Bin";
    public const string RevealAppOption = "Show the installed app in File Explorer";
    public const string FailedBody = "Nothing was changed. You can still open the file from File Explorer.";

    public static string Installing(string name) => $"Installing {name}…";

    /// <summary>Port of upstream <c>displayName</c>: trimmed, whitespace collapsed, no control characters, at most 80 characters.</summary>
    public static string DisplayName(string? preferred, string fileName)
    {
        var fallback = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(fallback)) fallback = fileName;
        var source = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred!;
        var words = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var flattened = new string(string.Join(' ', words)
            .Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned))
            .ToArray());
        var cut = flattened.Length > 80 ? (char.IsHighSurrogate(flattened[79]) ? 79 : 80) : flattened.Length;
        var result = flattened[..cut].Trim();
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Body of the "Install this app?" prompt.</summary>
    public static string PromptBody(string name, InstallerMethod method, InstallerSourceKind source, string? folder, bool trashDownload)
    {
        var download = trashDownload ? " and the download moved to the Recycle Bin" : string.Empty;
        if (method == InstallerMethod.Portable)
            return $"{name} will be copied to {folder}{download}.";
        var cleanup = source == InstallerSourceKind.Zip ? "the extracted files are removed" : "the disk image is ejected";
        return $"{name} will run its installer. When it closes, {cleanup}{download}.";
    }

    /// <summary>Title, body, whether it is a warning, and whether to post a notification.</summary>
    public static (string Title, string Body, bool Warning, bool Notify) Describe(InstallerResult r)
    {
        var image = r.Source != InstallerSourceKind.Zip;
        var name = r.DisplayName;
        var subject = r.Method == InstallerMethod.Portable
            ? $"{name} is ready in {r.Location}."
            : $"The {name} installer finished.";
        var cleaned = image ? "The disk image was ejected" : "The extracted files were removed";
        var installedTitle = r.Method == InstallerMethod.Portable ? "App installed" : "Installer finished";
        string WithDetail(string body) => string.IsNullOrWhiteSpace(r.Detail) ? body : $"{r.Detail.Trim()} {body}";

        switch (r.Outcome)
        {
            case InstallerOutcome.Installed when r.Method == InstallerMethod.Portable && !image:
                return (installedTitle, r.DownloadTrashed
                    ? $"{subject} Its download was moved to the Recycle Bin."
                    : $"{subject} Its download was kept.", false, true);
            case InstallerOutcome.Installed:
                return (installedTitle, r.DownloadTrashed
                    ? $"{subject} {cleaned} and its download moved to the Recycle Bin."
                    : $"{subject} {cleaned} and its download kept.", false, true);
            case InstallerOutcome.InstalledKeepingMount:
                return (installedTitle, r.Method == InstallerMethod.Portable
                    ? $"{name} is installed in {r.Location}, but the disk image could not be ejected. Its download was kept."
                    : $"The {name} installer finished, but the disk image could not be ejected. Its download was kept.", true, true);
            case InstallerOutcome.InstalledKeepingDownload:
                return (installedTitle, r.Method == InstallerMethod.Portable && !image
                    ? $"{subject} Its download could not be moved to the Recycle Bin."
                    : $"{subject} {cleaned}, but its download could not be moved to the Recycle Bin.", true, true);
            case InstallerOutcome.AlreadyInstalled:
                return ("Could not install", $"{name} is already installed in {r.Location}.", true, true);
            case InstallerOutcome.NoInstaller:
                return ("No installer found", WithDetail("Nothing was installed and the download was kept."), true, true);
            case InstallerOutcome.NeedsAdmin:
                return ("Could not install",
                    "Mounting VHD and VHDX files needs administrator rights. Nothing was changed; open the file from File Explorer instead.",
                    true, true);
            case InstallerOutcome.InstallerFailed:
                var tail = r.CleanedUp
                    ? $"{cleaned} and its download kept."
                    : image ? "The disk image could not be ejected and its download was kept." : "Its download was kept.";
                var why = string.IsNullOrWhiteSpace(r.Detail) ? string.Empty : " " + r.Detail.Trim();
                return ("Installer did not finish", $"The {name} installer was cancelled or failed.{why} {tail}", true, true);
            case InstallerOutcome.StillRunning:
                return ("Installer still running", image
                    ? $"The {name} installer was still running after 2 hours, so the disk image was left mounted and its download kept."
                    : $"The {name} installer was still running after 2 hours, so its files and download were kept.", true, true);
            case InstallerOutcome.Cancelled:
                return ("Installation cancelled", r.CleanedUp
                    ? $"Nothing was installed. {cleaned} and its download kept."
                    : "Nothing was installed and the download was kept.", false, false);
            default:
                return ("Could not install", WithDetail(FailedBody), true, true);
        }
    }
}
