// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.SuperKey;

/// <summary>The key held as the super key (upstream SuperKeySource; Command → Win).</summary>
public enum SuperKeySource { CapsLock, RightWin, RightAlt, RightCtrl, RightShift }

/// <summary>What a tap of the key on its own does (upstream SuperKeySoloAction).</summary>
public enum SuperKeySoloAction { None, CapsLock, InputSource, Escape }

/// <summary>The pure half of the super key: settings codecs and the per-event decision.</summary>
public static class SuperKeyMath
{
    public const Modifiers DefaultModifiers = Modifiers.Ctrl | Modifiers.Alt | Modifiers.Shift | Modifiers.Win;

    public static readonly SuperKeySource[] Sources =
        [SuperKeySource.CapsLock, SuperKeySource.RightWin, SuperKeySource.RightAlt, SuperKeySource.RightCtrl, SuperKeySource.RightShift];

    public static SuperKeySource ParseSource(string? raw) => raw switch
    {
        "rightWin" or "rightCommand" => SuperKeySource.RightWin,
        "rightAlt" or "rightOption" => SuperKeySource.RightAlt,
        "rightCtrl" or "rightControl" => SuperKeySource.RightCtrl,
        "rightShift" => SuperKeySource.RightShift,
        _ => SuperKeySource.CapsLock,
    };

    public static string StorageValue(SuperKeySource s) => s switch
    {
        SuperKeySource.RightWin => "rightWin",
        SuperKeySource.RightAlt => "rightAlt",
        SuperKeySource.RightCtrl => "rightCtrl",
        SuperKeySource.RightShift => "rightShift",
        _ => "capsLock",
    };

    public static int VirtualKey(SuperKeySource s) => s switch
    {
        SuperKeySource.RightWin => 0x5C,
        SuperKeySource.RightAlt => 0xA5,
        SuperKeySource.RightCtrl => 0xA3,
        SuperKeySource.RightShift => 0xA1,
        _ => 0x14,
    };

    public static string Label(SuperKeySource s) => s switch
    {
        SuperKeySource.RightWin => "Right Win",
        SuperKeySource.RightAlt => "Right Alt",
        SuperKeySource.RightCtrl => "Right Ctrl",
        SuperKeySource.RightShift => "Right Shift",
        _ => "Caps Lock",
    };

    public static SuperKeySoloAction ParseSolo(string? raw) => raw switch
    {
        "capsLock" => SuperKeySoloAction.CapsLock,
        "inputSource" => SuperKeySoloAction.InputSource,
        "escape" => SuperKeySoloAction.Escape,
        _ => SuperKeySoloAction.None,
    };

    public static string StorageValue(SuperKeySoloAction a) => a switch
    {
        SuperKeySoloAction.CapsLock => "capsLock",
        SuperKeySoloAction.InputSource => "inputSource",
        SuperKeySoloAction.Escape => "escape",
        _ => "none",
    };

    /// <summary>Ctrl, Alt or Win must be among them: Shift alone would only type capitals.</summary>
    public static bool HasPrimaryModifier(Modifiers m) => (m & (Modifiers.Ctrl | Modifiers.Alt | Modifiers.Win)) != 0;

    public static Modifiers ParseModifiers(string? stored)
    {
        if (stored is null) return DefaultModifiers;
        var m = Modifiers.None;
        foreach (var token in stored.Split('+'))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": m |= Modifiers.Ctrl; break;
                case "alt" or "option": m |= Modifiers.Alt; break;
                case "shift": m |= Modifiers.Shift; break;
                case "win" or "command": m |= Modifiers.Win; break;
                default: return DefaultModifiers;
            }
        }
        return HasPrimaryModifier(m) ? m : DefaultModifiers;
    }

    public static string StorageValue(Modifiers m)
    {
        if (!HasPrimaryModifier(m)) m = DefaultModifiers;
        var parts = new List<string>(4);
        if (m.HasFlag(Modifiers.Ctrl)) parts.Add("ctrl");
        if (m.HasFlag(Modifiers.Alt)) parts.Add("alt");
        if (m.HasFlag(Modifiers.Shift)) parts.Add("shift");
        if (m.HasFlag(Modifiers.Win)) parts.Add("win");
        return string.Join("+", parts);
    }

    public static string Display(Modifiers m) => new Shortcut(m, 0x41).ToString()[..^2];

    public static SuperKeySoloAction SoloEffect(SuperKeySoloAction action, bool longHold, bool repeated) => action switch
    {
        SuperKeySoloAction.Escape => repeated ? SuperKeySoloAction.None : SuperKeySoloAction.Escape,
        SuperKeySoloAction.CapsLock => repeated ? SuperKeySoloAction.None : SuperKeySoloAction.CapsLock,
        SuperKeySoloAction.InputSource => longHold ? SuperKeySoloAction.CapsLock : SuperKeySoloAction.InputSource,
        _ => SuperKeySoloAction.None,
    };
}

public enum SuperKeyDecision { Swallow, AddModifiers, Pass, SoloTap, SoloHold }

/// <summary>
/// Whether the key is held, whether anything else happened meanwhile and since when (port of
/// upstream SuperKeySupport.State). Times are milliseconds.
/// </summary>
public sealed class SuperKeyState
{
    public const long SoloHoldThresholdMs = 500;

    public bool IsHeld { get; private set; }
    public bool IsAlone { get; private set; }
    /// <summary>The key auto-repeated while held alone (reported with the solo decision).</summary>
    public bool Repeated { get; private set; }
    private long? _downAt;

    /// <param name="hasPrimaryModifiers">Ctrl/Alt/Win already held when the key went down.</param>
    public SuperKeyDecision TriggerDown(bool hasPrimaryModifiers, long timeMs)
    {
        if (!IsHeld)
        {
            IsAlone = !hasPrimaryModifiers;
            _downAt = hasPrimaryModifiers ? null : timeMs;
            Repeated = false;
        }
        else
        {
            Repeated = true; // a down while held is the keyboard's auto-repeat
        }
        IsHeld = true;
        return SuperKeyDecision.Swallow;
    }

    public SuperKeyDecision TriggerUp(long timeMs)
    {
        var wasAlone = IsHeld && IsAlone;
        var wasLong = wasAlone && _downAt is { } down && timeMs >= down && timeMs - down >= SoloHoldThresholdMs;
        IsHeld = false;
        IsAlone = false;
        _downAt = null;
        if (wasLong) return SuperKeyDecision.SoloHold;
        return wasAlone ? SuperKeyDecision.SoloTap : SuperKeyDecision.Swallow;
    }

    public SuperKeyDecision OtherKey()
    {
        if (!IsHeld) return SuperKeyDecision.Pass;
        IsAlone = false;
        _downAt = null;
        Repeated = false;
        return SuperKeyDecision.AddModifiers;
    }

    public SuperKeyDecision OtherModifier()
    {
        if (IsHeld)
        {
            IsAlone = false;
            _downAt = null;
            Repeated = false;
        }
        return SuperKeyDecision.Pass;
    }

    public void Reset()
    {
        IsHeld = IsAlone = Repeated = false;
        _downAt = null;
    }
}
