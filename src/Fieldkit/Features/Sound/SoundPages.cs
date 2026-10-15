// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>Settings → Sound: "Volume mixer" and "Audio devices".</summary>
internal static class SoundPages
{
    public static FrameworkElement BuildMixer() =>
        Ui.Page("Volume mixer", "Per-app volume, pinning and custom order. The same controls live in the panel's Volume mixer tab.",
            Ui.Group(null, MixerView.BuildControls(settingsMode: true)));

    public static FrameworkElement BuildDevices() =>
        Ui.Page("Audio devices", "Switch outputs with a shortcut, pick devices automatically, mute the microphone and tame media keys.",
            FeatureCatalog.IsAvailable("soundOutputSwitcher")
                ? Ui.Group("Output switcher", OutputSwitcherControls.Build(), MixerView.HeadphoneGuardControls())
                : null,
            FeatureCatalog.IsAvailable("audioPriority")
                ? Ui.Group("Audio priority", AudioPriorityView.Build(showHeader: false, initiallyExpanded: true))
                : null,
            FeatureCatalog.IsAvailable("micMute") ? Microphone() : null,
            FeatureCatalog.IsAvailable("musicBlock") ? MediaKeys() : null);

    private static FrameworkElement Microphone()
    {
        var muteButton = new Button { MinWidth = 96 };
        void Sync() => muteButton.Content = MicMute.IsMuted ? "Unmute" : "Mute";
        Sync();
        muteButton.Click += (_, _) => Ui.Guard(MicMute.Toggle);
        Ui.Track(muteButton, () => Settings.Observe(Sync, SoundKeys.MicActive));

        var shortcut = Ui.ShortcutRow("Shortcut", SoundKeys.MicShortcut, SoundKeys.MicDefaultShortcut);
        void SyncShortcut() => shortcut.Visibility = Settings.GetBool(SoundKeys.MicShortcutEnabled, false) ? Visibility.Visible : Visibility.Collapsed;
        SyncShortcut();
        Ui.Track(shortcut, () => Settings.Observe(SyncShortcut, SoundKeys.MicShortcutEnabled));

        return Ui.Group("Microphone",
            Ui.Split(Ui.Stack(2, Ui.Text("Mute microphone", 13.5, FontWeights.Medium),
                Ui.Caption("Cuts the PC's microphone with a click or shortcut, across every app.")), muteButton),
            Ui.Stack(10, Ui.ToggleRow("Mute with a shortcut", "Mutes or unmutes every microphone from anywhere.", SoundKeys.MicShortcutEnabled, false),
                shortcut),
            Ui.Stack(10,
                Ui.ToggleRow("Mute while typing", "Keyboard noise stays out of calls: microphones mute as you type and come back a moment after you stop.",
                    TypingMuteService.EnabledKey, false),
                Ui.SliderRow("Unmute after", TypingMuteService.HoldKey, 200, 3000, TypingMuteLogic.DefaultHoldMs, v => $"{v / 1000:0.0} s", 100)),
            Ui.ToggleRow("Show in the notification area while muted",
                "A red crossed-out mic appears in the notification area while this feature mutes it. Click it to unmute.",
                SoundKeys.MicTrayIndicator, true),
            MixerView.MicrophoneSection());
    }

    private static FrameworkElement MediaKeys()
    {
        var details = Ui.Stack(12);
        void Render()
        {
            details.Children.Clear();
            var enabled = Settings.GetBool(SoundKeys.MusicEnabled, false);
            details.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled) return;
            var apps = Ui.TextInput(SoundKeys.MusicApps, SoundLogic.DefaultBlockedMusicApps, "Process names, separated by commas");
            apps.Width = 300;
            Ui.Append(details, 12, Ui.Split(Ui.Stack(2, Ui.Text("Apps to block", 13.5, FontWeights.Medium),
                Ui.Caption("Process names, separated by commas. Media Player is Microsoft.Media.Player; Groove is Music.UI.")), apps));

            var path = Settings.GetString(SoundKeys.MusicReplacement, string.Empty);
            var name = path.Length == 0 ? "None" : Path.GetFileNameWithoutExtension(path);
            var rejected = Ui.Text("The apps being blocked can't open instead. Choose another app.", 12, brush: "Orange");
            rejected.Visibility = Visibility.Collapsed;
            var clear = path.Length == 0 ? null : Ui.IconButton(Glyphs.Close, "Clear", () => Settings.Set(SoundKeys.MusicReplacement, string.Empty), 11);
            Ui.Append(details, 12, Ui.Split(Ui.Text("Open instead", 13.5, FontWeights.Medium),
                Ui.Row(8, Ui.Text(name, 13, brush: "TextSecondary", wrap: false), Ui.Button("Choose app…", () => ChooseReplacement(rejected)), clear)));
            Ui.Append(details, 12, rejected);
            if (path.Length > 0)
                Ui.Append(details, 12, Ui.Check("Play after opening replacement", SoundKeys.MusicPlayReplacement, true));
        }
        Render();
        Ui.Track(details, () => Settings.Observe(Render, SoundKeys.MusicEnabled, SoundKeys.MusicReplacement));
        return Ui.Group("Media keys",
            Ui.ToggleRow("Stop the music app from opening on its own",
                "Blocks the music app opening after a detected media key when nothing is playing. " +
                "Headphone commands without a detected key are left alone.",
                SoundKeys.MusicEnabled, false),
            details);
    }

    private static void ChooseReplacement(FrameworkElement rejected)
    {
        rejected.Visibility = Visibility.Collapsed;
        var dialog = new OpenFileDialog
        {
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog() != true) return;
        // Picking a blocked app itself would start a launch-and-close loop.
        var blocked = SoundLogic.ParseProcessNames(Settings.GetString(SoundKeys.MusicApps, SoundLogic.DefaultBlockedMusicApps));
        if (blocked.Contains(Path.GetFileNameWithoutExtension(dialog.FileName).ToLowerInvariant()))
        {
            rejected.Visibility = Visibility.Visible;
            return;
        }
        Settings.Set(SoundKeys.MusicReplacement, dialog.FileName);
    }
}
