// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Interop;

namespace Fieldkit.Features.Snippets;

/// <summary>
/// Expands typed triggers (upstream TextSnippetService): the shared keyboard hook keeps a small buffer
/// of what was typed (reset by clicks, focus changes and navigation keys); when it completes a
/// trigger the last key is swallowed, the trigger is erased with Backspaces and the text is typed —
/// or pasted through a transient clipboard write when it has several lines. Never in password fields
/// Windows can recognize, never in apps the user excluded, never while the snippet menu is open.
/// </summary>
public sealed class TextSnippetService : FeatureService
{
    public const string Id = "textSnippets";
    public const string EnabledKey = "textSnippets.enabled";
    public const string SoundEnabledKey = "textSnippets.soundEnabled";
    public const string SoundNameKey = "textSnippets.soundName";
    public const string ExcludedAppsKey = "textSnippets.excludedApps";

    private const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_RMENU = 0xA5;

    private readonly object _gate = new();
    private string _buffer = string.Empty;
    private TextSnippet[] _immediate = [];
    private TextSnippet[] _delimited = [];
    private volatile bool _foregroundExcluded;
    private IDisposable? _keyboard;
    private IDisposable? _mouse;
    private IntPtr _foregroundHook;
    private Native.WinEventDelegate? _foregroundProc;
    private IDisposable? _excludedObserver;

    public override string FeatureId => Id;

    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    public override bool EnabledByDefault => false;

    /// <summary>Only runs with something to expand.</summary>
    public override bool ShouldRun() => base.ShouldRun() && SnippetStore.All.Any(s => s.Enabled);

    public static List<string> ExcludedApps
    {
        get => Settings.Get<List<string>>(ExcludedAppsKey, []) ?? [];
        set => Settings.Set(ExcludedAppsKey, value.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    protected override void OnStart()
    {
        Reload();
        SnippetStore.Changed += OnSnippetsChanged;
        _keyboard = InputHooks.AddKeyboardHandler(OnKey, priority: 80);
        _mouse = InputHooks.AddMouseHandler(OnMouse, priority: 200);
        _foregroundProc = OnForeground;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _excludedObserver = Settings.Observe(UpdateExclusion, ExcludedAppsKey);
        UpdateExclusion();
    }

    protected override void OnStop()
    {
        SnippetStore.Changed -= OnSnippetsChanged;
        _keyboard?.Dispose();
        _keyboard = null;
        _mouse?.Dispose();
        _mouse = null;
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundProc = null;
        _excludedObserver?.Dispose();
        _excludedObserver = null;
        ResetBuffer();
    }

    private void OnSnippetsChanged() => UI.Ui.OnUi(Reload);

    private void Reload()
    {
        var all = SnippetStore.All;
        lock (_gate)
        {
            _immediate = all.Where(s => s.Enabled && s.Expansion == SnippetExpansion.Immediate).ToArray();
            _delimited = all.Where(s => s.Enabled && s.Expansion == SnippetExpansion.AfterDelimiter).ToArray();
            _buffer = string.Empty;
        }
    }

    private void ResetBuffer()
    {
        lock (_gate) _buffer = string.Empty;
    }

    private void OnForeground(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Switching apps invalidates whatever was half-typed there.
        ResetBuffer();
        UpdateExclusion();
    }

    private void UpdateExclusion()
    {
        var pid = WindowHelper.GetProcessId(Native.GetForegroundWindow());
        // Fieldkit's own windows (the snippet editor, Settings) never expand: editing a trigger must stay possible.
        if (pid == (uint)Environment.ProcessId)
        {
            _foregroundExcluded = true;
            return;
        }
        var excluded = ExcludedApps;
        if (excluded.Count == 0)
        {
            _foregroundExcluded = false;
            return;
        }
        var exe = Path.GetFileName(WindowHelper.GetProcessPath(pid));
        _foregroundExcluded = excluded.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase));
    }

    // Hook thread. Clicks move the caret somewhere unknown: the deletes would no longer land on the trigger.
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.Action is MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown) ResetBuffer();
        return false;
    }

    // Hook thread: must stay fast.
    private bool OnKey(ref KeyEvent e)
    {
        if (!e.IsDown || e.IsOwn) return false;
        var vk = e.VirtualKey;
        if (Shortcut.IsModifierKey(vk) || vk == VK_CAPITAL) return false;
        if (_foregroundExcluded || SnippetLibraryWindow.IsOpen || IsPasswordField())
        {
            ResetBuffer();
            return false;
        }
        var ctrl = InputHooks.IsKeyDown(VK_CONTROL);
        var altGr = InputHooks.IsKeyDown(VK_RMENU);
        var win = InputHooks.IsKeyDown(VK_LWIN) || InputHooks.IsKeyDown(VK_RWIN);
        // Shortcuts are commands, not text (AltGr, which Windows reports as Ctrl+Alt, still types).
        if (win || (ctrl && !altGr) || (InputHooks.IsKeyDown(VK_MENU) && !altGr))
        {
            ResetBuffer();
            return false;
        }
        switch (vk)
        {
            case VK_BACK:
                lock (_gate) _buffer = SnippetEngine.Backspace(_buffer);
                return false;
            case VK_ESCAPE or VK_DELETE or (>= 0x21 and <= 0x28): // Esc, Delete, PgUp/PgDn/End/Home, arrows
                ResetBuffer();
                return false;
        }
        var typed = vk switch
        {
            VK_RETURN => "\r",
            VK_TAB => "\t",
            _ => Translate(vk, e.ScanCode),
        };
        if (typed.Length == 0) return false;

        if (SnippetEngine.IsDelimiter(typed))
        {
            TextSnippet? matched;
            lock (_gate)
            {
                matched = SnippetEngine.Match(_buffer, SnippetExpansion.AfterDelimiter, _delimited);
                _buffer = string.Empty;
            }
            if (matched is null) return false;
            // The delimiter is swallowed and typed again after the text.
            Schedule(matched, SnippetEngine.VisibleLength(matched.Trigger), typed, vk);
            return true;
        }

        TextSnippet? immediate;
        lock (_gate)
        {
            _buffer = SnippetEngine.Append(_buffer, typed);
            immediate = SnippetEngine.Match(_buffer, SnippetExpansion.Immediate, _immediate);
            if (immediate is not null) _buffer = string.Empty;
        }
        if (immediate is null) return false;
        // The last trigger key never reaches the app, so one Backspace fewer.
        Schedule(immediate, Math.Max(0, SnippetEngine.VisibleLength(immediate.Trigger) - SnippetEngine.VisibleLength(typed)), string.Empty, 0, swallowed: typed);
        return true;
    }

    /// <summary>The character a key types in the foreground app's keyboard layout (without disturbing dead keys).</summary>
    private static string Translate(int vk, int scan)
    {
        var state = new byte[256];
        if (InputHooks.IsKeyDown(VK_SHIFT)) state[VK_SHIFT] = 0x80;
        if (InputHooks.IsKeyDown(VK_RMENU))
        {
            state[VK_CONTROL] = 0x80;
            state[VK_MENU] = 0x80;
        }
        if ((Native.GetKeyState(VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 0x01;
        var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var layout = GetKeyboardLayout(thread);
        var buffer = new StringBuilder(8);
        // Flag 4: do not change the keyboard state (keeps the target app's dead keys intact).
        var n = ToUnicodeEx((uint)vk, (uint)scan, state, buffer, buffer.Capacity, 4, layout);
        if (n <= 0) return string.Empty;
        var text = buffer.ToString(0, Math.Min(n, buffer.Length));
        return text.Any(char.IsControl) ? string.Empty : text;
    }

    /// <summary>A classic Win32 password box has focus (ES_PASSWORD). Other toolkits are checked before expanding.</summary>
    private static bool IsPasswordField()
    {
        var fg = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(fg, out _);
        var info = new ClipboardNative.GUITHREADINFO { cbSize = Marshal.SizeOf<ClipboardNative.GUITHREADINFO>() };
        if (!ClipboardNative.GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero) return false;
        const long ES_PASSWORD = 0x20;
        var cls = WindowHelper.GetClassName(info.hwndFocus);
        if (!cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return false;
        return (Native.GetWindowLongPtr(info.hwndFocus, Native.GWL_STYLE).ToInt64() & ES_PASSWORD) != 0;
    }

    private void Schedule(TextSnippet snippet, int deleteCount, string trailing, int trailingVk, string swallowed = "")
    {
        var sound = Settings.GetBool(SoundEnabledKey) ? SnippetSounds.Resolve(Settings.GetString(SoundNameKey)) : null;
        Task.Run(async () =>
        {
            try
            {
                // A browser or modern app password box: give back the swallowed key and do nothing else.
                if (await SnippetFocus.IsPasswordAsync().ConfigureAwait(false))
                {
                    InputSender.TypeText(swallowed + trailing);
                    return;
                }
                await Insert(snippet.Replacement, deleteCount, trailing, trailingVk, swallowed).ConfigureAwait(false);
                if (sound is not null) SnippetSounds.Play(sound);
            }
            catch (Exception ex)
            {
                Log.Error("Expanding a snippet failed", ex);
            }
        });
    }

    /// <summary>Erases <paramref name="deleteCount"/> characters and inserts the expanded replacement plus the trailing delimiter.</summary>
    internal static async Task Insert(string replacement, int deleteCount, string trailing, int trailingVk, string swallowedOnFailure = "")
    {
        string? clipboard = null;
        if (SnippetVariables.NeedsClipboard(replacement))
        {
            var read = ClipboardLane.Run(access => access.IsPrivate() ? null : access.ReadText());
            if (await Task.WhenAny(read, Task.Delay(800)).ConfigureAwait(false) == read && read.Status == TaskStatus.RanToCompletion)
                clipboard = read.Result;
        }
        var expansion = SnippetVariables.Expand(replacement, DateTime.UtcNow, clipboard);
        var trailingText = trailing == "\r" ? "\n" : trailing;
        var caretBack = expansion.CaretBack + (expansion.CaretBack > 0 ? SnippetVariables.CaretSteps(trailingText) : 0);
        if (SnippetVariables.RequiresPaste(expansion.Text))
        {
            var ok = TransientPaste.Paste(expansion.Text + trailingText,
                beforePaste: () => InputSender.Backspace(deleteCount),
                afterPaste: () => MoveCaretBack(caretBack),
                onFail: () => InputSender.TypeText(swallowedOnFailure + trailing));
            if (!ok) InputSender.TypeText(swallowedOnFailure + trailing);
            return;
        }
        InputSender.Backspace(deleteCount);
        InputSender.TypeText(expansion.Text);
        if (trailing == "\r" && trailingVk != 0) InputSender.Tap(VK_RETURN);
        else if (trailing.Length > 0) InputSender.TypeText(trailing);
        MoveCaretBack(caretBack);
    }

    private static void MoveCaretBack(int steps)
    {
        for (var i = 0; i < steps; i++) InputSender.Tap(VK_LEFT);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
}

/// <summary>Asks UI Automation whether the focused control is a password box (bounded wait).</summary>
public static class SnippetFocus
{
    public static async Task<bool> IsPasswordAsync()
    {
        var check = Task.Run(() =>
        {
            try { return System.Windows.Automation.AutomationElement.FocusedElement?.Current.IsPassword == true; }
            catch { return false; }
        });
        return await Task.WhenAny(check, Task.Delay(250)).ConfigureAwait(false) == check && check.Result;
    }
}

/// <summary>The expansion sound: the .wav files in %WINDIR%\Media (the counterpart of upstream's alert sounds).</summary>
public static class SnippetSounds
{
    public const string DefaultName = "Windows Navigation Start";

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    public static List<string> Names()
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(Directory, "*.wav")
                .Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The sound file to use for a stored name (falls back to the default, then the first one).</summary>
    public static string? Resolve(string? stored, IReadOnlyList<string>? available = null)
    {
        available ??= Names();
        var name = ResolvedName(stored, available);
        return name is null ? null : Path.Combine(Directory, name + ".wav");
    }

    public static string? ResolvedName(string? stored, IReadOnlyList<string> available)
    {
        if (!string.IsNullOrEmpty(stored) && available.Contains(stored, StringComparer.OrdinalIgnoreCase)) return stored;
        if (available.Contains(DefaultName, StringComparer.OrdinalIgnoreCase)) return DefaultName;
        return available.FirstOrDefault();
    }

    public static void Play(string path)
    {
        const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;
        if (!PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME))
            Log.Warn($"Playing the snippet sound failed: {path}");
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
}
