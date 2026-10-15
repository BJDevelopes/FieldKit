// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Core;

/// <summary>A live state a feature shows on the app's tray icon (a coloured dot plus a tooltip line).</summary>
/// <param name="Text">One tooltip line, e.g. "Keep awake: ends in 25 min".</param>
/// <param name="Rgb">Dot colour as 0xRRGGBB.</param>
/// <param name="Priority">The highest priority badge picks the dot colour.</param>
public sealed record TrayBadge(string Text, int Rgb, int Priority = 0);

/// <summary>
/// Lets features mark the tray icon while they are doing something (upstream changes the menu bar
/// icon for an active keep-awake session). Safe from any thread; the icon redraws on the UI thread.
/// </summary>
public static class TrayStatus
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, TrayBadge> Badges = new(StringComparer.Ordinal);

    public static event Action? Changed;

    /// <summary>Sets (or with null, clears) the badge owned by <paramref name="source"/>.</summary>
    public static void Set(string source, TrayBadge? badge)
    {
        lock (Gate)
        {
            if (badge is null)
            {
                if (!Badges.Remove(source)) return;
            }
            else
            {
                if (Badges.TryGetValue(source, out var existing) && existing == badge) return;
                Badges[source] = badge;
            }
        }
        UI.Ui.OnUi(() => Changed?.Invoke());
    }

    public static IReadOnlyList<TrayBadge> Current
    {
        get
        {
            lock (Gate) return Badges.Values.OrderByDescending(b => b.Priority).ToList();
        }
    }
}
