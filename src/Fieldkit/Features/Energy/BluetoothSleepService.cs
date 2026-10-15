// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Windows.Devices.Radios;

namespace Fieldkit.Features.Energy;

/// <summary>Pure decisions of Bluetooth on sleep (upstream BluetoothSleepSupport).</summary>
public static class BluetoothSleepLogic
{
    /// <summary>On sleep: turn off when on; a restore is owed only when we turned it off and restore is wanted.</summary>
    public static (bool TurnOff, bool OwesRestore) SleepPlan(bool isPoweredOn, bool restoresOnWake) =>
        isPoweredOn ? (true, restoresOnWake) : (false, false);

    /// <summary>On wake (or launch): turn back on only when owed and still off.</summary>
    public static bool Restores(bool owes, bool isOn) => owes && !isOn;
}

/// <summary>
/// Bluetooth on sleep: switches the Bluetooth radio off when Windows suspends and back
/// on at resume, only when this app was the one that switched it off. The debt is
/// persisted, so a PC shut down while asleep gets Bluetooth back at the next launch.
/// </summary>
public sealed class BluetoothSleepService : FeatureService
{
    public const string Id = "bluetoothSleep";
    public const string EnabledKey = "bluetoothSleep.enabled";
    public const string RestoreOnWakeKey = "bluetoothSleep.restoreOnWake";
    public const string RestorePendingKey = "bluetoothSleep.restorePending";

    private IntPtr _registration;
    private EnergyNative.DeviceNotifyCallback? _callback;

    public override string FeatureId => Id;
    public override IReadOnlyList<string> EnableKeys => [EnabledKey, RestorePendingKey];
    public override bool EnabledByDefault => false;

    public override bool ShouldRun() =>
        Settings.GetBool(EnabledKey, false) || (!IsRunning && Settings.GetBool(RestorePendingKey, false));

    /// <summary>Whether the PC has a Bluetooth radio (blocking; call off the UI thread when possible).</summary>
    public static bool HasController()
    {
        try { return FindRadio(TimeSpan.FromSeconds(3)) is not null; }
        catch { return false; }
    }

    protected override void OnStart()
    {
        // Launch / settings sync: settle any debt first, even if the feature is now off.
        Task.Run(RestoreIfOwed);
        if (!Settings.GetBool(EnabledKey, false))
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => FeatureRuntime.Reevaluate(Id));
            return;
        }
        _callback = OnPowerEvent;
        var parameters = new EnergyNative.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_callback),
            Context = IntPtr.Zero,
        };
        var status = EnergyNative.PowerRegisterSuspendResumeNotification(EnergyNative.DEVICE_NOTIFY_CALLBACK, ref parameters, out _registration);
        if (status != 0) Log.Warn($"PowerRegisterSuspendResumeNotification failed: {status}");
    }

    protected override void OnStop()
    {
        if (_registration != IntPtr.Zero) EnergyNative.PowerUnregisterSuspendResumeNotification(_registration);
        _registration = IntPtr.Zero;
        _callback = null;
    }

    // Runs on a system thread. Suspend gives us about two seconds.
    private uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            switch (type)
            {
                case EnergyNative.PBT_APMSUSPEND:
                    OnSleep();
                    break;
                case EnergyNative.PBT_APMRESUMEAUTOMATIC:
                case EnergyNative.PBT_APMRESUMESUSPEND:
                    Task.Run(async () =>
                    {
                        await Task.Delay(1000); // let the radio stack come back
                        RestoreIfOwed();
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth on sleep failed", ex);
        }
        return 0;
    }

    private static void OnSleep()
    {
        var radio = FindRadio(TimeSpan.FromSeconds(1));
        if (radio is null) return;
        var (turnOff, owes) = BluetoothSleepLogic.SleepPlan(radio.State == RadioState.On, Settings.GetBool(RestoreOnWakeKey, true));
        Settings.Set(RestorePendingKey, owes);
        Settings.Flush();
        if (!turnOff) return;
        var result = Wait(radio.SetStateAsync(RadioState.Off).AsTask(), TimeSpan.FromMilliseconds(1500));
        Log.Info($"Bluetooth off before sleep: {result}");
    }

    private static void RestoreIfOwed()
    {
        try
        {
            var owes = Settings.GetBool(RestorePendingKey, false);
            if (!owes) return;
            var radio = FindRadio(TimeSpan.FromSeconds(5));
            // Clear the debt first: if the user turned Bluetooth on meanwhile, it is settled.
            Settings.Set(RestorePendingKey, false);
            if (radio is null || !BluetoothSleepLogic.Restores(owes, radio.State == RadioState.On)) return;
            var result = Wait(radio.SetStateAsync(RadioState.On).AsTask(), TimeSpan.FromSeconds(5));
            Log.Info($"Bluetooth back on after wake: {result}");
        }
        catch (Exception ex)
        {
            Log.Error("Restoring Bluetooth failed", ex);
        }
    }

    private static Radio? FindRadio(TimeSpan timeout)
    {
        var access = Wait(Radio.RequestAccessAsync().AsTask(), timeout);
        if (access != RadioAccessStatus.Allowed) return null;
        var radios = Wait(Radio.GetRadiosAsync().AsTask(), timeout);
        return radios?.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
    }

    private static T? Wait<T>(Task<T> task, TimeSpan timeout)
    {
        try { return task.Wait(timeout) ? task.Result : default; }
        catch { return default; }
    }
}
