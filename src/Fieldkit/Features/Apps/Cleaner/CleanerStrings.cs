// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>
/// English strings of the cleaner, taken from the upstream enUS table and adapted to Windows
/// (Trash → Recycle Bin, Mac → PC, Finder → File Explorer, WhatsApp downloads → Messaging downloads).
/// </summary>
internal static class S
{
    public const string Name = "Cleaner";
    public const string PanelCaption = "App leftovers, caches and logs";
    public const string IntroTitle = "Clean up your PC";
    public const string IntroCaption = "Scans for leftovers from uninstalled apps, caches, logs and the Recycle Bin. You review everything first and removed items go to the Recycle Bin.";
    public const string Scan = "Scan";
    public const string Scanning = "Scanning…";
    public const string Cleaning = "Cleaning…";
    public const string Cancel = "Cancel";

    public const string CatLeftovers = "Leftovers from uninstalled apps";
    public const string CatLoginItems = "Orphaned startup items";
    public const string CatCaches = "Caches";
    public const string CatLogs = "Logs and crash reports";
    public const string CatDeveloper = "Developer junk";
    public const string CatTrash = "Recycle Bin";
    public const string CatDeviceBackups = "iPhone backups";
    public const string CatScreenshots = "Forgotten screenshots";
    public const string CatOtherCaches = "Other caches";
    public const string CatTemp = "Temporary files";
    public const string CatSystemCaches = "Windows Update caches";

    public const string LeftoversNote = "Found by analysis and left unchecked. Check the path before ticking.";
    public const string LoginItemsNote = "Startup shortcuts go to the Recycle Bin; Run entries are backed up to a .reg file first.";
    public const string TrashNote = "Emptying the Recycle Bin is permanent.";
    public const string DeviceBackupsCaption = "Old iPhone and iPad backups take a big slice of your storage. Remove only the ones you no longer need; a new backup is made when you plug the device in again.";
    public const string ScreenshotsCaptionFormat = "Screenshots you have not opened in {0} days that still have the name and folder Windows gave them. Ones you renamed or moved never show up here.";
    public const string ScreenshotsSettingCaption = "The scan lists the screenshots you have not opened for this long, unchecked, with their total size.";
    public const string ScreenshotsAfterFormat = "After {0} days";
    public const string NothingFound = "Nothing to clean. Your PC is tidy.";
    public const string DoneNote = "Items went to the Recycle Bin and can be recovered from there.";
    public const string Again = "Scan again";
    public const string Reveal = "Reveal in File Explorer";
    public const string SafeSection = "Safe cleanup";
    public const string OptionalSection = "Optional, review first";
    public const string CachesCaption = "Temporary files apps rebuild on their own.";
    public const string LogsCaption = "Old diagnostic logs and crash dumps.";
    public const string DeveloperCaption = "Package manager download caches (npm, pip, NuGet, Yarn, Go).";
    public const string LoginItemsCaption = "Startup entries left by apps that no longer exist.";
    public const string LeftoversCaption = "Files left behind by apps you uninstalled.";
    public const string OtherCachesCaption = "Safe to remove, nothing breaks. Apps may open slower once and downloaded content, like offline music, downloads again.";
    public const string TempCaptionFormat = "Files in your temporary folder untouched for {0} days.";
    public const string SystemCachesCaption = "Downloaded update files Windows can fetch again. Needs administrator rights.";
    public const string CleanSizeFormat = "Clean {0}";
    public const string FoundTitle = "found";
    public const string SelectedFormat = "{0} of {1} selected";
    public const string DoneTitle = "Done!";
    public const string FreedFormat = "{0} removed";
    public const string SomeFailed = "Some items couldn’t be moved to the Recycle Bin.";
    public const string CloseToCleanFormat = "Close {0} to clean this.";

    public const string ScheduleTitle = "Automatic cleanup";
    public const string ScheduleOff = "Off";
    public const string ScheduleDaily = "Daily";
    public const string ScheduleWeekly = "Weekly";
    public const string ScheduleCaption = "Cleans only the safe part on its own at the chosen time and sends everything to the Recycle Bin.";
    public const string ScheduleLastFormat = "The last automatic cleanup freed {0}.";
    public const string AutoNotificationFormat = "{0} freed and sent to the Recycle Bin.";
    public const string ScheduleNextFormat = "Next cleanup {0}.";
    public const string ScheduleRanFormat = "Last automatic cleanup {0}.";
    public const string ScheduleNotifyToggle = "Notify when done";
    public const string ScheduleCategories = "What the automatic cleanup may clean";
    public const string ScheduleCategoriesCaption = "Only these safe groups, and only what a manual scan would start with checked. Leftovers, startup items, the Recycle Bin and messaging downloads are never cleaned automatically.";

    public const string ConfirmTrashTitle = "Empty the Recycle Bin?";
    public const string ConfirmTrashBody = "The Recycle Bin is included in this cleanup. Emptying it deletes its items permanently. Continue?";

    // Messaging downloads (upstream WhatsApp downloads).
    public const string MsgTitle = "Messaging downloads";
    public const string MsgHubDescription = "Keeps WhatsApp and Telegram files in Downloads under control";
    public const string MsgIntro = "Finds files that Windows confirms came from WhatsApp or Telegram. File contents and chats are never read.";
    public const string MsgAutomatic = "Clean up automatically";
    public const string MsgAutomaticCaption = "Checks once a day and sends matching files older than your limit to the Recycle Bin.";
    public const string MsgFolder = "Watched folders";
    public const string MsgFileTypes = "File types";
    public const string MsgAllTypes = "All";
    public const string MsgImage = "Images";
    public const string MsgVideo = "Videos";
    public const string MsgAudio = "Audio and voice notes";
    public const string MsgDocument = "Documents";
    public const string MsgArchive = "Archives";
    public const string MsgOther = "Other";
    public const string MsgRetention = "Keep for";
    public const string MsgRetentionCaption = "Recently edited files wait for the full period again.";
    public const string MsgDaysFormat = "{0} days";
    public const string MsgManualTitle = "Clean now";
    public const string MsgManualIntro = "Scan at any time. The initial selection follows your types and age limit; you can review every confirmed file.";
    public const string MsgNoFiles = "No confirmed WhatsApp or Telegram files found.";
    public const string MsgResultsFormat = "{0} confirmed files · {1}";
    public const string MsgSelectRules = "Select by my rules";
    public const string MsgCleanSelectedFormat = "Move {0} to Recycle Bin · {1}";
    public const string MsgKeep = "Keep";
    public const string MsgManageAgain = "Manage again";
    public const string MsgActivity = "Activity";
    public const string MsgNeverRun = "No cleanup has run yet.";
    public const string MsgLastRunFormat = "Last cleanup {0}: {1} files · {2} · {3} failed";
    public const string MsgNextRunFormat = "Next automatic check {0}.";
    public const string MsgFirstTitle = "What about existing files?";
    public const string MsgFirstMessageFormat = "{0} existing files already match your rules. Choose whether automation may manage them or only future downloads.";
    public const string MsgFutureOnly = "Only future downloads";
    public const string MsgIncludeExisting = "Include existing files";
    public const string MsgTrashNote = "Files are moved to the Recycle Bin and remain recoverable until you empty it.";
    public const string MsgLocalNote = "Only local file metadata is inspected. Fieldkit never reads chats or file contents.";
    public const string MsgNotificationTitle = "Messaging cleanup";
    public const string MsgNotificationFormat = "{0} files ({1}) moved to the Recycle Bin. {2} failed.";
    public const string MsgScanFailed = "Downloads could not be scanned.";
    public const string MsgManageButton = "Manage…";
    public const string MsgSources = "Sources";
    public const string MsgSourceWhatsApp = "WhatsApp";
    public const string MsgSourceWhatsAppCaption = "Downloads saved from WhatsApp, and the WhatsApp Desktop media folder (only while WhatsApp is closed).";
    public const string MsgSourceTelegram = "Telegram";
    public const string MsgSourceTelegramCaption = "Downloads saved from Telegram Web and the Downloads\\Telegram Desktop folder.";
    public const string MsgCloseWhatsApp = "WhatsApp is running, so its media folder was skipped.";

    public const string OrgTitle = "Automatic organization";
    public const string OrgExperimental = "Experimental";
    public const string OrgDescription = "Moves stable messaging downloads to a dedicated folder and detects exact repeat downloads.";
    public const string OrgEnabled = "Organize automatically";
    public const string OrgEnabledCaption = "WhatsApp or Telegram may download a moved file again. Fieldkit cannot prevent the network download, but it can detect and discard an identical extra copy.";
    public const string OrgDestination = "Destination folder";
    public const string OrgChooseFolder = "Choose…";
    public const string OrgUseDefault = "Use Downloads\\Messaging";
    public const string OrgInvalidDestination = "Choose a folder other than Downloads itself.";
    public const string OrgOrganization = "Folder structure";
    public const string OrgFlat = "No subfolders";
    public const string OrgByType = "By file type";
    public const string OrgByMonth = "By year and month";
    public const string OrgDelay = "Wait before moving";
    public const string OrgMinutesFormat = "{0} minutes";
    public const string OrgDuplicateAction = "When the same file is downloaded again";
    public const string OrgTrashDuplicate = "Move the new copy to the Recycle Bin";
    public const string OrgKeepBoth = "Keep both copies";
    public const string OrgReplaceExisting = "Replace the organized copy";
    public const string OrgDuplicateCaption = "Duplicates are confirmed with a private SHA-256 digest. The organized copy is rechecked before another copy is discarded.";
    public const string OrgOrganizeNow = "Organize eligible files now";
    public const string OrgUndo = "Undo last organization";
    public const string OrgWorking = "Organizing messaging files…";
    public const string OrgResultFormat = "{0} moved · {1} duplicates · {2} failed";
    public const string OrgLastRunFormat = "Last organization {0}: {1} moved · {2} duplicates · {3} failed";
    public const string OrgNeverRun = "No organization has run yet.";
    public const string OrgNotificationTitle = "Messaging organization";
    public const string OrgNotificationFormat = "{0} files organized. {1} duplicate downloads handled. {2} failed.";
    public const string OrgPrivacyNote = "To identify exact duplicates, file bytes are read locally only while calculating a cryptographic digest. Contents and chats are never stored or uploaded.";
    public const string OrgUndoNote = "Undo moves organized files back. Discarded duplicate copies stay in the Recycle Bin.";
}
