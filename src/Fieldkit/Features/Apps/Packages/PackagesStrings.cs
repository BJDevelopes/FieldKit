// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Packages;

/// <summary>English strings of the Packages tool (upstream Homebrew wording adapted to winget).</summary>
public static class PackagesStrings
{
    public const string Name = "Packages";
    public const string EnableCaption = "Search, install and remove winget packages.";
    public const string CheckPackages = "Check packages";
    public const string SearchPlaceholder = "Search packages";
    public const string SearchButton = "Search";
    public const string SearchResults = "Results";
    public const string Installed = "Installed";
    public const string All = "All";
    public const string AllSources = "All sources";
    public const string WingetSource = "winget";
    public const string StoreSource = "Microsoft Store";
    public const string Other = "Other";
    public const string OtherSourceFormat = "Source: {0}";
    public const string LocalSource = "Installed on this PC";
    public const string NoPackages = "No packages found";
    public const string NoSelection = "Select an installed package or search for a new one.";
    public const string DetailsTitle = "Package details";
    public const string Install = "Install";
    public const string Uninstall = "Uninstall";
    public const string Upgrade = "Update";
    public const string UpgradeAll = "Update all";
    public const string UpdateSources = "Update sources";
    public const string Version = "Version";
    public const string Source = "Source";
    public const string Publisher = "Publisher";
    public const string License = "License";
    public const string Homepage = "Open website";
    public const string ReleaseNotes = "Release notes";
    public const string InstalledBadge = "Installed";
    public const string NotInstalledBadge = "Not installed";
    public const string Updates = "Updates";
    public const string UpdateAvailableBadge = "Update available";
    public const string PinnedBadge = "Pinned";
    public const string PinnedNote = "Pinned: Update all skips this package. Updating it here targets it explicitly.";
    public const string LatestVersion = "Latest";
    public const string Loading = "Loading…";
    public const string SearchEmpty = "No results";
    public const string Details = "Details";
    public const string NotActionable = "winget shortened this entry, so Fieldkit cannot act on it safely.";
    public const string LocalDetails = "This app is installed on the PC but not linked to a winget source, so winget can only uninstall it.";
    public const string InstallLocation = "Install location";
    public const string Size = "Size";
    public const string WhatWillBeRemoved = "What will be removed";
    public const string UninstallNote = "winget runs the app's own uninstaller, which may open its own window. Settings and data files may remain on the PC; Uninstaller can review leftovers afterwards.";
    public const string WillBeUpdated = "Will be updated";
    public const string PinnedSkipped = "Pinned packages are skipped.";

    public const string ConfirmInstallTitle = "Install with winget?";
    public const string ConfirmInstallBodyFormat = "winget will download and install {0}. Dependencies may also be installed.";
    public const string ConfirmUninstallTitle = "Uninstall with winget?";
    public const string ConfirmUninstallBodyFormat = "winget will uninstall {0}. Configuration files may remain on the system.";
    public const string ConfirmUpgradeTitle = "Update with winget?";
    public const string ConfirmUpgradeBodyFormat = "winget will download and apply the latest version of {0}. Dependencies may also be updated.";
    public const string ConfirmUpgradeAllTitle = "Update all with winget?";
    public const string ConfirmUpgradeAllBody = "winget will download and apply the latest versions for packages with updates available. Dependencies may also be updated.";
    public const string ConfirmUpdateSourcesTitle = "Update sources?";
    public const string ConfirmUpdateSourcesBody = "winget will fetch the latest information from its sources and then reload your packages.";

    public const string PageIntro = "Search, install and remove winget packages without a terminal.";
    public const string OpenPackages = "Open Packages";
    public const string ManagerTitle = "Package manager";
    public const string WingetFound = "winget is installed and ready.";
    public const string AgreementsTitle = "Agreements";
    public const string AgreementsRow = "winget source and package terms";
    public const string AgreementsAccepted = "Accepted. Fieldkit passes --accept-source-agreements and --accept-package-agreements to winget.";
    public const string AgreementsNotAccepted = "Not accepted yet. Fieldkit asks before the first install, update or removal.";
    public const string Withdraw = "Withdraw";
    public const string Review = "Review";
    public const string SourcesTitle = "Sources";
    public const string SourcesCaption = "Sources are the catalogs winget installs from, like Homebrew taps. Adding or removing one needs an administrator terminal (winget source add).";
    public const string SourcesLoading = "Loading sources…";
    public const string SourcesNone = "No sources found.";
    public const string Explicit = "Explicit";
    public const string ShortcutTitle = "Shortcut";
    public const string ShortcutRow = "Open Packages";
}
