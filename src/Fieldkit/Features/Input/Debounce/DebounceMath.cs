// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Debounce;

public enum ClickPhase { Down, Up }

/// <summary>
/// Switch-bounce filtering for the left, right and middle buttons with exact down/up ownership
/// (port of upstream MouseClickDebounceState). A healthy click passes immediately; a new press
/// within the window after an accepted release is dropped together with its own release. The
/// release of an accepted press is never dropped, so nothing can stay held in the target app.
/// Times are milliseconds (the hook's event time).
/// </summary>
public sealed class ClickDebounceState
{
    public const int DefaultWindowMs = 25;
    public const int MinWindowMs = 5;
    public const int MaxWindowMs = 100;

    public static int SanitizedWindow(int ms) => ms is >= MinWindowMs and <= MaxWindowMs ? ms : DefaultWindowMs;

    private struct ButtonState
    {
        public bool AcceptedDown;
        public bool SuppressedDown;
        public long? LastAcceptedUp;
        public long? LastEventTime;
    }

    private readonly ButtonState[] _buttons = new ButtonState[3];

    public void Reset() => Array.Clear(_buttons);

    /// <param name="button">0 left, 1 right, 2 middle.</param>
    public bool ShouldSuppress(int button, ClickPhase phase, long timeMs, int windowMs)
    {
        if (button is < 0 or > 2) return false;
        ref var s = ref _buttons[button];
        if (s.LastEventTime is { } previous && timeMs < previous) s = default; // clock wrapped: start over
        s.LastEventTime = timeMs;
        switch (phase)
        {
            case ClickPhase.Down:
                // A repeated down while an accepted press is held is noise; its up still belongs to it.
                if (s.AcceptedDown || s.SuppressedDown) return true;
                if (s.LastAcceptedUp is { } release && timeMs >= release && timeMs - release < Math.Max(0, windowMs))
                {
                    s.SuppressedDown = true;
                    return true;
                }
                s.AcceptedDown = true;
                return false;
            default:
                if (s.SuppressedDown)
                {
                    s.SuppressedDown = false;
                    return true;
                }
                if (!s.AcceptedDown) return false; // unmatched up: pass, it cannot stick
                s.AcceptedDown = false;
                s.LastAcceptedUp = timeMs;
                return false;
        }
    }
}

/// <summary>
/// Per-key duplicate-press filtering (port of upstream KeyboardDebounceState). Auto-repeat
/// (a down while the key is already down) is never filtered; a press arriving within the
/// window after the same key's accepted release is dropped. Times are milliseconds.
/// </summary>
public sealed class KeyDebounceState
{
    public const int DefaultWindowMs = 5;
    public const int MinWindowMs = 0;
    public const int MaxWindowMs = 500;
    private const long StaleGapMs = 5000;

    public static int SanitizedWindow(int ms) => Math.Clamp(ms, MinWindowMs, MaxWindowMs);

    private struct KeyState
    {
        public bool IsDown;
        public bool SuppressedDown;
        public long? LastAcceptedPress;
        public long? LastAcceptedRelease;
        public long? LastEventTime;
    }

    private readonly KeyState[] _keys = new KeyState[256];
    private int _lastAcceptedKey = -1;

    public void Reset()
    {
        Array.Clear(_keys);
        _lastAcceptedKey = -1;
    }

    public bool ShouldSuppress(int vk, bool isDown, long timeMs, int windowMs)
    {
        if (vk is < 0 or > 255) return false;
        ref var s = ref _keys[vk];
        if (s.LastEventTime is { } previous && (timeMs < previous || timeMs - previous > StaleGapMs))
        {
            s = default;
            if (_lastAcceptedKey == vk) _lastAcceptedKey = -1;
        }
        s.LastEventTime = timeMs;

        if (!isDown)
        {
            // The release of a dropped press is dropped too, so the app never sees half a key.
            if (s.SuppressedDown)
            {
                s.SuppressedDown = false;
                return true;
            }
            if (s.IsDown)
            {
                s.IsDown = false;
                s.LastAcceptedRelease = timeMs;
            }
            return false;
        }
        if (s.IsDown) return false; // auto-repeat
        if (s.SuppressedDown)
        {
            // Auto-repeat after a dropped bounce: the key really is held, so it goes through
            // from here on (upstream passes auto-repeat the same way) and its release follows.
            s.SuppressedDown = false;
            s.IsDown = true;
            return false;
        }
        var window = SanitizedWindow(windowMs);
        if (window > 0 && s.LastAcceptedRelease is { } release && _lastAcceptedKey == vk
            && timeMs >= release && timeMs - release < window)
        {
            s.SuppressedDown = true;
            return true;
        }
        s.IsDown = true;
        s.LastAcceptedPress = timeMs;
        _lastAcceptedKey = vk;
        return false;
    }
}

/// <summary>Per-key window overrides, persisted as "vk:ms,vk:ms" like upstream.</summary>
public static class KeyWindowCodec
{
    public static Dictionary<int, int> Decode(string? raw)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', 2);
            if (pieces.Length == 2 && int.TryParse(pieces[0], out var vk) && vk is > 0 and < 256
                && int.TryParse(pieces[1], out var ms))
                result[vk] = KeyDebounceState.SanitizedWindow(ms);
        }
        return result;
    }

    public static string Encode(IReadOnlyDictionary<int, int> windows) =>
        string.Join(",", windows.OrderBy(p => p.Key).Select(p => $"{p.Key}:{KeyDebounceState.SanitizedWindow(p.Value)}"));

    /// <summary>Keys offered in the per-key picker (letters, digits, common punctuation and controls).</summary>
    public static IReadOnlyList<int> CommonKeys { get; } =
    [
        .. Enumerable.Range('A', 26), .. Enumerable.Range('0', 10),
        0x20, 0x0D, 0x09, 0x08, 0x1B, 0xBC, 0xBE, 0xBF, 0xBA, 0xDE, 0xBD, 0xBB, 0xDB, 0xDD, 0xDC, 0xC0,
    ];

    public static string Label(int vk) => vk switch
    {
        0x0D => "Enter",
        0x08 => "Backspace",
        0x1B => "Esc",
        _ => Shortcut.KeyName(vk),
    };
}
