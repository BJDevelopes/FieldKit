// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Timer;

public enum TimerMode { Timer, Pomodoro, Stopwatch }

public enum TimerPhase { Timer, Focus, ShortBreak, LongBreak, Stopwatch }

/// <summary>Pomodoro lengths, clamped like upstream (focus 1–180, breaks 1–60, counts 1–24).</summary>
public sealed record PomodoroConfig(int Focus = 25, int ShortBreak = 5, int LongBreak = 15, int LongBreakInterval = 4, int TotalSessions = 4)
{
    public PomodoroConfig Clamped() => new(Math.Clamp(Focus, 1, 180), Math.Clamp(ShortBreak, 1, 60), Math.Clamp(LongBreak, 1, 60),
        Math.Clamp(LongBreakInterval, 1, 24), Math.Clamp(TotalSessions, 1, 24));
}

/// <summary>
/// The single timer / Pomodoro / stopwatch session (port of upstream <c>NotchTimerSession</c>): an anchor on a
/// monotonic clock, so display ticks never drift. <c>now</c> is seconds on that clock. Memory only, like upstream.
/// </summary>
public sealed record TimerSession
{
    public TimerMode Mode { get; init; } = TimerMode.Timer;
    public TimerPhase Phase { get; init; } = TimerPhase.Timer;
    public double Duration { get; init; } = 300;
    /// <summary>Deadline for countdowns, start for the stopwatch; null while paused or idle.</summary>
    public double? Anchor { get; init; }
    public double? PausedReading { get; init; }
    public bool Completed { get; init; }
    public int CompletedFocuses { get; init; }
    public PomodoroConfig Configuration { get; init; } = new();

    public static TimerSession Default { get; } = new();

    public bool CountsUp => Mode == TimerMode.Stopwatch;
    public bool IsRunning => Anchor is not null;
    public bool IsPaused => PausedReading is not null;
    public bool HasSession => IsRunning || IsPaused || Completed;
    public double? Deadline => CountsUp ? null : Anchor;

    /// <summary>Remaining seconds (countdowns) or elapsed seconds (stopwatch).</summary>
    public double Reading(double now) => Anchor is { } a
        ? Math.Max(0, CountsUp ? now - a : a - now)
        : PausedReading ?? (Completed || CountsUp ? 0 : Duration);

    /// <summary>0..1 of the countdown elapsed.</summary>
    public double Progress(double now) => CountsUp || Duration <= 0 ? 0 : Math.Clamp(1 - Reading(now) / Duration, 0, 1);

    public TimerSession Start(TimerMode mode, int minutes, double now, PomodoroConfig config)
    {
        if (HasSession) return this;
        var c = config.Clamped();
        return mode switch
        {
            TimerMode.Pomodoro => new TimerSession { Mode = mode, Phase = TimerPhase.Focus, Duration = c.Focus * 60, Anchor = now + c.Focus * 60, Configuration = c },
            TimerMode.Stopwatch => new TimerSession { Mode = mode, Phase = TimerPhase.Stopwatch, Duration = 0, Anchor = now, Configuration = c },
            _ => new TimerSession { Mode = mode, Phase = TimerPhase.Timer, Duration = Math.Clamp(minutes, 1, 180) * 60, Anchor = now + Math.Clamp(minutes, 1, 180) * 60, Configuration = c },
        };
    }

    public TimerSession Pause(double now)
    {
        var due = FinishIfDue(now);
        if (due.Completed || !due.IsRunning) return due;
        return due with { PausedReading = due.Reading(now), Anchor = null };
    }

    public TimerSession Resume(double now)
    {
        if (PausedReading is not { } r) return this;
        return this with { Anchor = CountsUp ? now - r : now + r, PausedReading = null };
    }

    public TimerSession FinishIfDue(double now)
    {
        if (Deadline is not { } d || now < d) return this;
        return this with
        {
            Anchor = null,
            PausedReading = null,
            Completed = true,
            CompletedFocuses = Phase == TimerPhase.Focus ? CompletedFocuses + 1 : CompletedFocuses,
        };
    }

    public TimerPhase NextPhase => Phase == TimerPhase.Focus
        ? (CompletedFocuses % Configuration.LongBreakInterval == 0 ? TimerPhase.LongBreak : TimerPhase.ShortBreak)
        : TimerPhase.Focus;

    public bool CycleFinished => Mode == TimerMode.Pomodoro && Completed && CompletedFocuses >= Configuration.TotalSessions;
    public bool CanStartNext => Mode == TimerMode.Pomodoro && Completed && !CycleFinished;

    public int SessionNumber => Math.Min(Configuration.TotalSessions, CompletedFocuses + (Phase == TimerPhase.Focus && !Completed ? 1 : 0));

    public TimerSession StartNext(double now)
    {
        if (!CanStartNext) return this;
        var phase = NextPhase;
        var minutes = phase switch
        {
            TimerPhase.Focus => Configuration.Focus,
            TimerPhase.LongBreak => Configuration.LongBreak,
            _ => Configuration.ShortBreak,
        };
        return this with { Phase = phase, Duration = minutes * 60, Completed = false, Anchor = now + minutes * 60, PausedReading = null };
    }
}

/// <summary>Timer text formats (upstream <c>NotchTimerSupport</c>).</summary>
public static class TimerFormat
{
    /// <summary>"05:00", "1:02:03" for remaining time (ceiling, capped at 3 h).</summary>
    public static string Clock(double remaining)
    {
        var s = (long)Math.Ceiling(Math.Clamp(remaining, 0, 10800));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>Stopwatch: floor, capped at 99:59:59.</summary>
    public static string Stopwatch(double elapsed)
    {
        var s = (long)Math.Floor(Math.Clamp(elapsed, 0, 359999));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>"1h35" (never "1:35").</summary>
    public static string Hours(int h, int m) => $"{h}h{m:00}";

    /// <summary>Narrow units rounded down: "15m", "14m" at 14:59, "45s", "0s".</summary>
    public static string NarrowUnits(double remaining)
    {
        var s = (long)Math.Ceiling(Math.Max(0, remaining));
        if (s >= 3600) return $"{s / 3600}h {s % 3600 / 60}m";
        if (s >= 60) return $"{s / 60}m";
        return $"{s}s";
    }

    /// <summary>The closed island's reading.</summary>
    public static string Compact(TimerSession session, double now)
    {
        var r = session.Reading(now);
        if (session.CountsUp)
        {
            var e = (long)Math.Floor(r);
            return e < 3600 ? Stopwatch(r) : Hours((int)(e / 3600), (int)(e % 3600 / 60));
        }
        var c = (long)Math.Floor(r);
        return r >= 3600 ? Hours((int)(c / 3600), (int)(c % 3600 / 60)) : NarrowUnits(r);
    }

    /// <summary>Ruler labels: "15", "1h05".</summary>
    public static string RulerLabel(int minutes) => minutes >= 60 ? Hours(minutes / 60, minutes % 60) : minutes.ToString();

    public static string PhaseName(TimerPhase p) => p switch
    {
        TimerPhase.Focus => "Focus",
        TimerPhase.ShortBreak => "Short break",
        TimerPhase.LongBreak => "Long break",
        TimerPhase.Stopwatch => "Stopwatch",
        _ => "Timer",
    };
}
