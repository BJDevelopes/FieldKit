// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>English strings of App updates (upstream AppUpdateStrings.enUS, adapted to winget and the Microsoft Store).</summary>
public static class AppUpdateStrings
{
    public const string PageTitle = "App updates";
    public const string HubDescription = "Find and install updates for the apps you have";
    public const string Caption = "Looks for newer versions of the apps on this PC and helps you finish each update from its original source.";
    public const string PanelCaption = "See which apps have a newer version";
    public const string CheckNow = "Check now";
    public const string Checking = "Checking";
    public const string LastCheckFormat = "Last checked {0}";
    public const string NeverChecked = "Not checked yet";
    public const string UpToDate = "No updates found";
    public const string PartialUpToDate = "No updates found in this partial check";
    public const string CoverageNote = "Checks installed apps with winget, which also knows many Microsoft Store apps. Updates install through winget or the app's own updater.";
    public const string SelectAll = "Select all";
    public const string ClearSelection = "Clear";
    public const string UpdateSelectedFormat = "Update {0}";
    public const string UpdateOne = "Update";
    public const string OpenStore = "Open the Microsoft Store";
    public const string OpenInStore = "Open in Microsoft Store";
    public const string StoreBadge = "Microsoft Store";
    public const string StoreHint = "Updates through winget from the Microsoft Store";
    public const string FrequencyLabel = "Check in the background";
    public const string FrequencyOff = "Off";
    public const string FrequencyDaily = "Every day";
    public const string FrequencyWeekly = "Every week";
    public const string NextCheckFormat = "Next check {0}";
    public const string NotifyToggle = "Tell me when an app has an update";
    public const string IncludeStoreToggle = "Include apps from the Microsoft Store";
    public const string IncludeStoreCaption = "Uses winget's Microsoft Store source, which needs the store's terms accepted. These updates install through winget.";
    public const string PackageMissing = "winget is not installed, so apps cannot be updated from here yet.";
    public const string NotificationBodyFormat = "{0} apps have a newer version.";
    public const string NotificationBodyOne = "One app has a newer version.";
    public const string WingetBadge = "winget";
    public const string SourcesTitle = "Sources";
    public const string IncludeWingetToggle = "Include winget apps";
    public const string UnknownBadge = "Version unknown";
    public const string OpenApp = "Open";
    public const string OpenAppHint = "Opens the app so its own updater can finish";
    public const string OpenAppMissing = "Fieldkit could not find this app's executable";
    public const string IncludeUnknownToggle = "Include apps with an unknown version";
    public const string IncludeUnknownCaption = "winget cannot read these apps' installed version, so a newer release may already be installed. Open the app and let its own updater check.";
    public const string IncompleteCheck = "Check incomplete";
    public const string CheckUnavailable = "winget could not finish the check. Other results are still shown.";
    public const string StoreNotChecked = "Microsoft Store apps were not checked because the store's terms have not been accepted yet.";
    public const string SkipVersionFormat = "Skip version {0}";
    public const string ExcludeApp = "Don’t check this app";
    public const string RulesTitle = "Update rules";
    public const string SkippedVersionFormat = "Skipped version {0}";
    public const string ExcludedApp = "Not checked until this rule is removed";
    public const string RemoveRule = "Remove rule";
    public const string RulesHint = "Skipping a version still allows newer releases. After removing an app exclusion, use Check now to refresh it.";
    public const string NoVisibleUpdates = "No updates outside your rules";
    public const string ConfirmTitle = "Update these apps?";
    public const string ConfirmBody = "winget will download and install the latest version of each app below, one after another. Apps that are running may be closed by their installers.";
    public const string Settings = "Settings";
    public const string ShortcutTitle = "Shortcut";
    public const string ShortcutRow = "Open App updates";
    public const string CheckAction = "Check for app updates";
}
