// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Features.Sound;

/// <summary>Settings keys of the sound features, with their defaults (upstream DefaultsKey values).</summary>
internal static class SoundKeys
{
    // Volume mixer
    public const string HideInactive = "mixer.hideInactive";
    public const string Order = "mixer.order";
    public const string Pinned = "mixer.pinned";
    /// <summary>app id → display name of apps hidden from the list.</summary>
    public const string Hidden = "mixer.hidden";
    /// <summary>app id → requested level above 100 % (Windows caps the real session volume at 100 %).</summary>
    public const string Boost = "mixer.boost";
    public const string OptionsExpanded = "mixer.optionsExpanded";

    // Output switcher (+ headphone guard)
    public const string SwitcherEnabled = "soundOutputSwitcher.enabled";
    public const string SwitcherShortcut = "soundOutputSwitcher.shortcut";
    public const string SwitcherDefaultShortcut = "Ctrl+Alt+Win+S";
    public const string SwitcherDevices = "soundOutputSwitcher.devices";
    public const string LowerOnHeadphonesDisconnect = "soundOutputSwitcher.lowerOnHeadphonesDisconnect";
    public const string HeadphonesVolume = "soundOutputSwitcher.headphonesVolume";

    // Audio priority
    public const string PriorityOutputEnabled = "audioPriority.outputEnabled";
    public const string PriorityInputEnabled = "audioPriority.inputEnabled";
    public const string PriorityOutputIds = "audioPriority.outputIds";
    public const string PriorityInputIds = "audioPriority.inputIds";
    public const string PriorityNames = "audioPriority.names";

    // Microphone
    public const string MicShortcutEnabled = "micMute.shortcutEnabled";
    public const string MicShortcut = "micMute.shortcut";
    public const string MicDefaultShortcut = "Ctrl+Alt+Win+M";
    public const string MicActive = "micMute.active";
    public const string MicMutedDevices = "micMute.mutedDevices";
    public const string MicTrayIndicator = "micMute.trayIndicator";
    public const string MicPreferredInput = "micMute.preferredInput";

    // Music blocker
    public const string MusicEnabled = "musicBlock.enabled";
    public const string MusicApps = "musicBlock.apps";
    public const string MusicReplacement = "musicBlock.replacementPath";
    public const string MusicPlayReplacement = "musicBlock.playReplacement";

    public static List<string> GetList(string key) => Settings.Get<List<string>>(key, []) ?? [];

    public static void SetList(string key, IEnumerable<string> values) => Settings.Set(key, SoundLogic.Unique(values));

    public static Dictionary<string, string> GetMap(string key) =>
        Settings.Get<Dictionary<string, string>>(key, []) ?? [];

    public static Dictionary<string, int> GetIntMap(string key) =>
        Settings.Get<Dictionary<string, int>>(key, []) ?? [];
}
