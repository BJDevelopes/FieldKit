// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Energy;

/// <summary>Automation conditions (upstream KeepAwakeAutomationCondition).</summary>
[Flags]
public enum AwakeCondition
{
    None = 0,
    ExternalDisplay = 1,
    Power = 2,
    RunningApps = 4,
    /// <summary>An AI coding agent is working (a Fieldkit addition, upstream #3001).</summary>
    AgentsWorking = 8,
}

public enum AutomationAction { None, Activate, Deactivate }

/// <summary>Pure Keep awake decisions and formatting, ported from upstream (unit-tested).</summary>
public static class KeepAwakeLogic
{
    /// <summary>Panel chips, in minutes; 0 = indefinite.</summary>
    public static readonly int[] Durations = [15, 30, 60, 120, 240, 480, 0];

    public static readonly int[] BatteryLimits = [0, 5, 10, 15, 20];

    public static readonly int[] JiggleIntervals = [1, 2, 5, 10, 15];

    public static int SanitizedDuration(int minutes) => Durations.Contains(minutes) ? minutes : 0;

    public static int SanitizedBatteryLimit(int percent) => BatteryLimits.Contains(percent) ? percent : 10;

    public static int SanitizedJiggleInterval(int minutes) => JiggleIntervals.Contains(minutes) ? minutes : 5;

    /// <summary>"15m", "1h", "∞".</summary>
    public static string ChipLabel(int minutes) => minutes switch
    {
        0 => "∞",
        < 60 => $"{minutes}m",
        _ when minutes % 60 == 0 => $"{minutes / 60}h",
        _ => $"{minutes / 60}h {minutes % 60}m",
    };

    /// <summary>"15 minutes", "1 hour", "Indefinite".</summary>
    public static string LongLabel(int minutes) => minutes switch
    {
        0 => "Indefinite",
        < 60 => $"{minutes} minutes",
        60 => "1 hour",
        _ => $"{minutes / 60} hours",
    };

    /// <summary>"+15m", "+30m", "+1h".</summary>
    public static string ExtendLabel(int minutes) => "+" + ChipLabel(minutes);

    /// <summary>"1 h 05 min", "12 min 07 s", "42 s".</summary>
    public static string Remaining(TimeSpan left)
    {
        var total = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        if (hours > 0) return $"{hours} h {minutes:00} min";
        if (minutes > 0) return $"{minutes} min {seconds:00} s";
        return $"{seconds} s";
    }

    /// <summary>Chip countdown: "58:12" or "1:05:12".</summary>
    public static string Countdown(TimeSpan left)
    {
        var total = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>Abbreviated hours and minutes, at least one minute: "2h 15m", "45m".</summary>
    public static string HoursMinutes(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        var h = minutes / 60;
        var m = minutes % 60;
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>The next occurrence of a wall-clock time (today, or tomorrow when already past).</summary>
    public static DateTime ResolvedUntil(DateTime now, int hour, int minute)
    {
        var candidate = now.Date.AddHours(Math.Clamp(hour, 0, 23)).AddMinutes(Math.Clamp(minute, 0, 59));
        return candidate <= now ? candidate.AddDays(1) : candidate;
    }

    /// <summary>"Today · 2h 15m".</summary>
    public static string UntilSummary(DateTime now, DateTime end)
    {
        var day = end.Date == now.Date ? "Today" : end.Date == now.Date.AddDays(1) ? "Tomorrow" : end.ToString("d MMM");
        return $"{day} · {HoursMinutes(end - now)}";
    }

    /// <summary>Adds minutes to max(current end, now).</summary>
    public static DateTime Extended(DateTime? end, DateTime now, int minutes) =>
        (end is { } e && e > now ? e : now).AddMinutes(minutes);

    // ---- Battery protection ---------------------------------------------------

    /// <summary>True when a running session must end to protect the battery.</summary>
    public static bool BatteryCutoff(int limit, bool onBattery, int? percent) =>
        limit > 0 && onBattery && percent is { } p && p <= limit;

    /// <summary>Automatic sessions start only above the limit, on AC, or with no limit.</summary>
    public static bool AutomaticStartAllowed(int limit, bool onBattery, int? percent) =>
        limit == 0 || !onBattery || percent is null || percent > limit;

    // ---- Automation -----------------------------------------------------------

    public static bool Satisfied(AwakeCondition matching, AwakeCondition enabled, bool requireAll)
    {
        matching &= enabled;
        if (matching == AwakeCondition.None) return false;
        return !requireAll || matching == enabled;
    }

    public static AutomationAction Decide(bool featureAvailable, AwakeCondition matching, AwakeCondition enabled,
        bool requireAll, bool sessionActive, bool automaticSessionActive)
    {
        if (!featureAvailable || !Satisfied(matching, enabled, requireAll))
            return automaticSessionActive ? AutomationAction.Deactivate : AutomationAction.None;
        return sessionActive ? AutomationAction.None : AutomationAction.Activate;
    }

    /// <summary>Status line for an automatic session.</summary>
    public static string AutomationStatus(AwakeCondition matching) => matching switch
    {
        AwakeCondition.ExternalDisplay => "Active while an external display is connected",
        AwakeCondition.Power => "Active while connected to power",
        AwakeCondition.RunningApps => "Active while a selected app is running",
        AwakeCondition.AgentsWorking => "Active while an AI agent is working",
        _ => "Active because an automatic condition is met",
    };

    /// <summary>An agent's turns come with short pauses between them (a tool runs, the next turn starts): stay awake this long after the last work seen.</summary>
    public static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(2);

    public static bool AgentsWorking(DateTime? lastWorkSeen, DateTime now) =>
        lastWorkSeen is { } seen && now - seen <= AgentGrace;

    /// <summary>Normalises an app entry to a lower-case process name without ".exe".</summary>
    public static string ProcessKey(string entry)
    {
        var name = entry.Trim();
        var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    public static bool AnyAppRunning(IEnumerable<string> selected, IEnumerable<string> runningProcessNames)
    {
        var wanted = selected.Select(ProcessKey).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return false;
        return runningProcessNames.Any(p => wanted.Contains(ProcessKey(p)));
    }
}
