// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// Sound: the Volume mixer panel tab, the output switcher, audio device priority,
/// microphone tools and the music app blocker, with the "Volume mixer" and
/// "Audio devices" settings pages.
/// </summary>
public sealed class SoundModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("mixer", Parity.Partial,
            "Per-app volume, mute, live activity dot, per-app output (Windows 10 1803+), pin/reorder/hide. " +
            "Levels above 100% are shown and remembered but play at 100%: Windows caps session volume and the " +
            "process-loopback re-render boost is not implemented. Rows reorder from the context menu, not by dragging.");
        r.Implements("soundOutputSwitcher", Parity.Full,
            "Shortcut cycles the default output (all roles) through the chosen devices with a HUD; " +
            "optional lower-volume-on-headphone-disconnect with restore.");
        r.Implements("audioPriority", Parity.Full,
            "Ordered output and microphone lists that remember disconnected devices; the highest-priority " +
            "connected device is selected when devices arrive or leave.");
        r.Implements("micMute", Parity.Full,
            "Shortcut and quick toggle mute every capture endpoint (only ours are unmuted), preferred microphone, " +
            "input level, HUD and a notification-area indicator while muted.");
        r.Implements("musicBlock", Parity.Partial,
            "Watches for the chosen player processes after a media key seen by the keyboard hook and closes them; " +
            "the launch itself is not intercepted, so the player's window may flash before it closes.");

        r.Service("soundOutputSwitcher", () => new OutputSwitcherService());
        r.Service("soundOutputSwitcher", () => new HeadphoneGuardService());
        r.Service("audioPriority", () => new AudioPriorityService());
        r.Service("micMute", () => new MicMuteService());
        r.Service("micMute", () => new TypingMuteService());
        r.Service("musicBlock", () => new MusicBlockService());

        r.PanelSection(new PanelSectionDef("mixer", "Volume mixer", Glyphs.Mixer, 20, ["mixer"], MixerView.BuildPanel));

        r.SettingsPage(new SettingsPageDef("sound", SettingsCategory.Sound, "Volume mixer", Glyphs.Mixer, 0, ["mixer"],
            SoundPages.BuildMixer, ["volume", "mixer", "app", "boost", "pin", "output", "audio"]));
        r.SettingsPage(new SettingsPageDef("audioDevices", SettingsCategory.Sound, "Audio devices", Glyphs.Headphones, 10,
            ["soundOutputSwitcher", "audioPriority", "micMute", "musicBlock"], SoundPages.BuildDevices,
            ["output", "switcher", "headphones", "priority", "microphone", "mic", "mute", "media keys", "music", "player"]));

        r.Action(new ToolAction("soundOutputSwitcher.next", "soundOutputSwitcher", "Switch to next sound output", Glyphs.Speaker,
            () => OutputSwitcherService.SwitchToNext(), SoundKeys.SwitcherShortcut, SoundKeys.SwitcherDefaultShortcut));
        r.Action(new ToolAction("micMute.toggle", "micMute", "Mute microphone", Glyphs.Microphone,
            MicMute.Toggle, SoundKeys.MicShortcut, SoundKeys.MicDefaultShortcut));

        r.QuickToggle(new QuickToggleDef("micMute", "Microphone", Glyphs.Microphone,
            () => MicMute.IsMuted, MicMute.Toggle, 30, FeatureId: "micMute"));
    }

}
