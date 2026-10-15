// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Winget;

/// <summary>Where a package row comes from, which is also what can be done with it.</summary>
public enum WingetOrigin
{
    /// <summary>The community repository (source "winget").</summary>
    Winget,
    /// <summary>The Microsoft Store (source "msstore").</summary>
    Store,
    /// <summary>Another configured source (company REST source, ...).</summary>
    OtherSource,
    /// <summary>Installed on this PC but not matched to any source (ARP\… / MSIX\… ids).</summary>
    Local,
}

/// <summary>One row of <c>winget list</c>, <c>search</c> or <c>upgrade</c>.</summary>
public sealed record WingetPackage
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    /// <summary>Installed version (list/upgrade) or latest version (search).</summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>Newer version available, empty when none.</summary>
    public string Available { get; init; } = string.Empty;
    /// <summary>Source name ("winget", "msstore", ...), empty for local-only entries.</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>Why a search matched ("Tag: vscode", "Moniker: code").</summary>
    public string Match { get; init; } = string.Empty;
    /// <summary>The name or id was cut with "…" because the console was too narrow.</summary>
    public bool Truncated { get; init; }
    /// <summary>The update is held back by a pin (upgrade's "explicit targeting" table or <c>winget pin list</c>).</summary>
    public bool IsPinned { get; init; }

    public bool HasUpdate => Available.Length > 0;

    public WingetOrigin Origin => WingetPackageRules.OriginOf(Id, Source);

    /// <summary>Identity across lists: ids are case-insensitive in winget.</summary>
    public string Key => WingetPackageRules.Key(Id, Source);
}

/// <summary>Result of <c>winget upgrade</c> (listing only).</summary>
/// <param name="Packages">Packages <c>winget upgrade --all</c> would update.</param>
/// <param name="ExplicitOnly">Updates held by a pin: they only upgrade when named explicitly.</param>
/// <param name="UnknownVersionCount">Packages left out because their installed version is unknown.</param>
/// <param name="PinnedCount">Packages left out because a pin blocks them.</param>
public sealed record WingetUpgradeList(
    IReadOnlyList<WingetPackage> Packages,
    IReadOnlyList<WingetPackage> ExplicitOnly,
    int UnknownVersionCount,
    int PinnedCount)
{
    public static readonly WingetUpgradeList Empty = new([], [], 0, 0);
}

/// <summary>What <c>winget show</c> reports about a package (English field names; anything
/// unrecognised stays available in <see cref="Fields"/>).</summary>
public sealed record WingetDetails
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Homepage { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public string LicenseUrl { get; init; } = string.Empty;
    public string ReleaseNotesUrl { get; init; } = string.Empty;
    public string Moniker { get; init; } = string.Empty;
    public string InstallerType { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>A configured source (<c>winget source list</c>).</summary>
public sealed record WingetSource(string Name, string Argument, bool? Explicit);

/// <summary>A pin (<c>winget pin list</c>).</summary>
public sealed record WingetPin(string Name, string Id, string Version, string Source, string PinType);
