// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;
using static Fieldkit.Features.Energy.EnergyNative;

namespace Fieldkit.Features.Energy;

/// <summary>Live state of one display (owned by the UI thread).</summary>
public sealed class DisplayState(DisplayDescriptor display)
{
    public DisplayDescriptor Display { get; set; } = display;
    public string Key => Display.Key;
    public string Name => Display.Name;
    public BrightnessRoute Route { get; set; } = BrightnessRoute.Software;
    public double Level { get; set; } = 1;
    public bool ExtendedDimming { get; set; }
    public bool ForcedSoftware { get; set; }
    /// <summary>DDC answered a brightness read.</summary>
    public bool DdcReadable { get; set; }
    public bool Probed { get; set; }
    /// <summary>Built-in speaker volume (0..1) when the monitor reports one over DDC/CI (VCP 0x62), else null.</summary>
    public double? SpeakerVolume { get; set; }
}

/// <summary>
/// Display brightness (upstream BrightnessService): WMI for laptop panels, DDC/CI for
/// external monitors on a dedicated thread with coalesced writes, and a dimming overlay
/// as the fallback and for "Extra dimming" below the monitor's minimum.
/// </summary>
public sealed class BrightnessService : FeatureService
{
    public const string Id = "brightness";
    public const string EnabledKey = "brightness.enabled";
    public const string OsdKey = "brightness.osdEnabled";
    public const string FollowPointerKey = "brightness.followPointer";
    public const string AllDisplaysKey = "brightness.shortcutsAllDisplays";
    public const string KeyStepKey = "brightness.keyStep";
    public const string DecreaseShortcutKey = "brightness.decreaseShortcut";
    public const string IncreaseShortcutKey = "brightness.increaseShortcut";
    public const string DefaultDecreaseShortcut = "Ctrl+Alt+-";
    public const string DefaultIncreaseShortcut = "Ctrl+Alt+=";
    public const string ExtendedDimmingPathsKey = "brightness.extendedDimmingPaths";
    public const string ForcedSoftwarePathsKey = "brightness.forcedSoftwarePaths";

    private const byte VcpLuminance = 0x10;
    private const byte VcpSpeakerVolume = 0x62; // MCCS "Audio: speaker volume" (upstream PR #2927)
    private readonly Dictionary<string, uint> _speakerMax = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Software levels remembered for this app session (upstream restores to 1 on a fresh start).</summary>
    private static readonly Dictionary<string, double> RememberedPicture = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, DimOverlay> _overlays = new(StringComparer.OrdinalIgnoreCase);
    // Worker-thread state.
    private readonly Dictionary<string, EnergyNative.PHYSICAL_MONITOR[]> _physical = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (uint Max, uint Last)> _ddc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _wmiInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastDdcCommand = new(StringComparer.OrdinalIgnoreCase);
    private SerialWorker? _worker;
    private DispatcherTimer? _rebuildDebounce;
    private int _generation;

    public override string FeatureId => Id;
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    public List<DisplayState> Displays { get; private set; } = [];

    public event Action? Changed;

    public static BrightnessService? Current =>
        FeatureRuntime.Service<BrightnessService>() is { IsRunning: true } s ? s : null;

    protected override void OnStart()
    {
        _worker = new SerialWorker("Fieldkit brightness");
        _subscriptions.Add(Hotkeys.Bind(DecreaseShortcutKey, string.Empty, () => StepPrimary(-1)));
        _subscriptions.Add(Hotkeys.Bind(IncreaseShortcutKey, string.Empty, () => StepPrimary(+1)));
        _subscriptions.Add(Settings.Observe(Refresh, ExtendedDimmingPathsKey, ForcedSoftwarePathsKey));
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Refresh();
    }

    protected override void OnStop()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _rebuildDebounce?.Stop();
        foreach (var o in _overlays.Values) o.Close();
        _overlays.Clear();
        BrightnessOsd.CloseInstance();
        var worker = _worker;
        _worker = null;
        worker?.Post(ReleasePhysicalMonitors);
        worker?.Dispose();
        Displays = [];
    }

    // ---- Public API --------------------------------------------------------------

    /// <summary>Re-reads every display (also picks up changes made by keys or the monitor's buttons).</summary>
    public void Refresh()
    {
        var generation = ++_generation;
        var extended = PathList(ExtendedDimmingPathsKey);
        var forced = PathList(ForcedSoftwarePathsKey);
        _worker?.Post(() =>
        {
            var states = Probe(extended, forced);
            Ui.OnUi(() =>
            {
                if (generation != _generation || !IsRunning) return;
                Displays = states;
                SyncOverlays();
                RaiseChanged();
            });
        });
    }

    /// <summary>Sets a display's level (0..1). The UI updates at once; the hardware write is coalesced.</summary>
    public void SetLevel(string key, double level, bool showOsd = false)
    {
        var state = Displays.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        if (state is null) return;
        level = DisplayMath.Clamp01(level);
        state.Level = level;
        var route = state.Route;
        var extended = state.ExtendedDimming;
        var display = state.Display;
        if (route == BrightnessRoute.Software)
        {
            RememberedPicture[key] = level;
            ApplyPicture(display, level);
        }
        else if (route == BrightnessRoute.Ddc && extended)
        {
            var (_, picture) = DisplayMath.ExtendedDimmingComponents(level);
            RememberedPicture[key] = level;
            if (picture >= 0.999) ApplyPicture(display, 1); // restore the picture first, then the hardware
        }
        _worker?.PostLatest(key, () => WriteHardware(display, route, extended, level));
        if (showOsd && Settings.GetBool(OsdKey, false)) BrightnessOsd.ShowLevel(display.Bounds, level);
        RaiseChanged();
    }

    /// <summary>Sets a monitor's built-in speaker volume (0..1); coalesced like brightness writes.</summary>
    public void SetSpeakerVolume(string key, double level)
    {
        var state = Displays.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        if (state?.SpeakerVolume is null) return;
        level = DisplayMath.Clamp01(level);
        state.SpeakerVolume = level;
        _worker?.PostLatest(key + "#speaker", () =>
        {
            if (!_physical.TryGetValue(key, out var monitors) || !_speakerMax.TryGetValue(key, out var max)) return;
            Pace(key);
            var value = (uint)Math.Round(level * max);
            foreach (var m in monitors) SetVCPFeature(m.hPhysicalMonitor, VcpSpeakerVolume, value);
        });
        RaiseChanged();
    }

    public static void SetExtendedDimming(string key, bool on) =>
        Settings.Set(ExtendedDimmingPathsKey, DisplayMath.UpdatedPathList(PathList(ExtendedDimmingPathsKey), key, on));

    public static void SetForcedSoftware(string key, bool on) =>
        Settings.Set(ForcedSoftwarePathsKey, DisplayMath.UpdatedPathList(PathList(ForcedSoftwarePathsKey), key, on));

    public static List<string> PathList(string key) => Settings.Get<List<string>?>(key, null) ?? [];

    // ---- Shortcuts ---------------------------------------------------------------

    /// <summary>One shortcut step on the primary display, the one under the pointer, or every display (upstream #3086).</summary>
    public void StepPrimary(int direction)
    {
        var step = DisplayMath.ParseStep(Settings.GetString(KeyStepKey, "standard"));
        var delta = DisplayMath.Limited(step, direction / 16.0);
        if (Settings.GetBool(AllDisplaysKey, false))
        {
            // Each display keeps its own level and moves by the same step; the OSD shows the primary one.
            var primary = Displays.FirstOrDefault(d => d.Display.IsPrimary) ?? Displays.FirstOrDefault();
            foreach (var display in Displays.ToList())
                SetLevel(display.Key, DisplayMath.Stepped(display.Level, delta), showOsd: display == primary);
            return;
        }
        DisplayState? target;
        if (Settings.GetBool(FollowPointerKey, false) && Native.GetCursorPos(out var p))
        {
            var monitor = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
            target = Displays.FirstOrDefault(d => d.Display.HMonitor == monitor);
        }
        else
        {
            target = Displays.FirstOrDefault(d => d.Display.IsPrimary) ?? Displays.FirstOrDefault();
        }
        if (target is null) return;
        SetLevel(target.Key, DisplayMath.Stepped(target.Level, delta), showOsd: true);
    }

    // ---- Overlays (UI thread) ----------------------------------------------------

    private void ApplyPicture(DisplayDescriptor display, double picture)
    {
        var opacity = DisplayMath.OverlayOpacity(picture);
        if (opacity <= 0)
        {
            if (_overlays.Remove(display.Key, out var old)) old.Close();
            return;
        }
        if (!_overlays.TryGetValue(display.Key, out var overlay))
        {
            overlay = new DimOverlay(display.Bounds);
            _overlays[display.Key] = overlay;
        }
        overlay.Apply(display.Bounds, opacity);
    }

    private void SyncOverlays()
    {
        foreach (var key in _overlays.Keys.ToList())
        {
            if (Displays.Any(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
            _overlays[key].Close();
            _overlays.Remove(key);
        }
        foreach (var d in Displays)
        {
            var picture = d.Route switch
            {
                BrightnessRoute.Software => d.Level,
                BrightnessRoute.Ddc when d.ExtendedDimming => DisplayMath.ExtendedDimmingComponents(d.Level).Picture,
                _ => 1,
            };
            ApplyPicture(d.Display, picture);
        }
    }

    // ---- Worker thread -----------------------------------------------------------

    private List<DisplayState> Probe(List<string> extended, List<string> forced)
    {
        ReleasePhysicalMonitors();
        _ddc.Clear();
        _wmiInstances.Clear();
        var descriptors = DisplayInventory.Enumerate();
        var wmi = descriptors.Any(d => d.IsInternal) ? WmiBrightness.Read() : [];
        var internalCount = descriptors.Count(d => d.IsInternal);
        var states = new List<DisplayState>();
        foreach (var d in descriptors)
        {
            var state = new DisplayState(d)
            {
                ExtendedDimming = !d.IsInternal && extended.Contains(d.Key, StringComparer.OrdinalIgnoreCase),
                ForcedSoftware = !d.IsInternal && forced.Contains(d.Key, StringComparer.OrdinalIgnoreCase),
                Probed = true,
            };
            var instance = DisplayMath.MatchWmiInstance(d.DevicePath, d.IsInternal, wmi.Keys.ToList(), internalCount);
            if (instance is not null)
            {
                _wmiInstances[d.Key] = instance;
                state.Route = BrightnessRoute.System;
                state.Level = wmi[instance] / 100.0;
            }
            else if (!state.ForcedSoftware && TryReadDdc(d, out var current, out var max))
            {
                state.Route = BrightnessRoute.Ddc;
                state.DdcReadable = true;
                _ddc[d.Key] = (max, current);
                state.SpeakerVolume = TryReadSpeaker(d.Key);
                var hardware = DisplayMath.Normalized(current, max);
                if (state.ExtendedDimming)
                {
                    RememberedPicture.TryGetValue(d.Key, out var remembered);
                    var dimmed = RememberedPicture.ContainsKey(d.Key) && remembered < DisplayMath.ExtendedDimmingRange;
                    state.Level = DisplayMath.ExtendedDimmingLevel(hardware, dimmed ? remembered : null, dimmed);
                }
                else
                {
                    state.Level = hardware;
                }
            }
            else
            {
                state.Route = BrightnessRoute.Software;
                state.Level = RememberedPicture.TryGetValue(d.Key, out var remembered) ? remembered : 1;
            }
            states.Add(state);
        }
        return states;
    }

    private bool TryReadDdc(DisplayDescriptor d, out uint current, out uint max)
    {
        current = max = 0;
        if (d.IsInternal) return false;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(d.HMonitor, out var count) || count == 0) return false;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(d.HMonitor, count, monitors)) return false;
            _physical[d.Key] = monitors;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Pace(d.Key);
                if (GetVCPFeatureAndVCPFeatureReply(monitors[0].hPhysicalMonitor, VcpLuminance, out _, out current, out max) && max > 0)
                    return true;
                Thread.Sleep(50);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"DDC probe of {d.Name} failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>The monitor's speaker volume, or null when it has no speakers or does not report them.</summary>
    private double? TryReadSpeaker(string key)
    {
        _speakerMax.Remove(key);
        if (!_physical.TryGetValue(key, out var monitors)) return null;
        try
        {
            Pace(key);
            if (GetVCPFeatureAndVCPFeatureReply(monitors[0].hPhysicalMonitor, VcpSpeakerVolume, out _, out var current, out var max) && max > 0)
            {
                _speakerMax[key] = max;
                return DisplayMath.Normalized(current, max);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"DDC speaker read failed: {ex.Message}");
        }
        return null;
    }

    private void WriteHardware(DisplayDescriptor display, BrightnessRoute route, bool extended, double level)
    {
        switch (route)
        {
            case BrightnessRoute.System when _wmiInstances.TryGetValue(display.Key, out var instance):
                WmiBrightness.Write(instance, DisplayMath.WholePercent(level));
                break;
            case BrightnessRoute.Ddc when _physical.TryGetValue(display.Key, out var monitors) && _ddc.TryGetValue(display.Key, out var ddc):
            {
                var (hardware, picture) = extended ? DisplayMath.ExtendedDimmingComponents(level) : (level, 1.0);
                var value = DisplayMath.DeviceValue(hardware, ddc.Max);
                if (value != ddc.Last)
                {
                    var ok = false;
                    for (var attempt = 0; attempt < 5 && !ok; attempt++)
                    {
                        Pace(display.Key);
                        foreach (var m in monitors) ok |= SetVCPFeature(m.hPhysicalMonitor, VcpLuminance, value);
                        if (!ok) Thread.Sleep(20);
                    }
                    if (ok) _ddc[display.Key] = (ddc.Max, value);
                }
                if (extended && picture < 0.999) Ui.OnUi(() => ApplyPicture(display, picture));
                break;
            }
        }
    }

    /// <summary>DDC/CI wants ~50 ms between commands to the same monitor.</summary>
    private void Pace(string key)
    {
        var now = Environment.TickCount64;
        if (_lastDdcCommand.TryGetValue(key, out var last))
        {
            var wait = 50 - (now - last);
            if (wait > 0) Thread.Sleep((int)wait);
        }
        _lastDdcCommand[key] = Environment.TickCount64;
    }

    private void ReleasePhysicalMonitors()
    {
        foreach (var monitors in _physical.Values)
        {
            try { DestroyPhysicalMonitors((uint)monitors.Length, monitors); }
            catch { }
        }
        _physical.Clear();
    }

    // ---- System events -----------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Ui.OnUi(() => Debounce(TimeSpan.FromMilliseconds(500)));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Ui.OnUi(() => Debounce(TimeSpan.FromSeconds(3)));
    }

    private void Debounce(TimeSpan delay)
    {
        if (!IsRunning) return;
        _rebuildDebounce ??= new DispatcherTimer();
        _rebuildDebounce.Stop();
        _rebuildDebounce.Interval = delay;
        _rebuildDebounce.Tick -= OnRebuildTick;
        _rebuildDebounce.Tick += OnRebuildTick;
        _rebuildDebounce.Start();
    }

    private void OnRebuildTick(object? sender, EventArgs e)
    {
        _rebuildDebounce?.Stop();
        Refresh();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Brightness observer failed", ex); }
    }
}
