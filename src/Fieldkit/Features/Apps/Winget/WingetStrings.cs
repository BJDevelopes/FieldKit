// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Winget;

/// <summary>English UI strings shared by Packages and App updates (upstream Homebrew wording, adapted to winget).</summary>
public static class WingetStrings
{
    public const string MissingTitle = "winget not found";
    public const string MissingBody = "Packages uses winget, which comes with App Installer from the Microsoft Store. Fieldkit can open its Store page so you can install or update it.";
    public const string GetAppInstaller = "Get App Installer";
    public const string GetAppInstallerCaption = "When the Store finishes, come back here and click Refresh.";
    public const string GetAppInstallerOpened = "App Installer opened in the Microsoft Store.";
    public const string Refresh = "Refresh";

    public const string Cancel = "Cancel";
    public const string CancelOperation = "Cancel";
    public const string ClearLog = "Clear log";
    public const string Copy = "Copy";
    public const string OpenTerminal = "Open Terminal";
    public const string TerminalFallback = "This operation needs a terminal window to finish interactively, for example to approve administrator rights. Fieldkit never sees passwords.";
    public const string ShowDetails = "Show details";
    public const string HideDetails = "Hide details";
    public const string TechnicalLog = "Technical details";
    public const string ProgressUnknown = "winget has not reported a percentage yet.";
    public const string ElapsedFormat = "{0} elapsed";
    public const string AllPackages = "packages";

    public const string OperationInstallFormat = "Installing {0}";
    public const string OperationUninstallFormat = "Uninstalling {0}";
    public const string OperationUpgradeFormat = "Updating {0}";
    public const string OperationUpgradeAll = "Updating packages";
    public const string OperationUpdateSources = "Updating sources";
    public const string OperationInstalledFormat = "{0} installed.";
    public const string OperationUninstalledFormat = "{0} uninstalled.";
    public const string OperationUpgradedFormat = "{0} updated.";
    public const string OperationUpgradedAll = "Packages updated.";
    public const string OperationUpdatedSources = "Sources updated.";
    public const string OperationFailedFormat = "Could not finish {0}.";
    public const string OperationCancelled = "Operation cancelled.";
    public const string OperationTerminal = "Continue in Terminal.";
    public const string OperationSilent = "winget printed nothing for 15 minutes, so it was stopped.";
    public const string OperationStepFormat = "{0} of {1}";

    public const string PhasePreparing = "Preparing…";
    public const string PhaseDownloading = "Downloading files…";
    public const string PhaseInstalling = "Installing files…";
    public const string PhaseUninstalling = "Removing files…";
    public const string PhaseUpgrading = "Updating files…";
    public const string PhaseFinalizing = "Finishing…";
    public const string PhaseRefreshing = "Refreshing list…";

    public const string ConsentTitle = "Agree to winget terms?";
    public const string ConsentBody = "winget installs software from sources such as its community repository and the Microsoft Store. Sources and many packages come with their own license terms, which winget asks you to accept.";
    public const string ConsentDetail = "To search, install, update or remove packages from here, Fieldkit tells winget that you accept those terms (--accept-source-agreements and --accept-package-agreements). Each package is licensed to you by its owner; Microsoft and Fieldkit do not license third-party packages. You can withdraw this in Settings → Packages.";
    public const string ConsentAgree = "Agree and continue";
    public const string ConsentWingetTerms = "winget license";
    public const string ConsentStoreTerms = "Microsoft Store terms";
    public const string ConsentStoreNotice = "Microsoft Store results need your agreement to the store's terms first.";
    public const string ConsentReview = "Review agreements";
}
