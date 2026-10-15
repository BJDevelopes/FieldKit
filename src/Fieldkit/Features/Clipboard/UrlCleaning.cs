// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Tracking-parameter removal for copied links, ported from upstream <c>URLCleaning</c>: a global
/// list (every <c>utm_*</c> plus click ids), per-site lists that only apply on that host and its
/// subdomains, and the user's additions / switched-off built-ins stored as a difference.
/// Never alters text that is not a single http(s) link.
/// </summary>
public static partial class UrlCleaning
{
    private static readonly HashSet<string> TrackedParameters = new(StringComparer.Ordinal)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "utm_id", "utm_name", "utm_reader", "utm_viz_id", "utm_pubreferrer",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "twclid", "ttclid", "li_fat_id",
        "mkt_tok", "_hsenc", "_hsmi", "__twitter_impression",
        "fb_action_ids", "fb_action_types", "fb_source", "mibextid",
    };

    /// <summary>
    /// Trackers only one site uses: <c>si</c> is a share token on YouTube but a real parameter
    /// elsewhere, <c>t</c> a tracker on X but the playback position on YouTube. Keys match the host
    /// itself or any subdomain of it.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> HostParameters = new(StringComparer.Ordinal)
    {
        ["youtube.com"] = ["si", "pp", "feature", "kw"],
        ["youtu.be"] = ["si", "pp", "feature", "kw"],
        ["twitter.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["x.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["instagram.com"] = ["igsh"],
        ["spotify.com"] = ["si"],
        ["reddit.com"] =
        [
            "correlation_id", "ref_campaign", "ref_source", "rdt", "share_id",
            "_branch_match_id", "$deep_link", "$3p", "$original_url",
        ],
        ["tiktok.com"] =
        [
            "u_code", "preview_pb", "_d", "_t", "_r", "timestamp", "user_id",
            "share_app_name", "share_iid",
        ],
        ["bilibili.com"] =
        [
            "spm_id_from", "from_spmid", "from_source", "share_source", "share_from",
            "share_medium", "share_plat", "share_tag", "share_session_id", "msource",
            "refer_from", "seid", "unique_k", "vd_source", "plat_id", "buvid", "bbid",
            "up_id", "is_story_h5", "timestamp", "ts", "visit_id", "session_id",
            "broadcast_type", "is_room_feed",
        ],
        ["xiaohongshu.com"] =
        [
            "xhsshare", "author_share", "xsec_source", "share_from_user_hidden",
            "shareredid", "share_id", "exsource", "app_version", "app_platform",
            "apptime", "appuid",
        ],
    };

    /// <summary>The key the global rules live under (a stored token reads "|ref" or "youtube.com|si").</summary>
    public const string AllSites = "";

    /// <summary>The single row standing for every <c>utm_</c> name (the cleaner matches the prefix).</summary>
    public const string UtmWildcard = "utm_*";

    /// <summary>A cleaned link and the names taken out of it.</summary>
    public sealed record Result(string Url, IReadOnlyList<string> Removed);

    /// <summary>What the user changed: names added and built-ins switched off, keyed by site (<see cref="AllSites"/> = global).</summary>
    public sealed record Rules(IReadOnlyDictionary<string, HashSet<string>> Added, IReadOnlyDictionary<string, HashSet<string>> Disabled)
    {
        public static Rules None { get; } = new(new Dictionary<string, HashSet<string>>(), new Dictionary<string, HashSet<string>>());
    }

    public sealed record RuleEntry(string Name, bool IsBuiltIn, bool IsEnabled);

    /// <summary>One site's rules as Settings shows them.</summary>
    public sealed record RuleGroup(string Site, IReadOnlyList<RuleEntry> Entries)
    {
        public int EnabledCount => Entries.Count(e => e.IsEnabled);
    }

    public enum OutcomeKind { NotAUrl, Unchanged, Rewritten, Removed }

    public sealed record Outcome(OutcomeKind Kind, IReadOnlyList<string> Removed);

    /// <summary>The global list as Settings shows it: the wildcard row, then the names the prefix does not cover.</summary>
    public static IReadOnlyList<string> GlobalBuiltInNames =>
        [UtmWildcard, .. TrackedParameters.Where(n => !n.StartsWith("utm_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>What a surface says after cleaning (Settings, the Clean URL tool).</summary>
    public static Outcome OutcomeFor(Result? result, string input)
    {
        if (result is null) return new Outcome(OutcomeKind.NotAUrl, []);
        if (result.Removed.Count > 0) return new Outcome(OutcomeKind.Removed, result.Removed);
        return new Outcome(result.Url == input.Trim() ? OutcomeKind.Unchanged : OutcomeKind.Rewritten, []);
    }

    public static string Message(Outcome outcome) => outcome.Kind switch
    {
        OutcomeKind.NotAUrl => "Paste a valid URL.",
        OutcomeKind.Unchanged => "Nothing to clean.",
        OutcomeKind.Rewritten => "URL cleaned.",
        _ => $"Removed {string.Join(", ", outcome.Removed)}",
    };

    /// <summary>Reads the three stored strings into one value.</summary>
    public static Rules RulesFrom(string? globalNames, string? siteNames, string? disabledNames)
    {
        var added = Tokens(siteNames);
        added[AllSites] = CustomParameters(globalNames);
        var addedFiltered = added.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var disabled = Tokens(disabledNames).Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new Rules(addedFiltered, disabled);
    }

    /// <summary>Removes tracking parameters. Null when <paramref name="text"/> is not one http(s) link.</summary>
    public static Result? Clean(string text, Rules? rules = null)
    {
        rules ??= Rules.None;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        if (!trimmed.Contains("://", StringComparison.Ordinal)) return null;

        // Slice the original text, so existing escapes are never re-encoded.
        var fragmentStart = trimmed.IndexOf('#');
        if (fragmentStart < 0) fragmentStart = trimmed.Length;
        var queryStart = trimmed.IndexOf('?', 0, fragmentStart);
        if (queryStart < 0) return new Result(trimmed, []);
        var query = trimmed[(queryStart + 1)..fragmentStart];
        var removed = new List<string>();
        var matcher = MatcherFor(uri.Host, rules);
        var kept = query.Split('&').Where(pair =>
        {
            var name = DecodedName(pair);
            if (!matcher(name)) return true;
            if (!removed.Contains(name)) removed.Add(name);
            return false;
        }).ToList();
        if (removed.Count == 0) return new Result(trimmed, []);
        var url = trimmed[..queryStart] + (kept.Count == 0 ? string.Empty : "?" + string.Join("&", kept)) + trimmed[fragmentStart..];
        return new Result(url, removed);
    }

    public static List<RuleGroup> RuleGroupsFor(Rules rules)
    {
        var sites = new SortedSet<string>(HostParameters.Keys.Concat(rules.Added.Keys).Concat(rules.Disabled.Keys), StringComparer.Ordinal);
        sites.Remove(AllSites);
        var groups = new List<RuleGroup> { Group(AllSites, GlobalBuiltInNames, rules) };
        groups.AddRange(sites.Select(site => Group(site,
            HostParameters.TryGetValue(site, out var names) ? names.Order(StringComparer.Ordinal).ToList() : [], rules)));
        return groups.Where(g => g.Entries.Count > 0).ToList();
    }

    /// <summary>Comma-separated names, the format the global custom list is stored in.</summary>
    public static HashSet<string> CustomParameters(string? stored) =>
        new(Split(stored).Select(s => s.ToLowerInvariant()), StringComparer.Ordinal);

    public static string StorageValue(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));

    /// <summary><c>host|name</c> tokens (site additions and switched-off built-ins).</summary>
    public static Dictionary<string, HashSet<string>> Tokens(string? stored)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var token in Split(stored))
        {
            var bar = token.IndexOf('|');
            if (bar < 0) continue;
            var name = token[(bar + 1)..].ToLowerInvariant();
            if (name.Length == 0) continue;
            var site = token[..bar].ToLowerInvariant();
            if (!map.TryGetValue(site, out var set)) map[site] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(name);
        }
        return map;
    }

    public static string StorageValue(IReadOnlyDictionary<string, HashSet<string>> map) =>
        string.Join(",", map.Keys.Order(StringComparer.Ordinal)
            .SelectMany(site => map[site].Order(StringComparer.Ordinal).Select(name => $"{site}|{name}")));

    /// <summary>A bare host, a www. host or a whole link → the host key rules are stored under; null when not a host.</summary>
    public static string? SiteKey(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) value = value[(scheme + 3)..];
        var end = value.IndexOfAny(['/', '?', '#']);
        if (end >= 0) value = value[..end];
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        if (!value.Contains('.') || value.StartsWith('.') || value.EndsWith('.')) return null;
        return value.All(c => !char.IsWhiteSpace(c) && c != '|' && c != ',') ? value : null;
    }

    /// <summary>A single query parameter name, or null when a query could not carry it as one name.</summary>
    public static string? ParameterName(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        return value.All(c => !char.IsWhiteSpace(c) && c is not ('|' or ',' or '&' or '=')) ? value : null;
    }

    /// <summary>
    /// The name half of one <c>name=value</c> pair, percent-decoded so <c>%75tm_source</c> and
    /// <c>%24deep_link</c> still match. A pair without '=' is a bare flag whose whole text is its name.
    /// </summary>
    private static string DecodedName(string pair)
    {
        var eq = pair.IndexOf('=');
        var raw = eq < 0 ? pair : pair[..eq];
        if (!raw.Contains('%')) return raw;
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }

    private static Func<string, bool> MatcherFor(string host, Rules rules)
    {
        var disabledGlobally = rules.Disabled.TryGetValue(AllSites, out var dg) ? dg : [];
        var names = new HashSet<string>(TrackedParameters.Where(n => !n.StartsWith("utm_", StringComparison.Ordinal) && !disabledGlobally.Contains(n)), StringComparer.Ordinal);
        if (rules.Added.TryGetValue(AllSites, out var addedGlobal)) names.UnionWith(addedGlobal.Where(n => !disabledGlobally.Contains(n)));
        foreach (var site in SiteKeysMatching(host, rules))
        {
            var disabled = rules.Disabled.TryGetValue(site, out var d) ? d : [];
            if (HostParameters.TryGetValue(site, out var builtIn)) names.UnionWith(builtIn.Where(n => !disabled.Contains(n)));
            if (rules.Added.TryGetValue(site, out var added)) names.UnionWith(added.Where(n => !disabled.Contains(n)));
        }
        var utm = !disabledGlobally.Contains(UtmWildcard);
        return name =>
        {
            var normalized = name.ToLowerInvariant();
            return names.Contains(normalized) || (utm && normalized.StartsWith("utm_", StringComparison.Ordinal));
        };
    }

    /// <summary>Site rules are additive: <c>open.spotify.com</c> picks up the <c>spotify.com</c> set.</summary>
    private static IEnumerable<string> SiteKeysMatching(string host, Rules rules)
    {
        var normalized = host.ToLowerInvariant();
        return HostParameters.Keys.Concat(rules.Added.Keys).Concat(rules.Disabled.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(k => k.Length > 0 && (normalized == k || normalized.EndsWith("." + k, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
    }

    private static RuleGroup Group(string site, IReadOnlyList<string> builtIn, Rules rules)
    {
        var disabled = rules.Disabled.TryGetValue(site, out var d) ? d : [];
        IEnumerable<string> added = rules.Added.TryGetValue(site, out var a) ? a.Where(n => !builtIn.Contains(n)).Order(StringComparer.Ordinal) : [];
        var entries = builtIn.Select(n => new RuleEntry(n, true, !disabled.Contains(n)))
            .Concat(added.Select(n => new RuleEntry(n, false, !disabled.Contains(n))))
            .ToList();
        return new RuleGroup(site, entries);
    }

    private static IEnumerable<string> Split(string? stored) =>
        (stored ?? string.Empty).Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    // ------------------------------------------------------ automatic rewrite

    /// <summary>Formats a plain link copy carries (text, URL names, locale) — a rewrite loses nothing they hold.</summary>
    private static readonly HashSet<string> PlainFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "CF_UNICODETEXT", "CF_TEXT", "CF_OEMTEXT", "CF_LOCALE",
        "UniformResourceLocator", "UniformResourceLocatorW", "text/x-moz-url", "text/uri-list",
        "HTML Format", "text/html", "Rich Text Format", "Rich Text Format Without Objects", "RTF As Text",
    };

    /// <summary>Content of its own: a picture, a file or a promise of one. A link next to it is only its fallback.</summary>
    private static readonly HashSet<string> ContentFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "CF_DIB", "CF_DIBV5", "CF_BITMAP", "CF_ENHMETAFILE", "CF_METAFILEPICT", "CF_HDROP", "CF_PALETTE",
        "PNG", "image/png", "JFIF", "GIF", "image/gif", "image/jpeg", "image/svg+xml", "TIFF",
        "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents", "FileName", "FileNameW",
        "Shell IDList Array", "Embed Source", "Object Descriptor", "Link Source", "Embedded Object",
    };

    /// <summary>
    /// Whether the clipboard can be replaced by the cleaned link without losing anything: every format is
    /// text, a link, formatting of the link or an app's private note; never a picture, a file, or a copy an
    /// app marked private (password managers).
    /// </summary>
    public static bool CanRewriteClipboard(IReadOnlyCollection<string> formatNames)
    {
        if (formatNames.Count == 0) return false;
        if (ClipboardPrivacy.IsPrivate(formatNames, null)) return false;
        foreach (var name in formatNames)
        {
            if (PlainFormats.Contains(name)) continue;
            if (ContentFormats.Contains(name)) return false;
            if (name.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;
            // Anything else is an app's private note about the copy (a browser's source page, …).
        }
        return true;
    }

    [GeneratedRegex(@"<(head|style|script|title)\b[^<>]*>[\s\S]*?(?:</\1\s*>|(?=<body\b)|\z)", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenHtml();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex("<[^<>]*>")]
    private static partial Regex Tag();

    /// <summary>
    /// The rewrite drops the HTML, which is only right when the HTML adds nothing to the link but
    /// formatting: every anchor leads to this link and there is no media. Markup without anchors that
    /// shows more than the link is left alone.
    /// </summary>
    public static bool HtmlAllowsRewrite(string html, string link)
    {
        if (html.Length > 64 * 1024) return false;
        var trimmedLink = link.Trim();
        var linkAddress = Address(trimmedLink);
        var shown = HiddenHtml().Replace(html, string.Empty);
        var targets = Href().Matches(shown)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            .Select(v => Address(v.Replace("&amp;", "&")))
            .ToList();
        var lower = html.ToLowerInvariant();
        string[] media = ["<img", "<video", "<audio", "<picture", "<svg", "<iframe", "<object", "<embed"];
        if (media.Any(lower.Contains)) return false;
        if (!targets.All(t => t == linkAddress)) return false;
        // Windows' "HTML Format" wraps the fragment in a header and comment markers; only the markup counts.
        var visible = Tag().Replace(StripCfHtmlHeader(shown).Replace("&amp;", "&"), string.Empty).Trim();
        return targets.Count > 0 || visible.Length == 0 || visible == trimmedLink;
    }

    private static string StripCfHtmlHeader(string html)
    {
        var start = html.IndexOf('<');
        var body = start >= 0 ? html[start..] : string.Empty;
        return body.Replace("<!--StartFragment-->", string.Empty).Replace("<!--EndFragment-->", string.Empty);
    }

    /// <summary>Links compare as addresses: scheme and host ignore case, an empty path is "/".</summary>
    private static string Address(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return value.Trim();
        var path = uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath;
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}{uri.Query}{uri.Fragment}";
    }
}
