// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Remap;

/// <summary>One remap. <paramref name="App"/> limits it to one program (its exe name), empty for everywhere.</summary>
public sealed record KeyRemapRule(Shortcut From, Shortcut To, string App = "")
{
    /// <summary>A plain key for a plain key: it applies whatever modifiers are held, so Shift+Caps Lock becomes Shift+Esc.</summary>
    public bool IsKeyRemap => From.Modifiers == Modifiers.None && To.Modifiers == Modifiers.None;

    public bool IsValid => !From.IsEmpty && !To.IsEmpty && From != To && !Shortcut.IsModifierKey(From.Key);

    public bool MatchesApp(string? app) =>
        App.Length == 0 || (app is not null && string.Equals(KeyRemapEngine.AppName(App), KeyRemapEngine.AppName(app), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A key the engine wants sent.</summary>
public readonly record struct KeyOut(int Vk, bool Down);

/// <summary>
/// The remapping state machine, free of Win32 (unit tested; the service feeds it hook events on the hook
/// thread and sends what it returns).
///
/// A key remap swaps one key for another while modifiers pass through. A shortcut remap fires only when
/// exactly its modifiers are held: those the target does not use are lifted first and pressed again when
/// the trigger key comes up (if still held), so holding Ctrl and pressing K again repeats it. Lifting Alt or
/// Win on its own would open the menu bar or Start, so a masking key goes in first.
/// </summary>
public sealed class KeyRemapEngine
{
    private sealed class Active(KeyRemapRule rule, List<int> lifted, int[] pressed)
    {
        public KeyRemapRule Rule { get; } = rule;
        /// <summary>Physically held modifiers that were released for the target.</summary>
        public List<int> Lifted { get; } = lifted;
        /// <summary>Modifiers pressed for the target.</summary>
        public int[] Pressed { get; } = pressed;
    }

    private readonly HashSet<int> _heldModifiers = [];
    private readonly Dictionary<int, Active> _active = [];
    private List<KeyRemapRule> _rules = [];
    private HashSet<int> _triggers = [];

    public IReadOnlyList<KeyRemapRule> Rules
    {
        get => _rules;
        set
        {
            // Shortcut remaps before key remaps, so Ctrl+K → X wins over K → Y while Ctrl is held.
            _rules = value.Where(r => r.IsValid).OrderBy(r => r.IsKeyRemap ? 1 : 0).ThenBy(r => r.App.Length == 0 ? 1 : 0).ToList();
            _triggers = _rules.Select(r => r.From.Key).ToHashSet();
        }
    }

    public bool IsActive => _active.Count > 0;

    /// <summary>"Chrome.exe", "C:\…\chrome.exe" and "chrome" all name the same app.</summary>
    public static string AppName(string value)
    {
        var name = System.IO.Path.GetFileName(value.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// Handles one physical key event. Returns true to swallow it; <paramref name="output"/> receives the keys
    /// to send. <paramref name="foregroundApp"/> is only asked for when a rule could apply;
    /// <paramref name="canStart"/> false lets keys through untouched (Fieldkit itself is in front, for recording).
    /// </summary>
    public bool Handle(int vk, bool down, Func<string?> foregroundApp, bool canStart, List<KeyOut> output)
    {
        if (Shortcut.IsModifierKey(vk)) return HandleModifier(vk, down);

        if (_active.TryGetValue(vk, out var active))
        {
            if (down)
            {
                output.Add(new KeyOut(active.Rule.To.Key, true)); // auto-repeat
                return true;
            }
            _active.Remove(vk);
            output.Add(new KeyOut(active.Rule.To.Key, false));
            Release(active.Pressed, output);
            foreach (var m in active.Lifted) output.Add(new KeyOut(m, true));
            if (active.Lifted.Any(IsMenuOrWin)) Mask(output);
            return true;
        }

        if (!down || !canStart || !_triggers.Contains(vk)) return false;
        var held = HeldFlags();
        string? app = null;
        var appRead = false;
        foreach (var rule in _rules)
        {
            if (rule.From.Key != vk) continue;
            if (!rule.IsKeyRemap && rule.From.Modifiers != held) continue;
            if (rule.App.Length > 0)
            {
                if (!appRead)
                {
                    app = foregroundApp();
                    appRead = true;
                }
                if (!rule.MatchesApp(app)) continue;
            }
            Start(vk, rule, held, output);
            return true;
        }
        return false;
    }

    /// <summary>Lets go of everything the engine holds (the feature is switching off).</summary>
    public void ReleaseAll(List<KeyOut> output)
    {
        foreach (var active in _active.Values)
        {
            output.Add(new KeyOut(active.Rule.To.Key, false));
            Release(active.Pressed, output);
        }
        _active.Clear();
    }

    private void Start(int vk, KeyRemapRule rule, Modifiers held, List<KeyOut> output)
    {
        var lifted = new List<int>();
        var pressed = Array.Empty<int>();
        if (!rule.IsKeyRemap)
        {
            lifted = _heldModifiers.Where(m => !rule.To.Modifiers.HasFlag(FlagOf(m))).OrderBy(m => m).ToList();
            pressed = KeyCombo.ModifierKeys(rule.To.Modifiers & ~held);
            if (lifted.Any(IsMenuOrWin)) Mask(output);
            foreach (var m in lifted) output.Add(new KeyOut(m, false));
            foreach (var m in pressed) output.Add(new KeyOut(m, true));
        }
        output.Add(new KeyOut(rule.To.Key, true));
        _active[vk] = new Active(rule, lifted, pressed);
    }

    private bool HandleModifier(int vk, bool down)
    {
        if (down)
        {
            _heldModifiers.Add(vk);
            return false;
        }
        _heldModifiers.Remove(vk);
        // Released while the trigger is still down: it is already up for apps, so its up goes nowhere
        // and it is not pressed again afterwards.
        foreach (var active in _active.Values)
        {
            if (active.Lifted.Remove(vk)) return true;
        }
        return false;
    }

    private static void Release(int[] pressed, List<KeyOut> output)
    {
        if (pressed.Any(IsMenuOrWin)) Mask(output);
        for (var i = pressed.Length - 1; i >= 0; i--) output.Add(new KeyOut(pressed[i], false));
    }

    private static void Mask(List<KeyOut> output)
    {
        output.Add(new KeyOut(InputNative.VK_MASK, true));
        output.Add(new KeyOut(InputNative.VK_MASK, false));
    }

    private static bool IsMenuOrWin(int vk) => vk is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private Modifiers HeldFlags()
    {
        var m = Modifiers.None;
        foreach (var vk in _heldModifiers) m |= FlagOf(vk);
        return m;
    }

    public static Modifiers FlagOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Modifiers.Shift,
        0x11 or 0xA2 or 0xA3 => Modifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => Modifiers.Alt,
        0x5B or 0x5C => Modifiers.Win,
        _ => Modifiers.None,
    };
}
