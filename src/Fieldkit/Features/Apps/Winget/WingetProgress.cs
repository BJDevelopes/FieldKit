// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>What an operation does.</summary>
public enum WingetAction { Install, Uninstall, Upgrade, UpgradeAll, UpgradeSelected, UpdateSources }

/// <summary>Where a running operation is, as far as the output tells.</summary>
public enum WingetPhase { Preparing, Downloading, Installing, Uninstalling, Upgrading, Finalizing, Refreshing }

/// <summary>How an operation ended (or that it is still running).</summary>
public enum WingetResult { Running, Succeeded, Failed, Cancelled, NeedsTerminal }

/// <summary>
/// Reads progress out of winget's streamed output: the percentage or "12 MB / 40 MB" of its
/// progress bar, the phase from its status lines, the last meaningful line for the activity
/// text, and the few lines worth showing when something failed.
/// </summary>
public static partial class WingetProgress
{
    [GeneratedRegex(@"(?<a>\d+(?:[.,]\d+)?)\s*(?<au>[KMGT]?i?B)\s*/\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<bu>[KMGT]?i?B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<p>\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    /// <summary>The latest progress fraction (0…1) reported in <paramref name="text"/>, if any.</summary>
    public static double? Fraction(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        double? latest = null;
        var latestIndex = -1;
        foreach (Match m in SizeRegex().Matches(text))
        {
            var a = Number(m.Groups["a"].Value) * UnitFactor(m.Groups["au"].Value);
            var b = Number(m.Groups["b"].Value) * UnitFactor(m.Groups["bu"].Value);
            if (b <= 0) continue;
            if (m.Index >= latestIndex)
            {
                latest = Math.Clamp(a / b, 0, 1);
                latestIndex = m.Index;
            }
        }
        foreach (Match m in PercentRegex().Matches(text))
        {
            var p = Number(m.Groups["p"].Value);
            if (p > 100) continue;
            if (m.Index >= latestIndex)
            {
                latest = Math.Clamp(p / 100, 0, 1);
                latestIndex = m.Index;
            }
        }
        return latest;
    }

    private static double Number(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double UnitFactor(string unit) => unit.ToUpperInvariant() switch
    {
        "KB" or "KIB" => 1e3,
        "MB" or "MIB" => 1e6,
        "GB" or "GIB" => 1e9,
        "TB" or "TIB" => 1e12,
        _ => 1,
    };

    /// <summary>The phase a status line announces (English output), or null.</summary>
    public static WingetPhase? Phase(string line, WingetAction action)
    {
        var lower = WingetText.StripAnsi(line).ToLowerInvariant();
        if (lower.Length == 0) return null;
        if (action == WingetAction.UpdateSources
            && (lower.Contains("updating source") || lower.Contains("updating all") || lower.Contains("done")))
            return WingetPhase.Refreshing;
        if (lower.Contains("successfully installed") || lower.Contains("successfully uninstalled")
            || lower.Contains("restart") || lower.Contains("reboot"))
            return WingetPhase.Finalizing;
        if (lower.Contains("downloading")) return WingetPhase.Downloading;
        if (lower.Contains("starting package uninstall") || lower.Contains("uninstalling")) return WingetPhase.Uninstalling;
        if (lower.Contains("starting package install") || lower.Contains("installing")
            || lower.Contains("successfully verified installer hash") || lower.Contains("extracting"))
        {
            return action switch
            {
                WingetAction.Uninstall => WingetPhase.Uninstalling,
                WingetAction.Upgrade or WingetAction.UpgradeAll or WingetAction.UpgradeSelected => WingetPhase.Upgrading,
                WingetAction.UpdateSources => WingetPhase.Refreshing,
                _ => WingetPhase.Installing,
            };
        }
        return null;
    }

    /// <summary>The line worth showing under the title while it runs (null for noise).</summary>
    public static string? Activity(string line)
    {
        var t = WingetText.StripAnsi(line).Trim();
        if (t.Length == 0 || WingetText.IsNoise(t) || WingetTableParser.IsRule(t)) return null;
        return t;
    }

    /// <summary>The last few meaningful lines of a failed run.</summary>
    public static string VisibleError(string output, int count = 3)
    {
        var lines = WingetText.CleanLines(output)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !WingetText.IsNoise(l) && !WingetTableParser.IsRule(l))
            .ToList();
        return string.Join("\n", lines.TakeLast(count));
    }

    /// <summary>winget stopped because source or package agreements were not accepted.</summary>
    public static bool NeedsAgreements(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("--accept-source-agreements") || lower.Contains("--accept-package-agreements")
            || (lower.Contains("agreement") && (lower.Contains("not agreed") || lower.Contains("do you agree") || lower.Contains("must be accepted")));
    }

    /// <summary>The run needs something only an interactive console can give (administrator
    /// rights, an installer that insists on interaction).</summary>
    public static bool NeedsTerminal(string output, int exitCode)
    {
        if (exitCode == WingetCommands.RequiresAdmin) return true;
        var lower = output.ToLowerInvariant();
        return lower.Contains("requires administrator") || lower.Contains("run as administrator")
            || lower.Contains("administrator privileges") || lower.Contains("requires interactivity")
            || lower.Contains("interactivity is disabled") || lower.Contains("elevation is required");
    }

    /// <summary>Final verdict for a finished run.</summary>
    public static WingetResult Classify(int exitCode, string output, bool cancelled)
    {
        if (cancelled) return WingetResult.Cancelled;
        if (exitCode == 0) return WingetResult.Succeeded;
        if (NeedsTerminal(output, exitCode)) return WingetResult.NeedsTerminal;
        return WingetResult.Failed;
    }
}
