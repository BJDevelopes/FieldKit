// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>One row of the Packages tool: an installed package, a search result, or both.</summary>
public sealed record PackageItem
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Source { get; init; } = string.Empty;
    /// <summary>Installed version; null when the package is not installed.</summary>
    public string? InstalledVersion { get; init; }
    /// <summary>Latest version known from a search or details lookup.</summary>
    public string? LatestVersion { get; init; }
    /// <summary>Newer version winget can install; null when up to date.</summary>
    public string? Available { get; init; }
    public bool IsPinned { get; init; }
    public string Match { get; init; } = string.Empty;
    public bool Truncated { get; init; }

    public bool IsInstalled => InstalledVersion is not null;
    public bool HasUpdate => Available is not null;
    public string? VersionText => InstalledVersion ?? LatestVersion;
    public WingetOrigin Origin => WingetPackageRules.OriginOf(Id, Source);
    public string Key => WingetPackageRules.Key(Id, Source);

    /// <summary>Safe to pass to winget (full id, no truncation).</summary>
    public bool CanAct => !Truncated && WingetCommands.IsValidId(Id);

    /// <summary><c>winget show</c> needs a source.</summary>
    public bool HasSource => Source.Length > 0;

    public WingetTarget ToTarget() => new(Name, Id, Source);

    /// <summary>"1.2 → 1.3" for rows with an update.</summary>
    public string VersionSummary => Available is null ? VersionText ?? string.Empty : $"{InstalledVersion} → {Available}";
}

/// <summary>The installed-list filter (upstream: All / Casks / Formulae).</summary>
public enum InstalledFilter { All, Winget, Store, Other }

/// <summary>Pure decisions of the Packages tool (unit tested).</summary>
public static class PackageRules
{
    public static PackageItem FromInstalled(WingetPackage p, ISet<string>? pinnedIds = null) => new()
    {
        Name = p.Name,
        Id = p.Id,
        Source = p.Source,
        InstalledVersion = p.Version,
        Available = p.HasUpdate ? p.Available : null,
        IsPinned = p.IsPinned || pinnedIds?.Contains(p.Id) == true,
        Truncated = p.Truncated,
    };

    /// <summary>A search hit, shown with its installed state when it is installed.</summary>
    public static PackageItem FromSearch(WingetPackage p, IReadOnlyList<PackageItem> installed)
    {
        var match = installed.FirstOrDefault(i => string.Equals(i.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match with { LatestVersion = p.Version, Match = p.Match, Source = match.Source.Length > 0 ? match.Source : p.Source };
        return new PackageItem
        {
            Name = p.Name,
            Id = p.Id,
            Source = p.Source,
            LatestVersion = p.Version,
            Match = p.Match,
            Truncated = p.Truncated,
        };
    }

    /// <summary>Search results kept in step with a fresh installed list (port of HomebrewSearchResults.reconciled).</summary>
    public static List<PackageItem> Reconcile(IEnumerable<PackageItem> results, IReadOnlyList<PackageItem> installed) =>
        results.Select(r =>
        {
            var match = installed.FirstOrDefault(i => string.Equals(i.Id, r.Id, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match with { LatestVersion = r.LatestVersion, Match = r.Match };
            return r with { InstalledVersion = null, Available = null, IsPinned = false };
        }).ToList();

    public static bool Matches(PackageItem item, InstalledFilter filter) => filter switch
    {
        InstalledFilter.Winget => item.Origin == WingetOrigin.Winget,
        InstalledFilter.Store => item.Origin == WingetOrigin.Store || item.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase),
        InstalledFilter.Other => item.Origin is WingetOrigin.OtherSource or WingetOrigin.Local
                                 && !item.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    public static int Count(IEnumerable<PackageItem> items, InstalledFilter filter) => items.Count(i => Matches(i, filter));

    /// <summary>Filtered, with packages that have updates first (upstream updatesFirst).</summary>
    public static List<PackageItem> Visible(IEnumerable<PackageItem> installed, InstalledFilter filter)
    {
        var list = installed.Where(i => Matches(i, filter)).ToList();
        return [.. list.Where(i => i.HasUpdate), .. list.Where(i => !i.HasUpdate)];
    }

    /// <summary>What "Update all" will touch: updates that are not held by a pin.</summary>
    public static List<PackageItem> UpgradeAllTargets(IEnumerable<PackageItem> installed) =>
        installed.Where(i => i.HasUpdate && !i.IsPinned).ToList();

    public static string SourceLabel(string source) => WingetPackageRules.OriginOf(string.Empty, source) switch
    {
        WingetOrigin.Winget => PackagesStrings.WingetSource,
        WingetOrigin.Store => PackagesStrings.StoreSource,
        WingetOrigin.OtherSource => string.Format(PackagesStrings.OtherSourceFormat, source),
        _ => PackagesStrings.LocalSource,
    };

    public static string ConfirmTitle(WingetAction action) => action switch
    {
        WingetAction.Install => PackagesStrings.ConfirmInstallTitle,
        WingetAction.Uninstall => PackagesStrings.ConfirmUninstallTitle,
        WingetAction.Upgrade => PackagesStrings.ConfirmUpgradeTitle,
        WingetAction.UpdateSources => PackagesStrings.ConfirmUpdateSourcesTitle,
        _ => PackagesStrings.ConfirmUpgradeAllTitle,
    };

    public static string ConfirmBody(WingetAction action, PackageItem? item) => action switch
    {
        WingetAction.Install => string.Format(PackagesStrings.ConfirmInstallBodyFormat, item?.Name),
        WingetAction.Uninstall => string.Format(PackagesStrings.ConfirmUninstallBodyFormat, item?.Name),
        WingetAction.Upgrade => string.Format(PackagesStrings.ConfirmUpgradeBodyFormat, item?.Name),
        WingetAction.UpdateSources => PackagesStrings.ConfirmUpdateSourcesBody,
        _ => PackagesStrings.ConfirmUpgradeAllBody,
    };

    public static string ActionTitle(WingetAction action) => action switch
    {
        WingetAction.Install => PackagesStrings.Install,
        WingetAction.Uninstall => PackagesStrings.Uninstall,
        WingetAction.Upgrade => PackagesStrings.Upgrade,
        WingetAction.UpdateSources => PackagesStrings.UpdateSources,
        _ => PackagesStrings.UpgradeAll,
    };

    /// <summary>Uninstall clears the selection on success; other actions reload the package.</summary>
    public static bool ClearsSelectionOnSuccess(WingetAction action) => action == WingetAction.Uninstall;

    /// <summary>Searching another source than "winget" uses the Microsoft Store, which needs its terms accepted.</summary>
    public static bool SearchNeedsConsent(string source) =>
        !string.Equals(source, WingetPackageRules.WingetSource, StringComparison.OrdinalIgnoreCase);
}
