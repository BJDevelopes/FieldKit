// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>
/// Extra brightness (beta): raises the SDR content brightness of displays running in HDR
/// toward Windows' 480-nit maximum, using the display-config white-level request the
/// Settings app uses. The user's own level is saved first and restored on stop (and at
/// the next launch after a crash).
/// </summary>
public sealed class ExtraBrightnessService : FeatureService
{
    public const string Id = "extraBrightness";
    public const string EnabledKey = "extraBrightness.enabled";
    public const string LevelKey = "extraBrightness.level";
    public const string RestoreKey = "extraBrightness.restore";

    private IDisposable? _levelObserver;
    private DispatcherTimer? _debounce;

    public override string FeatureId => Id;
    public override IReadOnlyList<string> EnableKeys => [EnabledKey, RestoreKey];
    public override bool EnabledByDefault => false;

    /// <summary>Also runs (briefly) when a previous run left boosted displays behind, to restore them.</summary>
    public override bool ShouldRun() => Settings.GetBool(EnabledKey, false) || (!IsRunning && Settings.Has(RestoreKey));

    /// <summary>True when at least one display has HDR turned on.</summary>
    public static bool Supported()
    {
        try { return DisplayInventory.Enumerate().Any(d => DisplayInventory.ReadHdr(d).HdrOn); }
        catch { return false; }
    }

    protected override void OnStart()
    {
        if (!Settings.GetBool(EnabledKey, false))
        {
            RestoreAll();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => FeatureRuntime.Reevaluate(Id));
            return;
        }
        _levelObserver = Settings.Observe(ApplyAll, LevelKey);
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        ApplyAll();
    }

    protected override void OnStop()
    {
        _levelObserver?.Dispose();
        _levelObserver = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _debounce?.Stop();
        RestoreAll();
    }

    private Dictionary<string, uint> Saved() => Settings.Get<Dictionary<string, uint>?>(RestoreKey, null) ?? [];

    private void ApplyAll()
    {
        if (!Settings.GetBool(EnabledKey, false)) return;
        var level = Math.Clamp(Settings.GetInt(LevelKey, 100), 10, 100);
        var saved = Saved();
        var changed = false;
        foreach (var d in DisplayInventory.Enumerate())
        {
            var (hdr, white) = DisplayInventory.ReadHdr(d);
            if (!hdr || white == 0) continue;
            if (!saved.ContainsKey(d.Key))
            {
                saved[d.Key] = white;
                changed = true;
            }
            var target = DisplayMath.BoostedWhiteLevel(saved[d.Key], level);
            if (target != white && !DisplayInventory.WriteSdrWhiteLevel(d, target))
                Log.Warn($"Setting the SDR white level of {d.Name} failed");
        }
        if (changed)
        {
            Settings.Set(RestoreKey, saved);
            Settings.Flush();
        }
    }

    private void RestoreAll()
    {
        var saved = Saved();
        if (saved.Count == 0)
        {
            Settings.Remove(RestoreKey);
            return;
        }
        var displays = DisplayInventory.Enumerate();
        foreach (var (key, white) in saved.ToList())
        {
            var d = displays.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (d is null) continue; // not connected now: leave it saved for later
            if (DisplayInventory.WriteSdrWhiteLevel(d, white)) saved.Remove(key);
        }
        // Forget displays that cannot be restored now rather than retrying forever.
        Settings.Remove(RestoreKey);
        if (saved.Count > 0) Log.Warn($"Extra brightness: {saved.Count} display(s) were not connected to restore");
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => Ui.OnUi(() =>
    {
        _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Start();
    });

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        if (IsRunning) ApplyAll();
    }
}
