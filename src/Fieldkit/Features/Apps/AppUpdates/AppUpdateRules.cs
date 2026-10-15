// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>Where a pending update comes from, which is also what the app can do about it.</summary>
public enum AppUpdateSource
{
    /// <summary>winget's community source (or another configured source): updates in place.</summary>
    Winget,
    /// <summary>winget's Microsoft Store source: updates in place through winget.</summary>
    Store,
    /// <summary>winget cannot read the installed version: only the app's own updater can tell.</summary>
    Unknown,
}

/// <summary>One row of the update list.</summary>
public sealed record AppUpdateItem(
    string Id,
    AppUpdateSource Source,
    string Name,
    string InstalledVersion,
    string LatestVersion,
    string PackageId,
    string PackageSource)
{
    public bool IsSelectable => Source != AppUpdateSource.Unknown;
    public string VersionSummary => $"{InstalledVersion} → {LatestVersion}";
    public WingetTarget ToTarget() => new(Name, PackageId, PackageSource);
}

/// <summary>Skip exactly one release, or stop checking an app until the rule is removed.</summary>
public sealed record AppUpdateRule(string PackageId, string Name, string? Version)
{
    public bool Hides(AppUpdateItem item)
    {
        if (!string.Equals(item.PackageId, PackageId, StringComparison.OrdinalIgnoreCase)) return false;
        if (Version is null) return true;
        return AppUpdateRules.VersionCore(Version) == AppUpdateRules.VersionCore(item.LatestVersion);
    }
}

public enum CheckFrequency { Off, Daily, Weekly }

/// <summary>
/// Everything App updates decides, with no processes, network or files (port of upstream's
/// AppUpdatesSupport): version comparison, which rows are shown, rules, selection, the
/// background schedule and the wording that depends on counts and dates.
/// </summary>
public static class AppUpdateRules
{
    /// <summary>A check that came due while the PC was asleep or off runs this long after start.</summary>
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(180);

    /// <summary>How old a finished check can be before reopening the list scans again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------ items

    /// <summary>Rows for a <c>winget upgrade</c> listing. Pinned updates are left out (winget's
    /// own "update all" skips them too), as are updates without a comparable version.</summary>
    public static List<AppUpdateItem> Items(WingetUpgradeList list, bool includeWinget, bool includeStore, bool includeUnknown)
    {
        var items = new List<AppUpdateItem>();
        foreach (var p in list.Packages)
        {
            if (p.IsPinned || p.Truncated || !WingetCommands.IsValidId(p.Id)) continue;
            if (IsUncomparable(p.Available)) continue;
            AppUpdateSource source;
            if (WingetPackageRules.IsUnknownVersion(p.Version)) source = AppUpdateSource.Unknown;
            else if (p.Origin == WingetOrigin.Store) source = AppUpdateSource.Store;
            else if (p.Origin is WingetOrigin.Winget or WingetOrigin.OtherSource) source = AppUpdateSource.Winget;
            else continue;
            var wanted = source switch
            {
                AppUpdateSource.Winget => includeWinget,
                AppUpdateSource.Store => includeStore,
                _ => includeUnknown,
            };
            if (!wanted) continue;
            items.Add(new AppUpdateItem($"{source}:{p.Id.ToLowerInvariant()}", source, p.Name, p.Version, VersionCore(p.Available), p.Id, p.Source));
        }
        return Merged(items);
    }

    /// <summary>One list: apps that update in place first, then the rest; alphabetical in each group.</summary>
    public static List<AppUpdateItem> Merged(IEnumerable<AppUpdateItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(i => seen.Add(i.Id))
            .OrderBy(i => (int)i.Source)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<AppUpdateItem> VisibleItems(IEnumerable<AppUpdateItem> items, IReadOnlyList<AppUpdateRule> rules) =>
        items.Where(i => !rules.Any(r => r.Hides(i))).ToList();

    // ------------------------------------------------------------ versions

    /// <summary>The comparable part of a version: no leading "v", nothing after a comma.</summary>
    public static string VersionCore(string version)
    {
        var v = version.Trim();
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1])) v = v[1..];
        var comma = v.IndexOf(',');
        return comma >= 0 ? v[..comma] : v;
    }

    /// <summary>Versions that carry no number to compare ("latest", "Unknown", empty).</summary>
    public static bool IsUncomparable(string version)
    {
        var core = VersionCore(version).ToLowerInvariant();
        return core.Length == 0 || core == "latest" || !core.Any(char.IsDigit);
    }

    /// <summary>Numeric-aware comparison: digit parts compare as numbers, "1.2" equals "1.2.0",
    /// and a bare number beats the same number with a suffix ("5" &gt; "5beta").</summary>
    public static int Compare(string lhs, string rhs)
    {
        var left = Parts(lhs);
        var right = Parts(rhs);
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var result = ComparePart(i < left.Count ? left[i] : string.Empty, i < right.Count ? right[i] : string.Empty);
            if (result != 0) return result;
        }
        return 0;
    }

    public static bool IsNewer(string candidate, string installed) => Compare(VersionCore(candidate), VersionCore(installed)) > 0;

    private static List<string> Parts(string version)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in version)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static int ComparePart(string lhs, string rhs)
    {
        var leftDigits = lhs.Length > 0 && lhs.All(char.IsDigit);
        var rightDigits = rhs.Length > 0 && rhs.All(char.IsDigit);
        if (leftDigits && rightDigits) return CompareDigits(lhs, rhs);
        if (lhs.Length == 0) return rightDigits && StripZeros(rhs).Length == 0 ? 0 : -1;
        if (rhs.Length == 0) return leftDigits && StripZeros(lhs).Length == 0 ? 0 : 1;
        if (lhs == rhs) return 0;
        var leftNumber = new string(lhs.TakeWhile(char.IsDigit).ToArray());
        var rightNumber = new string(rhs.TakeWhile(char.IsDigit).ToArray());
        if ((leftNumber.Length == 0) != (rightNumber.Length == 0)) return leftNumber.Length == 0 ? -1 : 1;
        if (leftNumber.Length > 0)
        {
            var numbers = CompareDigits(leftNumber, rightNumber);
            if (numbers != 0) return numbers;
            var leftBare = leftNumber.Length == lhs.Length;
            var rightBare = rightNumber.Length == rhs.Length;
            if (leftBare != rightBare) return leftBare ? 1 : -1;
        }
        var l = lhs.ToLowerInvariant();
        var r = rhs.ToLowerInvariant();
        return Math.Sign(string.CompareOrdinal(l, r));
    }

    private static int CompareDigits(string lhs, string rhs)
    {
        var a = StripZeros(lhs);
        var b = StripZeros(rhs);
        if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static string StripZeros(string value) => value.TrimStart('0');

    // ------------------------------------------------------------ rules

    public static List<AppUpdateRule> DecodeRules(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        List<AppUpdateRule>? rules;
        try { rules = JsonSerializer.Deserialize<List<AppUpdateRule>>(raw); }
        catch (JsonException) { return []; }
        if (rules is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return rules.Where(r => r is not null
                                && WingetCommands.IsValidId(r.PackageId)
                                && !r.PackageId.Any(char.IsWhiteSpace)
                                && !string.IsNullOrWhiteSpace(r.Name)
                                && (r.Version is null || !IsUncomparable(r.Version))
                                && seen.Add(r.PackageId))
            .ToList();
    }

    public static string EncodeRules(IEnumerable<AppUpdateRule> rules) => JsonSerializer.Serialize(rules.ToList());

    /// <summary>The rule list after adding (or replacing) the rule for this item's app.</summary>
    public static List<AppUpdateRule> WithRule(IReadOnlyList<AppUpdateRule> rules, AppUpdateItem item, string? version) =>
        [.. rules.Where(r => !string.Equals(r.PackageId, item.PackageId, StringComparison.OrdinalIgnoreCase)),
            new AppUpdateRule(item.PackageId, item.Name, version)];

    // ------------------------------------------------------------ selection

    /// <summary>Rows that are gone drop out; rows that appeared arrive ticked.</summary>
    public static HashSet<string> ReconciledSelection(IReadOnlySet<string> previous, IReadOnlySet<string> knownIds, IEnumerable<AppUpdateItem> items)
    {
        var selectable = items.Where(i => i.IsSelectable).ToList();
        var next = new HashSet<string>(previous.Where(id => selectable.Any(i => i.Id == id)), StringComparer.Ordinal);
        foreach (var item in selectable)
        {
            if (!knownIds.Contains(item.Id)) next.Add(item.Id);
        }
        return next;
    }

    /// <summary>Packages to update for the ticked rows, in list order.</summary>
    public static List<WingetTarget> Targets(IEnumerable<AppUpdateItem> items, IReadOnlySet<string> selection) =>
        items.Where(i => i.IsSelectable && selection.Contains(i.Id)).Select(i => i.ToTarget()).ToList();

    // ------------------------------------------------------------ schedule

    public static CheckFrequency ParseFrequency(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "daily" => CheckFrequency.Daily,
        "weekly" => CheckFrequency.Weekly,
        _ => CheckFrequency.Off,
    };

    public static string FrequencyValue(CheckFrequency frequency) => frequency switch
    {
        CheckFrequency.Daily => "daily",
        CheckFrequency.Weekly => "weekly",
        _ => "off",
    };

    public static TimeSpan Interval(CheckFrequency frequency) => frequency switch
    {
        CheckFrequency.Daily => TimeSpan.FromDays(1),
        CheckFrequency.Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero,
    };

    /// <summary>When the background check should run next, or null when it is off. A check that
    /// is already due runs after <see cref="CatchUpDelay"/> so starting the app never waits on winget.</summary>
    public static DateTime? NextCheckDate(DateTime? lastCheck, CheckFrequency frequency, DateTime now)
    {
        if (frequency == CheckFrequency.Off) return null;
        if (lastCheck is null) return now + CatchUpDelay;
        var due = lastCheck.Value + Interval(frequency);
        return due <= now ? now + CatchUpDelay : due;
    }

    /// <summary>Whether a surface opening again should scan: nothing read in this session, an update
    /// was handed to another app, or the last answer is old.</summary>
    public static bool ShouldRecheck(bool hasCheckedThisSession, bool handoffPending, DateTime? lastCheck, DateTime now)
    {
        if (handoffPending) return true;
        if (!hasCheckedThisSession || lastCheck is null) return true;
        return now - lastCheck.Value >= StaleAfter;
    }

    /// <summary>Rows not announced yet (only those trigger a notification).</summary>
    public static List<AppUpdateItem> Fresh(IEnumerable<AppUpdateItem> items, IReadOnlySet<string> announced) =>
        items.Where(i => !announced.Contains(i.Id)).ToList();

    public static string NotificationBody(int total) =>
        total == 1 ? AppUpdateStrings.NotificationBodyOne : string.Format(AppUpdateStrings.NotificationBodyFormat, total);

    // ------------------------------------------------------------ wording

    /// <summary>"now", "5 minutes ago", "yesterday", "3 days ago", "last week" (named relative style).</summary>
    public static string RelativeTime(DateTime then, DateTime now)
    {
        var diff = now - then;
        if (diff < TimeSpan.FromMinutes(1)) return "now";
        if (diff < TimeSpan.FromHours(1))
        {
            var m = (int)diff.TotalMinutes;
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }
        if (then.Date == now.Date)
        {
            var h = (int)diff.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }
        var days = (now.Date - then.Date).Days;
        if (days == 1) return "yesterday";
        if (days < 7) return $"{days} days ago";
        if (days < 14) return "last week";
        if (days < 31) return $"{days / 7} weeks ago";
        return then.ToString("d", CultureInfo.CurrentCulture);
    }

    public static string LastCheckLine(DateTime? lastCheck, DateTime now) =>
        lastCheck is null ? AppUpdateStrings.NeverChecked : string.Format(AppUpdateStrings.LastCheckFormat, RelativeTime(lastCheck.Value, now));
}
