// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Island.Agents;

/// <summary>Number formatting of the AI agents surfaces (port of upstream <c>AgentFormat</c>).</summary>
public static class AgentFormat
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"$3.40", "$1,234", "$12.5K".</summary>
    public static string Cost(double v)
    {
        var a = Math.Abs(v);
        if (a >= 10000) return "$" + Scaled(v / 1000) + "K";
        if (a >= 100) return "$" + v.ToString("#,##0", En);
        return "$" + v.ToString("0.00", En);
    }

    /// <summary>"812", "48K", "1.2M", "3.4B".</summary>
    public static string Tokens(long n)
    {
        if (n < 1000) return n.ToString(En);
        if (n < 1_000_000) return Scaled(n / 1e3) + "K";
        if (n < 1_000_000_000) return Scaled(n / 1e6) + "M";
        return Scaled(n / 1e9) + "B";
    }

    /// <summary>Under 10 floors to one decimal ("4.2", trailing zero dropped); otherwise no decimals.</summary>
    public static string Scaled(double x)
    {
        if (Math.Abs(x) < 10)
        {
            var floored = Math.Floor(x * 10) / 10;
            return floored.ToString("0.#", En);
        }
        return Math.Floor(x).ToString("0", En);
    }

    public static string Percent(double fraction) => Math.Round(fraction * 100).ToString("0", En) + "%";

    /// <summary>"2d 3h", "1h 5m", "4m 12s", "45s" (two units at most).</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        if (s >= 86400) return $"{s / 86400}d {s % 86400 / 3600}h";
        if (s >= 3600) return $"{s / 3600}h {s % 3600 / 60}m";
        if (s >= 60) return $"{s / 60}m {s % 60}s";
        return $"{s}s";
    }

    /// <summary>"0:42", "12:05", "1:02:05".</summary>
    public static string Clock(double seconds)
    {
        var s = (long)Math.Max(0, Math.Floor(seconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"Oct 9, 2026".</summary>
    public static string Day(DateTimeOffset date) => date.ToString("MMM d, yyyy", En);

    /// <summary>Relative time like "now", "5 min ago", "3 h ago", "Oct 9".</summary>
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        var d = now - date;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        return date.ToLocalTime().ToString("MMM d", En);
    }

    /// <summary>Countdown to a reset: "2h 14m", "3d 4h".</summary>
    public static string Countdown(DateTimeOffset target, DateTimeOffset now) => Duration((target - now).TotalSeconds);
}
