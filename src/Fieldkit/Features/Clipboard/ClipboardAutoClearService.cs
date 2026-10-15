// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Empties the system clipboard on four independent, opt-in triggers: a delay after the last copy,
/// sleep, display sleep and screen lock. Saved history entries are never touched. Independent of
/// "Save clipboard history": someone who keeps no history is exactly who wants a password gone.
/// Event driven: the clipboard listener restarts a one-shot timer, so nothing polls.
/// </summary>
public sealed class ClipboardAutoClearService : FeatureService
{
    public const string OnDelayKey = "clipboardHistory.autoClearOnDelay";
    public const string DelayKey = "clipboardHistory.autoClearDelay";
    public const string OnSleepKey = "clipboardHistory.autoClearOnSleep";
    public const string OnDisplaySleepKey = "clipboardHistory.autoClearOnDisplaySleep";
    public const string OnLockKey = "clipboardHistory.autoClearOnLock";

    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private IDisposable? _subscription;
    private IDisposable? _observer;
    private DispatcherTimer? _timer;
    private HwndSource? _powerWindow;
    private IntPtr _displayNotification;
    private uint _lastSequence;
    private uint? _lastCleared;
    private DateTime _lastChange = DateTime.Now;
    private bool _systemEvents;

    public override string FeatureId => ClipboardHistory.FeatureId;

    public override IReadOnlyList<string> EnableKeys => [OnDelayKey, OnSleepKey, OnDisplaySleepKey, OnLockKey];

    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        Sync();
        _observer = Settings.Observe(Sync, OnDelayKey, DelayKey, OnSleepKey, OnDisplaySleepKey, OnLockKey);
    }

    protected override void OnStop()
    {
        _observer?.Dispose();
        _observer = null;
        StopDelay();
        StopSystemEvents();
        StopDisplayWatch();
    }

    private void Sync()
    {
        if (Settings.GetBool(OnDelayKey)) StartDelay();
        else StopDelay();
        if (Settings.GetBool(OnSleepKey) || Settings.GetBool(OnLockKey)) StartSystemEvents();
        else StopSystemEvents();
        if (Settings.GetBool(OnDisplaySleepKey)) StartDisplayWatch();
        else StopDisplayWatch();
    }

    // ---------------------------------------------------------------- delay

    private void StartDelay()
    {
        if (_subscription is not null)
        {
            Arm();
            return;
        }
        // Turning it on (or starting the app) gives what is already there a full delay.
        _lastSequence = Native.GetClipboardSequenceNumber();
        _lastChange = DateTime.Now;
        _subscription = ClipboardLane.Subscribe((change, _) => UI.Ui.OnUi(() => OnChange(change)), priority: 300);
        Arm();
    }

    private void StopDelay()
    {
        _subscription?.Dispose();
        _subscription = null;
        _timer?.Stop();
        _timer = null;
    }

    private void OnChange(ClipboardChange change)
    {
        if (_subscription is null) return;
        if (change.OwnWrite == OwnWriteKind.Clear) _lastCleared = change.Sequence;
        Evaluate();
    }

    private TimeSpan Delay => TimeSpan.FromSeconds(AutoClearLogic.SanitizedDelay(Settings.GetInt(DelayKey, AutoClearLogic.DefaultDelaySeconds)));

    private void Evaluate()
    {
        if (_subscription is null) return;
        var now = DateTime.Now;
        var sequence = Native.GetClipboardSequenceNumber();
        switch (AutoClearLogic.Decide(sequence, _lastSequence, _lastCleared, _lastChange, now, Delay))
        {
            case AutoClearDecision.NoteChange:
                _lastSequence = sequence;
                _lastChange = now;
                break;
            case AutoClearDecision.Clear:
                ClearNow(OnDelayKey);
                return;
        }
        // Our own clear left nothing to wait for; the listener reports the next copy.
        if (sequence == _lastCleared)
        {
            _timer?.Stop();
            return;
        }
        Arm();
    }

    private void Arm()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background);
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        var remaining = _lastChange + Delay - DateTime.Now;
        _timer.Interval = remaining < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : remaining;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        Evaluate();
    }

    private void ClearNow(string triggerKey)
    {
        if (!IsRunning || !Settings.GetBool(triggerKey)) return;
        ClipboardLane.Run(access =>
        {
            var ok = access.Clear();
            return (ok, access.Sequence);
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            UI.Ui.OnUi(() =>
            {
                if (t.Result.ok)
                {
                    _lastCleared = t.Result.Sequence;
                    _lastSequence = t.Result.Sequence;
                    _lastChange = DateTime.Now;
                    Log.Info("Clipboard cleared automatically");
                }
            });
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------- sleep / lock / display

    private void StartSystemEvents()
    {
        if (_systemEvents) return;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _systemEvents = true;
    }

    private void StopSystemEvents()
    {
        if (!_systemEvents) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _systemEvents = false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) UI.Ui.OnUi(() => ClearNow(OnSleepKey));
        else if (e.Mode == PowerModes.Resume) UI.Ui.OnUi(Evaluate);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) UI.Ui.OnUi(() => ClearNow(OnLockKey));
    }

    private void StartDisplayWatch()
    {
        if (_powerWindow is not null) return;
        _powerWindow = new HwndSource(new HwndSourceParameters("FieldkitClipboardPower")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        });
        _powerWindow.AddHook(PowerProc);
        var guid = ConsoleDisplayState;
        _displayNotification = RegisterPowerSettingNotification(_powerWindow.Handle, ref guid, 0);
    }

    private void StopDisplayWatch()
    {
        if (_displayNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotification);
        _displayNotification = IntPtr.Zero;
        _powerWindow?.Dispose();
        _powerWindow = null;
    }

    private IntPtr PowerProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            // Data: 0 = display off, 1 = on, 2 = dimmed.
            if (setting.PowerSetting == ConsoleDisplayState && setting.Data == 0) ClearNow(OnDisplaySleepKey);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
