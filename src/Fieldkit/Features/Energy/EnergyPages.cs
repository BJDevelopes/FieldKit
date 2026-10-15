// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>Settings → Energy (Keep awake, Bluetooth on sleep) and Settings → Displays.</summary>
public static class EnergyPages
{
    public static FrameworkElement BuildEnergy() => Ui.Page("Energy",
        "Keep the PC awake, control your displays and save battery.",
        FeatureCatalog.IsAvailable(KeepAwakeService.Id) ? KeepAwakeCard() : null,
        FeatureCatalog.IsAvailable(KeepAwakeService.Id) ? KeepAwakeOptions() : null,
        FeatureCatalog.IsAvailable(BluetoothSleepService.Id) ? BluetoothCard() : null,
        FeatureCatalog.IsAvailable("monitorPower") ? ChargeReminderService.SettingsGroup() : null);

    // ---- Keep awake ----------------------------------------------------------------

    private static FrameworkElement KeepAwakeCard()
    {
        var caption = Ui.Caption(string.Empty);
        var toggle = new CheckBox();
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        toggle.Click += (_, _) =>
        {
            if (KeepAwakeService.Current is not { } s) { toggle.IsChecked = false; return; }
            if (toggle.IsChecked == true) s.StartLastPick();
            else s.Stop();
        };
        var session = Ui.Split(EnergyUi.WithIcon(Glyphs.Moon, Ui.Stack(2, Ui.Text("Keep awake", 13.5, FontWeights.Medium), caption), 16), toggle);

        var durations = new WrapPanel();
        void BuildDurations()
        {
            durations.Children.Clear();
            var current = Settings.GetBool(KeepAwakeService.SwitchUsesUntilKey, false)
                ? -1
                : KeepAwakeLogic.SanitizedDuration(Settings.GetInt(KeepAwakeService.DefaultDurationKey, 0));
            foreach (var m in KeepAwakeLogic.Durations)
            {
                var chip = DurationTile(KeepAwakeLogic.LongLabel(m), m == current, () =>
                {
                    Settings.Set(KeepAwakeService.DefaultDurationKey, m);
                    Settings.Set(KeepAwakeService.SwitchUsesUntilKey, false);
                });
                durations.Children.Add(chip);
            }
        }

        var root = Ui.Group(null,
            session,
            Ui.Stack(8, Ui.Text("Default duration", 13, FontWeights.SemiBold), durations),
            Ui.Stack(8, Ui.Text("Automation", 13, FontWeights.SemiBold), KeepAwakeView.AutomationEditor(compact: false)),
            Ui.ToggleRow("Pause while the PC is locked",
                "Follows normal sleep rules while locked and resumes the remaining session after you unlock.",
                KeepAwakeService.PauseWhenLockedKey, false));

        void Refresh()
        {
            var s = KeepAwakeService.Current;
            toggle.IsChecked = s?.IsActive == true;
            caption.Text = s?.StatusText() ?? "The PC follows its normal energy rules";
        }
        Refresh();
        BuildDurations();
        Ui.Every(root, TimeSpan.FromSeconds(1), Refresh);
        Ui.Track(root, () => Settings.Observe(BuildDurations, KeepAwakeService.DefaultDurationKey, KeepAwakeService.SwitchUsesUntilKey));
        return root;
    }

    private static Border DurationTile(string text, bool selected, Action onClick)
    {
        var label = Ui.Text(text, 12.5, FontWeights.Medium, selected ? "Accent" : "TextPrimary", wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(10, 6, 10, 7),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 96,
            Child = label,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
        tile.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBorder");
        tile.MouseLeftButtonUp += (_, _) => Ui.Guard(onClick);
        return tile;
    }

    private static FrameworkElement KeepAwakeOptions()
    {
        var hasBattery = KeepAwakeService.HasBattery();
        var lidCaption = Ui.Caption(string.Empty);
        var lidToggle = new CheckBox { IsChecked = Settings.GetBool(KeepAwakeService.LidClosedKey, false) };
        lidToggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        lidToggle.Click += (_, _) =>
        {
            var on = lidToggle.IsChecked == true;
            if (KeepAwakeService.Current is { } s) s.SetLidPreference(on);
            else Settings.Set(KeepAwakeService.LidClosedKey, on);
        };
        var lidRow = Ui.Split(Ui.Stack(2, Ui.Text("Keep working with the lid closed", 13.5, FontWeights.Medium), lidCaption), lidToggle);
        var dimRow = Ui.ToggleRow("Dim the display to zero",
            "Dims the display when the lid closes and brings the brightness back when it opens.",
            KeepAwakeService.DimOnLidCloseKey, false);
        dimRow.Margin = new Thickness(20, 0, 0, 0);
        var lid = Ui.Stack(10, lidRow, dimRow);
        void SyncLid()
        {
            var s = KeepAwakeService.Current;
            lidToggle.IsChecked = Settings.GetBool(KeepAwakeService.LidClosedKey, false);
            lidCaption.Text = s?.LidSetupFailed == true
                ? "Couldn’t turn on closed-lid mode. Try again."
                : "“Keep working with the lid closed” sets the laptop lid action to Do nothing while “Keep awake” is active " +
                  "and puts it back automatically when the session ends or the app quits. Prefer using it plugged in.";
            lidCaption.SetResourceReference(TextBlock.ForegroundProperty, s?.LidSetupFailed == true ? "Red" : "TextSecondary");
            dimRow.Visibility = lidToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncLid();
        Ui.Track(lid, () => Settings.Observe(SyncLid, KeepAwakeService.LidClosedKey));

        var battery = hasBattery
            ? EnergyUi.IntChoiceRow("Disable when battery drops below", "Keeps a forgotten session from draining the laptop battery.",
                KeepAwakeService.BatteryLimitKey, KeepAwakeLogic.BatteryLimits, p => p == 0 ? "Never" : $"{p}%",
                KeepAwakeLogic.SanitizedBatteryLimit(Settings.GetInt(KeepAwakeService.BatteryLimitKey, 10)))
            : null;

        return Ui.Group("Options",
            Ui.ToggleRow($"Keep Awake when {AppInfo.Name} opens", "Starts a session with the default duration.", KeepAwakeService.AutoStartKey, false),
            Ui.ToggleRow("Allow the display to sleep", "Keeps the PC awake while the display follows its normal sleep timer.",
                KeepAwakeService.AllowDisplaySleepKey, false),
            KeepAwakeView.JiggleOptions(compact: false),
            battery,
            EnergyUi.ShortcutToggle("Global shortcut", "Turns Keep awake on or off from any app.",
                KeepAwakeService.ShortcutKey, KeepAwakeService.DefaultShortcut),
            KeepAwakeService.LidPresent ? lid : null);
    }

    // ---- Bluetooth -----------------------------------------------------------------

    private static FrameworkElement BluetoothCard()
    {
        var host = new ContentControl { Content = Ui.Caption("Looking for Bluetooth…") };
        var restore = Ui.ToggleRow("Turn Bluetooth back on when the PC wakes", $"Only when {AppInfo.Name} was the one that switched it off.",
            BluetoothSleepService.RestoreOnWakeKey, true);
        restore.Margin = new Thickness(20, 0, 0, 0);
        var main = Ui.ToggleRow("Turn Bluetooth off when the PC sleeps", "Bluetooth already off before sleep is left alone and stays off on wake.",
            BluetoothSleepService.EnabledKey, false);
        var content = Ui.Stack(10, main, restore);
        void Sync() => restore.Visibility = Settings.GetBool(BluetoothSleepService.EnabledKey, false) ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(content, () => Settings.Observe(Sync, BluetoothSleepService.EnabledKey));
        host.Content = content;
        // The controller check is a WinRT call: do it in the background and swap in the "none" text if needed.
        Task.Run(BluetoothSleepService.HasController).ContinueWith(t =>
        {
            if (!t.Result) Ui.OnUi(() => host.Content = Ui.Caption("This PC has no Bluetooth radio."));
        }, TaskScheduler.Default);
        return Ui.Group("Bluetooth on sleep", host);
    }

    // ---- Displays ------------------------------------------------------------------

    public static FrameworkElement BuildDisplays()
    {
        var enabled = Ui.ToggleRow("Control displays",
            "Brightness controls for the built-in and external displays, here and in the notification area panel.",
            BrightnessService.EnabledKey, false);
        var body = new ContentControl();
        void Sync()
        {
            if (!Settings.GetBool(BrightnessService.EnabledKey, false))
            {
                body.Content = null;
                return;
            }
            var list = new BrightnessView.DisplayList(compact: false);
            body.Content = Ui.Stack(14,
                Ui.Divider(0),
                list.Root,
                EnergyUi.Disclosure("More options", () => Ui.Stack(14,
                    Ui.ToggleRow("Shortcuts follow the pointer", "The display brightness shortcuts change the display under the pointer.",
                        BrightnessService.FollowPointerKey, false),
                    Ui.ToggleRow("Shortcuts change every display", "Each press steps all displays together. Overrides following the pointer.",
                        BrightnessService.AllDisplaysKey, false),
                    Ui.ChoiceRow("Brightness key steps", BrightnessService.KeyStepKey,
                        [("standard", "Standard"), ("half", "Half steps"), ("quarter", "Quarter steps")], "standard",
                        "Smaller steps for each press of the display brightness shortcuts."),
                    BrightnessView.ShortcutControls(compact: false),
                    Ui.ToggleRow("Show brightness when adjusting", "Shows the brightness percentage when you use the brightness shortcuts or sliders.",
                        BrightnessService.OsdKey, false),
                    Ui.Text("External displays are adjusted through the same protocol as their own buttons (DDC/CI). When the connection " +
                            "cannot carry it, as with some adapters and docks, the slider dims the picture instead, so brightness control works either way.",
                        11.5, brush: "TextTertiary")), indent: 0));
        }
        Sync();
        var group = Ui.Group(null, Ui.Stack(0, enabled, body));
        Ui.Track(group, () => Settings.Observe(Sync, BrightnessService.EnabledKey));

        return Ui.Page("Displays", "Brightness for every display, with hardware control where the display supports it.",
            group,
            FeatureCatalog.IsAvailable(ExtraBrightnessService.Id) ? ExtraBrightnessCard() : null);
    }

    private static FrameworkElement ExtraBrightnessCard()
    {
        var host = new ContentControl();
        var slider = Ui.SliderRow("Intensity", ExtraBrightnessService.LevelKey, 10, 100, 100, v => $"{v:0}%", 5);
        void Sync()
        {
            slider.Visibility = Settings.GetBool(ExtraBrightnessService.EnabledKey, false) ? Visibility.Visible : Visibility.Collapsed;
        }
        var row = Ui.ToggleRow("Extra brightness", BrightnessView.ExtraBrightnessCaption, ExtraBrightnessService.EnabledKey, false);
        host.Content = Ui.Stack(12, row, slider);
        Sync();
        Ui.Track(host, () => Settings.Observe(Sync, ExtraBrightnessService.EnabledKey));
        Task.Run(ExtraBrightnessService.Supported).ContinueWith(t =>
        {
            if (!t.Result && !Settings.GetBool(ExtraBrightnessService.EnabledKey, false))
                Ui.OnUi(() => host.Content = Ui.Stack(4, Ui.Text("Extra brightness", 13.5, FontWeights.Medium),
                    Ui.Caption("Available only on displays with HDR turned on in Windows display settings.")));
        }, TaskScheduler.Default);
        return Ui.Group("Extra brightness", host);
    }
}
