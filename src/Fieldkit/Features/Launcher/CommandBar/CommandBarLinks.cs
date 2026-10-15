// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>What a saved shortcut is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandBarLinkKind>))]
public enum CommandBarLinkKind
{
    /// <summary>Anything with a scheme: a site, or a link another app answers to.</summary>
    Link,
    /// <summary>A folder or a file on this PC.</summary>
    Place,
    /// <summary>A PowerShell, cmd or executable file run with what follows the name as its argument.</summary>
    Script,
}

/// <summary>
/// A place the person goes to often, saved under a name of their choosing: a site, a folder, a
/// file, a search that takes what is typed after the name, or a script.
/// </summary>
public sealed class CommandBarLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public CommandBarLinkKind Kind { get; set; } = CommandBarLinkKind.Link;
    /// <summary>The destination, placeholders still in it.</summary>
    public string Destination { get; set; } = string.Empty;
    /// <summary>Whether the bare name is enough to run a script.</summary>
    public bool RunsWithoutArgument { get; set; }
    /// <summary>Whether the script's own global shortcut runs it with nothing on screen.</summary>
    public bool RunsDirectly { get; set; }

    [JsonIgnore] public bool TakesQuery => Destination.Contains(CommandBarLinks.QueryToken, StringComparison.Ordinal);

    /// <summary>Reads what follows its name: every query link and every script.</summary>
    [JsonIgnore] public bool TakesArgument => Kind == CommandBarLinkKind.Script || TakesQuery;

    public CommandBarLink Clone() => (CommandBarLink)MemberwiseClone();
}

/// <summary>Reading, writing and filling in what the person saved (port of upstream CommandBarLinks). Pure.</summary>
public static partial class CommandBarLinks
{
    public const int Limit = 60;
    public const string QueryToken = "{query}";
    public const string ClipboardToken = "{clipboard}";
    public const string SelectionToken = "{selection}";
    public const string DateToken = "{date}";

    public static readonly string[] Placeholders = [QueryToken, ClipboardToken, SelectionToken, DateToken];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<CommandBarLink> Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var links = JsonSerializer.Deserialize<List<CommandBarLink>>(raw, Json) ?? [];
            return links.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name) && !string.IsNullOrWhiteSpace(l.Destination)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Encode(IEnumerable<CommandBarLink> links) => JsonSerializer.Serialize(links.Take(Limit).ToList(), Json);

    /// <summary>The destination with every placeholder filled in. Values going into a web address
    /// are escaped; values going into a path are not.</summary>
    public static string Expand(string destination, CommandBarLinkKind kind, string query = "", string clipboard = "",
        string selection = "", string date = "")
    {
        var result = destination;
        foreach (var (token, raw) in new[] { (QueryToken, query), (ClipboardToken, clipboard), (SelectionToken, selection), (DateToken, date) })
        {
            if (!result.Contains(token, StringComparison.Ordinal)) continue;
            result = result.Replace(token, kind == CommandBarLinkKind.Link ? Escaped(raw) : raw, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Everything that is not unreserved is escaped, including "+" and "&amp;".</summary>
    public static string Escaped(string value) => Uri.EscapeDataString(value);

    /// <summary>Where a saved place sits on the disk (only a finished path without placeholders).</summary>
    public static string? RevealPath(CommandBarLink link)
    {
        if (link.Kind != CommandBarLinkKind.Place) return null;
        var trimmed = link.Destination.Trim();
        if (trimmed.Length == 0 || Placeholders.Any(p => trimmed.Contains(p, StringComparison.Ordinal))) return null;
        return ExpandHome(trimmed);
    }

    /// <summary>"~" and %VARS% expand to real paths.</summary>
    public static string ExpandHome(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path);
        if (p == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p.StartsWith("~\\", StringComparison.Ordinal) || p.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);
        return p;
    }

    /// <summary>What the person typed after the link's name, matched whole and case-insensitively
    /// ("gh fieldkit" finds "gh", "ghost" does not). Null when the query does not name it.</summary>
    public static string? TrailingArgument(string query, string name)
    {
        var normalizedName = CommandBarSearch.Normalize(name);
        if (normalizedName.Length == 0) return null;
        var normalizedQuery = CommandBarSearch.Normalize(query);
        if (!normalizedQuery.StartsWith(normalizedName + " ", StringComparison.Ordinal)) return null;
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var nameWords = normalizedName.Split(' ').Length;
        if (words.Length <= nameWords) return null;
        return string.Join(' ', words.Skip(nameWords));
    }

    /// <summary>What a link's row is scored against: everything typed once an argument follows its name.</summary>
    public static string RankingTitle(string name, string query) => TrailingArgument(query, name) is not null ? query : name;

    /// <summary>What a script would run with for this query (bare name only when marked so).</summary>
    public static string? ScriptArgument(CommandBarLink link, string query)
    {
        if (TrailingArgument(query, link.Name) is { } trailing) return trailing;
        if (!link.RunsWithoutArgument) return null;
        var normalizedName = CommandBarSearch.Normalize(link.Name);
        return normalizedName.Length > 0 && CommandBarSearch.Normalize(query) == normalizedName ? string.Empty : null;
    }

    /// <summary>The script a row shortcut should run with nothing on screen, if it is one.</summary>
    public static CommandBarLink? DirectRunScript(string stableKey, IEnumerable<CommandBarLink> links)
    {
        const string prefix = "link.";
        if (!stableKey.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(stableKey[prefix.Length..], out var id)) return null;
        var link = links.FirstOrDefault(l => l.Id == id);
        return link is { Kind: CommandBarLinkKind.Script, RunsDirectly: true } ? link : null;
    }

    public static List<CommandBarLink> MatchingScriptLinks(IEnumerable<CommandBarLink> links, string query) =>
        links.Where(l => l.Kind == CommandBarLinkKind.Script && ScriptArgument(l, query) is not null).ToList();

    /// <summary>The most specific script the query names (a longer name wins over its prefix).</summary>
    public static (CommandBarLink Link, string Argument)? MatchingScriptLink(IEnumerable<CommandBarLink> links, string query)
    {
        (CommandBarLink Link, string Argument, int Length)? best = null;
        foreach (var link in MatchingScriptLinks(links, query))
        {
            var argument = ScriptArgument(link, query);
            if (argument is null) continue;
            var length = CommandBarSearch.Normalize(link.Name).Length;
            if (best is not null && length <= best.Value.Length) continue;
            best = (link, argument, length);
        }
        return best is null ? null : (best.Value.Link, best.Value.Argument);
    }

    /// <summary>What a script printed, trimmed; empty output means nothing to show.</summary>
    public static string? ResultText(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The address a link opens. A destination without a scheme is treated as a site.</summary>
    public static Uri? Url(CommandBarLink link, string expanded)
    {
        var trimmed = expanded.Trim();
        if (trimmed.Length == 0) return null;
        switch (link.Kind)
        {
            case CommandBarLinkKind.Place:
                return Uri.TryCreate(ExpandHome(trimmed), UriKind.Absolute, out var file) ? file : null;
            case CommandBarLinkKind.Link:
                if (SchemeRegex().IsMatch(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return url;
                return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var site) ? site : null;
            default:
                return null;
        }
    }

    /// <summary>Common top-level domains a bare address may end in. Kept short on purpose so file
    /// names ("notes.md", "build.sh") stay searches.</summary>
    private static readonly HashSet<string> KnownTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "io", "dev", "app", "co", "ai", "edu", "gov", "me", "info", "biz", "xyz", "tech", "site", "online",
        "blog", "news", "store", "shop", "cloud", "page", "tv", "gg", "uk", "de", "fr", "br", "pt", "es", "it", "nl", "eu", "us",
        "ca", "au", "jp", "cn", "ru", "in", "ch", "se", "no", "fi", "dk", "pl", "cz", "at", "be", "ie", "nz", "mx", "ar", "kr",
        "tw", "hk", "sg", "za", "tr", "ua", "gr", "ro", "hu", "sk", "il",
    };

    /// <summary>A web address typed on its own, or null when the text should remain a search.</summary>
    public static Uri? TypedUrl(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return null;
        // An email address is not a site.
        if (trimmed.Contains('@') && !trimmed.Contains("://", StringComparison.Ordinal)) return null;
        Uri? url;
        if (ExplicitHttpRegex().IsMatch(trimmed))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out url)) return null;
        }
        else
        {
            var match = BareHostRegex().Match(trimmed);
            if (!match.Success || !KnownTlds.Contains(match.Groups["tld"].Value)) return null;
            if (!Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out url)) return null;
        }
        if (url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host)) return null;
        return url;
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^https?://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitHttpRegex();

    [GeneratedRegex(@"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+(?<tld>[a-zA-Z]{2,24})(?::\d{1,5})?(?:[/?#]\S*)?$")]
    private static partial Regex BareHostRegex();
}
