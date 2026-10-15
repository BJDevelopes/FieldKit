// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// The app switcher: a keyboard hook claims the switcher shortcuts (Alt+Tab, Alt+` by
/// default), holds the session while the modifier is down and commits on release, like the
/// Windows switcher. A WinEvent hook keeps a most-recently-used window order.
/// </summary>
public sealed class SwitcherService : FeatureService
{
    public const string EnabledKey = "switcher.enabled";
    public const string ShortcutKey = "switcher.shortcut";
    public const string WindowShortcutKey = "switcher.windowShortcut";
    public const string TakeOverKey = "switcher.takeOverSystemShortcuts";
    public const string IconRowModeKey = "switcher.iconRowMode";
    public const string SimpleModeKey = "switcher.simpleMode";
    public const string MergeKey = "switcher.mergeTabs";
    public const string WindowlessKey = "switcher.windowlessApps";
    public const string MinimizedPlacementKey = "switcher.minimizedPlacement";
    public const string ScreenPlacementKey = "switcher.screenPlacement";
    public const string CurrentDisplayOnlyKey = "switcher.currentDisplayOnly";
    public const string CurrentDesktopOnlyKey = "switcher.currentSpaceOnly";
    public const string SearchPinKey = "switcher.searchPinEnabled";
    public const string ShowHintsKey = "switcher.showShortcutHints";
    public const string AppearanceDelayKey = "switcher.appearanceDelay";
    public const string MaxColumnsKey = "switcher.maxColumns";
    public const string PreviewSizeKey = "switcher.previewSize";
    public const string MinimalPreviewsKey = "switcher.minimalPreviews";
    public const string PreviewExcludedKey = "switcher.previewExcludedApps";
    public const string AppRulesKey = "switcher.appRules";

    public const string DefaultShortcut = "Alt+Tab";
    public const string DefaultWindowShortcut = "Alt+`";

    public static AppList PreviewExcludedApps { get; } = new(PreviewExcludedKey);

    private IDisposable? _keyHook;
    private Native.WinEventDelegate? _foregroundCallback;
    private IntPtr _foregroundHook;
    private readonly List<IntPtr> _mru = [];
    private readonly Dictionary<string, (string Path, uint Pid)> _usedApps = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];

    // Hook-thread state (read on the UI thread only through the posted messages).
    private volatile bool _active;
    private Shortcut _apps;
    private Shortcut _windows;
    private Modifiers _sessionModifiers;
    private bool _takeOver;
    private int _lastDownVk;
    private volatile bool _pinned;

    private Session? _session;
    private SwitcherWindow? _window;

    public override string FeatureId => "switcher";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    protected override void OnStart()
    {
        LoadShortcuts();
        _subscriptions.Add(Settings.Observe(LoadShortcuts, ShortcutKey, WindowShortcutKey, TakeOverKey));
        _keyHook = InputHooks.AddKeyboardHandler(OnKey, priority: 20);
        _foregroundCallback = OnForeground;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        Remember(Native.GetForegroundWindow());
    }

    protected override void OnStop()
    {
        _keyHook?.Dispose();
        _keyHook = null;
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundCallback = null;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _active = false;
        EndSession();
        _window?.Close();
        _window = null;
        _mru.Clear();
        _usedApps.Clear();
    }

    private void LoadShortcuts()
    {
        _apps = Shortcut.FromSetting(ShortcutKey, DefaultShortcut);
        _windows = Shortcut.FromSetting(WindowShortcutKey, DefaultWindowShortcut);
        _takeOver = Settings.GetBool(TakeOverKey, true);
    }

    /// <summary>Alt+Tab and Alt+Shift+Tab belong to Windows unless the user lets Fieldkit take them over.</summary>
    public static bool IsSystemCombo(Shortcut s) =>
        s.Key == 0x09 && (s.Modifiers & ~Modifiers.Shift) == Modifiers.Alt;

    // ------------------------------------------------------------------ MRU tracking

    private void OnForeground(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        Remember(hwnd);
        // A pinned search must not keep the keyboard once the user moves on with the mouse.
        if (_session is { Pinned: true } session && WindowEnumerator.RootOwner(hwnd) != session.Origin)
        {
            _active = false;
            EndSession();
        }
    }

    private void Remember(IntPtr hwnd)
    {
        hwnd = WindowEnumerator.RootOwner(hwnd);
        if (hwnd == IntPtr.Zero) return;
        _mru.Remove(hwnd);
        _mru.Insert(0, hwnd);
        if (_mru.Count > 200) _mru.RemoveRange(200, _mru.Count - 200);
        var pid = WindowEnumerator.ResolveProcessId(hwnd);
        var path = WindowHelper.GetProcessPath(pid);
        if (path.Length > 0 && !AppNames.IsSystemProcess(path)) _usedApps[AppNames.Key(path)] = (path, pid);
    }

    // ------------------------------------------------------------------ keyboard hook (hook thread)

    private bool OnKey(ref KeyEvent e)
    {
        if (e.IsOwn) return false;
        var vk = e.VirtualKey;
        var held = Shortcut.CurrentModifiers();

        if (!_active)
        {
            if (!e.IsDown) return false;
            var scope = SessionScope.None;
            if (Allowed(_apps) && SwitcherLogic.MatchesShortcut(_apps, vk, held)) scope = SessionScope.AllApps;
            else if (Allowed(_windows) && SwitcherLogic.MatchesShortcut(_windows, vk, held)) scope = SessionScope.FrontmostApp;
            if (scope == SessionScope.None) return false;
            var shortcut = scope == SessionScope.AllApps ? _apps : _windows;
            _active = true;
            _pinned = shortcut.Modifiers == Modifiers.None;
            _sessionModifiers = shortcut.Modifiers & ~Modifiers.Shift;
            _lastDownVk = vk;
            var backwards = held.HasFlag(Modifiers.Shift) && !shortcut.Modifiers.HasFlag(Modifiers.Shift);
            // A swallowed key between Alt/Win down and up keeps the menu bar or Start from opening.
            if ((_sessionModifiers & (Modifiers.Alt | Modifiers.Win)) != 0) InputSender.Tap(0xE8);
            Post(() => StartSession(scope, backwards));
            return true;
        }

        if (Shortcut.IsModifierKey(vk))
        {
            if (!e.IsDown && !_pinned && SessionModifierReleased(vk))
            {
                _active = false;
                Post(() => Commit(null));
            }
            return false;
        }

        if (!e.IsDown)
        {
            if (vk == _lastDownVk) _lastDownVk = 0;
            return true;
        }
        var repeat = vk == _lastDownVk;
        _lastDownVk = vk;
        var shift = held.HasFlag(Modifiers.Shift);
        var character = CharacterFor(vk, shift);
        if (vk == 0x1B) _active = false; // Esc ends the session right away
        if (vk == 0x0D) _active = false; // Enter commits
        var appsMatch = SwitcherLogic.MatchesShortcut(_apps, vk, held);
        var windowsMatch = SwitcherLogic.MatchesShortcut(_windows, vk, held, tolerate: _sessionModifiers) ||
                           (!_windows.IsEmpty && _windows.Key == vk);
        Post(() => OnSessionKey(vk, shift, repeat, character, appsMatch, windowsMatch));
        return true;
    }

    private bool Allowed(Shortcut s) => !s.IsEmpty && (_takeOver || !IsSystemCombo(s));

    private bool SessionModifierReleased(int vk)
    {
        var released = vk switch
        {
            0x10 or 0xA0 or 0xA1 => Modifiers.Shift,
            0x11 or 0xA2 or 0xA3 => Modifiers.Ctrl,
            0x12 or 0xA4 or 0xA5 => Modifiers.Alt,
            _ => Modifiers.Win,
        };
        if ((_sessionModifiers & released) == 0) return false;
        var other = vk switch
        {
            0xA0 => 0xA1, 0xA1 => 0xA0, 0xA2 => 0xA3, 0xA3 => 0xA2, 0xA4 => 0xA5, 0xA5 => 0xA4, 0x5B => 0x5C, 0x5C => 0x5B,
            _ => 0,
        };
        return other == 0 || !InputHooks.IsKeyDown(other);
    }

    private static char? CharacterFor(int vk, bool shift)
    {
        if (vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or 0x20 or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDE))
        {
            var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            var layout = WinApi.GetKeyboardLayout(thread);
            var mapped = WinApi.MapVirtualKeyEx((uint)vk, WinApi.MAPVK_VK_TO_CHAR, layout) & 0x7FFF;
            if (mapped == 0) return null;
            var ch = (char)mapped;
            return shift ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch);
        }
        return null;
    }

    private static void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Send, () => Ui.Guard(action));

    // ------------------------------------------------------------------ session (UI thread)

    private enum SessionScope { None, AllApps, FrontmostApp }

    private sealed class Session
    {
        public required SessionScope Scope { get; init; }
        public required List<SwitcherItem> All { get; set; }
        public List<SwitcherItem> Items { get; set; } = [];
        public int Selected { get; set; }
        public string Query { get; set; } = string.Empty;
        public bool Pinned { get; set; }
        public bool Shown { get; set; }
        public DispatcherTimer? AppearTimer { get; set; }
        public DispatcherTimer? Watchdog { get; set; }
        public Native.RECT WorkArea { get; set; }
        public IntPtr Origin { get; init; }
    }

    private void StartSession(SessionScope scope, bool backwards)
    {
        EndSession();
        var origin = WindowEnumerator.RootOwner(Native.GetForegroundWindow());
        var all = Enumerate(scope, origin);
        var work = ChooseDisplay(origin);
        if (Settings.GetBool(CurrentDisplayOnlyKey, false))
        {
            Native.GetCursorPos(out var cursor);
            var monitor = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
            all = SwitcherLogic.OnDisplay(all, new Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height));
        }
        if (all.Count == 0)
        {
            _active = false;
            return;
        }
        var session = new Session
        {
            Scope = scope,
            All = all,
            Items = all,
            Selected = SwitcherLogic.InitialSelection(all.Count, backwards),
            Pinned = _pinned,
            WorkArea = work,
            Origin = origin,
        };
        _session = session;
        // Safety net: if the modifier release was missed (hook timed out, secure desktop),
        // never keep the keyboard captured.
        session.Watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        session.Watchdog.Tick += (_, _) =>
        {
            if (_session != session || session.Pinned || _sessionModifiers == Modifiers.None) return;
            if ((Shortcut.CurrentModifiers() & _sessionModifiers) != Modifiers.None) return;
            _active = false;
            Commit(null);
        };
        session.Watchdog.Start();
        var delay =SwitcherLogic.SanitizedDelay(Settings.GetInt(AppearanceDelayKey, SwitcherLogic.DefaultAppearanceDelay));
        if (delay == 0 || session.Pinned) Show(session);
        else
        {
            session.AppearTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(delay) };
            session.AppearTimer.Tick += (_, _) =>
            {
                session.AppearTimer?.Stop();
                if (_session == session) Show(session);
            };
            session.AppearTimer.Start();
        }
    }

    private List<SwitcherItem> Enumerate(SessionScope scope, IntPtr origin)
    {
        var otherDesktops = !Settings.GetBool(CurrentDesktopOnlyKey, false);
        var rules = LoadRules();
        var windows = WindowEnumerator.List(otherDesktops)
            .Select(w =>
            {
                var r = WindowHelper.GetVisibleBounds(w.Handle);
                return SwitcherItem.From(w, new Rect(r.Left, r.Top, r.Width, r.Height));
            })
            .ToList();
        var ordered = SwitcherLogic.Order(windows, _mru, SwitcherLogic.ParsePlacement(Settings.GetString(MinimizedPlacementKey, "normal")));
        ordered = SwitcherLogic.ApplyRules(ordered, rules);

        if (scope == SessionScope.FrontmostApp)
        {
            var pid = WindowEnumerator.ResolveProcessId(origin);
            var key = AppNames.Key(WindowHelper.GetProcessPath(pid));
            return ordered.Where(i => i.AppKey == key || i.ProcessId == pid).ToList();
        }

        if (Settings.GetBool(MergeKey, false)) ordered = SwitcherLogic.MergeByApp(ordered);
        ordered.AddRange(WindowlessEntries(ordered, rules));
        return ordered;
    }

    private IEnumerable<SwitcherItem> WindowlessEntries(List<SwitcherItem> withWindows, IReadOnlyDictionary<string, SwitcherAppRule> rules)
    {
        var mode = SwitcherLogic.ParseWindowless(Settings.GetString(WindowlessKey, "explorer"));
        var present = withWindows.Select(i => i.AppKey).ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<string, (string Path, uint Pid)>(StringComparer.Ordinal);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        candidates["explorer.exe"] = (explorer, 0);
        foreach (var (key, value) in _usedApps)
            if (IsProcessRunning(value.Pid, value.Path)) candidates[key] = value;
        foreach (var (key, rule) in rules)
        {
            if (rule != SwitcherAppRule.ShowWithoutWindows || candidates.ContainsKey(key)) continue;
            var running = FindRunning(key);
            if (running is not null) candidates[key] = running.Value;
        }
        foreach (var (key, (path, pid)) in candidates)
        {
            if (present.Contains(key) || !SwitcherLogic.ShowsWindowlessApp(key, mode, rules)) continue;
            yield return SwitcherItem.AppEntry(path, key == "explorer.exe" ? "File Explorer" : AppNames.DisplayName(path), pid);
        }
    }

    /// <summary>
    /// Whether the remembered app is still running: the process id alone is not enough, since
    /// Windows reuses ids and a quit app would come back as a ghost entry (upstream #512 family).
    /// </summary>
    private static bool IsProcessRunning(uint pid, string path)
    {
        if (pid == 0) return false;
        using var p = WindowHelper.TryGetProcess(pid);
        return p is { HasExited: false } &&
               string.Equals(WindowHelper.GetProcessPath(pid), path, StringComparison.OrdinalIgnoreCase);
    }

    private static (string, uint)? FindRunning(string key)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(key)))
            {
                using (p)
                {
                    var path = WindowHelper.GetProcessPath((uint)p.Id);
                    if (path.Length > 0) return (path, (uint)p.Id);
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    public static Dictionary<string, SwitcherAppRule> LoadRules()
    {
        var raw = Settings.Get<Dictionary<string, string>>(AppRulesKey, []) ?? [];
        var rules = new Dictionary<string, SwitcherAppRule>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
            if (SwitcherLogic.ParseRule(value) is { } rule) rules[AppNames.Key(key)] = rule;
        return rules;
    }

    private static Native.RECT ChooseDisplay(IntPtr origin)
    {
        Native.GetCursorPos(out var cursor);
        var monitor = Settings.GetString(ScreenPlacementKey, "pointer") switch
        {
            "primary" => Native.MonitorFromPoint(new Native.POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */),
            "activeWindow" when origin != IntPtr.Zero => Native.MonitorFromWindow(origin, Native.MONITOR_DEFAULTTONEAREST),
            _ => Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST),
        };
        return WindowHelper.GetMonitorInfo(monitor).rcWork;
    }

    private SwitcherLayoutMode Mode(Session session)
    {
        if (Settings.GetBool(SimpleModeKey, false)) return SwitcherLayoutMode.Simple;
        if (session.Scope == SessionScope.AllApps && Settings.GetBool(IconRowModeKey, false)) return SwitcherLayoutMode.Icons;
        return SwitcherLayoutMode.Windows;
    }

    private void Show(Session session)
    {
        session.Shown = true;
        Render(session);
    }

    private void Render(Session session)
    {
        if (!session.Shown) return;
        _window ??= CreateWindow();
        var mode = Mode(session);
        var hints = mode != SwitcherLayoutMode.Windows && Settings.GetBool(ShowHintsKey, true);
        var fgPath = WindowHelper.GetProcessPath(WindowEnumerator.ResolveProcessId(session.Origin));
        _window.Render(new SwitcherViewState
        {
            Items = session.Items,
            Mode = mode,
            Selected = session.Selected,
            Query = session.Query,
            SearchPinned = session.Pinned && _sessionModifiers != Modifiers.None,
            Scale = SwitcherLogic.PreviewScale(Settings.GetString(PreviewSizeKey, "normal")),
            Minimal = Settings.GetBool(MinimalPreviewsKey, false),
            Thumbnails = mode != SwitcherLayoutMode.Simple && !PreviewExcludedApps.Contains(fgPath),
            MaxColumns = SwitcherLogic.SanitizedMaxColumns(Settings.GetInt(MaxColumnsKey, 0)),
            HintApps = hints && !_apps.IsEmpty ? _apps.ToString() : null,
            HintWindows = hints && !_windows.IsEmpty ? _windows.ToString() : null,
        }, session.WorkArea);
    }

    private SwitcherWindow CreateWindow()
    {
        var window = new SwitcherWindow();
        window.ItemClicked += index =>
        {
            if (_session is null) return;
            _active = false;
            Commit(index);
        };
        window.CloseClicked += index => RunOn(index, SwitcherLetterAction.CloseWindow);
        window.MinimizeClicked += index => RunOn(index, SwitcherLetterAction.MinimizeWindow);
        return window;
    }

    private void OnSessionKey(int vk, bool shift, bool repeat, char? character, bool appsMatch, bool windowsMatch)
    {
        var session = _session;
        if (session is null) return;
        var count = session.Items.Count;
        var mode = Mode(session);
        if (appsMatch)
        {
            if (session.Scope == SessionScope.AllApps && mode == SwitcherLayoutMode.Icons) MoveGroup(session, shift ? -1 : 1);
            else Move(session, SwitcherLogic.Advance(session.Selected, shift ? -1 : 1, count, wrap: !repeat));
            return;
        }
        if (windowsMatch && session.Query.Length == 0)
        {
            var delta = shift ? -1 : 1;
            if (session.Scope == SessionScope.FrontmostApp) Move(session, SwitcherLogic.Advance(session.Selected, delta, count, wrap: !repeat));
            else Move(session, SwitcherLogic.NextOfSameApp(session.Items, session.Selected, delta), rerender: mode == SwitcherLayoutMode.Icons);
            return;
        }
        switch (vk)
        {
            case 0x27: // Right
                if (mode == SwitcherLayoutMode.Icons) MoveGroup(session, 1);
                else Move(session, SwitcherLogic.Advance(session.Selected, 1, count));
                return;
            case 0x25: // Left
                if (mode == SwitcherLayoutMode.Icons) MoveGroup(session, -1);
                else Move(session, SwitcherLogic.Advance(session.Selected, -1, count));
                return;
            case 0x28: // Down
                if (mode == SwitcherLayoutMode.Icons) Move(session, SwitcherLogic.NextOfSameApp(session.Items, session.Selected, 1), rerender: true);
                else Move(session, SwitcherLogic.MoveRow(session.Selected, 1, _window?.Columns ?? 1, count));
                return;
            case 0x26: // Up
                if (mode == SwitcherLayoutMode.Icons) Move(session, SwitcherLogic.NextOfSameApp(session.Items, session.Selected, -1), rerender: true);
                else Move(session, SwitcherLogic.MoveRow(session.Selected, -1, _window?.Columns ?? 1, count));
                return;
            case 0x09: // Tab while pinned / with another shortcut
                Move(session, SwitcherLogic.Advance(session.Selected, shift ? -1 : 1, count));
                return;
            case 0x08: // Backspace
                if (session.Query.Length > 0) SetQuery(session, session.Query[..^1]);
                return;
            case 0x1B:
                EndSession();
                return;
            case 0x0D:
                Commit(null);
                return;
        }
        if (character is not { } ch) return;
        if (session.Query.Length == 0 && !session.Pinned &&
            SwitcherLogic.LetterAction(ch, Settings.GetBool(SearchPinKey, false)) is { } action)
        {
            if (repeat) return; // one window per press
            if (action == SwitcherLetterAction.PinSearch)
            {
                session.Pinned = _pinned = true;
                if (!session.Shown) Show(session);
                else Render(session);
                return;
            }
            RunOn(session.Selected, action);
            return;
        }
        SetQuery(session, session.Query + ch);
    }

    private void Move(Session session, int index, bool rerender = false)
    {
        if (index < 0) return;
        session.Selected = index;
        if (!session.Shown) return;
        if (rerender || Mode(session) != SwitcherLayoutMode.Windows) Render(session);
        else _window?.Select(index);
    }

    private void MoveGroup(Session session, int delta)
    {
        var groups = SwitcherLogic.GroupByApp(session.Items);
        if (groups.Count == 0) return;
        var current = Math.Max(0, groups.FindIndex(g => g.ItemIndices.Contains(session.Selected)));
        var next = groups[SwitcherLogic.Advance(current, delta, groups.Count)];
        Move(session, next.ItemIndices[0], rerender: true);
    }

    private void SetQuery(Session session, string query)
    {
        session.Query = query;
        session.Items = session.All.Where(i => SwitcherLogic.Matches(i, query)).ToList();
        session.Selected = session.Items.Count == 0 ? -1 : 0;
        if (!session.Shown) Show(session);
        else Render(session);
    }

    private void RunOn(int index, SwitcherLetterAction action)
    {
        var session = _session;
        if (session is null || index < 0 || index >= session.Items.Count) return;
        var item = session.Items[index];
        switch (action)
        {
            case SwitcherLetterAction.CloseWindow:
                if (item.IsAppEntry) return;
                WindowActions.Close(item.Handle);
                Remove(session, i => i.Handle == item.Handle);
                break;
            case SwitcherLetterAction.QuitApp:
                WindowActions.QuitApp(item.ProcessId, item.Handle);
                var key = item.AppKey;
                Remove(session, i => i.AppKey == key && (key != "explorer.exe" || i.Handle == item.Handle));
                break;
            case SwitcherLetterAction.MinimizeWindow:
                if (item.IsAppEntry) return;
                WindowActions.Minimize(item.Handle);
                Replace(session, item, item with { IsMinimized = true });
                break;
        }
    }

    private void Remove(Session session, Func<SwitcherItem, bool> predicate)
    {
        session.All = session.All.Where(i => !predicate(i)).ToList();
        session.Items = session.Items.Where(i => !predicate(i)).ToList();
        if (session.Items.Count == 0)
        {
            _active = false;
            EndSession();
            return;
        }
        session.Selected = Math.Clamp(session.Selected, 0, session.Items.Count - 1);
        if (!session.Shown) Show(session);
        else Render(session);
    }

    private void Replace(Session session, SwitcherItem old, SwitcherItem updated)
    {
        session.All = session.All.Select(i => i == old ? updated : i).ToList();
        session.Items = session.Items.Select(i => i == old ? updated : i).ToList();
        if (session.Shown) Render(session);
    }

    private void Commit(int? index)
    {
        var session = _session;
        if (session is null) return;
        var choice = index ?? session.Selected;
        var item = choice >= 0 && choice < session.Items.Count ? session.Items[choice] : null;
        EndSession();
        if (item is null) return;
        if (item.IsAppEntry) Launch(item);
        else WindowActions.Activate(item.Handle);
    }

    private static void Launch(SwitcherItem item)
    {
        try
        {
            // Opening the app again shows its window (single-instance apps), or a new
            // File Explorer window.
            Process.Start(new ProcessStartInfo(item.AppPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {item.AppName} failed", ex);
        }
    }

    private void EndSession()
    {
        var session = _session;
        _session = null;
        session?.AppearTimer?.Stop();
        session?.Watchdog?.Stop();
        _window?.Dismiss();
        _pinned = false;
    }

    // ------------------------------------------------------------------ preview (tests / docs)

    /// <summary>Renders the switcher panel with sample entries to a PNG (no thumbnails, no hooks).</summary>
    public static void RenderPreview(string path, SwitcherLayoutMode mode)
    {
        var sample = new List<SwitcherItem>
        {
            SwitcherItem.AppEntry(string.Empty, "Google Chrome", 1) with { Handle = new IntPtr(1), Title = "New Tab" },
            SwitcherItem.AppEntry(string.Empty, "Google Chrome", 1) with { Handle = new IntPtr(2), Title = "Stocks" },
            SwitcherItem.AppEntry(string.Empty, "Notepad", 2) with { Handle = new IntPtr(3), Title = "notes.txt - Notepad", IsMinimized = true },
            SwitcherItem.AppEntry(string.Empty, "File Explorer", 3),
        };
        var state = new SwitcherViewState
        {
            Items = sample,
            Mode = mode,
            Selected = 1,
            Thumbnails = false,
            HintApps = "Alt+Tab",
            HintWindows = "Alt+`",
        };
        var visual = SwitcherWindow.PreviewContent(state, 1400);
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new Rect(visual.DesiredSize));
        visual.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
}
