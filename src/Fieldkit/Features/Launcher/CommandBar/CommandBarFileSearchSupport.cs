// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The rules behind finding a file from the bar (port of upstream CommandBarFileSearchSupport):
/// what is asked of Windows Search, which folders are searched and what never comes back. Pure.
/// Two rules are structural: hidden paths (dot-names or the Hidden attribute) and the inside of
/// AppData are never offered.
/// </summary>
public static class CommandBarFileSearchSupport
{
    /// <summary>Names almost never meant, shipped rather than stored.</summary>
    public static readonly string[] ShippedIgnores = ["node_modules", ".git", "obj", ".vs", "__pycache__", "vendor"];

    /// <summary>Below this a filename search says almost nothing.</summary>
    public const int ShortestQuery = 2;

    /// <summary>How many names Windows Search may hand back before filtering.</summary>
    public const int CandidateLimit = 1000;

    /// <summary>How many rows survive to be ranked.</summary>
    public const int ResultLimit = 200;

    /// <summary>The Windows Search SQL for a query, or null when there is nothing worth asking. Every
    /// word must appear in the file name, in any order.</summary>
    public static string? Sql(string query, IReadOnlyList<string> scopes)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || query.Trim().Length < ShortestQuery || scopes.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("SELECT TOP ").Append(CandidateLimit).Append(" System.ItemPathDisplay FROM SystemIndex WHERE (");
        for (var i = 0; i < scopes.Count; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append("SCOPE='file:").Append(SqlString(scopes[i].Replace('\\', '/'))).Append('\'');
        }
        sb.Append(')');
        foreach (var word in words) sb.Append(" AND System.FileName LIKE '%").Append(SqlString(LikeEscaped(word))).Append("%'");
        return sb.ToString();
    }

    /// <summary>Quotes are doubled inside a SQL string.</summary>
    public static string SqlString(string value) => value.Replace("'", "''");

    /// <summary>Wildcards the person typed are literal characters, never a wider search.</summary>
    public static string LikeEscaped(string word)
    {
        var sb = new StringBuilder();
        foreach (var c in word)
        {
            switch (c)
            {
                case '%': sb.Append("[%]"); break;
                case '_': sb.Append("[_]"); break;
                case '[': sb.Append("[[]"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Whether a file name contains every word (the fallback walk's own matcher).</summary>
    public static bool NameMatches(string fileName, string query)
    {
        var name = CommandBarSearch.Normalize(fileName);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(w => name.Contains(CommandBarSearch.Normalize(w), StringComparison.Ordinal));
    }

    /// <summary>The folders a saved list means. "~" is the profile; the profile itself expands into the
    /// folders inside it (never AppData or dot-folders). An empty list searches nothing.</summary>
    public static List<string> ResolvedScopes(IEnumerable<string> saved, string home, IEnumerable<string> homeChildren,
        Func<string, bool> isSearchableDirectory)
    {
        var standardizedHome = Standardized(home);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopes = new List<string>();
        var children = homeChildren.ToList();
        foreach (var raw in saved)
        {
            var path = Standardized(ExpandTilde(raw.Trim(), standardizedHome));
            if (path.Length == 0) continue;
            if (string.Equals(path, standardizedHome, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in children)
                {
                    if (child.Equals("AppData", StringComparison.OrdinalIgnoreCase) || child.StartsWith('.')) continue;
                    var expanded = Path.Combine(standardizedHome, child);
                    if (isSearchableDirectory(expanded) && seen.Add(expanded)) scopes.Add(expanded);
                }
                continue;
            }
            if (isSearchableDirectory(path) && seen.Add(path)) scopes.Add(path);
        }
        return scopes;
    }

    private static string ExpandTilde(string path, string home)
    {
        if (path == "~") return home;
        if (path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal)) return Path.Combine(home, path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string Standardized(string path)
    {
        if (path.Length == 0) return path;
        try
        {
            var full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Whether a path can be offered at all: no dot-named segment, nothing inside AppData,
    /// and no segment the caller says is hidden.</summary>
    public static bool IsOfferable(string path, Func<string, bool> isHidden)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts[^1].StartsWith('.')) return false;
        var ancestor = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            ancestor = i == 0 ? part + "\\" : Path.Combine(ancestor, part);
            if (i == 0) continue;
            if (part.StartsWith('.') || part.Equals("AppData", StringComparison.OrdinalIgnoreCase) || part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return false;
            if (isHidden(ancestor)) return false;
        }
        return true;
    }

    /// <summary>Whether a name the person never wants to see stands in this path: a whole folder or
    /// file name ("build" must not take out "rebuild-notes.md"), or an extension ("*.log", ".log").</summary>
    public static bool IsIgnored(string path, IEnumerable<string> patterns)
    {
        var components = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToLowerInvariant()).ToArray();
        if (components.Length == 0) return false;
        var name = components[^1];
        foreach (var pattern in patterns)
        {
            var wanted = pattern.Trim().ToLowerInvariant();
            if (wanted.Length == 0) continue;
            if (components.Contains(wanted)) return true;
            if (wanted.StartsWith("*.", StringComparison.Ordinal) && name.EndsWith(wanted[1..], StringComparison.Ordinal)) return true;
            if (wanted.StartsWith('.') && !wanted.Contains('\\') && !wanted.Contains('/') && name != wanted && name.EndsWith(wanted, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The paths worth showing, in order: found ones minus what is structurally out and what the
    /// person never wants to see, capped.</summary>
    public static List<string> Offerable(IEnumerable<string> paths, IEnumerable<string> patterns, Func<string, bool> isHidden)
    {
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool CachedHidden(string p)
        {
            if (cache.TryGetValue(p, out var v)) return v;
            return cache[p] = isHidden(p);
        }
        var all = patterns.Concat(ShippedIgnores).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!IsOfferable(path, CachedHidden) || IsIgnored(path, all) || !seen.Add(path)) continue;
            result.Add(path);
            if (result.Count >= ResultLimit) break;
        }
        return result;
    }

    /// <summary>One path or pattern per line, de-duplicated.</summary>
    public static List<string> DecodeList(string? raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (raw ?? string.Empty).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && seen.Add(s)).ToList();
    }

    public static string EncodeList(IEnumerable<string> items) => string.Join('\n', DecodeList(string.Join('\n', items)));

    /// <summary>A path written the short way ("~\Documents\x").</summary>
    public static string Abbreviating(string path, string home)
    {
        var h = home.TrimEnd('\\', '/');
        if (h.Length == 0) return path;
        if (string.Equals(path, h, StringComparison.OrdinalIgnoreCase)) return "~";
        return path.StartsWith(h + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[h.Length..] : path;
    }

    /// <summary>Stored with "~" for the profile so an exported list still points somewhere on another PC.</summary>
    public static string WithTilde(string path, string home) => Abbreviating(path.TrimEnd('\\', '/'), home);

    /// <summary>Only the latest query may refresh the visible bar.</summary>
    public static bool ShouldPublish(string query, string? currentQuery) => query == currentQuery;
}
