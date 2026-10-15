// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Timer;

/// <summary>
/// The island's timer, Pomodoro and stopwatch (port of upstream <c>NotchTimerService</c>): one session in
/// memory on a monotonic clock, a notice when time is up and an alert sound repeated every 2 s for up to 5 minutes.
/// </summary>
public sealed class TimerService : IslandExtensionService
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _deadline = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _alarm = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private double _alarmUntil;
    private IDisposable? _hotkey;
    private SoundPlayer? _player;

    public override string FeatureId => "notchTimer";
    protected override string EnableKey => IslandKeys.TimerEnabled;

    public TimerSession Session { get; private set; } = TimerSession.Default;

    /// <summary>Seconds on the monotonic clock sessions are anchored to.</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

    public TimerService()
    {
        _deadline.Tick += (_, _) => CheckDeadline();
        _alarm.Tick += (_, _) => AlarmTick();
    }

    protected override void OnStart()
    {
        IslandHub.Timer = this;
        _hotkey = Hotkeys.Bind(IslandKeys.TimerShortcut, string.Empty, ToggleFromShortcut);
        Schedule();
        IslandHub.RaiseChanged();
    }

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        _deadline.Stop();
        StopAlarm();
        _player?.Dispose();
        _player = null;
        Session = TimerSession.Default;
        if (IslandHub.Timer == this) IslandHub.Timer = null;
        IslandHub.RaiseChanged();
    }

    public static PomodoroConfig Config => new PomodoroConfig(
        Settings.GetInt(IslandKeys.PomodoroFocus, 25), Settings.GetInt(IslandKeys.PomodoroShortBreak, 5),
        Settings.GetInt(IslandKeys.PomodoroLongBreak, 15), Settings.GetInt(IslandKeys.PomodoroInterval, 4),
        Settings.GetInt(IslandKeys.PomodoroTotal, 4)).Clamped();

    public static TimerMode SelectedMode => Settings.GetString(IslandKeys.TimerMode, "timer") switch
    {
        "pomodoro" => TimerMode.Pomodoro,
        "stopwatch" => TimerMode.Stopwatch,
        _ => TimerMode.Timer,
    };

    /// <summary>Whether the closed island shows the timer (a finished timer stays until dismissed).</summary>
    public bool ShowsActivity => Session.HasSession && (Session.Completed || !Settings.GetBool(IslandKeys.TimerHideCountdown, false));

    public void Start()
    {
        StopAlarm();
        Session = Session.Start(SelectedMode, Settings.GetInt(IslandKeys.TimerMinutes, IslandDefaults.TimerMinutes), Now, Config);
        IslandHub.React(MascotEvent.TimerStarted);
        Changed();
    }

    public void PauseOrResume()
    {
        Session = Session.IsPaused ? Session.Resume(Now) : Session.Pause(Now);
        Changed();
    }

    public void StartNext()
    {
        StopAlarm();
        Session = Session.StartNext(Now);
        IslandHub.React(MascotEvent.TimerStarted);
        Changed();
    }

    /// <summary>Cancel, or Done once completed: also stops the alert.</summary>
    public void Cancel()
    {
        StopAlarm();
        Session = TimerSession.Default;
        Changed();
    }

    private void ToggleFromShortcut()
    {
        if (!Session.HasSession) Start();
        else if (Session.Completed) Cancel();
        else PauseOrResume();
    }

    private void Changed()
    {
        Schedule();
        IslandHub.RaiseChanged();
    }

    private void Schedule()
    {
        _deadline.Stop();
        if (Session.Deadline is not { } d) return;
        _deadline.Interval = TimeSpan.FromSeconds(Math.Max(0.05, d - Now));
        _deadline.Start();
    }

    private void CheckDeadline()
    {
        _deadline.Stop();
        var before = Session;
        Session = Session.FinishIfDue(Now);
        if (!Session.Completed || before.Completed)
        {
            Schedule();
            return;
        }
        var title = Session.CycleFinished ? "Pomodoro complete" : "Time is up";
        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Timer, title, TimerFormat.PhaseName(Session.Phase), Glyphs.Timer, "Orange", OpenSection: "timer"));
        IslandHub.React(Session.Phase switch
        {
            TimerPhase.Focus => MascotEvent.FocusDone,
            TimerPhase.ShortBreak or TimerPhase.LongBreak => MascotEvent.BreakDone,
            _ => MascotEvent.TimerDone,
        });
        if (Settings.GetBool(IslandKeys.TimerSound, true))
        {
            _alarmUntil = Now + 300;
            AlarmTick();
            _alarm.Start();
        }
        IslandHub.RaiseChanged();
    }

    private void AlarmTick()
    {
        if (Now > _alarmUntil || !Settings.GetBool(IslandKeys.TimerSound, true) || !Session.Completed)
        {
            StopAlarm();
            return;
        }
        PlayChime();
    }

    private void PlayChime()
    {
        try
        {
            var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
            if (!File.Exists(media)) media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
            if (File.Exists(media))
            {
                _player ??= new SoundPlayer(media);
                _player.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Timer sound failed: {ex.Message}");
        }
    }

    private void StopAlarm()
    {
        _alarm.Stop();
        _player?.Stop();
    }
}
