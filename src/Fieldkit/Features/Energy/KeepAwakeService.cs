// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

public enum AwakeTrigger { Manual, Automation }

public enum SessionEndReason { Manual, Timer, Battery, Quit }

/// <summary>
/// Keep awake sessions (upstream KeepAwakeManager): timed / until / indefinite sessions,
/// automations, battery protection, pause while locked, pointer nudge and the
/// "keep working with the laptop lid closed" override with crash-safe restore.
/// Lives on the UI thread; the execution-state request is held by a dedicated thread.
/// </summary>
public sealed class KeepAwakeService : FeatureService
{
    public const string Id = "keepAwake";
    public const string DefaultDurationKey = "keepAwake.defaultDuration";
    public const string BatteryLimitKey = "keepAwake.batteryLimit";
    public const string AutoStartKey = "keepAwake.autoStart";
    public const string AllowDisplaySleepKey = "keepAwake.allowDisplaySleep";
    public const string JiggleKey = "keepAwake.jiggle";
    public const string JiggleIntervalKey = "keepAwake.jiggleInterval";
    public const string AutoExternalDisplayKey = "keepAwake.autoExternalDisplay";
    public const string AutoPowerKey = "keepAwake.autoPower";
    public const string AutoAppsKey = "keepAwake.autoApps";
    public const string AutoAgentsKey = "keepAwake.autoAgents";
    public const string AppsKey = "keepAwake.apps";
    public const string RequireAllKey = "keepAwake.requireAll";
    public const string PauseWhenLockedKey = "keepAwake.pauseWhenLocked";
    public const string SwitchUsesUntilKey = "keepAwake.switchUsesUntil";
    public const string UntilTimeKey = "keepAwake.untilTime";
    public const string LidClosedKey = "keepAwake.lidClosed";
    public const string DimOnLidCloseKey = "keepAwake.dimOnLidClose";
    public const string ShortcutKey = "keepAwake.shortcut";
    public const string DefaultShortcut = "Ctrl+Alt+Win+A";
    public const string DimRestoreKey = "keepAwake.restore.dim";

    private static bool _launchHandled;

    private readonly ExecutionStateKeeper _keeper = new();
    private readonly List<IDisposable> _subscriptions = [];
    private DispatcherTimer? _endTimer;
    private DispatcherTimer? _batteryTimer;
    private DispatcherTimer? _jiggleTimer;
    private DispatcherTimer? _automationTimer;
    private HwndSource? _powerWindow;
    private IntPtr _lidNotification;
    private bool _locked;
    private bool _suppressed;
    private bool _lidClosed;
    private bool _evaluating;

    public override string FeatureId => Id;

    // ---- Observable state ------------------------------------------------------

    public bool IsActive { get; private set; }
    public AwakeTrigger Trigger { get; private set; }
    public DateTime? End { get; private set; }
    /// <summary>The chip that started the session (minutes, 0 = indefinite), or null for until/automation.</summary>
    public int? Preset { get; private set; }
    public bool IsUntilSession { get; private set; }
    public AwakeCondition Matching { get; private set; }
    public bool IsPaused => IsActive && _locked && Settings.GetBool(PauseWhenLockedKey, false);
    public bool LidOverrideActive => LidCloseOverride.IsApplied;
    public bool LidSetupFailed { get; private set; }
    public static bool LidPresent { get; } = EnergyNative.LidPresent();

    /// <summary>Raised on the UI thread whenever the session or its status changes.</summary>
    public event Action? Changed;

    /// <summary>The running service, or null when the feature is off.</summary>
    public static KeepAwakeService? Current =>
        FeatureRuntime.Service<KeepAwakeService>() is { IsRunning: true } s ? s : null;

    // ---- Lifetime ----------------------------------------------------------------

    protected override void OnStart()
    {
        // Crash recovery: a previous run may have left the lid action or the panel dimmed.
        if (LidCloseOverride.IsApplied) LidCloseOverride.Restore();
        RestoreDimmedPanel();

        _subscriptions.Add(Hotkeys.Bind(ShortcutKey, DefaultShortcut, Toggle));
        _subscriptions.Add(Settings.Observe(OnOptionsChanged, AllowDisplaySleepKey, JiggleKey, JiggleIntervalKey,
            BatteryLimitKey, PauseWhenLockedKey, LidClosedKey, DimOnLidCloseKey));
        _subscriptions.Add(Settings.Observe(AutomationPreferencesChanged, AutoExternalDisplayKey, AutoPowerKey, AutoAppsKey, AutoAgentsKey, AppsKey, RequireAllKey));
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        FeatureRuntime.AvailabilityChanged += Evaluate;
        RegisterLidNotifications();

        UpdateAutomationTimer();
        if (!_launchHandled)
        {
            _launchHandled = true;
            if (Settings.GetBool(AutoStartKey, false))
                Activate(KeepAwakeLogic.SanitizedDuration(Settings.GetInt(DefaultDurationKey, 0)), fromChip: false);
        }
        Evaluate();
    }

    protected override void OnStop()
    {
        Deactivate(SessionEndReason.Quit);
        TrayStatus.Set(Id, null);
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        FeatureRuntime.AvailabilityChanged -= Evaluate;
        _automationTimer?.Stop();
        _automationTimer = null;
        UnregisterLidNotifications();
        _keeper.Dispose();
        if (LidCloseOverride.IsApplied) LidCloseOverride.Restore();
        RestoreDimmedPanel();
    }

    // ---- Public actions ----------------------------------------------------------

    /// <summary>Starts a manual session of <paramref name="minutes"/> (0 = indefinite).</summary>
    public void Activate(int minutes, bool fromChip = true)
    {
        minutes = Math.Max(0, minutes);
        _suppressed = false;
        if (fromChip)
        {
            Settings.Set(DefaultDurationKey, KeepAwakeLogic.SanitizedDuration(minutes));
            Settings.Set(SwitchUsesUntilKey, false);
        }
        Begin(AwakeTrigger.Manual, minutes == 0 ? null : DateTime.Now.AddMinutes(minutes), minutes, until: false);
    }

    /// <summary>Starts a manual session that ends at <paramref name="end"/>.</summary>
    public void ActivateUntil(DateTime end)
    {
        _suppressed = false;
        Settings.Set(SwitchUsesUntilKey, true);
        Settings.Set(UntilTimeKey, end);
        Begin(AwakeTrigger.Manual, end, null, until: true);
    }

    /// <summary>The main switch: restarts the saved until-time when still ahead, else the default duration.</summary>
    public void StartLastPick()
    {
        if (Settings.GetBool(SwitchUsesUntilKey, false) && Settings.Get<DateTime?>(UntilTimeKey, null) is { } until && until > DateTime.Now)
        {
            ActivateUntil(until);
            return;
        }
        var minutes = KeepAwakeLogic.SanitizedDuration(Settings.GetInt(DefaultDurationKey, 0));
        _suppressed = false;
        Begin(AwakeTrigger.Manual, minutes == 0 ? null : DateTime.Now.AddMinutes(minutes), minutes, until: false);
    }

    /// <summary>Stops the session, or starts the last pick.</summary>
    public void Toggle()
    {
        if (IsActive) Stop();
        else StartLastPick();
    }

    /// <summary>User stop. Suppresses automation so it does not immediately restart the session.</summary>
    public void Stop()
    {
        if (!IsActive) return;
        if (Trigger == AwakeTrigger.Automation || AutomationSatisfied()) _suppressed = true;
        Deactivate(SessionEndReason.Manual);
    }

    public void Extend(int minutes)
    {
        if (!IsActive || End is null) return;
        End = KeepAwakeLogic.Extended(End, DateTime.Now, minutes);
        ArmEndTimer();
        RaiseChanged();
    }

    /// <summary>Status text shown under the switch.</summary>
    public string StatusText()
    {
        if (!IsActive) return "The PC follows its normal energy rules";
        if (Trigger == AwakeTrigger.Automation) return KeepAwakeLogic.AutomationStatus(Matching);
        if (End is { } end) return "Ends in " + KeepAwakeLogic.Remaining(end - DateTime.Now);
        return "Active until you turn it off";
    }

    /// <summary>"Battery at N%..." when battery protection would end any session right away.</summary>
    public static int? BatteryBlockingPercent()
    {
        var limit = KeepAwakeLogic.SanitizedBatteryLimit(Settings.GetInt(BatteryLimitKey, 10));
        var (onBattery, percent) = ReadBattery();
        return KeepAwakeLogic.BatteryCutoff(limit, onBattery, percent) ? percent : null;
    }

    public static bool HasBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return false;
        return s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }

    /// <summary>Enables or disables the closed-lid preference (applies immediately during a session).</summary>
    public void SetLidPreference(bool on)
    {
        LidSetupFailed = false;
        Settings.Set(LidClosedKey, on);
    }

    // ---- Session core ------------------------------------------------------------

    private void Begin(AwakeTrigger trigger, DateTime? end, int? preset, bool until)
    {
        IsActive = true;
        Trigger = trigger;
        End = end;
        Preset = trigger == AwakeTrigger.Manual && !until ? preset : null;
        IsUntilSession = until;
        if (trigger == AwakeTrigger.Manual) Matching = AwakeCondition.None;
        ArmEndTimer();
        ApplyHolds();
        CheckBattery();
        RaiseChanged();
    }

    private void Deactivate(SessionEndReason reason)
    {
        if (!IsActive) return;
        var lidWasClosed = _lidClosed && LidCloseOverride.IsApplied;
        IsActive = false;
        End = null;
        Preset = null;
        IsUntilSession = false;
        Matching = AwakeCondition.None;
        _endTimer?.Stop();
        ReleaseHolds();
        RaiseChanged();
        switch (reason)
        {
            case SessionEndReason.Timer:
                Notifier.Show("Session ended", "Time is up. The PC will sleep normally again.");
                break;
            case SessionEndReason.Battery:
                Notifier.Show($"{AppInfo.Name} disabled", "Low battery. Normal sleep was restored to protect the charge.", warning: true);
                break;
        }
        if (lidWasClosed && reason != SessionEndReason.Quit)
        {
            // The lid is shut and Windows will not re-check it: ask for sleep ourselves.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!IsActive && _lidClosed) LidCloseOverride.RequestSleep();
            };
            t.Start();
        }
    }

    private void ApplyHolds()
    {
        if (!IsActive) return;
        if (IsPaused)
        {
            ReleaseHolds();
            return;
        }
        _keeper.Hold(display: !Settings.GetBool(AllowDisplaySleepKey, false));
        ApplyLidOverride();
        StartBatteryTimer();
        StartJiggleTimer();
    }

    private void ReleaseHolds()
    {
        _keeper.Release();
        _batteryTimer?.Stop();
        _jiggleTimer?.Stop();
        if (LidCloseOverride.IsApplied) LidCloseOverride.Restore();
        RestoreDimmedPanel();
    }

    private void ApplyLidOverride()
    {
        var wanted = IsActive && !IsPaused && Settings.GetBool(LidClosedKey, false);
        if (wanted && !LidCloseOverride.IsApplied)
        {
            if (!LidCloseOverride.Apply())
            {
                LidSetupFailed = true;
                Settings.Set(LidClosedKey, false);
            }
            else
            {
                LidSetupFailed = false;
                if (_lidClosed) DimPanelForClosedLid();
            }
        }
        else if (!wanted && LidCloseOverride.IsApplied)
        {
            LidCloseOverride.Restore();
            RestoreDimmedPanel();
        }
        if (LidCloseOverride.IsApplied && !Settings.GetBool(DimOnLidCloseKey, false)) RestoreDimmedPanel();
    }

    private void ArmEndTimer()
    {
        _endTimer?.Stop();
        if (End is not { } end) return;
        _endTimer ??= new DispatcherTimer();
        _endTimer.Tick -= OnEndTimer;
        _endTimer.Tick += OnEndTimer;
        var due = end - DateTime.Now;
        _endTimer.Interval = due < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : due > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : due;
        _endTimer.Start();
    }

    private void OnEndTimer(object? sender, EventArgs e)
    {
        if (End is not { } end || !IsActive)
        {
            _endTimer?.Stop();
            return;
        }
        if (DateTime.Now < end)
        {
            ArmEndTimer(); // long sessions re-arm every minute (survives clock changes and sleep)
            return;
        }
        if (IsPaused)
        {
            _endTimer?.Stop();
            return; // handled at unlock
        }
        TimerExpired();
    }

    private void TimerExpired()
    {
        if (Trigger == AwakeTrigger.Manual && HandOverToAutomation()) return;
        Deactivate(SessionEndReason.Timer);
    }

    /// <summary>A timed manual session continues as an automatic one when the automation wants it.</summary>
    private bool HandOverToAutomation()
    {
        if (!FeatureCatalog.IsAvailable(Id) || _suppressed || !AutomaticStartAllowed()) return false;
        var (enabled, matching) = ReadConditions();
        if (!KeepAwakeLogic.Satisfied(matching, enabled, Settings.GetBool(RequireAllKey, false))) return false;
        Trigger = AwakeTrigger.Automation;
        End = null;
        Preset = null;
        IsUntilSession = false;
        Matching = matching & enabled;
        _endTimer?.Stop();
        RaiseChanged();
        return true;
    }

    // ---- Battery -----------------------------------------------------------------

    private static (bool OnBattery, int? Percent) ReadBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return (false, null);
        var onBattery = s.ACLineStatus == 0;
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        if (s.BatteryFlag is 128 or 255) return (false, null);
        return (onBattery, percent);
    }

    private static bool AutomaticStartAllowed()
    {
        var limit = KeepAwakeLogic.SanitizedBatteryLimit(Settings.GetInt(BatteryLimitKey, 10));
        var (onBattery, percent) = ReadBattery();
        return KeepAwakeLogic.AutomaticStartAllowed(limit, onBattery, percent);
    }

    private void StartBatteryTimer()
    {
        _batteryTimer ??= CreateTimer(TimeSpan.FromSeconds(30), CheckBattery);
        _batteryTimer.Start();
    }

    private void CheckBattery()
    {
        if (!IsActive || IsPaused) return;
        if (BatteryBlockingPercent() is not null) Deactivate(SessionEndReason.Battery);
    }

    // ---- Pointer nudge -------------------------------------------------------------

    private void StartJiggleTimer()
    {
        _jiggleTimer?.Stop();
        if (!Settings.GetBool(JiggleKey, false)) return;
        var minutes = KeepAwakeLogic.SanitizedJiggleInterval(Settings.GetInt(JiggleIntervalKey, 5));
        _jiggleTimer ??= CreateTimer(TimeSpan.FromMinutes(minutes), Jiggle);
        _jiggleTimer.Interval = TimeSpan.FromMinutes(minutes);
        _jiggleTimer.Start();
    }

    private static void Jiggle()
    {
        if (!Native.GetCursorPos(out var start)) return;
        var area = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(start, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        var dx = start.X + 1 < area.Right - 2 ? 1 : -1;
        SendMove(dx, 0);
        var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            if (Native.GetCursorPos(out var now) && Math.Abs(now.X - (start.X + dx)) <= 2 && Math.Abs(now.Y - start.Y) <= 2)
                SendMove(-dx, 0);
        };
        back.Start();
    }

    private static void SendMove(int dx, int dy) => InputSender.Send(new Native.INPUT
    {
        type = Native.INPUT_MOUSE,
        U = new Native.InputUnion
        {
            mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = Native.MOUSEEVENTF_MOVE, dwExtraInfo = InputSender.Tag },
        },
    });

    // ---- Automation --------------------------------------------------------------

    public static AwakeCondition EnabledConditions()
    {
        var c = AwakeCondition.None;
        if (Settings.GetBool(AutoExternalDisplayKey, false)) c |= AwakeCondition.ExternalDisplay;
        if (Settings.GetBool(AutoPowerKey, false)) c |= AwakeCondition.Power;
        if (Settings.GetBool(AutoAppsKey, false) && SelectedApps().Count > 0) c |= AwakeCondition.RunningApps;
        if (Settings.GetBool(AutoAgentsKey, false)) c |= AwakeCondition.AgentsWorking;
        return c;
    }

    public static List<string> SelectedApps() => Settings.Get<List<string>?>(AppsKey, null) ?? [];

    private static (AwakeCondition Enabled, AwakeCondition Matching) ReadConditions()
    {
        var enabled = EnabledConditions();
        var matching = AwakeCondition.None;
        if (enabled.HasFlag(AwakeCondition.ExternalDisplay) && DisplayInventory.ExternalDisplayConnected())
            matching |= AwakeCondition.ExternalDisplay;
        if (enabled.HasFlag(AwakeCondition.Power) && Native.GetSystemPowerStatus(out var s) && s.ACLineStatus == 1)
            matching |= AwakeCondition.Power;
        if (enabled.HasFlag(AwakeCondition.RunningApps) && AppsRunning())
            matching |= AwakeCondition.RunningApps;
        if (enabled.HasFlag(AwakeCondition.AgentsWorking) && AgentsWorking())
            matching |= AwakeCondition.AgentsWorking;
        return (enabled, matching);
    }

    private static DateTime? _agentWorkSeen;

    /// <summary>
    /// Live work as the Dynamic Island's AI agents section sees it (Claude Code, Codex, OpenCode, Copilot from their
    /// local logs). Without that section running nothing is known, so this never matches.
    /// </summary>
    public static bool AgentTrackingAvailable => Features.Island.IslandHub.Agents is not null;

    private static bool AgentsWorking()
    {
        var now = DateTime.UtcNow;
        if (Features.Island.IslandHub.Agents?.Snapshot is { Live.Count: > 0 }) _agentWorkSeen = now;
        return KeepAwakeLogic.AgentsWorking(_agentWorkSeen, now);
    }

    private static bool AppsRunning()
    {
        var processes = Process.GetProcesses();
        try
        {
            return KeepAwakeLogic.AnyAppRunning(SelectedApps(), processes.Select(p => p.ProcessName));
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private bool AutomationSatisfied()
    {
        var (enabled, matching) = ReadConditions();
        return KeepAwakeLogic.Satisfied(matching, enabled, Settings.GetBool(RequireAllKey, false));
    }

    private void AutomationPreferencesChanged()
    {
        _suppressed = false;
        UpdateAutomationTimer();
        Evaluate();
    }

    private void UpdateAutomationTimer()
    {
        if (EnabledConditions() == AwakeCondition.None)
        {
            _automationTimer?.Stop();
            return;
        }
        _automationTimer ??= CreateTimer(TimeSpan.FromSeconds(4), Evaluate);
        _automationTimer.Start();
    }

    /// <summary>Upstream evaluateAutomation.</summary>
    private void Evaluate()
    {
        if (!IsRunning || _evaluating) return;
        _evaluating = true;
        try
        {
            var requireAll = Settings.GetBool(RequireAllKey, false);
            var (enabled, matching) = ReadConditions();
            var satisfied = KeepAwakeLogic.Satisfied(matching, enabled, requireAll);
            var automatic = IsActive && Trigger == AwakeTrigger.Automation;
            if (_suppressed)
            {
                if (!satisfied)
                {
                    _suppressed = false;
                    if (automatic) Deactivate(SessionEndReason.Manual);
                }
                return;
            }
            if (_locked && Settings.GetBool(PauseWhenLockedKey, false))
            {
                if (automatic && Matching != (matching & enabled))
                {
                    Matching = matching & enabled;
                    RaiseChanged();
                }
                return;
            }
            switch (KeepAwakeLogic.Decide(FeatureCatalog.IsAvailable(Id), matching, enabled, requireAll, IsActive, automatic))
            {
                case AutomationAction.Activate when AutomaticStartAllowed():
                    Begin(AwakeTrigger.Automation, null, null, until: false);
                    Matching = matching & enabled;
                    RaiseChanged();
                    break;
                case AutomationAction.Deactivate:
                    Deactivate(SessionEndReason.Manual);
                    break;
                default:
                    if (automatic && Matching != (matching & enabled))
                    {
                        Matching = matching & enabled;
                        RaiseChanged();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keep awake automation failed", ex);
        }
        finally
        {
            _evaluating = false;
        }
    }

    // ---- System events -----------------------------------------------------------

    private void OnOptionsChanged()
    {
        if (!IsActive) return;
        ApplyHolds();
        if (!IsPaused) StartJiggleTimer();
        RaiseChanged();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Ui.OnUi(() =>
        {
            if (!IsRunning) return;
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _locked = true;
                    if (IsActive && Settings.GetBool(PauseWhenLockedKey, false))
                    {
                        ReleaseHolds();
                        RaiseChanged();
                    }
                    break;
                case SessionSwitchReason.SessionUnlock:
                    var wasPaused = IsPaused;
                    _locked = false;
                    if (!wasPaused) break;
                    if (End is { } end && end <= DateTime.Now)
                    {
                        TimerExpired();
                        break;
                    }
                    if (Trigger == AwakeTrigger.Automation && !AutomationSatisfied())
                    {
                        Deactivate(SessionEndReason.Manual);
                        break;
                    }
                    ApplyHolds();
                    ArmEndTimer();
                    RaiseChanged();
                    break;
            }
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Ui.OnUi(() =>
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            CheckBattery();
            Evaluate();
        }
        else if (e.Mode == PowerModes.Resume && IsActive && End is { } end && end <= DateTime.Now && !IsPaused)
        {
            TimerExpired();
        }
    });

    private DispatcherTimer? _displayDebounce;

    private void OnDisplaysChanged(object? sender, EventArgs e) => Ui.OnUi(() =>
    {
        _displayDebounce ??= CreateTimer(TimeSpan.FromMilliseconds(350), () =>
        {
            _displayDebounce?.Stop();
            Evaluate();
        });
        _displayDebounce.Stop();
        _displayDebounce.Start();
    });

    // ---- Lid state and dimming ---------------------------------------------------

    private void RegisterLidNotifications()
    {
        if (!LidPresent) return;
        try
        {
            _powerWindow = new HwndSource(new HwndSourceParameters("FieldkitKeepAwakePower")
            {
                Width = 0,
                Height = 0,
                ParentWindow = new IntPtr(-3),
                WindowStyle = 0,
            });
            _powerWindow.AddHook(PowerWndProc);
            var guid = EnergyNative.GUID_LIDSWITCH_STATE_CHANGE;
            _lidNotification = EnergyNative.RegisterPowerSettingNotification(_powerWindow.Handle, ref guid, EnergyNative.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch (Exception ex)
        {
            Log.Warn($"Lid notifications unavailable: {ex.Message}");
        }
    }

    private void UnregisterLidNotifications()
    {
        if (_lidNotification != IntPtr.Zero) EnergyNative.UnregisterPowerSettingNotification(_lidNotification);
        _lidNotification = IntPtr.Zero;
        _powerWindow?.Dispose();
        _powerWindow = null;
    }

    private IntPtr PowerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == EnergyNative.WM_POWERBROADCAST && (uint)wParam.ToInt64() == EnergyNative.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<Guid>(lParam);
            if (setting == EnergyNative.GUID_LIDSWITCH_STATE_CHANGE)
            {
                var state = Marshal.ReadInt32(lParam, 20); // Guid (16) + DataLength (4)
                var closed = state == 0;
                if (closed != _lidClosed)
                {
                    _lidClosed = closed;
                    if (closed) DimPanelForClosedLid();
                    else RestoreDimmedPanel();
                }
                else
                {
                    _lidClosed = closed;
                }
            }
        }
        return IntPtr.Zero;
    }

    private sealed record DimRestore(string Instance, int Percent);

    private void DimPanelForClosedLid()
    {
        if (!LidCloseOverride.IsApplied || !Settings.GetBool(DimOnLidCloseKey, false) || Settings.Has(DimRestoreKey)) return;
        Task.Run(() =>
        {
            var panels = WmiBrightness.Read();
            if (panels.Count == 0) return;
            var (instance, level) = panels.First();
            if (level == 0) return;
            Settings.Set(DimRestoreKey, new DimRestore(instance, level));
            Settings.Flush();
            WmiBrightness.Write(instance, 0);
        });
    }

    private static void RestoreDimmedPanel()
    {
        if (Settings.Get<DimRestore?>(DimRestoreKey, null) is not { } saved)
        {
            return;
        }
        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (WmiBrightness.Write(saved.Instance, saved.Percent))
                {
                    Settings.Remove(DimRestoreKey);
                    return;
                }
                await Task.Delay(500);
            }
        });
    }

    // ---- Helpers -----------------------------------------------------------------

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => Ui.Guard(tick);
        return t;
    }

    private void RaiseChanged()
    {
        // Upstream swaps the menu bar icon while a session runs; here the tray icon gets an orange dot.
        TrayStatus.Set(Id, IsActive ? new TrayBadge($"Keep awake: {StatusText()}", IsPaused ? 0x8E8E93 : 0xFF9F0A, 10) : null);
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("Keep awake observer failed", ex); }
    }
}
