// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>Energy and display: Keep awake, display brightness, extra brightness, Bluetooth on sleep.</summary>
public sealed class EnergyModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(KeepAwakeService.Id, Parity.Partial,
            "Sessions, durations, until a time, extend, automations (apps, power, external displays), battery limit, pause while locked, " +
            "pointer nudge, closed-lid mode (power-scheme lid action, restored after crashes) and dim-to-zero are in. Missing: the active " +
            "tray icon/colour picker, the countdown next to the tray icon and right-click toggling, which need tray hooks in core.");
        r.Implements(BrightnessService.Id, Parity.Partial,
            "WMI for laptop panels, DDC/CI for monitors (background thread, coalesced writes), overlay dimming as fallback and below the " +
            "minimum, shortcuts with half/quarter steps and an OSD. Missing: per-display on/off, keyboard backlight, and the hardware " +
            "brightness keys (Windows handles them; use the shortcuts). Overlay dimming stops at 10% to keep the screen readable.");
        r.Implements(ExtraBrightnessService.Id, Parity.Partial,
            "Raises the SDR content brightness of HDR displays toward Windows' 480-nit ceiling (display-config white level, restored on " +
            "stop). It cannot exceed what Windows allows, unlike the XDR headroom boost upstream.");
        r.Implements(BluetoothSleepService.Id, Parity.Full,
            "Windows.Devices.Radios on suspend/resume notifications; the restore debt is persisted across shutdowns.");


        r.Service(KeepAwakeService.Id, () => new KeepAwakeService());
        r.Service(BrightnessService.Id, () => new BrightnessService());
        r.Service(ExtraBrightnessService.Id, () => new ExtraBrightnessService());
        r.Service(BluetoothSleepService.Id, () => new BluetoothSleepService());
        r.Service("monitorPower", () => new ChargeReminderService());

        r.PanelSection(new PanelSectionDef("keepAwake", "Keep awake", Glyphs.Moon, 0, [KeepAwakeService.Id],
            () => Ui.Stack(12, KeepAwakeView.Build(), PowerTimer.View())));
        r.PanelSection(new PanelSectionDef("brightness", "Displays", Glyphs.Brightness, 10, [BrightnessService.Id], BrightnessView.Build));

        r.SettingsPage(new SettingsPageDef("energy", SettingsCategory.Essentials, "Energy", Glyphs.Bolt, 2,
            [KeepAwakeService.Id, BluetoothSleepService.Id, "monitorPower"], EnergyPages.BuildEnergy,
            ["keep awake", "sleep", "caffeine", "lid", "battery", "automation", "bluetooth", "power", "unplug", "charge"]));
        r.SettingsPage(new SettingsPageDef("displays", SettingsCategory.Essentials, "Displays", Glyphs.Brightness, 4,
            [BrightnessService.Id, ExtraBrightnessService.Id], EnergyPages.BuildDisplays,
            ["brightness", "monitor", "ddc", "dim", "hdr", "extra brightness", "osd"]));

        r.Action(new ToolAction("keepAwake.toggle", KeepAwakeService.Id, "Keep awake", Glyphs.Moon, ToggleKeepAwake,
            KeepAwakeService.ShortcutKey, KeepAwakeService.DefaultShortcut));
        r.Action(new ToolAction("brightness.decrease", BrightnessService.Id, "Decrease display brightness", Glyphs.Brightness,
            () => Step(-1), ShortcutKey: BrightnessService.DecreaseShortcutKey));
        r.Action(new ToolAction("brightness.increase", BrightnessService.Id, "Increase display brightness", Glyphs.Brightness,
            () => Step(+1), ShortcutKey: BrightnessService.IncreaseShortcutKey));

        // Sleep and shutdown timer (upstream #3044): also reachable from scripts (--action) and the Command Bar.
        foreach (var minutes in PowerTimerLogic.PresetMinutes)
        {
            foreach (var action in Enum.GetValues<PowerTimerAction>())
            {
                var (m, a) = (minutes, action);
                r.Action(new ToolAction($"powerTimer.{a.ToString().ToLowerInvariant()}{m}", KeepAwakeService.Id,
                    $"{PowerTimerLogic.Verb(a)} in {(m >= 60 ? $"{m / 60} h" : $"{m} min")}", Glyphs.Power,
                    () => PowerTimer.Start(a, TimeSpan.FromMinutes(m))));
            }
        }
        r.Action(new ToolAction("powerTimer.cancel", KeepAwakeService.Id, "Cancel the sleep or shutdown timer", Glyphs.Power, PowerTimer.Cancel));
    }

    public static void ToggleKeepAwake()
    {
        if (KeepAwakeService.Current is { } s) s.Toggle();
        else Notifier.Show("Keep awake", "Keep awake is not installed. Turn it on in Settings → Features.");
    }

    private static void Step(int direction)
    {
        if (BrightnessService.Current is not { } s)
        {
            Notifier.Show("Displays", "Turn on “Control displays” in Settings → Displays first.");
            return;
        }
        s.StepPrimary(direction);
    }
}
