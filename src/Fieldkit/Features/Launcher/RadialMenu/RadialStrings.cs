// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// English strings of the radial menu, taken from upstream <c>RadialMenuFeatureStrings.enUS</c>
/// and adapted to Windows (Vorssaint → Fieldkit, macOS → Windows, trackpad sentences dropped).
/// </summary>
internal static class RadialStrings
{
    public const string PageTitle = "Radial menu";
    public const string HubDescription = "Opens a wheel of your favorite actions around the pointer";
    public const string EnableLabel = "Use the radial menu";
    public const string EnableCaption = "Hold the shortcut, point at an action and release. A quick press keeps the wheel open.";
    public const string PositionLabel = "Opens";
    public const string PositionPointer = "At the pointer";
    public const string PositionCenter = "At the screen center";
    public const string TryButton = "Try it";
    public const string ActionsHeader = "Actions";
    public const string AddButton = "Add action";
    public const string LimitCaption = "A wheel holds up to 12 actions.";
    public const string EmptyCaption = "No actions here yet.";
    public const string BackButton = "Back";
    public const string EditActionsButton = "Edit actions";
    public const string NameLabel = "Name";
    public const string AutomaticLabel = "Automatic";
    public const string IconLabel = "Icon";
    public const string ActionLabel = "Action";
    public const string KindApp = "Open an app";
    public const string KindFile = "Open a file or folder";
    public const string KindUrl = "Open a link";
    public const string KindShortcut = "Press a shortcut";
    public const string KindTool = "Fieldkit tool";
    public const string KindQuickToggle = "Quick toggles";
    public const string KindWindowLayout = "Window layout";
    public const string KindMedia = "Media control";
    public const string KindSubmenu = "Submenu";
    public const string ChooseButton = "Choose…";
    public const string ChooseFolderButton = "Choose folder…";
    public const string StartMenuButton = "Start menu…";
    public const string UrlPlaceholder = "example.com";
    public const string UrlInvalid = "Enter a valid link.";
    public const string ToolLabel = "Tool";
    public const string MediaLabel = "Control";
    public const string MediaPlayPause = "Play or pause";
    public const string MediaPrevious = "Previous track";
    public const string MediaNext = "Next track";
    public const string MediaNowPlaying = "Now Playing";
    public const string MediaNothingPlaying = "Nothing playing";
    public const string MediaOpenAppFormat = "Open {0}";
    public const string SubmenuCaption = "Opens a second wheel with its own actions.";
    public const string SaveButton = "Save";
    public const string CancelButton = "Cancel";
    public const string DeleteButton = "Remove";
    public const string ManageButton = "Manage the menu";
    public const string PanelCaption = "Your favorite actions on a wheel";
    public const string MouseTriggerOff = "Off";
    public const string MouseTriggerMiddle = "Middle button";
    public const string MouseTriggerBack = "Back side button";
    public const string MouseTriggerForward = "Forward side button";
    public const string MouseTriggerWarning = "While this is on, that button no longer goes back or forward in apps. If you navigate with it, leave this off.";
    public const string MouseTriggerMiddleWarning = "While this is on, that button no longer middle-clicks in apps (opening links in new tabs, closing tabs). Add a key below to keep the plain click.";
    public const string MouseModifierLabel = "Together with";
    public const string MouseModifierNone = "Button alone";
    public const string ButtonTestLabel = "Button test";
    public const string ButtonTestWaiting = "Press the button now";
    public const string ButtonTestSeen = "Fieldkit sees this button";
    public const string ButtonTestOther = "That was a different button";
    public const string ButtonTestBlind = "Fieldkit cannot watch the mouse right now";
    public const string ButtonTestHint = "If nothing lights up, Windows is not sending that button to apps. Mice that come with their own software often turn the side buttons into something else. Change it back there, or give the button a key combination and use that combination above.";
    public const string ActivationModeLabel = "Opening behavior";
    public const string ActivationModePressOrHold = "Press or hold";
    public const string ActivationModePress = "Press to open";
    public const string ActivationModeHold = "Hold to select";
    public const string ActivationModeCaption = "Press or hold keeps the current adaptive gesture. Press stays open; hold runs the highlighted action on release.";
    public const string ProfilesHeader = "Profiles";
    public const string ProfilePickerLabel = "Profile";
    public const string AddProfileButton = "Add profile";
    public const string DuplicateProfileButton = "Duplicate profile";
    public const string DeleteProfileButton = "Delete profile";
    public const string DeleteProfileConfirmFormat = "Delete “{0}”?";
    public const string DeleteProfileConfirmMessage = "Its actions, shortcut and mouse button will be removed. This can’t be undone.";
    public const string ProfileNameLabel = "Profile name";
    public const string ProfileColorLabel = "Color";
    public const string ProfileShortcutLabel = "Shortcut";
    public const string ProfileMouseTriggerLabel = "Mouse button";
    public const string PresetGeneral = "General";
    public const string PresetMedia = "Media";
    public const string PresetTools = "Tools";
    public const string PresetWindowLayout = "Window layout";
    public const string PresetQuickToggles = "Quick toggles";
    public const string PresetBlank = "Blank";
    public const string FetchFaviconButton = "Fetch Website Icon";
    public const string FetchFaviconDisclaimer = "Connects to the website once to download its icon. Saved locally.";
    public const string FetchFaviconLoading = "Fetching icon…";
    public const string FetchFaviconSuccess = "Icon downloaded";
    public const string FetchFaviconError = "Could not find a website icon";
    public const string MouseTriggerRequirement = "Only extra mouse buttons work here. With a mouse without extra buttons, use the keyboard shortcut above.";
    public const string CanvasHint = "Click a button to choose what it starts, or to remove it. Drag a button to move it.";
    public const string ResetActionsButton = "Reset";
    public const string ResetActionsConfirm = "Reset actions";
    public const string ResetActionsConfirmMessage = "Restore the default actions for this profile? Custom actions will be replaced.";
    public const string ShowListButton = "Show as list";
    public const string HideListButton = "Hide list";
    public const string ProfileNoTrigger = "Picking a profile here only edits it. Give this one a shortcut or a mouse button below to open it.";

    // Shared shortcut strings (upstream Localization enUS), adapted to Windows.
    public const string ShortcutNone = "None";
    public const string ShortcutPressKeys = "Press keys";
    public const string ShortcutRecordingHint = "Press the new combination. Esc cancels, Backspace removes the shortcut.";
    public const string ShortcutInvalid = "Use at least Ctrl, Alt, Shift or Win with a key.";
    public const string ShortcutConflictFormat = "This shortcut is already used by {0}.";
    public const string ShortcutRegistrationFailed = "Windows or another app already uses one of these shortcuts. Try another one.";
    public const string MoveUp = "Move up";
    public const string MoveDown = "Move down";
    public const string EditButton = "Edit";
    public const string StartMenuTitle = "Choose an app";
    public const string SearchPlaceholder = "Search apps";

    public static string ColorTitle(RadialMenuColor color) => color switch
    {
        RadialMenuColor.Accent => "Accent",
        RadialMenuColor.Blue => "Blue",
        RadialMenuColor.Purple => "Purple",
        RadialMenuColor.Pink => "Pink",
        RadialMenuColor.Red => "Red",
        RadialMenuColor.Orange => "Orange",
        RadialMenuColor.Yellow => "Yellow",
        RadialMenuColor.Green => "Green",
        RadialMenuColor.Mint => "Mint",
        RadialMenuColor.Cyan => "Cyan",
        RadialMenuColor.Indigo => "Indigo",
        RadialMenuColor.Graphite => "Graphite",
        _ => color.ToString(),
    };

    public static string PresetTitle(RadialMenuPreset preset) => preset switch
    {
        RadialMenuPreset.General => PresetGeneral,
        RadialMenuPreset.Media => PresetMedia,
        RadialMenuPreset.Tools => PresetTools,
        RadialMenuPreset.WindowLayout => PresetWindowLayout,
        RadialMenuPreset.QuickToggles => PresetQuickToggles,
        RadialMenuPreset.Blank => PresetBlank,
        _ => preset.ToString(),
    };

    public static string KindTitle(RadialItemKind kind) => kind switch
    {
        RadialItemKind.App => KindApp,
        RadialItemKind.File => KindFile,
        RadialItemKind.Url => KindUrl,
        RadialItemKind.Shortcut => KindShortcut,
        RadialItemKind.Tool => KindTool,
        RadialItemKind.QuickToggle => KindQuickToggle,
        RadialItemKind.WindowLayout => KindWindowLayout,
        RadialItemKind.Media => KindMedia,
        RadialItemKind.Submenu => KindSubmenu,
        _ => kind.ToString(),
    };

    public static string MediaTitle(RadialMediaKey key) => key switch
    {
        RadialMediaKey.PlayPause => MediaPlayPause,
        RadialMediaKey.PreviousTrack => MediaPrevious,
        RadialMediaKey.NextTrack => MediaNext,
        RadialMediaKey.NowPlaying => MediaNowPlaying,
        _ => KindMedia,
    };

    public static string MouseButtonTitle(RadialMouseButton button) => button switch
    {
        RadialMouseButton.Middle => MouseTriggerMiddle,
        RadialMouseButton.Back => MouseTriggerBack,
        RadialMouseButton.Forward => MouseTriggerForward,
        _ => MouseTriggerOff,
    };
}
