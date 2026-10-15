// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Sound;

/// <summary>
/// Where a device goes in a priority list that has not ranked it yet. Virtual devices
/// (loopback cables, mixers) play or record nothing on their own, so they never take
/// the place of hardware.
/// </summary>
public enum PriorityTier { Hardware = 1, Virtual = 2 }

/// <summary>What the headphone guard should do after an endpoint change.</summary>
public enum HeadphoneGuardAction { None, Lower, Restore }

/// <summary>
/// User order and pins of the mixer rows (upstream MixerAppArrangement). Pinned apps come
/// first; within each group the remembered order wins and unknown apps keep arrival order.
/// Moves only swap slots of the visible group, so closed and hidden apps keep their places.
/// </summary>
public sealed class MixerArrangement
{
    public MixerArrangement(IEnumerable<string>? order = null, IEnumerable<string>? pinned = null)
    {
        Order = SoundLogic.Unique(order ?? []);
        Pinned = SoundLogic.Unique(pinned ?? []);
    }

    public List<string> Order { get; private set; }
    public List<string> Pinned { get; }

    public bool IsPinned(string? id) => id is not null && Pinned.Contains(id);

    public List<T> Ordered<T>(IEnumerable<T> items, Func<T, string?> identity)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Order.Count; i++) ranks[Order[i]] = i;
        return items
            .Select((item, index) => (item, index, id: identity(item)))
            .OrderBy(x => IsPinned(x.id) ? 0 : 1)
            .ThenBy(x => x.id is not null && ranks.TryGetValue(x.id, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    public void TogglePin(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!Pinned.Remove(id)) Pinned.Add(id);
    }

    public string? Neighbor(string id, int offset, IReadOnlyList<string> visibleIds)
    {
        var group = visibleIds.Where(v => IsPinned(v) == IsPinned(id)).ToList();
        var index = group.IndexOf(id);
        if (index < 0) return null;
        var target = index + offset;
        return target >= 0 && target < group.Count ? group[target] : null;
    }

    public void Move(string id, int offset, IReadOnlyList<string> visibleIds)
    {
        if (Neighbor(id, offset, visibleIds) is { } neighbor) Move(id, neighbor, offset > 0, visibleIds);
    }

    public void Move(string id, string target, bool after, IReadOnlyList<string> visibleIds)
    {
        if (id == target || !visibleIds.Contains(id) || !visibleIds.Contains(target) || IsPinned(id) != IsPinned(target)) return;
        var group = SoundLogic.Unique(visibleIds.Where(v => IsPinned(v) == IsPinned(id)));
        group.Remove(id);
        var index = group.IndexOf(target);
        if (index < 0) return;
        group.Insert(index + (after ? 1 : 0), id);
        var moving = new HashSet<string>(group, StringComparer.Ordinal);
        using var reordered = group.GetEnumerator();
        Order = SoundLogic.Unique(Order.Concat(visibleIds))
            .Select(existing =>
            {
                if (!moving.Contains(existing)) return existing;
                reordered.MoveNext();
                return reordered.Current;
            })
            .ToList();
    }
}

/// <summary>Pure decision logic of the sound features, kept free of COM so it can be unit tested.</summary>
public static class SoundLogic
{
    /// <summary>Highest per-app level the mixer offers (200 %).</summary>
    public const int MaxAppPercent = 200;
    public const int MinHeadphoneVolumePercent = 10;
    public const int DefaultHeadphoneVolumePercent = 25;
    /// <summary>A media key explains a music-app launch only within this window (upstream: 2 s, plus Windows' slower UWP start).</summary>
    public const int MusicLaunchWindowMs = 3000;
    public const string DefaultBlockedMusicApps = "Microsoft.Media.Player, Music.UI, wmplayer";

    public static List<string> Unique(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ids.Where(id => !string.IsNullOrEmpty(id) && seen.Add(id)).ToList();
    }

    // ------------------------------------------------------------------ mixer

    /// <summary>Text typed beside a slider ("85", "85 %", "85,5") to a 0–1 (or 0–2) level; null if invalid.</summary>
    public static double? VolumeFraction(string text, int maximumPercent, CultureInfo? culture = null)
    {
        if (maximumPercent < 0) return null;
        var normalized = text.Trim();
        if (normalized.EndsWith('%')) normalized = normalized[..^1].Trim();
        var separator = (culture ?? CultureInfo.CurrentCulture).NumberFormat.NumberDecimalSeparator;
        if (separator != ".") normalized = normalized.Replace(separator, ".", StringComparison.Ordinal);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
            !double.IsFinite(percent)) return null;
        return Math.Clamp(percent, 0, maximumPercent) / 100.0;
    }

    public static int Percent(double level) => (int)Math.Round(level * 100, MidpointRounding.AwayFromZero);

    public static bool IsUnity(double level) => Percent(level) == 100;

    /// <summary>With "Hide inactive apps" on, only playing apps or ones the user adjusted stay listed.</summary>
    public static bool ShouldShowApp(bool isPlaying, double level, bool hasCustomOutput, bool hideInactive) =>
        !hideInactive || isPlaying || !IsUnity(level) || hasCustomOutput;

    /// <summary>
    /// The level a row shows: the session volume below 100 %, and the requested boost above it
    /// (Windows caps session volume at 100 %).
    /// </summary>
    public static double DisplayLevel(float sessionVolume, int? boostPercent) =>
        sessionVolume < 0.995f || boostPercent is not (> 100) ? sessionVolume : Math.Min(boostPercent.Value, MaxAppPercent) / 100.0;

    /// <summary>The stable identity of an app: its executable name, lower-case, without extension.</summary>
    public static string AppId(string? exePath, string? processName)
    {
        var name = !string.IsNullOrEmpty(exePath) ? Path.GetFileNameWithoutExtension(exePath) : processName;
        return string.IsNullOrEmpty(name) ? string.Empty : name.ToLowerInvariant();
    }

    /// <summary>Strips the device-interface wrapping AudioPolicyConfig puts around endpoint ids.</summary>
    public static string? UnpackPolicyDeviceId(string? packed, string token, string renderSuffix, string captureSuffix)
    {
        if (string.IsNullOrEmpty(packed)) return null;
        var id = packed;
        if (id.StartsWith(token, StringComparison.OrdinalIgnoreCase)) id = id[token.Length..];
        if (id.EndsWith(renderSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^renderSuffix.Length];
        else if (id.EndsWith(captureSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^captureSuffix.Length];
        return id.Length == 0 ? null : id;
    }

    // ------------------------------------------------------------- devices

    private static readonly string[] HeadphoneTerms =
    [
        "headphone", "headphones", "headset", "earphone", "earphones", "earbud", "earbuds",
        "airpod", "airpods", "galaxy buds", "pixel buds", "beats", "bose qc", "sony wh", "sony wf",
        "jabra", "soundcore", "hands free", "hands-free",
    ];

    private static readonly string[] VirtualTerms =
    [
        "virtual", "vb-audio", "voicemeeter", "cable input", "cable output", "steam streaming",
        "nvidia broadcast", "obs", "loopback",
    ];

    /// <summary>Endpoint form factor Headphones (3) or Headset (5), or a name that says so.</summary>
    public static bool LooksLikeHeadphones(string name, uint formFactor)
    {
        if (formFactor is 3 or 5) return true;
        var normalized = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]+", " ");
        return HeadphoneTerms.Any(t => normalized.Contains(t, StringComparison.Ordinal));
    }

    public static bool LooksVirtual(string name)
    {
        var lower = name.ToLowerInvariant();
        return VirtualTerms.Any(t => Regex.IsMatch(lower, $@"(^|[^a-z]){Regex.Escape(t)}($|[^a-z])"));
    }

    /// <summary>The output the switcher moves to: the selected one after the current, wrapping. Null if none (or only the current).</summary>
    public static string? NextSelectedOutput(string? currentId, IReadOnlyList<string> selectedIds, ISet<string> availableIds)
    {
        var candidates = Unique(selectedIds).Where(availableIds.Contains).ToList();
        if (candidates.Count == 0) return null;
        var index = currentId is null ? -1 : candidates.IndexOf(currentId);
        if (index < 0) return candidates[0];
        return candidates.Count > 1 ? candidates[(index + 1) % candidates.Count] : null;
    }

    /// <summary>The first listed device that is connected now.</summary>
    public static string? FirstAvailablePriority(IEnumerable<string> orderedIds, ISet<string> availableIds) =>
        Unique(orderedIds).FirstOrDefault(availableIds.Contains);

    /// <summary>A first-time list: the device in use, then hardware, then virtual devices, otherwise in Windows' order.</summary>
    public static List<string> InitialPriorityList(IEnumerable<string> availableIds, string? currentId, Func<string, PriorityTier> tier)
    {
        var unique = Unique(availableIds);
        var lead = unique.Where(id => id == currentId);
        var rest = unique.Select((id, index) => (id, index))
            .Where(x => x.id != currentId)
            .OrderBy(x => (int)tier(x.id)).ThenBy(x => x.index)
            .Select(x => x.id);
        return lead.Concat(rest).ToList();
    }

    /// <summary>
    /// Where a device the list has never seen joins it: first when it is the one in use
    /// (Windows or the user just picked it), else above the virtual entries, or last when it
    /// is virtual itself.
    /// </summary>
    public static List<string> PlaceNewPriorityDevice(string id, IReadOnlyList<string> list, bool isCurrent, Func<string, PriorityTier> tier)
    {
        if (string.IsNullOrEmpty(id) || list.Contains(id)) return list.ToList();
        if (isCurrent) return [id, .. list];
        var placed = list.ToList();
        var firstVirtual = placed.FindIndex(x => tier(x) == PriorityTier.Virtual);
        if (tier(id) == PriorityTier.Virtual || firstVirtual < 0) placed.Add(id);
        else placed.Insert(firstVirtual, id);
        return placed;
    }

    /// <summary>Priority only acts when a device arrives or leaves, never when the default merely changes.</summary>
    public static bool AvailabilityChanged(ISet<string>? previous, ISet<string> current) =>
        previous is not null && !previous.SetEquals(current);

    /// <summary>Moves one entry of an ordered list by <paramref name="offset"/>; unchanged when out of range.</summary>
    public static List<string> MoveInList(IReadOnlyList<string> list, string id, int offset)
    {
        var result = list.ToList();
        var index = result.IndexOf(id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= result.Count) return result;
        (result[index], result[target]) = (result[target], result[index]);
        return result;
    }

    // ------------------------------------------------------- headphone guard

    public static int SanitizedHeadphoneVolume(int percent) => Math.Clamp(percent, MinHeadphoneVolumePercent, 100);

    /// <summary>
    /// After an endpoint change: headphones back as the default restore the speakers' earlier
    /// level; headphones that were the default and are now gone lower the new (non-headphone)
    /// default, once per device.
    /// </summary>
    public static HeadphoneGuardAction EvaluateHeadphoneGuard(
        bool enabled,
        string? previousDefaultId, bool previousWasHeadphones, bool previousStillPresent,
        string? nextDefaultId, bool nextIsHeadphones,
        string? lastLoweredId)
    {
        if (nextDefaultId is not null && nextIsHeadphones) return HeadphoneGuardAction.Restore;
        if (!enabled || previousDefaultId is null || !previousWasHeadphones || previousStillPresent) return HeadphoneGuardAction.None;
        if (nextDefaultId is null || nextDefaultId == lastLoweredId) return HeadphoneGuardAction.None;
        return HeadphoneGuardAction.Lower;
    }

    /// <summary>The lowered level is only put back while nobody changed it since.</summary>
    public static bool ShouldRestoreVolume(double applied, double? current) =>
        current is { } c && Math.Abs(c - applied) < 0.005;

    // ---------------------------------------------------------- music block

    /// <summary>Play/Pause, Next, Previous and Stop: the keys that make Windows open a player.</summary>
    public static bool IsMusicLaunchKey(int vk) => vk is 0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xFA;

    /// <summary>"Music.UI.exe, wmplayer" → {"music.ui", "wmplayer"}.</summary>
    public static HashSet<string> ParseProcessNames(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
            if (name.Length > 0) set.Add(name.ToLowerInvariant());
        }
        return set;
    }

    /// <summary>
    /// A launch is blocked only when a media key explains it: it started within the window
    /// after the key, the app was not already running then, and no click or other key came
    /// after the media key (that newer gesture may be the user opening the app on purpose).
    /// </summary>
    public static bool ShouldBlockLaunch(long nowMs, long? triggerMs, long? lastGestureMs, bool wasRunningAtTrigger)
    {
        if (triggerMs is not { } trigger || wasRunningAtTrigger) return false;
        var elapsed = nowMs - trigger;
        if (elapsed < 0 || elapsed > MusicLaunchWindowMs) return false;
        return lastGestureMs is not { } gesture || gesture < trigger;
    }
}
