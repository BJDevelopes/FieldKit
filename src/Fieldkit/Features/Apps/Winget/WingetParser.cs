// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>Identity and classification rules shared by every package list.</summary>
public static class WingetPackageRules
{
    public const string WingetSource = "winget";
    public const string StoreSource = "msstore";

    public static WingetOrigin OriginOf(string id, string source)
    {
        if (string.Equals(source, WingetSource, StringComparison.OrdinalIgnoreCase)) return WingetOrigin.Winget;
        if (string.Equals(source, StoreSource, StringComparison.OrdinalIgnoreCase)) return WingetOrigin.Store;
        if (source.Length > 0) return WingetOrigin.OtherSource;
        return WingetOrigin.Local;
    }

    /// <summary>Entries winget only knows from Add/Remove Programs or the package store.</summary>
    public static bool IsLocalId(string id) =>
        id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase);

    public static string Key(string id, string source) => id.ToLowerInvariant();

    /// <summary>A version string with no digit at all ("Unknown", "Unbekannt", "latest").</summary>
    public static bool IsUnknownVersion(string version) => !version.Any(char.IsDigit);
}

/// <summary>Turns winget's console output into packages, details, sources and pins.</summary>
public static partial class WingetParser
{
    /// <summary>Exit code for "No package found matching input criteria" (0x8A150014).</summary>
    public const int NoPackagesFound = unchecked((int)0x8A150014);

    private static readonly string[] AvailableTitles =
        ["available", "verfügbar", "disponible", "disponibile", "beschikbaar", "dostępne", "disponível", "доступно",
         "доступна", "利用可能", "可用", "사용 가능", "dostupné", "kullanılabilir", "доступні"];

    private static readonly string[] SourceTitles =
        ["source", "quelle", "origen", "fonte", "bron", "źródło", "источник", "ソース", "源", "원본", "zdroj", "kaynak", "джерело"];

    private static readonly string[] MatchTitles =
        ["match", "übereinstimmung", "correspondance", "coincidencia", "corrispondenza", "overeenkomst", "dopasowanie",
         "correspondência", "совпадение", "一致", "匹配", "일치", "zhoda", "eşleşme", "збіг"];

    private static readonly HashSet<string> WellKnownSources = new(StringComparer.OrdinalIgnoreCase)
    {
        WingetPackageRules.WingetSource, WingetPackageRules.StoreSource, "winget-font",
    };

    /// <summary>Packages of the first table (<c>list</c>, <c>search</c>, <c>upgrade</c>).</summary>
    /// <param name="knownSources">Configured source names (helps when a column title is not recognised).</param>
    public static List<WingetPackage> ParsePackages(string output, IEnumerable<string>? knownSources = null)
    {
        var tables = WingetTableParser.Parse(output, maxColumns: 5, requiredCells: 2);
        return tables.Tables.Count == 0 ? [] : Packages(tables.Tables[0], knownSources);
    }

    /// <summary>Parses <c>winget upgrade</c> (listing form): updatable packages, pinned ones that
    /// need explicit targeting, and the counts from the footer hints.</summary>
    public static WingetUpgradeList ParseUpgrade(string output, IEnumerable<string>? knownSources = null)
    {
        var tables = WingetTableParser.Parse(output, maxColumns: 5, requiredCells: 2);
        if (tables.Tables.Count == 0 && tables.Messages.Count == 0) return WingetUpgradeList.Empty;
        var packages = tables.Tables.Count > 0 ? Packages(tables.Tables[0], knownSources) : [];
        var explicitOnly = tables.Tables.Skip(1)
            .SelectMany(t => Packages(t, knownSources))
            .Select(p => p with { IsPinned = true })
            .ToList();
        var unknown = 0;
        var pinned = 0;
        foreach (var message in tables.Messages)
        {
            if (message.Contains("--include-unknown", StringComparison.OrdinalIgnoreCase)) unknown = FirstNumber(message) ?? unknown;
            else if (message.Contains("--include-pinned", StringComparison.OrdinalIgnoreCase)) pinned = FirstNumber(message) ?? pinned;
        }
        return new WingetUpgradeList(packages, explicitOnly, unknown, pinned);
    }

    private static List<WingetPackage> Packages(WingetTable table, IEnumerable<string>? knownSources)
    {
        if (table.Headers.Count < 2) return [];
        var sources = new HashSet<string>(WellKnownSources, StringComparer.OrdinalIgnoreCase);
        if (knownSources is not null) sources.UnionWith(knownSources);
        var roles = ColumnRoles(table, sources);
        var result = new List<WingetPackage>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            string Cell(ColumnRole role)
            {
                var index = Array.IndexOf(roles, role);
                return index >= 0 && index < row.Count ? row[index] : string.Empty;
            }
            var name = Cell(ColumnRole.Name);
            var id = Cell(ColumnRole.Id);
            if (name.Length == 0 || id.Length == 0) continue;
            result.Add(new WingetPackage
            {
                Name = name,
                Id = id,
                Version = Cell(ColumnRole.Version),
                Available = Cell(ColumnRole.Available),
                Source = Cell(ColumnRole.Source),
                Match = Cell(ColumnRole.Match),
                Truncated = name.EndsWith('…') || id.EndsWith('…'),
            });
        }
        return result;
    }

    private enum ColumnRole { Name, Id, Version, Available, Source, Match, Unknown }

    /// <summary>Name, Id and Version are always the first three columns; the optional ones are
    /// told apart by their (localized) title, or by their values when the title is unknown.</summary>
    private static ColumnRole[] ColumnRoles(WingetTable table, HashSet<string> sources)
    {
        var roles = new ColumnRole[table.Headers.Count];
        for (var k = 0; k < roles.Length; k++)
        {
            roles[k] = k switch
            {
                0 => ColumnRole.Name,
                1 => ColumnRole.Id,
                2 => ColumnRole.Version,
                _ => ByTitle(table.Headers[k]),
            };
        }
        for (var k = 3; k < roles.Length; k++)
        {
            if (roles[k] != ColumnRole.Unknown) continue;
            var values = table.Rows.Select(r => k < r.Count ? r[k] : string.Empty).Where(v => v.Length > 0).ToList();
            if (k == roles.Length - 1 && (values.Count == 0 || values.All(sources.Contains)) && !roles.Contains(ColumnRole.Source))
                roles[k] = ColumnRole.Source;
            else if (values.Count > 0 && values.All(v => v.Contains(": ")))
                roles[k] = ColumnRole.Match;
            else if (values.Count > 0 && values.All(v => v.Any(char.IsDigit)))
                roles[k] = ColumnRole.Available;
        }
        return roles;
    }

    private static ColumnRole ByTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        if (AvailableTitles.Contains(t)) return ColumnRole.Available;
        if (SourceTitles.Contains(t)) return ColumnRole.Source;
        if (MatchTitles.Contains(t)) return ColumnRole.Match;
        return ColumnRole.Unknown;
    }

    private static int? FirstNumber(string text)
    {
        var m = NumberRegex().Match(text);
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\S.*?\s(?<name>.+?)\s\[(?<id>[^\]\s]+)\]\s*$")]
    private static partial Regex FoundRegex();

    /// <summary>Parses <c>winget show</c>. Returns null when no package header is present
    /// (e.g. "No package found matching input criteria.").</summary>
    public static WingetDetails? ParseShow(string output)
    {
        var lines = WingetText.CleanLines(output);
        var start = -1;
        Match? found = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(' ')) continue;
            var m = FoundRegex().Match(lines[i]);
            if (m.Success)
            {
                start = i;
                found = m;
                break;
            }
        }
        if (found is null) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            var indented = line.StartsWith(' ');
            var text = line.Trim();
            if (!indented)
            {
                var colon = text.IndexOf(':');
                if (colon <= 0)
                {
                    section = null;
                    continue;
                }
                var key = text[..colon].Trim();
                var value = text[(colon + 1)..].Trim();
                fields[key] = value;
                section = value.Length == 0 ? key : null;
                if (section is not null) lists[section] = [];
                continue;
            }
            if (section is null) continue;
            lists[section].Add(text);
            // Nested "Key: value" pairs (Installer:, Documentation:) are also addressable.
            var nestedColon = text.IndexOf(": ", StringComparison.Ordinal);
            if (nestedColon > 0 && !text.StartsWith("- ", StringComparison.Ordinal))
            {
                var nestedKey = text[..nestedColon].Trim();
                if (!fields.ContainsKey(nestedKey)) fields[nestedKey] = text[(nestedColon + 2)..].Trim();
            }
        }

        string Field(string key) => fields.TryGetValue(key, out var v) ? v : string.Empty;
        string Block(string key)
        {
            var single = Field(key);
            if (single.Length > 0) return single;
            return lists.TryGetValue(key, out var l) ? string.Join("\n", l) : string.Empty;
        }

        return new WingetDetails
        {
            Name = found.Groups["name"].Value.Trim(),
            Id = found.Groups["id"].Value.Trim(),
            Version = Field("Version"),
            Publisher = Field("Publisher"),
            Description = Block("Description"),
            Homepage = Field("Homepage"),
            License = Field("License"),
            LicenseUrl = Field("License Url"),
            ReleaseNotesUrl = Field("Release Notes Url"),
            Moniker = Field("Moniker"),
            InstallerType = Field("Installer Type"),
            Tags = lists.TryGetValue("Tags", out var tags) ? tags : [],
            Fields = fields,
        };
    }

    /// <summary>Parses <c>winget source list</c>.</summary>
    public static List<WingetSource> ParseSources(string output)
    {
        var tables = WingetTableParser.Parse(output, maxColumns: 3, requiredCells: 2);
        if (tables.Tables.Count == 0) return [];
        var table = tables.Tables[0];
        return table.Rows
            .Where(r => r.Count >= 2 && r[0].Length > 0)
            .Select(r => new WingetSource(r[0], r[1], r.Count > 2 && bool.TryParse(r[2], out var b) ? b : null))
            .ToList();
    }

    /// <summary>Parses <c>winget pin list</c> ("There are no pins configured." gives an empty list).</summary>
    public static List<WingetPin> ParsePins(string output)
    {
        var tables = WingetTableParser.Parse(output, maxColumns: 5, requiredCells: 2);
        if (tables.Tables.Count == 0) return [];
        return tables.Tables[0].Rows
            .Where(r => r.Count >= 2)
            .Select(r => new WingetPin(r[0], r[1], r.Count > 2 ? r[2] : string.Empty, r.Count > 3 ? r[3] : string.Empty,
                r.Count > 4 ? r[4] : string.Empty))
            .ToList();
    }
}
