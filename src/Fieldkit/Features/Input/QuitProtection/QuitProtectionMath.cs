// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.QuitProtection;

/// <summary>The two protected actions (upstream QuitProtectionShortcut: ⌘Q quit, ⌘W close).</summary>
public enum ProtectedAction { Quit, Close }

public enum ConfirmationMode { Hold, DoublePress, ExtraModifier }

public enum AppScope { All, SelectedOnly, AllExceptSelected }

/// <summary>A protected key combination: the action, its own modifiers and its key.</summary>
public readonly record struct ProtectedCombo(ProtectedAction Action, Modifiers Base, int Key)
{
    public override string ToString() => new Shortcut(Base, Key).ToString();
}

/// <summary>Per-action configuration (upstream QuitProtectionConfiguration).</summary>
public sealed record QuitProtectionConfig(
    bool Enabled,
    ConfirmationMode Mode,
    int HoldMs,
    int DoublePressMs,
    Modifiers ExtraModifier,
    AppScope Scope,
    IReadOnlySet<string> Apps,
    bool ShowFeedback)
{
    public static readonly QuitProtectionConfig Off =
        new(false, ConfirmationMode.Hold, 800, 600, Modifiers.Shift, AppScope.All, new HashSet<string>(), true);
}

/// <summary>
/// The pure rules of quit and close protection. On Windows "quit" is Alt+F4 and Ctrl+Q, and
/// "close" is Ctrl+W and Ctrl+F4. Only the exact combination is protected; every other shortcut
/// passes untouched.
/// </summary>
public static class QuitProtectionMath
{
    public const int HoldMinMs = 250, HoldMaxMs = 2000, DefaultHoldMs = 800;
    public const int DoubleMinMs = 200, DoubleMaxMs = 1500, DefaultDoubleMs = 600;
    public const int ExtraModifierExpiryMs = 1500;

    public static readonly ProtectedCombo[] Combos =
    [
        new(ProtectedAction.Quit, Modifiers.Alt, 0x73),   // Alt+F4
        new(ProtectedAction.Quit, Modifiers.Ctrl, 'Q'),
        new(ProtectedAction.Close, Modifiers.Ctrl, 'W'),
        new(ProtectedAction.Close, Modifiers.Ctrl, 0x73), // Ctrl+F4
    ];

    public static string Prefix(ProtectedAction a) => a == ProtectedAction.Quit ? "quitWindowProtection.quit" : "quitWindowProtection.close";

    public static string Title(ProtectedAction a) => a == ProtectedAction.Quit ? "Alt+F4 and Ctrl+Q" : "Ctrl+W and Ctrl+F4";

    public static int SanitizedHold(int ms) => Math.Clamp(ms, HoldMinMs, HoldMaxMs);
    public static int SanitizedDouble(int ms) => Math.Clamp(ms, DoubleMinMs, DoubleMaxMs);

    public static ConfirmationMode ParseMode(string? raw) => raw switch
    {
        "doublePress" => ConfirmationMode.DoublePress,
        "extraModifier" => ConfirmationMode.ExtraModifier,
        _ => ConfirmationMode.Hold,
    };

    public static AppScope ParseScope(string? raw) => raw switch
    {
        "selectedOnly" => AppScope.SelectedOnly,
        "allExceptSelected" => AppScope.AllExceptSelected,
        _ => AppScope.All,
    };

    public static Modifiers ParseExtraModifier(string? raw) => raw switch
    {
        "alt" or "option" => Modifiers.Alt,
        "ctrl" or "control" => Modifiers.Ctrl,
        _ => Modifiers.Shift,
    };

    /// <summary>The extra key for a combination; one it already uses falls back to Shift.</summary>
    public static Modifiers EffectiveExtra(ProtectedCombo combo, Modifiers extra) =>
        (combo.Base & extra) != 0 || extra == Modifiers.None ? Modifiers.Shift : extra;

    public static bool ScopeAllows(AppScope scope, string? processName, IReadOnlySet<string> apps)
    {
        var contains = !string.IsNullOrEmpty(processName) && apps.Contains(processName);
        return scope switch
        {
            AppScope.SelectedOnly => contains,
            AppScope.AllExceptSelected => !contains,
            _ => true,
        };
    }

    /// <summary>The protected combination this key belongs to, held exactly with its own modifiers.</summary>
    public static ProtectedCombo? MatchBase(int vk, Modifiers held)
    {
        foreach (var c in Combos)
        {
            if (c.Key == vk && c.Base == held) return c;
        }
        return null;
    }

    /// <summary>The combination this key confirms in extra-modifier mode (its modifiers plus the extra one).</summary>
    public static ProtectedCombo? MatchExtra(int vk, Modifiers held, Func<ProtectedAction, Modifiers> extraFor)
    {
        foreach (var c in Combos)
        {
            if (c.Key == vk && held == (c.Base | EffectiveExtra(c, extraFor(c.Action)))) return c;
        }
        return null;
    }

    /// <summary>Hook event times are milliseconds; the second press must land within the interval.</summary>
    public static bool IsWithinDoublePress(long firstMs, long secondMs, int intervalMs) =>
        secondMs >= firstMs && secondMs - firstMs <= SanitizedDouble(intervalMs);

    public static string HoldTitle(ProtectedCombo c) => $"Hold {c} to {Verb(c.Action)}";
    public static string DoubleTitle(ProtectedCombo c) => $"Press {c} again to {Verb(c.Action)}";
    public static string ExtraTitle(ProtectedCombo c, Modifiers extra) =>
        $"Use {new Shortcut(c.Base | EffectiveExtra(c, extra), c.Key)} to {Verb(c.Action)}";

    private static string Verb(ProtectedAction a) => a == ProtectedAction.Quit ? "quit" : "close";
}
