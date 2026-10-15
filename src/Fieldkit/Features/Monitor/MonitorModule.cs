// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Fans;
using Fieldkit.Features.Monitor.Services;
using Fieldkit.Features.Monitor.Views;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor;

/// <summary>
/// The system monitor: System / Network / Disk / Power / Fan Control panel sections, one shared
/// sampler, notification-area readouts, alerts and fan control.
/// </summary>
public sealed class MonitorModule : IFeatureModule
{
    private static readonly string[] Features =
        ["monitorCPU", "monitorGPU", "monitorMemory", "monitorNetwork", "monitorDisk", "monitorPower", "connectedDevices", "fanControl"];

    public void Register(ModuleRegistry r)
    {
        r.Implements("monitorCPU", Parity.Partial,
            "Usage, per-core matrix with P/E groups, graph, top apps, uptime, readouts and alerts match upstream. CPU temperature needs the opt-in PawnIO driver and administrator rights. Panel blocks show/hide but cannot be dragged into a new order; readouts have no bars style, spacing or hide-icon option and keep a fixed order.");
        r.Implements("monitorGPU", Parity.Full,
            "Task Manager's GPU engine counters for usage and per-app use; temperature from the WDDM driver (no admin).");
        r.Implements("monitorMemory", Parity.Partial,
            "Pressure is derived from available memory and commit charge (Windows has no pressure signal); shows committed, cached and page file instead of compressed memory.");
        r.Implements("monitorNetwork", Parity.Partial,
            "Live speed, graph, session totals, local IPv4 and the Cloudflare speed test. No per-app network list (needs ETW tracing) and no Wi-Fi name.");
        r.Implements("monitorDisk", Parity.Partial,
            "Drive picker, usage, live read/write with graph and tools. No SMART data and no eject/protection block.");
        r.Implements("monitorPower", Parity.Partial,
            "Charge, state, battery rate, time remaining, health, cycles and battery temperature where the battery reports them; Bluetooth peripheral batteries are listed under Connected Devices. Windows exposes no adapter wattage and no system draw while plugged in.");
        r.Implements("connectedDevices", Parity.Full,
            "External USB devices (built-in devices and hubs skipped) plus connected Bluetooth devices with battery level where Windows knows it.");
        r.Implements("fanControl", Parity.Partial,
            "Live RPM, manual level and one temperature curve per PC via LibreHardwareMonitor; needs the opt-in PawnIO driver, administrator rights and a supported fan controller. Single-sensor curve editor.");

        foreach (var id in Features) r.Service(id, () => new MonitorFeatureService(id));
        r.Service("fanControl", () => new FanControlService());

        r.PanelSection(new PanelSectionDef("system", "System", Glyphs.Cpu, 30,
            ["monitorCPU", "monitorGPU", "monitorMemory", "monitorPower"], SystemView.Build, () => MonitorPanel.OptionsButton("system")));
        r.PanelSection(new PanelSectionDef("network", "Network", Glyphs.Globe, 40,
            ["monitorNetwork"], NetworkView.Build, () => MonitorPanel.OptionsButton("network")));
        r.PanelSection(new PanelSectionDef("disk", "Disk", Glyphs.HardDrive, 50,
            ["monitorDisk"], DiskView.Build, () => MonitorPanel.OptionsButton("disk")));
        r.PanelSection(new PanelSectionDef("power", "Power", Glyphs.Bolt, 60,
            ["monitorPower", "connectedDevices"], PowerView.Build, () => MonitorPanel.OptionsButton("power")));
        r.PanelSection(new PanelSectionDef("fanControl", "Fan Control", Glyphs.Fan, 70,
            ["fanControl"], FanControlView.Build));

        r.SettingsPage(new SettingsPageDef("monitor", SettingsCategory.Essentials, "Monitor", Glyphs.Cpu, 3, Features,
            MonitorSettingsPage.Build,
            ["cpu", "gpu", "memory", "ram", "network", "disk", "battery", "temperature", "readout", "notification area", "alerts", "graph", "sensors"]));
        r.SettingsPage(new SettingsPageDef("fanControl", SettingsCategory.Essentials, "Fan Control", Glyphs.Fan, 4, ["fanControl"],
            FanControlSettingsPage.Build, ["fan", "rpm", "curve", "cooling", "pawnio"]));

        r.Action(new ToolAction("monitor.speedTest", "monitorNetwork", "Run network speed test", MonitorGlyphs.Gauge, () =>
        {
            SpeedTest.Start();
            PanelHost.Show("network");
        }));
        r.Action(new ToolAction("fanControl.system", "fanControl", "Return fans to system control", Glyphs.Fan,
            () => FanController.ReturnToSystem()));
    }
}
