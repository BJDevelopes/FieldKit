// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.WindowTools;

/// <summary>Every window placement, in upstream order (raw ids are the upstream raw values).</summary>
public enum LayoutAction
{
    LeftHalf, RightHalf, TopHalf, BottomHalf, CenterHalf,
    LeftThird, CenterThird, RightThird, LeftTwoThirds, RightTwoThirds, CenterTwoThirds,
    TopThird, MiddleThird, BottomThird, TopTwoThirds, BottomTwoThirds,
    TopQuarter, UpperMiddleQuarter, LowerMiddleQuarter, BottomQuarter,
    LeftQuarter, LeftMiddleQuarter, RightMiddleQuarter, RightQuarter,
    TopLeftSixth, TopCenterSixth, TopRightSixth,
    BottomLeftSixth, BottomCenterSixth, BottomRightSixth,
    TopLeft, TopRight, BottomLeft, BottomRight,
    Maximize, MarginMaximize, FullScreen, Center,
    PreviousDisplay, NextDisplay, Restore,
    // Step resizes around the window's centre (upstream PR #2876). Appended: ids are persisted.
    Grow, Shrink,
}

/// <summary>Titles, glyphs, groups, setting keys and default shortcuts of the layout actions.</summary>
public static class LayoutActions
{
    public static readonly LayoutAction[] Halves =
        [LayoutAction.LeftHalf, LayoutAction.RightHalf, LayoutAction.TopHalf, LayoutAction.BottomHalf, LayoutAction.CenterHalf];

    public static readonly LayoutAction[] Thirds =
    [
        LayoutAction.LeftThird, LayoutAction.CenterThird, LayoutAction.RightThird, LayoutAction.LeftTwoThirds,
        LayoutAction.RightTwoThirds, LayoutAction.CenterTwoThirds, LayoutAction.TopThird, LayoutAction.MiddleThird,
        LayoutAction.BottomThird, LayoutAction.TopTwoThirds, LayoutAction.BottomTwoThirds,
    ];

    public static readonly LayoutAction[] QuarterRows =
        [LayoutAction.TopQuarter, LayoutAction.UpperMiddleQuarter, LayoutAction.LowerMiddleQuarter, LayoutAction.BottomQuarter];

    public static readonly LayoutAction[] QuarterColumns =
        [LayoutAction.LeftQuarter, LayoutAction.LeftMiddleQuarter, LayoutAction.RightMiddleQuarter, LayoutAction.RightQuarter];

    public static readonly LayoutAction[] Sixths =
    [
        LayoutAction.TopLeftSixth, LayoutAction.TopCenterSixth, LayoutAction.TopRightSixth,
        LayoutAction.BottomLeftSixth, LayoutAction.BottomCenterSixth, LayoutAction.BottomRightSixth,
    ];

    public static readonly LayoutAction[] Corners =
        [LayoutAction.TopLeft, LayoutAction.TopRight, LayoutAction.BottomLeft, LayoutAction.BottomRight];

    public static readonly LayoutAction[] Other =
    [
        LayoutAction.Maximize, LayoutAction.MarginMaximize, LayoutAction.FullScreen, LayoutAction.Center,
        LayoutAction.PreviousDisplay, LayoutAction.NextDisplay, LayoutAction.Restore,
        LayoutAction.Grow, LayoutAction.Shrink,
    ];

    /// <summary>The families in the order Settings and the panel list them.</summary>
    public static IReadOnlyList<(string Title, LayoutAction[] Actions)> Groups { get; } =
    [
        ("Halves", Halves), ("Thirds", Thirds), ("Quarter rows", QuarterRows), ("Quarter columns", QuarterColumns),
        ("Sixths", Sixths), ("Corners", Corners), ("Actions", Other),
    ];

    public static IReadOnlyList<LayoutAction> All { get; } = Enum.GetValues<LayoutAction>();

    /// <summary>Upstream raw value ("leftHalf").</summary>
    public static string Id(this LayoutAction action)
    {
        var name = action.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static LayoutAction? FromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var a in All)
            if (string.Equals(a.Id(), id.Trim(), StringComparison.Ordinal)) return a;
        return null;
    }

    public static string ShortcutKey(this LayoutAction action) => $"windowLayout.shortcut.{action.Id()}";

    /// <summary>
    /// Upstream ⌃⌥ defaults map to Ctrl+Alt for arrows and Enter, and Ctrl+Alt+Win for letters
    /// (Ctrl+Alt+letter is AltGr+letter on many keyboard layouts and would block typing €, @, …); ⌃⌥⌘→ (next display) becomes Ctrl+Alt+Shift+Right
    /// because Win+Ctrl+arrows switch virtual desktops. Newer actions start unassigned, like upstream.
    /// </summary>
    public static string DefaultShortcut(this LayoutAction action) => action switch
    {
        LayoutAction.LeftHalf => "Ctrl+Alt+Left",
        LayoutAction.RightHalf => "Ctrl+Alt+Right",
        LayoutAction.TopHalf => "Ctrl+Alt+Up",
        LayoutAction.BottomHalf => "Ctrl+Alt+Down",
        LayoutAction.TopLeft => "Ctrl+Alt+Win+U",
        LayoutAction.TopRight => "Ctrl+Alt+Win+I",
        LayoutAction.BottomLeft => "Ctrl+Alt+Win+J",
        LayoutAction.BottomRight => "Ctrl+Alt+Win+K",
        LayoutAction.Maximize => "Ctrl+Alt+Enter",
        LayoutAction.Center => "Ctrl+Alt+Win+C",
        LayoutAction.Grow => "Ctrl+Alt+Win+=",
        LayoutAction.Shrink => "Ctrl+Alt+Win+-",
        LayoutAction.Restore => "Ctrl+Alt+Win+R",
        LayoutAction.LeftThird => "Ctrl+Alt+Win+D",
        LayoutAction.CenterThird => "Ctrl+Alt+Win+F",
        LayoutAction.RightThird => "Ctrl+Alt+Win+G",
        LayoutAction.LeftTwoThirds => "Ctrl+Alt+Win+E",
        LayoutAction.RightTwoThirds => "Ctrl+Alt+Win+T",
        LayoutAction.NextDisplay => "Ctrl+Alt+Shift+Right",
        _ => string.Empty,
    };

    public static string Title(this LayoutAction action) => action switch
    {
        LayoutAction.LeftHalf => "Left",
        LayoutAction.RightHalf => "Right",
        LayoutAction.TopHalf => "Top",
        LayoutAction.BottomHalf => "Bottom",
        LayoutAction.CenterHalf => "Center half",
        LayoutAction.LeftThird => "Left 1/3",
        LayoutAction.CenterThird => "Center 1/3",
        LayoutAction.RightThird => "Right 1/3",
        LayoutAction.LeftTwoThirds => "Left 2/3",
        LayoutAction.RightTwoThirds => "Right 2/3",
        LayoutAction.CenterTwoThirds => "Center 2/3",
        LayoutAction.TopThird => "Top 1/3",
        LayoutAction.MiddleThird => "Middle 1/3",
        LayoutAction.BottomThird => "Bottom 1/3",
        LayoutAction.TopTwoThirds => "Top 2/3",
        LayoutAction.BottomTwoThirds => "Bottom 2/3",
        LayoutAction.TopQuarter => "Top 1/4",
        LayoutAction.UpperMiddleQuarter => "Upper middle 1/4",
        LayoutAction.LowerMiddleQuarter => "Lower middle 1/4",
        LayoutAction.BottomQuarter => "Bottom 1/4",
        LayoutAction.LeftQuarter => "Left 1/4",
        LayoutAction.LeftMiddleQuarter => "Left middle 1/4",
        LayoutAction.RightMiddleQuarter => "Right middle 1/4",
        LayoutAction.RightQuarter => "Right 1/4",
        LayoutAction.TopLeftSixth => "Top left 1/6",
        LayoutAction.TopCenterSixth => "Top center 1/6",
        LayoutAction.TopRightSixth => "Top right 1/6",
        LayoutAction.BottomLeftSixth => "Bottom left 1/6",
        LayoutAction.BottomCenterSixth => "Bottom center 1/6",
        LayoutAction.BottomRightSixth => "Bottom right 1/6",
        LayoutAction.TopLeft => "Top left",
        LayoutAction.TopRight => "Top right",
        LayoutAction.BottomLeft => "Bottom left",
        LayoutAction.BottomRight => "Bottom right",
        LayoutAction.Maximize => "Maximize",
        LayoutAction.MarginMaximize => "Maximize with Margin",
        LayoutAction.FullScreen => "Full Screen",
        LayoutAction.Center => "Center",
        LayoutAction.PreviousDisplay => "Previous display",
        LayoutAction.NextDisplay => "Next display",
        LayoutAction.Restore => "Restore",
        LayoutAction.Grow => "Make larger",
        LayoutAction.Shrink => "Make smaller",
        _ => action.ToString(),
    };

    /// <summary>Actions that fill a fixed part of the screen (usable by edge-snap areas).</summary>
    public static bool IsPlacement(this LayoutAction action) =>
        action is not (LayoutAction.Center or LayoutAction.FullScreen or LayoutAction.Restore
            or LayoutAction.PreviousDisplay or LayoutAction.NextDisplay or LayoutAction.Grow or LayoutAction.Shrink);

    /// <summary>Hidden-from-grid set, stored as a sorted comma list of ids.</summary>
    public static HashSet<LayoutAction> ParseHidden(string? stored) =>
        (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FromId).OfType<LayoutAction>().ToHashSet();

    public static string HiddenStorage(IEnumerable<LayoutAction> actions) =>
        string.Join(",", actions.Select(a => a.Id()).Distinct().OrderBy(s => s, StringComparer.Ordinal));
}
