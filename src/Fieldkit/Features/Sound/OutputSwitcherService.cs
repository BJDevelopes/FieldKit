// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// Cycles the default output through the devices chosen for the cycle (upstream
/// SoundOutputSwitcher). Holds the global shortcut only while "Switch outputs with shortcut" is on.
/// </summary>
internal sealed class OutputSwitcherService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "soundOutputSwitcher";
    public override IReadOnlyList<string> EnableKeys => [SoundKeys.SwitcherEnabled];
    public override bool EnabledByDefault => false;

    /// <summary>The last shortcut press found no available output in the cycle.</summary>
    public static bool LastSwitchFailed { get; private set; }

    protected override void OnStart() =>
        _hotkey = Hotkeys.Bind(SoundKeys.SwitcherShortcut, SoundKeys.SwitcherDefaultShortcut, () => SwitchToNext());

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        LastSwitchFailed = false;
    }

    /// <summary>Moves the default output to the next selected device and names it in a HUD.</summary>
    public static bool SwitchToNext()
    {
        var devices = AudioEndpoints.List(EDataFlow.Render);
        var available = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var selected = SoundKeys.GetList(SoundKeys.SwitcherDevices);
        var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
        var next = SoundLogic.NextSelectedOutput(current, selected, available);
        if (next is null)
        {
            // With an available selection, no next output means the only one is already playing.
            LastSwitchFailed = !selected.Any(available.Contains);
            if (LastSwitchFailed) SoundHud.Show(Glyphs.Warning, "Select at least one available output.", "Orange");
            else if (devices.FirstOrDefault(d => d.IsDefault) is { } only) Announce(only);
            return !LastSwitchFailed;
        }
        LastSwitchFailed = false;
        var device = devices.First(d => d.Id == next);
        if (!AudioEndpoints.SetDefault(next))
        {
            SoundHud.Show(Glyphs.Warning, $"Could not switch: {device.Name}", "Orange");
            return false;
        }
        Announce(device);
        return true;
    }

    private static void Announce(AudioDevice device) =>
        SoundHud.Show(device.IsHeadphones ? Glyphs.Headphones : Glyphs.Speaker, device.Name);
}

/// <summary>
/// "Lower volume when headphones disconnect": when the default output was headphones and
/// they go away, the device Windows falls back to gets the chosen level; when headphones
/// come back, that device gets its earlier level again if nobody changed it since.
/// </summary>
internal sealed class HeadphoneGuardService : FeatureService
{
    private IDisposable? _watch;
    private List<AudioDevice> _devices = [];
    private string? _lastLoweredId;
    private (string Id, float Previous, float Applied)? _lowered;

    public override string FeatureId => "soundOutputSwitcher";
    public override IReadOnlyList<string> EnableKeys => [SoundKeys.LowerOnHeadphonesDisconnect];
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        _devices = AudioEndpoints.List(EDataFlow.Render);
        _watch = AudioDeviceWatcher.Subscribe(OnDevicesChanged);
    }

    protected override void OnStop()
    {
        _watch?.Dispose();
        _watch = null;
        _devices = [];
        _lowered = null;
        _lastLoweredId = null;
    }

    private void OnDevicesChanged()
    {
        var previous = _devices;
        var next = AudioEndpoints.List(EDataFlow.Render);
        _devices = next;
        var previousDefault = previous.FirstOrDefault(d => d.IsDefault);
        var nextDefault = next.FirstOrDefault(d => d.IsDefault);
        var action = SoundLogic.EvaluateHeadphoneGuard(
            enabled: true,
            previousDefault?.Id, previousDefault?.IsHeadphones == true,
            previousStillPresent: previousDefault is not null && next.Any(d => d.Id == previousDefault.Id),
            nextDefault?.Id, nextDefault?.IsHeadphones == true,
            _lastLoweredId);

        switch (action)
        {
            case HeadphoneGuardAction.Restore:
                _lastLoweredId = null;
                if (_lowered is { } lowered && next.Any(d => d.Id == lowered.Id))
                {
                    if (SoundLogic.ShouldRestoreVolume(lowered.Applied, AudioEndpoints.GetVolume(lowered.Id)))
                        AudioEndpoints.SetVolume(lowered.Id, lowered.Previous);
                    _lowered = null;
                }
                break;
            case HeadphoneGuardAction.Lower when nextDefault is not null:
                var target = SoundLogic.SanitizedHeadphoneVolume(
                    (int)Math.Round(Settings.GetDouble(SoundKeys.HeadphonesVolume, SoundLogic.DefaultHeadphoneVolumePercent))) / 100f;
                var before = AudioEndpoints.GetVolume(nextDefault.Id);
                if (AudioEndpoints.SetVolume(nextDefault.Id, target))
                {
                    _lastLoweredId = nextDefault.Id;
                    if (before is { } b && b > target) _lowered = (nextDefault.Id, b, target);
                    Log.Info($"Headphones disconnected; {nextDefault.Name} lowered to {target:P0}");
                }
                break;
        }
    }
}
