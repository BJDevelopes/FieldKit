// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Wellbeing;

/// <summary>Where each reminder stands: active (non-idle) time since the last break.</summary>
public readonly record struct BreakState(TimeSpan EyesActive, TimeSpan MoveActive);

/// <summary>What one tick decided.</summary>
public readonly record struct BreakDecision(BreakState State, bool RemindEyes, bool RemindMove);

/// <summary>
/// Pure timing rules (unit tested). Only time at the keyboard counts; being away long enough counts as the
/// break itself (20 s for the eyes, 5 min for moving). A reminder that falls due while the user should not be
/// interrupted (full screen, presentation) waits and is shown once that ends.
/// </summary>
public static class BreakLogic
{
    public static readonly TimeSpan EyesBreak = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MoveBreak = TimeSpan.FromMinutes(5);
    /// <summary>Input within this long counts as being at the PC.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(1);

    public static BreakDecision Tick(BreakState state, TimeSpan elapsed, TimeSpan idle,
        TimeSpan? eyesInterval, TimeSpan? moveInterval, bool canInterrupt)
    {
        var eyes = state.EyesActive;
        var move = state.MoveActive;
        if (idle >= EyesBreak) eyes = TimeSpan.Zero;
        if (idle >= MoveBreak) move = TimeSpan.Zero;
        if (idle < ActiveWindow)
        {
            eyes += elapsed;
            move += elapsed;
        }
        var remindEyes = canInterrupt && eyesInterval is { } e && eyes >= e;
        var remindMove = canInterrupt && moveInterval is { } m && move >= m;
        // One notification at a time: the movement break covers the eyes too.
        if (remindMove)
        {
            remindEyes = false;
            eyes = TimeSpan.Zero;
            move = TimeSpan.Zero;
        }
        else if (remindEyes)
        {
            eyes = TimeSpan.Zero;
        }
        return new BreakDecision(new BreakState(eyes, move), remindEyes, remindMove);
    }
}

/// <summary>Break reminders (upstream PR #2524): rest your eyes every 20 minutes and move every hour.</summary>
public sealed class BreakRemindersService : FeatureService
{
    public const string Id = "breakReminders";
    public const string EyesKey = "breakReminders.eyes";
    public const string EyesMinutesKey = "breakReminders.eyesMinutes";
    public const string MoveKey = "breakReminders.move";
    public const string MoveMinutesKey = "breakReminders.moveMinutes";

    /// <summary>The reminders stay off until the user switches one on, so installing every feature never starts nagging.</summary>
    public const bool OnByDefault = false;

    private const string MigratedKey = "breakReminders.defaultsMigrated";

    /// <summary>
    /// Run once at start-up: an install that already had Break reminders keeps its reminders on (they used to be on
    /// by default), a fresh one starts with them off.
    /// </summary>
    public static void MigrateDefaults()
    {
        if (Settings.GetBool(MigratedKey, false)) return;
        var info = FeatureCatalog.Get(Id);
        if (Settings.Has(info.AvailabilityKey) && info.IsAvailable)
        {
            if (!Settings.Has(EyesKey)) Settings.Set(EyesKey, true);
            if (!Settings.Has(MoveKey)) Settings.Set(MoveKey, true);
        }
        Settings.Set(MigratedKey, true);
    }

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
    private BreakState _state;
    private DateTime _last;

    public static DateTime SnoozedUntil { get; set; } = DateTime.MinValue;

    public override string FeatureId => Id;
    public override IReadOnlyList<string> EnableKeys => [EyesKey, MoveKey];
    public override bool EnabledByDefault => OnByDefault;

    public BreakRemindersService() => _timer.Tick += (_, _) => Ui.Guard(Tick);

    protected override void OnStart()
    {
        _state = default;
        _last = DateTime.UtcNow;
        _timer.Start();
    }

    protected override void OnStop() => _timer.Stop();

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _last;
        _last = now;
        if (DateTime.Now < SnoozedUntil) return;
        var eyes = Settings.GetBool(EyesKey, OnByDefault) ? TimeSpan.FromMinutes(Math.Clamp(Settings.GetInt(EyesMinutesKey, 20), 5, 120)) : (TimeSpan?)null;
        var move = Settings.GetBool(MoveKey, OnByDefault) ? TimeSpan.FromMinutes(Math.Clamp(Settings.GetInt(MoveMinutesKey, 60), 15, 240)) : (TimeSpan?)null;
        var decision = BreakLogic.Tick(_state, elapsed, IdleTime(), eyes, move, CanInterrupt());
        _state = decision.State;
        if (decision.RemindMove) Notifier.Show("Time to move", "Stand up, stretch and walk around for a few minutes.");
        else if (decision.RemindEyes) Notifier.Show("Rest your eyes", "Look at something about 6 metres (20 feet) away for 20 seconds.");
    }

    // ------------------------------------------------------------------ system state

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Not during full-screen apps, presentations or Focus (QUNS_ACCEPTS_NOTIFICATIONS = 5).</summary>
    private static bool CanInterrupt() => SHQueryUserNotificationState(out var state) != 0 || state == 5;
}

public sealed class BreakRemindersModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(BreakRemindersService.Id, Parity.Full, "Fieldkit extra, from Vorssaint PR #2524.");
        r.Service(BreakRemindersService.Id, () => new BreakRemindersService());
        r.SettingsPage(new SettingsPageDef("breakReminders", SettingsCategory.Utilities, "Break reminders", Glyphs.Eye, 65,
            [BreakRemindersService.Id], BuildPage, ["break", "eyes", "20-20-20", "stand", "move", "health", "rest"]));
        r.Action(new ToolAction("breakReminders.snooze", BreakRemindersService.Id, "Pause break reminders for an hour", Glyphs.Eye,
            () =>
            {
                BreakRemindersService.SnoozedUntil = DateTime.Now.AddHours(1);
                Notifier.Show("Break reminders paused", "They come back in an hour.");
            }));
    }

    private static FrameworkElement BuildPage() => Ui.Page("Break reminders",
        "Gentle reminders to rest your eyes and get up. Only time at the PC counts, and stepping away resets them. " +
        "Full-screen apps and presentations are never interrupted. Both reminders are off until you switch them on.",
        Ui.Group("Eyes",
            Ui.ToggleRow("Remind me to rest my eyes", "Look about 6 metres away for 20 seconds (the 20-20-20 rule).", BreakRemindersService.EyesKey, BreakRemindersService.OnByDefault),
            Ui.SliderRow("Every", BreakRemindersService.EyesMinutesKey, 5, 120, 20, v => $"{v:0} min", 5)),
        Ui.Group("Moving",
            Ui.ToggleRow("Remind me to stand up and move", "A few minutes away from the desk.", BreakRemindersService.MoveKey, BreakRemindersService.OnByDefault),
            Ui.SliderRow("Every", BreakRemindersService.MoveMinutesKey, 15, 240, 60, v => $"{v:0} min", 15)));
}
