// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Windows.Interop;
using Fieldkit.Core;

namespace Fieldkit.Interop;

[Flags]
public enum Modifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A keyboard shortcut (modifiers + one virtual key). Persisted as text such as
/// "Ctrl+Alt+V" so settings files stay readable. An empty string means "none".
/// </summary>
public readonly record struct Shortcut(Modifiers Modifiers, int Key)
{
    public static Shortcut None => default;
    public bool IsEmpty => Key == 0;

    public override string ToString()
    {
        if (IsEmpty) return string.Empty;
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(Modifiers.Ctrl)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(Modifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(Modifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(Modifiers.Win)) sb.Append("Win+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public static Shortcut Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = Modifiers.None;
        var key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Modifiers.Ctrl; break;
                case "alt": mods |= Modifiers.Alt; break;
                case "shift": mods |= Modifiers.Shift; break;
                case "win" or "windows" or "meta": mods |= Modifiers.Win; break;
                default: key = KeyFromName(raw); break;
            }
        }
        return key == 0 ? None : new Shortcut(mods, key);
    }

    /// <summary>Reads a shortcut stored at <paramref name="settingKey"/> (falls back to <paramref name="fallback"/>).</summary>
    public static Shortcut FromSetting(string settingKey, string fallback = "") =>
        Parse(Settings.GetString(settingKey, fallback));

    /// <summary>The modifiers currently held (physical state).</summary>
    public static Modifiers CurrentModifiers()
    {
        var m = Modifiers.None;
        if (InputHooks.IsKeyDown(0x11)) m |= Modifiers.Ctrl;
        if (InputHooks.IsKeyDown(0x12)) m |= Modifiers.Alt;
        if (InputHooks.IsKeyDown(0x10)) m |= Modifiers.Shift;
        if (InputHooks.IsKeyDown(0x5B) || InputHooks.IsKeyDown(0x5C)) m |= Modifiers.Win;
        return m;
    }

    public static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "CapsLock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Multiply",
        0x6B => "Add",
        0x6D => "Subtract",
        0x6E => "Decimal",
        0x6F => "Divide",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"VK{vk:X2}",
    };

    public static int KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        for (var vk = 1; vk < 256; vk++)
        {
            if (string.Equals(KeyName(vk), name, StringComparison.OrdinalIgnoreCase)) return vk;
        }
        if (name.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out var parsed)) return parsed;
        return 0;
    }
}

/// <summary>
/// Global hotkeys through RegisterHotKey on a hidden message-only window owned by the UI
/// thread. Callbacks run on the UI thread. For combinations Windows reserves (Alt+Tab,
/// Win+...) use <see cref="InputHooks"/> instead.
/// </summary>
public static class Hotkeys
{
    private static HwndSource? _source;
    private static int _nextId = 1;
    private static readonly Dictionary<int, Action> Callbacks = [];
    private static readonly Dictionary<int, (uint Mods, uint Key)> Registered = [];
    private static int _suspensions;

    /// <summary>Registers <paramref name="shortcut"/>. Returns null when empty or already taken.</summary>
    public static IDisposable? Register(Shortcut shortcut, Action callback)
    {
        if (shortcut.IsEmpty) return null;
        EnsureWindow();
        var id = _nextId++;
        var mods = Native.MOD_NOREPEAT;
        if (shortcut.Modifiers.HasFlag(Modifiers.Alt)) mods |= Native.MOD_ALT;
        if (shortcut.Modifiers.HasFlag(Modifiers.Ctrl)) mods |= Native.MOD_CONTROL;
        if (shortcut.Modifiers.HasFlag(Modifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (shortcut.Modifiers.HasFlag(Modifiers.Win)) mods |= Native.MOD_WIN;
        if (_suspensions == 0 && !Native.RegisterHotKey(_source!.Handle, id, mods, (uint)shortcut.Key))
        {
            Log.Warn($"Hotkey {shortcut} is already in use by another app");
            return null;
        }
        Callbacks[id] = callback;
        Registered[id] = (mods, (uint)shortcut.Key);
        return new Registration(id);
    }

    /// <summary>
    /// Releases every Fieldkit hotkey until the returned token is disposed, so a shortcut recorder
    /// receives combinations Fieldkit itself uses instead of triggering them (upstream #308).
    /// </summary>
    public static IDisposable SuspendAll()
    {
        if (_suspensions++ == 0 && _source is not null)
        {
            foreach (var id in Registered.Keys) Native.UnregisterHotKey(_source.Handle, id);
        }
        return new Composite(() =>
        {
            if (--_suspensions > 0 || _source is null) return;
            foreach (var (id, (mods, key)) in Registered)
            {
                if (!Native.RegisterHotKey(_source.Handle, id, mods, key))
                    Log.Warn($"Hotkey {id} could not be restored after recording");
            }
        });
    }

    /// <summary>
    /// Registers the shortcut stored in <paramref name="settingKey"/> and re-registers it
    /// whenever that setting changes. Dispose to unregister.
    /// </summary>
    public static IDisposable Bind(string settingKey, string defaultShortcut, Action callback)
    {
        IDisposable? current = null;
        void Apply()
        {
            current?.Dispose();
            var shortcut = Shortcut.FromSetting(settingKey, defaultShortcut);
            current = Register(shortcut, callback);
            SetUnavailable(settingKey, !shortcut.IsEmpty && current is null);
        }
        Apply();
        var observer = Settings.Observe(Apply, settingKey);
        return new Composite(() =>
        {
            observer.Dispose();
            current?.Dispose();
            SetUnavailable(settingKey, false);
        });
    }

    private static readonly HashSet<string> Unavailable = new(StringComparer.Ordinal);

    /// <summary>Raised (UI thread) when a bound shortcut becomes taken or free again.</summary>
    public static event Action? UnavailableChanged;

    /// <summary>Whether the shortcut bound to <paramref name="settingKey"/> could not be registered
    /// because another app already owns it.</summary>
    public static bool IsUnavailable(string settingKey) => Unavailable.Contains(settingKey);

    private static void SetUnavailable(string settingKey, bool unavailable)
    {
        var changed = unavailable ? Unavailable.Add(settingKey) : Unavailable.Remove(settingKey);
        if (changed) UnavailableChanged?.Invoke();
    }

    private static void EnsureWindow()
    {
        if (_source is not null) return;
        var parameters = new HwndSourceParameters("FieldkitHotkeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && Callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            try { callback(); }
            catch (Exception ex) { Log.Error("Hotkey callback failed", ex); }
        }
        return IntPtr.Zero;
    }

    private sealed class Registration(int id) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Callbacks.Remove(id);
            Registered.Remove(id);
            if (_source is not null && _suspensions == 0) Native.UnregisterHotKey(_source.Handle, id);
        }
    }

    private sealed class Composite(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
