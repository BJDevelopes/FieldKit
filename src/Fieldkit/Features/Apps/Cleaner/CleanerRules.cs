// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>What the cleaner can find. Raw values are stable; new cases append at the end.</summary>
public enum CleanerCategory
{
    Leftovers,
    LoginItems,
    Caches,
    Logs,
    Developer,
    Trash,
    DeviceBackups,
    Screenshots,
    Temp,
    SystemCaches,
}

/// <summary>How finds are presented: safe groups first (pre-checked), judgment calls under optional.</summary>
public enum DisplayGroup
{
    LoginItems,
    Temp,
    SafeCaches,
    Logs,
    Developer,
    Leftovers,
    OtherCaches,
    SystemCaches,
    DeviceBackups,
    Screenshots,
    Trash,
}

/// <summary>The well-known folders every rule is expressed against (injected so tests use fixtures).</summary>
public sealed record CleanerPaths(
    string LocalAppData,
    string RoamingAppData,
    string LocalLow,
    string Temp,
    string UserProfile,
    string Screenshots,
    string WindowsDir)
{
    public static CleanerPaths Current()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new CleanerPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(profile, "AppData", "LocalLow"),
            Path.GetTempPath().TrimEnd('\\'),
            profile,
            CleanerNative.ScreenshotsFolder() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    /// <summary>Replaces {Local}, {Roaming}, {LocalLow}, {Temp}, {Profile} and {Windows}.</summary>
    public string Expand(string pattern) => pattern
        .Replace("{Local}", LocalAppData, StringComparison.Ordinal)
        .Replace("{Roaming}", RoamingAppData, StringComparison.Ordinal)
        .Replace("{LocalLow}", LocalLow, StringComparison.Ordinal)
        .Replace("{Temp}", Temp, StringComparison.Ordinal)
        .Replace("{Profile}", UserProfile, StringComparison.Ordinal)
        .Replace("{Windows}", WindowsDir, StringComparison.Ordinal);
}

/// <summary>A well-known cache location. <c>*</c> in the pattern matches one directory level
/// (browser profiles) or, in the last segment, a file name wildcard.</summary>
/// <param name="Processes">Process names that must not be running while this is cleaned (browsers).</param>
public sealed record CacheRule(string App, string Pattern, CleanerCategory Category, bool Recommended, params string[] Processes);

/// <summary>Settings keys of the cleaner (all prefixed with the feature id).</summary>
public static class CleanerKeys
{
    public const string Shortcut = "cleaner.shortcut";
    public const string ScheduleFrequency = "cleaner.scheduleFrequency";
    public const string ScheduleHour = "cleaner.scheduleHour";
    public const string ScheduleMinute = "cleaner.scheduleMinute";
    public const string ScheduleWeekday = "cleaner.scheduleWeekday";
    public const string ScheduleNotify = "cleaner.scheduleNotify";
    public const string LastAutoRun = "cleaner.lastAutoRun";
    public const string LastAutoFreed = "cleaner.lastAutoFreed";
    public const string LastAutoFailed = "cleaner.lastAutoFailed";
    public const string ScreenshotAgeDays = "cleaner.screenshotAgeDays";
    public const string TempAgeDays = "cleaner.tempAgeDays";
    public const string SystemCaches = "cleaner.includeWindowsUpdateCaches";
    public const string ScheduleTemp = "cleaner.schedule.temp";
    public const string ScheduleCaches = "cleaner.schedule.caches";
    public const string ScheduleLogs = "cleaner.schedule.logs";
    public const string ScheduleDeveloper = "cleaner.schedule.developer";

    public const int DefaultHour = 9;
    public const int DefaultMinute = 0;
    /// <summary>Upstream default: Monday (1 = Sunday … 7 = Saturday).</summary>
    public const int DefaultWeekday = 2;
}

/// <summary>
/// Every judgment call of the cleaner in one place (the upstream CleanerPolicy + CleanerSupport,
/// mapped to Windows): what starts checked, what never appears, and the safety guards that run
/// again right before anything is removed. Pure, so the tests can pin every rule.
/// </summary>
public static partial class CleanerRules
{
    // ------------------------------------------------------------------ policy

    public const bool PrecheckLeftovers = false;
    public const bool PrecheckLoginItems = true;
    public const bool PrecheckLogs = true;
    public const bool PrecheckDeveloper = true;
    public const bool PrecheckTemp = true;
    public const bool PrecheckDeviceBackups = false;
    public const bool PrecheckScreenshots = false;
    public const bool PrecheckSystemCaches = false;

    public static readonly int[] ScreenshotAgeChoices = [7, 14, 30, 60, 90];
    public const int DefaultScreenshotAgeDays = 30;

    public static int SanitizedScreenshotAgeDays(int raw) => raw <= 0 ? 0 : Math.Min(raw, 3650);

    public static readonly int[] TempAgeChoices = [1, 2, 7, 14, 30];
    public const int DefaultTempAgeDays = 7;

    public static int SanitizedTempAgeDays(int raw) => TempAgeChoices.Contains(raw) ? raw : DefaultTempAgeDays;

    /// <summary>A leftover folder must sit untouched this long: on Windows names are far fuzzier
    /// than bundle identifiers, so recent activity is treated as proof of a living owner.</summary>
    public const int LeftoverIdleDays = 30;

    // ---------------------------------------------------------- display groups

    public static bool IsSafe(DisplayGroup g) => g is DisplayGroup.LoginItems or DisplayGroup.Temp
        or DisplayGroup.SafeCaches or DisplayGroup.Logs or DisplayGroup.Developer;

    public static DisplayGroup GroupOf(CleanerCategory category, bool recommended) => category switch
    {
        CleanerCategory.LoginItems => DisplayGroup.LoginItems,
        CleanerCategory.Temp => DisplayGroup.Temp,
        CleanerCategory.Caches => recommended ? DisplayGroup.SafeCaches : DisplayGroup.OtherCaches,
        CleanerCategory.Logs => DisplayGroup.Logs,
        CleanerCategory.Developer => recommended ? DisplayGroup.Developer : DisplayGroup.OtherCaches,
        CleanerCategory.Leftovers => DisplayGroup.Leftovers,
        CleanerCategory.SystemCaches => DisplayGroup.SystemCaches,
        CleanerCategory.DeviceBackups => DisplayGroup.DeviceBackups,
        CleanerCategory.Screenshots => DisplayGroup.Screenshots,
        CleanerCategory.Trash => DisplayGroup.Trash,
        _ => DisplayGroup.OtherCaches,
    };

    /// <summary>The group a scan category is reported under in the progress line.</summary>
    public static string ScanTitle(CleanerCategory c) => c switch
    {
        CleanerCategory.Leftovers => S.CatLeftovers,
        CleanerCategory.LoginItems => S.CatLoginItems,
        CleanerCategory.Caches => S.CatCaches,
        CleanerCategory.Logs => S.CatLogs,
        CleanerCategory.Developer => S.CatDeveloper,
        CleanerCategory.Trash => S.CatTrash,
        CleanerCategory.DeviceBackups => S.CatDeviceBackups,
        CleanerCategory.Screenshots => S.CatScreenshots,
        CleanerCategory.Temp => S.CatTemp,
        CleanerCategory.SystemCaches => S.CatSystemCaches,
        _ => c.ToString(),
    };

    /// <summary>The automatic cleanup only ever cleans these safe cache/temp groups, and only
    /// those the user left enabled for the schedule.</summary>
    public static string? ScheduleKey(CleanerCategory c) => c switch
    {
        CleanerCategory.Temp => CleanerKeys.ScheduleTemp,
        CleanerCategory.Caches => CleanerKeys.ScheduleCaches,
        CleanerCategory.Logs => CleanerKeys.ScheduleLogs,
        CleanerCategory.Developer => CleanerKeys.ScheduleDeveloper,
        _ => null,
    };

    /// <summary>Whether an unattended pass may take a find: pre-checked (recommended), in a
    /// schedulable group the user enabled, and not blocked by a running app.</summary>
    public static bool AutomaticMayClean(CleanerCategory category, bool recommended, bool blocked, Func<string, bool> enabled)
    {
        if (!recommended || blocked) return false;
        var key = ScheduleKey(category);
        return key is not null && enabled(key);
    }

    // ------------------------------------------------------------------ caches

    /// <summary>Folders that never appear at all: this app's own data and caches known to hold
    /// things that cannot be rebuilt (Spotify keeps installed customizations next to its cache).</summary>
    private static readonly string[] HiddenCacheOwners = ["fieldkit", "spotify"];

    public static bool IsExcludedCacheOwner(string name)
    {
        var lowered = name.ToLowerInvariant();
        return HiddenCacheOwners.Any(h => lowered.StartsWith(h, StringComparison.Ordinal));
    }

    private static readonly string[] ChromiumCaches = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    private static readonly string[] ChromiumShared = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache"];

    private static IEnumerable<CacheRule> Chromium(string app, string userData, string process)
    {
        foreach (var c in ChromiumCaches) yield return new CacheRule(app, $@"{userData}\*\{c}", CleanerCategory.Caches, true, process);
        foreach (var c in ChromiumShared) yield return new CacheRule(app, $@"{userData}\{c}", CleanerCategory.Caches, true, process);
    }

    /// <summary>Well-known cache locations. Browser caches are only ever cleaned while that browser is closed.</summary>
    public static IReadOnlyList<CacheRule> CacheCatalog { get; } =
    [
        .. Chromium("Google Chrome", @"{Local}\Google\Chrome\User Data", "chrome"),
        .. Chromium("Microsoft Edge", @"{Local}\Microsoft\Edge\User Data", "msedge"),
        .. Chromium("Brave", @"{Local}\BraveSoftware\Brave-Browser\User Data", "brave"),
        .. Chromium("Vivaldi", @"{Local}\Vivaldi\User Data", "vivaldi"),
        .. Chromium("Chromium", @"{Local}\Chromium\User Data", "chrome"),
        new("Opera", @"{Local}\Opera Software\Opera Stable\Cache", CleanerCategory.Caches, true, "opera"),
        new("Opera", @"{Local}\Opera Software\Opera Stable\Default\Cache", CleanerCategory.Caches, true, "opera"),
        new("Opera GX", @"{Local}\Opera Software\Opera GX Stable\Cache", CleanerCategory.Caches, true, "opera"),
        new("Opera GX", @"{Local}\Opera Software\Opera GX Stable\Default\Cache", CleanerCategory.Caches, true, "opera"),
        new("Firefox", @"{Local}\Mozilla\Firefox\Profiles\*\cache2", CleanerCategory.Caches, true, "firefox"),
        new("Firefox", @"{Local}\Mozilla\Firefox\Profiles\*\startupCache", CleanerCategory.Caches, true, "firefox"),
        new("Thunderbird", @"{Local}\Thunderbird\Profiles\*\cache2", CleanerCategory.Caches, true, "thunderbird"),
        new("Steam", @"{Local}\Steam\htmlcache", CleanerCategory.Caches, true, "steam", "steamwebhelper"),
        new("Visual Studio Code", @"{Roaming}\Code\CachedData", CleanerCategory.Caches, true),
        new("Visual Studio Code", @"{Roaming}\Code\CachedExtensionVSIXs", CleanerCategory.Caches, true),
        new("Teams", @"{Local}\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache", CleanerCategory.Caches, true, "ms-teams"),
        // System and GPU caches: safe, but Windows and games rebuild them eagerly, so they are
        // listed for the willing and pre-checked for nobody (like Apple's own caches upstream).
        new("Windows internet cache", @"{Local}\Microsoft\Windows\INetCache", CleanerCategory.Caches, false),
        new("File Explorer thumbnails", @"{Local}\Microsoft\Windows\Explorer\thumbcache_*.db", CleanerCategory.Caches, false),
        new("DirectX shader cache", @"{Local}\D3DSCache", CleanerCategory.Caches, false),
        new("NVIDIA shader cache", @"{Local}\NVIDIA\DXCache", CleanerCategory.Caches, false),
        new("NVIDIA shader cache", @"{Local}\NVIDIA\GLCache", CleanerCategory.Caches, false),
        new("NVIDIA shader cache", @"{LocalLow}\NVIDIA\PerDriverVersion\DXCache", CleanerCategory.Caches, false),
        new("AMD shader cache", @"{Local}\AMD\DxCache", CleanerCategory.Caches, false),
        new("AMD shader cache", @"{Local}\AMD\DxcCache", CleanerCategory.Caches, false),
        new("AMD shader cache", @"{Local}\AMD\VkCache", CleanerCategory.Caches, false),
        new("AMD shader cache", @"{Local}\AMD\GLCache", CleanerCategory.Caches, false),
        new("Intel shader cache", @"{Local}\Intel\ShaderCache", CleanerCategory.Caches, false),
        // Downloads the user paid bandwidth or setup for: shown, never pre-checked.
        new("Playwright browsers", @"{Local}\ms-playwright", CleanerCategory.Developer, false),
        // Developer download caches: pure downloads that come back on the next install.
        new("npm", @"{Local}\npm-cache\_cacache", CleanerCategory.Developer, true),
        new("npx", @"{Local}\npm-cache\_npx", CleanerCategory.Developer, true),
        new("Bun", @"{Profile}\.bun\install\cache", CleanerCategory.Developer, true),
        new("pnpm", @"{Local}\pnpm-cache", CleanerCategory.Developer, true),
        new("pip", @"{Local}\pip\cache", CleanerCategory.Developer, true),
        new("NuGet", @"{Local}\NuGet\v3-cache", CleanerCategory.Developer, true),
        new("NuGet", @"{Local}\NuGet\plugins-cache", CleanerCategory.Developer, true),
        new("Yarn", @"{Local}\Yarn\Cache", CleanerCategory.Developer, true),
        new("Go", @"{Local}\go-build", CleanerCategory.Developer, true),
        new("node-gyp", @"{Local}\node-gyp\Cache", CleanerCategory.Developer, true),
        new("Electron", @"{Local}\electron\Cache", CleanerCategory.Developer, true),
        new("electron-builder", @"{Local}\electron-builder\Cache", CleanerCategory.Developer, true),
        new("Cypress", @"{Local}\Cypress\Cache", CleanerCategory.Developer, true),
    ];

    /// <summary>Cache folder names Electron/Chromium-based apps keep directly in their AppData folder.</summary>
    public static readonly string[] AppCacheFolderNames = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    /// <summary>Log folder names apps keep directly in their AppData folder.</summary>
    public static readonly string[] AppLogFolderNames = ["logs"];

    /// <summary>Crash dumps and Windows Error Reporting queues (the counterpart of DiagnosticReports).</summary>
    public static IReadOnlyList<(string Name, string Pattern)> CrashReportLocations { get; } =
    [
        ("Crash dumps", @"{Local}\CrashDumps"),
        ("Windows Error Reporting archive", @"{Local}\Microsoft\Windows\WER\ReportArchive"),
        ("Windows Error Reporting queue", @"{Local}\Microsoft\Windows\WER\ReportQueue"),
        ("Windows Error Reporting temp", @"{Local}\Microsoft\Windows\WER\Temp"),
    ];

    /// <summary>Windows Update / Delivery Optimization download caches (admin and opt-in only).
    /// Their contents are cleaned, never the folders themselves.</summary>
    public static IReadOnlyList<(string Name, string Pattern)> SystemCacheLocations { get; } =
    [
        ("Windows Update downloads", @"{Windows}\SoftwareDistribution\Download"),
        ("Delivery Optimization cache", @"{Windows}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
    ];

    /// <summary>Where iTunes / Apple Devices keep iPhone backups on Windows.</summary>
    public static readonly string[] DeviceBackupRoots = [@"{Roaming}\Apple Computer\MobileSync\Backup", @"{Profile}\Apple\MobileSync\Backup"];

    // ---------------------------------------------------------------- leftovers

    /// <summary>
    /// AppData folders that are never leftovers: the operating system's own, shared runtimes
    /// and caches other rules own, GPU vendors and this app. Compared case-insensitively.
    /// </summary>
    public static readonly HashSet<string> ExcludedLeftoverNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache",
        "Diagnostics", "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "IsolatedStorage", "Package Cache",
        "PeerDistRepub", "Microsoft_Corporation", "MicrosoftEdge", "Microsoft Help", "History", "Application Data",
        "Temporary Internet Files", "assembly", "Deployment", "Apps", "SquirrelTemp", "fontconfig", "CEF",
        "Windows", "WindowsApps", "Sun", "Identities", "Media Center Programs", "Adobe", "Apple", "Apple Computer",
        "npm", "npm-cache", "pip", "NuGet", "Yarn", "go-build", "pnpm", "pnpm-cache", "node-gyp", "electron",
        "electron-builder", "ms-playwright", "Cypress", "pypa", "Python", ".IdentityService", "IdentityNexusIntegration",
        "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Intel", "Realtek", "Fieldkit", "OneDrive",
        // Shared Windows / Visual Studio components that no uninstall entry names.
        "DWriteCore", "DigitalEntitlements", "ServiceHub", "AzureFunctionsTools", "Microsoft SDKs", "WindowsPowerShell",
        "PowerToys", "Mozilla", "Google",
    };

    /// <summary>Whether an AppData entry may even be considered: not hidden, not excluded, a real name.</summary>
    public static bool IsLeftoverCandidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.StartsWith('{')) return false;
        if (ExcludedLeftoverNames.Contains(name)) return false;
        return InstalledAppRules.Normalize(name).Length >= 3;
    }

    /// <summary>Normalized tokens that prove something is alive: installed app names, publishers,
    /// install folder names, Store package names and running process names.</summary>
    public static HashSet<string> OwnerTokens(IEnumerable<InstalledApp> apps, IEnumerable<string> processNames)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            var n = InstalledAppRules.Normalize(value);
            if (n.Length >= 3) tokens.Add(n);
        }

        foreach (var app in apps)
        {
            Add(app.Name);
            Add(app.Publisher);
            foreach (var word in (app.Publisher ?? string.Empty).Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)) Add(word);
            var firstWord = app.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            Add(firstWord);
            if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                var trimmed = app.InstallLocation.TrimEnd('\\', '/');
                Add(Path.GetFileName(trimmed));
                var parent = Path.GetDirectoryName(trimmed);
                var parentName = parent is null ? null : Path.GetFileName(parent);
                if (parentName is not null && !parentName.StartsWith("Program", StringComparison.OrdinalIgnoreCase)
                    && !parentName.Equals("AppData", StringComparison.OrdinalIgnoreCase)
                    && !parentName.Equals("Local", StringComparison.OrdinalIgnoreCase)
                    && !parentName.Equals("Roaming", StringComparison.OrdinalIgnoreCase))
                    Add(parentName);
            }
            if (!string.IsNullOrEmpty(app.PackageFamilyName))
            {
                var identity = app.PackageFamilyName.Split('_')[0];
                Add(identity);
                foreach (var part in identity.Split('.')) Add(part);
            }
        }
        foreach (var p in processNames) Add(p);
        return tokens;
    }

    /// <summary>
    /// Whether a folder name belongs to something alive. Deliberately generous (fewer leftovers):
    /// exact match, an owner token containing the name, or the name containing a token of four
    /// characters or more. A vendor folder is alive when any child folder is.
    /// </summary>
    public static bool IsOwned(string folderName, IEnumerable<string> childNames, IReadOnlySet<string> tokens)
    {
        if (NameIsOwned(folderName, tokens)) return true;
        return childNames.Any(c => NameIsOwned(c, tokens));
    }

    public static bool NameIsOwned(string name, IReadOnlySet<string> tokens)
    {
        var n = InstalledAppRules.Normalize(name);
        if (n.Length < 3) return true; // unattributable: treat as owned
        if (tokens.Contains(n)) return true;
        foreach (var t in tokens)
        {
            if (t.Contains(n, StringComparison.Ordinal)) return true;
            if (t.Length >= 4 && n.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static bool IsIdleFor(DateTime newestWriteUtc, DateTime nowUtc, int days) =>
        nowUtc - newestWriteUtc >= TimeSpan.FromDays(days);

    // ---------------------------------------------------------------- screenshots

    [GeneratedRegex(@"^Screenshot (\d{4}-\d{2}-\d{2}) (\d{6})(?: \(\d+\))?\.(png|jpe?g)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotNameRegex();

    /// <summary>
    /// Whether a capture still carries the name the Snipping Tool gave it ("Screenshot
    /// 2026-10-14 142233.png", optionally with a collision index), with the capture day matching
    /// its creation date. A renamed file is a decision the user made, so it never counts.
    /// </summary>
    public static bool ScreenshotKeepsDefaultName(string fileName, DateTime createdLocal)
    {
        var m = ScreenshotNameRegex().Match(fileName);
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return false;
        var t = m.Groups[2].Value;
        if (int.Parse(t[..2], CultureInfo.InvariantCulture) > 23 || int.Parse(t[2..4], CultureInfo.InvariantCulture) > 59
            || int.Parse(t[4..], CultureInfo.InvariantCulture) > 59) return false;
        return day.Date == createdLocal.Date;
    }

    /// <summary>A capture is forgotten when nothing happened to it for <paramref name="days"/>:
    /// not taken, changed or opened since then.</summary>
    public static bool IsForgottenScreenshot(DateTime created, DateTime? modified, DateTime? lastUsed, DateTime now, int days)
    {
        if (days <= 0) return false;
        var latest = new[] { created, modified ?? created, lastUsed ?? created }.Max();
        return now - latest >= TimeSpan.FromDays(days);
    }

    // ----------------------------------------------------------------- startup

    /// <summary>
    /// Whether a Run entry's command points at an executable that is gone. Only absolute paths
    /// on fixed drives count: a bare command runs from PATH and a missing file on a removable or
    /// network drive is inconclusive (the drive may just be unplugged).
    /// </summary>
    public static bool IsOrphanedCommand(string? command, Func<string, bool> exists, Func<string, bool> isFixedDrive)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var (file, _) = InstalledAppRules.SplitCommandLine(Environment.ExpandEnvironmentVariables(command));
        if (string.IsNullOrWhiteSpace(file)) return false;
        if (!Path.IsPathFullyQualified(file)) return false;
        if (file.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var name = Path.GetFileName(file).ToLowerInvariant();
        if (name is "rundll32.exe" or "cmd.exe" or "powershell.exe" or "explorer.exe" or "wscript.exe" or "cscript.exe" or "msiexec.exe") return false;
        if (!isFixedDrive(file)) return false;
        return !exists(file);
    }

    /// <summary>Startup entries that belong to the system or this app are never offered.</summary>
    public static bool IsProtectedStartupName(string name)
    {
        var lowered = name.ToLowerInvariant();
        return lowered.StartsWith("fieldkit", StringComparison.Ordinal) || lowered.StartsWith("securityhealth", StringComparison.Ordinal)
               || lowered.StartsWith("windows", StringComparison.Ordinal) || lowered == "onedrive" || lowered == "desktop.ini";
    }

    // ------------------------------------------------------------------- temp

    public static bool TempIsOldEnough(DateTime newestWriteUtc, DateTime nowUtc, int days) =>
        nowUtc - newestWriteUtc >= TimeSpan.FromDays(SanitizedTempAgeDays(days));

    // ----------------------------------------------------------------- guards

    /// <summary>
    /// Last line of defense before anything is removed, even if a scanner bug produced a bad
    /// path: never a protected root or well-known folder, never something shallow (fewer than four
    /// segments below the drive root), never a fixture outside the scan roots.
    /// </summary>
    public static bool MayRemovePath(string path, IEnumerable<string> critical)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return false; }
        if (full.Length <= 3) return false;
        foreach (var c in critical)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (string.Equals(full, c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        }
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4;
    }

    /// <summary>The folders that can never be an item themselves.</summary>
    public static IEnumerable<string> CriticalPaths(CleanerPaths p)
    {
        yield return p.LocalAppData;
        yield return p.RoamingAppData;
        yield return p.LocalLow;
        yield return p.Temp;
        yield return p.UserProfile;
        yield return p.Screenshots;
        yield return p.WindowsDir;
        yield return Path.Combine(p.UserProfile, "AppData");
        yield return Path.Combine(p.UserProfile, "Downloads");
        yield return Path.Combine(p.UserProfile, "Documents");
        yield return Path.Combine(p.UserProfile, "Desktop");
        yield return Path.Combine(p.UserProfile, "Pictures");
        yield return Path.Combine(p.UserProfile, "Music");
        yield return Path.Combine(p.UserProfile, "Videos");
        yield return Path.Combine(p.LocalAppData, "Microsoft");
        yield return Path.Combine(p.RoamingAppData, "Microsoft");
        yield return Path.Combine(p.LocalAppData, "Packages");
        yield return Path.Combine(p.LocalAppData, "Programs");
    }

    // ----------------------------------------------------------- device backups

    /// <summary>Reads "Device Name" and "Last Backup Date" from a backup's XML Info.plist.</summary>
    public static (string? Device, DateTime? Date) ParseBackupInfo(string xml)
    {
        try
        {
            var dict = XDocument.Parse(xml).Root?.Element("dict");
            if (dict is null) return (null, null);
            string? device = null;
            DateTime? date = null;
            var elements = dict.Elements().ToList();
            for (var i = 0; i < elements.Count - 1; i++)
            {
                if (elements[i].Name != "key") continue;
                var value = elements[i + 1];
                switch (elements[i].Value)
                {
                    case "Device Name" when value.Name == "string":
                        device = value.Value;
                        break;
                    case "Last Backup Date" when value.Name == "date" &&
                        DateTime.TryParse(value.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d):
                        date = d;
                        break;
                }
            }
            return (device, date);
        }
        catch
        {
            return (null, null);
        }
    }

    // ------------------------------------------------------------------ text

    /// <summary>"C:\Users\me\AppData\Local\Foo" → "%LOCALAPPDATA%\Foo" style display, like upstream's "~".</summary>
    public static string PrettyPath(string path, CleanerPaths p)
    {
        (string Root, string Label)[] roots =
        [
            (p.Temp, "%TEMP%"), (p.LocalAppData, "%LOCALAPPDATA%"), (p.RoamingAppData, "%APPDATA%"), (p.UserProfile, "%USERPROFILE%"),
        ];
        foreach (var (root, label) in roots)
        {
            if (!string.IsNullOrEmpty(root) && ProtectedPaths.IsInside(path, root))
                return label + path[root.TrimEnd('\\').Length..];
        }
        return path;
    }

    /// <summary>"today at 9:00 AM", "tomorrow at …", "yesterday at …" or "Oct 20, 2026 at …".</summary>
    public static string RelativeDate(DateTime when, DateTime now)
    {
        var time = when.ToString("t", CultureInfo.CurrentCulture);
        var days = (when.Date - now.Date).Days;
        return days switch
        {
            0 => $"today at {time}",
            1 => $"tomorrow at {time}",
            -1 => $"yesterday at {time}",
            _ => $"{when.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} at {time}",
        };
    }
}
