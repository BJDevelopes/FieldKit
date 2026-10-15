// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>
/// Builds winget command lines. Every command runs with <c>--disable-interactivity</c> so winget
/// never waits for keyboard input that cannot come; agreement flags are only added once the
/// person accepted them in Fieldkit (see <see cref="WingetConsent"/>).
/// </summary>
public static class WingetCommands
{
    public const string DisableInteractivity = "--disable-interactivity";
    public const string AcceptSource = "--accept-source-agreements";
    public const string AcceptPackage = "--accept-package-agreements";

    /// <summary>APPINSTALLER_CLI_ERROR_COMMAND_REQUIRES_ADMIN.</summary>
    public const int RequiresAdmin = unchecked((int)0x8A150019);
    /// <summary>APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE.</summary>
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    /// <summary>APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED.</summary>
    public const int AlreadyInstalled = unchecked((int)0x8A150061);

    /// <summary>App Installer (which ships winget) in the Microsoft Store.</summary>
    public const string AppInstallerStoreUri = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";
    public const string AppInstallerWebUrl = "https://aka.ms/getwinget";
    public const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";
    public const string WingetLicenseUrl = "https://aka.ms/winget-license";
    public const string StoreTermsUrl = "https://www.microsoft.com/en-us/storedocs/terms-of-sale";

    /// <summary>Package ids and source names that are safe to put on a command line:
    /// no quotes or control characters and no leading dash (never read as an option).</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512) return false;
        if (id.StartsWith('-') || id.EndsWith('…')) return false;
        return !id.Any(c => c == '"' || char.IsControl(c));
    }

    public static bool IsValidQuery(string? query) =>
        !string.IsNullOrWhiteSpace(query) && query.Trim().Length <= 200 && !query.Any(char.IsControl);

    /// <summary>Quotes one argument for CommandLineToArgvW (backslashes before a quote are doubled).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    private static IEnumerable<string> Common(bool acceptSource)
    {
        yield return DisableInteractivity;
        if (acceptSource) yield return AcceptSource;
    }

    public static List<string> List(bool acceptSource) => ["list", .. Common(acceptSource)];

    public static List<string> PinList() => ["pin", "list", DisableInteractivity];

    public static List<string> SourceList() => ["source", "list", DisableInteractivity];

    /// <summary>The upgrade listing (never upgrades: no package and no --all).</summary>
    public static List<string> UpgradeList(bool includeUnknown, bool acceptSource)
    {
        List<string> args = ["upgrade", .. Common(acceptSource)];
        if (includeUnknown) args.Add("--include-unknown");
        return args;
    }

    public static List<string>? Search(string query, string? source, bool acceptSource)
    {
        if (!IsValidQuery(query)) return null;
        List<string> args = ["search", "--query", query.Trim()];
        if (!string.IsNullOrEmpty(source))
        {
            if (!IsValidId(source)) return null;
            args.AddRange(["--source", source]);
        }
        args.AddRange(Common(acceptSource));
        return args;
    }

    public static List<string>? Show(string id, string source, bool acceptSource)
    {
        if (!IsValidId(id) || source.Length == 0 || !IsValidId(source)) return null;
        return ["show", "--id", id, "--exact", "--source", source, .. Common(acceptSource)];
    }

    public static List<string>? Install(string id, string source)
    {
        if (!IsValidId(id) || source.Length == 0 || !IsValidId(source)) return null;
        return ["install", "--id", id, "--exact", "--source", source, DisableInteractivity, AcceptSource, AcceptPackage];
    }

    public static List<string>? Upgrade(string id, string source)
    {
        if (!IsValidId(id)) return null;
        List<string> args = ["upgrade", "--id", id, "--exact"];
        if (source.Length > 0)
        {
            if (!IsValidId(source)) return null;
            args.AddRange(["--source", source]);
        }
        args.AddRange([DisableInteractivity, AcceptSource, AcceptPackage]);
        return args;
    }

    public static List<string> UpgradeAll() => ["upgrade", "--all", DisableInteractivity, AcceptSource, AcceptPackage];

    /// <summary>Uninstalls one package. Local entries (ARP\…, MSIX\…) have no source.</summary>
    public static List<string>? Uninstall(string id, string source)
    {
        if (!IsValidId(id)) return null;
        List<string> args = ["uninstall", "--id", id, "--exact"];
        if (source.Length > 0)
        {
            if (!IsValidId(source)) return null;
            args.AddRange(["--source", source]);
        }
        args.AddRange([DisableInteractivity, AcceptSource]);
        return args;
    }

    public static List<string> UpdateSources() => ["source", "update", DisableInteractivity];

    /// <summary>The same command for a person to run in a console, where winget may ask questions.</summary>
    public static string TerminalCommand(IEnumerable<string> args) =>
        "winget " + Join(args.Where(a => a != DisableInteractivity));

    /// <summary>A short explanation for well-known winget exit codes, or the hex code.</summary>
    public static string DescribeExitCode(int code) => code switch
    {
        0 => string.Empty,
        WingetParser.NoPackagesFound => "No package found matching input criteria.",
        RequiresAdmin => "This operation needs administrator rights.",
        UpdateNotApplicable => "No applicable update found.",
        AlreadyInstalled => "The package is already installed.",
        -1 => "winget stopped before it finished.",
        _ => $"winget exited with code 0x{code:X8}.",
    };

    /// <summary>The winget executable (the App Installer execution alias), or null when App Installer is missing.</summary>
    public static string? FindExecutable()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(alias)) return alias;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "winget.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // malformed PATH entry
                }
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }
}
