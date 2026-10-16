// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fieldkit.Core.Updates;

/// <summary>A release version as <c>major.minor.patch[-prerelease]</c>, ordered the way semantic versioning orders them.</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string Pre = "") : IComparable<AppVersion>
{
    private static readonly Regex Shape = new(
        @"^\s*v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:\.\d+)?(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.\-]*))?(?:\+[0-9A-Za-z.\-]+)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsPrerelease => Pre.Length > 0;

    /// <summary>Reads "0.4.0", "v0.4.0", "0.4.0+abc123", "0.5.0-beta.1" (and "0.4" as 0.4.0).</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = new AppVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Shape.Match(text);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["major"].Value, out var major) || !int.TryParse(m.Groups["minor"].Value, out var minor)) return false;
        var patch = 0;
        if (m.Groups["patch"].Success && !int.TryParse(m.Groups["patch"].Value, out patch)) return false;
        version = new AppVersion(major, minor, patch, m.Groups["pre"].Success ? m.Groups["pre"].Value : string.Empty);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A pre-release comes before the release it leads up to.
        if (Pre.Length == 0 && other.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (other.Pre.Length == 0) return -1;
        return ComparePre(Pre, other.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi);
            var yn = int.TryParse(y[i], out var yi);
            int c;
            if (xn && yn) c = xi.CompareTo(yi);
            else if (xn) c = -1; // numbers come before words
            else if (yn) c = 1;
            else c = string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre.Length > 0 ? "-" + Pre : string.Empty)}";
}

/// <summary>A file attached to a release. <see cref="Sha256"/> is the lowercase hex digest GitHub computed, when it has one.</summary>
public sealed record UpdateAsset(string Name, string Url, long Size, string? Sha256);

public sealed record UpdateRelease(AppVersion Version, string Tag, string PageUrl, string Notes, bool Draft, bool Prerelease, IReadOnlyList<UpdateAsset> Assets);

public enum InstallKind { Installed, Portable }

/// <summary>What the updater decides, in plain static code: which release is newer, which file to fetch, what to trust.</summary>
public static class UpdateLogic
{
    public const string Owner = "BJDevelopes";
    public const string Repository = "FieldKit";
    public const string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=10";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repository}/releases/latest";

    /// <summary>An installer is about 50 MB; anything far beyond that is not one of ours.</summary>
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    public const int MaxApiBytes = 2 * 1024 * 1024;

    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    // ------------------------------------------------------------- reading releases

    /// <summary>Parses GitHub's "list releases" answer. Releases without a readable version are left out.</summary>
    public static List<UpdateRelease> ParseReleases(string json)
    {
        var result = new List<UpdateRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var tag = Text(item, "tag_name");
                if (!AppVersion.TryParse(tag, out var version)) continue;
                var assets = new List<UpdateAsset>();
                if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in list.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object) continue;
                        var name = Text(a, "name");
                        var url = Text(a, "browser_download_url");
                        if (name.Length == 0 || url.Length == 0) continue;
                        var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                        assets.Add(new UpdateAsset(name, url, size, NormalisedSha256(Text(a, "digest"))));
                    }
                }
                result.Add(new UpdateRelease(version, tag, Text(item, "html_url"), Text(item, "body"),
                    Flag(item, "draft"), Flag(item, "prerelease"), assets));
            }
        }
        catch (JsonException)
        {
            // an answer that is not JSON gives no releases
        }
        return result;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The newest release that is newer than <paramref name="current"/>. Drafts are never offered, and neither are
    /// versions with a pre-release suffix (0.5.0-beta.1) unless asked for. (GitHub flags every 0.x release a
    /// "pre-release"; that flag is not used.)
    /// </summary>
    public static UpdateRelease? Newest(IEnumerable<UpdateRelease> releases, AppVersion current, bool includeBeta = false) =>
        releases
            .Where(r => !r.Draft && (includeBeta || !r.Version.IsPrerelease) && r.Version > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    // ------------------------------------------------------------- what to download

    public static string? ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    public static string InstallerName(AppVersion version, string architecture) => $"Fieldkit-{version}-setup-{architecture}.exe";

    /// <summary>The installer asset for this PC, or null when the release has none (or this architecture has no installer).</summary>
    public static UpdateAsset? InstallerFor(UpdateRelease release, Architecture architecture)
    {
        if (ArchitectureName(architecture) is not { } arch) return null;
        var name = InstallerName(release.Version, arch);
        return release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"sha256:ABC…" as lowercase hex, or null when it is not a SHA-256 digest.</summary>
    public static string? NormalisedSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>Only the release file of this project, over HTTPS, is ever fetched to be run.</summary>
    public static bool IsExpectedAssetUrl(string? url, string tag, string fileName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Query.Length > 0 || uri.UserInfo.Length > 0) return false;
        var expected = $"/{Owner}/{Repository}/releases/download/{tag}/{fileName}";
        return string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where GitHub may send the download on (its release storage), still only over HTTPS.</summary>
    public static bool IsTrustedDownloadHost(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- this installation

    /// <summary>An installed copy has the uninstaller beside its exe; anything else is the portable one.</summary>
    public static InstallKind DetectInstall(string executableDirectory, Func<string, bool> fileExists) =>
        fileExists(Path.Combine(executableDirectory, "unins000.exe")) ? InstallKind.Installed : InstallKind.Portable;

    /// <summary>Whether the install lives under Program Files (installed for every user, so the installer must elevate).</summary>
    public static bool IsAllUsersInstall(string executableDirectory, IEnumerable<string?> programFolders)
    {
        var dir = Path.GetFullPath(executableDirectory).TrimEnd('\\') + "\\";
        return programFolders.Where(p => !string.IsNullOrEmpty(p))
            .Any(p => dir.StartsWith(Path.GetFullPath(p!).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the installer with a progress window but no questions, over the existing install, and starts Fieldkit
    /// again afterwards (<c>/RELAUNCH=1</c> is read by the installer script).
    /// </summary>
    public static string InstallerArguments(bool allUsers) =>
        $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 {(allUsers ? "/ALLUSERS" : "/CURRENTUSER")}";

    // ------------------------------------------------------------- when to look

    /// <summary>Asks GitHub at most every <see cref="CheckEvery"/>; a clock that moved back counts as due.</summary>
    public static bool NeedsNetworkCheck(DateTime nowUtc, DateTime? lastCheckUtc) =>
        lastCheckUtc is not { } last || last > nowUtc || nowUtc - last >= CheckEvery;

    // ------------------------------------------------------------- what to show

    /// <summary>The release notes as a few plain lines for the prompt: markdown marks and GitHub's compare link removed.</summary>
    public static string NotesExcerpt(string? notes, int maxLines = 10, int maxChars = 900)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        var lines = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase)) continue;
            // A bullet that wraps onto an indented line is still one point.
            var continues = raw.StartsWith("  ") && lines.Count > 0 && !line.StartsWith("- ") && !line.StartsWith("* ");
            line = line.TrimStart('#', ' ');
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..].Trim();
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            if (line.Length == 0) continue;
            if (continues) lines[^1] += " " + line;
            else lines.Add(line);
        }
        var sb = new StringBuilder();
        foreach (var line in lines.Take(maxLines))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        var text = sb.ToString();
        if (text.Length > maxChars) text = text[..maxChars].TrimEnd() + "…";
        else if (lines.Count > maxLines) text += "\n…";
        return text;
    }
}
