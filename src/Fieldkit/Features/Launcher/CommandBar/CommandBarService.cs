// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Runs while the Command Bar is installed: owns its global shortcut, the shortcuts people gave single
/// rows, the floating window and what it searches. Closed, it holds nothing but the hotkeys and the
/// last app scan; stopped (uninstalled), it releases everything.
/// </summary>
public sealed class CommandBarService : FeatureService, ICommandBarHost
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<IDisposable> _rowHotkeys = [];
    private IDisposable? _hotkey;
    private CommandBarController? _controller;
    private CommandBarWindow? _window;
    private List<CommandBarApp>? _apps;
    private Dictionary<string, CommandBarApp> _appsById = new(StringComparer.Ordinal);
    private DateTime _appsLoadedAt = DateTime.MinValue;
    private bool _appsLoading;
    private bool _suspended;
    private IntPtr _previous;

    public override string FeatureId => CommandBarKeys.FeatureId;

    /// <summary>Rows whose combination another app already holds (shown in Settings).</summary>
    public HashSet<string> RefusedRowShortcutKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Raised when the app scan or refused shortcuts change (Settings pages listen).</summary>
    public event Action? StateChanged;

    public IReadOnlyList<CommandBarApp> Apps => _apps ?? [];
    public bool AppsLoading => _appsLoading;

    public bool IsVisible => _window?.IsVisible == true;

    public IntPtr PreviousWindow => _previous;

    protected override void OnStart()
    {
        _controller = new CommandBarController(this);
        _controller.FileEntryFactory = CommandBarCatalog.FileEntry;
        _controller.ScriptAnswerFactory = CommandBarCatalog.ScriptAnswer;
        _controller.AnswerFactory = CommandBarCatalog.Answer;
        _controller.OpenUrlFactory = CommandBarCatalog.OpenUrl;
        _controller.ExtraActions = entry => CommandBarCatalog.ExtraActions(entry, _controller, this, _appsById);
        BindHotkeys();
        _subscriptions.Add(Settings.Observe(() => { if (!_suspended) BindRowHotkeys(); }, CommandBarKeys.RowShortcuts, CommandBarKeys.Links));
    }

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        UnbindRowHotkeys();
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _window?.CloseForReal();
        _window = null;
        _controller?.Release();
        _controller = null;
        _apps = null;
        _appsById = new(StringComparer.Ordinal);
        _appsLoadedAt = DateTime.MinValue;
        RefusedRowShortcutKeys.Clear();
        CommandBarIcons.Clear();
        CommandBarCatalog.ClearCaches();
        CommandBarSystem.ClearCaches();
    }

    // ------------------------------------------------------------------ hotkeys

    private void BindHotkeys()
    {
        _hotkey?.Dispose();
        // Empty by default (upstream ships the shortcut switched off); Settings → Command Bar turns it on.
        _hotkey = Hotkeys.Bind(CommandBarKeys.Shortcut, string.Empty, Toggle);
        BindRowHotkeys();
    }

    private void UnbindRowHotkeys()
    {
        foreach (var h in _rowHotkeys) h.Dispose();
        _rowHotkeys.Clear();
    }

    private void BindRowHotkeys()
    {
        UnbindRowHotkeys();
        RefusedRowShortcutKeys.Clear();
        var shortcuts = CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty));
        foreach (var (key, shortcut) in shortcuts)
        {
            var stableKey = key;
            var registration = Hotkeys.Register(shortcut, () => RunRowShortcut(stableKey));
            if (registration is null) RefusedRowShortcutKeys.Add(key);
            else _rowHotkeys.Add(registration);
        }
        StateChanged?.Invoke();
    }

    public void SuspendHotkeys(bool suspend)
    {
        if (_suspended == suspend) return;
        _suspended = suspend;
        if (suspend)
        {
            _hotkey?.Dispose();
            _hotkey = null;
            UnbindRowHotkeys();
        }
        else
        {
            BindHotkeys();
        }
    }

    public void RowShortcutsChanged()
    {
        if (!_suspended) BindRowHotkeys();
    }

    /// <summary>One row's own shortcut: an app comes forward (or goes away when in front), a script
    /// marked so runs with nothing on screen, anything else runs or opens the bar to ask.</summary>
    private void RunRowShortcut(string stableKey)
    {
        var links = CommandBarLinks.Decode(Settings.GetString(CommandBarKeys.Links, string.Empty));
        if (CommandBarLinks.DirectRunScript(stableKey, links) is { } script)
        {
            CommandBarScriptRunner.RunDirectly(script);
            return;
        }
        if (stableKey.StartsWith("app.", StringComparison.Ordinal))
        {
            if (_appsById.TryGetValue(stableKey, out var app))
            {
                CommandBarApps.ToggleOrLaunch(app);
                return;
            }
            LoadApps(() =>
            {
                if (_appsById.TryGetValue(stableKey, out var loaded)) CommandBarApps.ToggleOrLaunch(loaded);
            });
            return;
        }
        Show(stableKey);
    }

    // ------------------------------------------------------------------ showing

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    /// <summary>Opens the bar; with <paramref name="promptFor"/>, runs that row (asking first when it needs to).</summary>
    public void Show(string? promptFor = null)
    {
        if (_controller is null) return;
        var foreground = Native.GetForegroundWindow();
        _window ??= new CommandBarWindow(_controller, Hide);
        var ownWindow = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (foreground != ownWindow) _previous = foreground;

        var prefs = CommandBarPrefs.Load();
        var windows = WindowHelper.GetAltTabWindows();
        var running = CommandBarApps.RunningPaths(windows);
        _controller.Reset();
        _controller.SetData(prefs, catalog: [], apps: [], windows: [], quit: [], windowsSettings: [], emoji: []);
        var catalog = CommandBarCatalog.Build(_controller, this);
        _controller.SetData(prefs,
            catalog: catalog,
            apps: _apps is null ? [] : CommandBarCatalog.AppEntries(_apps, running),
            windows: CommandBarPreferences.IsEnabled(CommandBarSource.Windows, prefs.Disabled) ? CommandBarCatalog.WindowEntries(windows) : [],
            quit: CommandBarCatalog.QuitEntries(windows),
            windowsSettings: CommandBarCatalog.WindowsSettingsEntries(),
            emoji: CommandBarPreferences.IsEnabled(CommandBarSource.Emoji, prefs.Disabled) ? CommandBarCatalog.EmojiEntries(prefs.SkinTone, this) : []);

        if (promptFor is not null)
        {
            var entry = _controller.Catalog.Concat(_controller.WindowsSettingsEntries).Concat(_controller.EmojiEntries)
                .FirstOrDefault(e => e.StableKey == promptFor);
            if (entry is not null && !entry.NeedsPrompt && !entry.KeepsBarOpen)
            {
                _controller.Run(entry);
                return;
            }
            _window.ShowBar(_previous);
            if (entry is not null) _controller.Run(entry);
        }
        else
        {
            _window.ShowBar(_previous);
        }

        // Background refreshes land in the open bar when they finish.
        if (DateTime.UtcNow - _appsLoadedAt > TimeSpan.FromSeconds(30))
            LoadApps(() =>
            {
                if (IsVisible && _controller is not null && _apps is not null)
                    _controller.SetData(_controller.Prefs, apps: CommandBarCatalog.AppEntries(_apps, CommandBarApps.RunningPaths(WindowHelper.GetAltTabWindows())));
            });
        var wifiBefore = CommandBarSystem.CachedWifiOn;
        _ = RefreshWifiThenAsync(wifiBefore);
    }

    private async Task RefreshWifiThenAsync(bool? before)
    {
        await CommandBarSystem.RefreshWifiAsync();
        if (CommandBarSystem.CachedWifiOn == before || !IsVisible || _controller is null) return;
        _controller.SetData(_controller.Prefs, catalog: CommandBarCatalog.Build(_controller, this));
    }

    public void Hide()
    {
        _window?.HideBar();
        if (_suspended) SuspendHotkeys(false);
        _controller?.Reset();
    }

    public void OpenSettings(string pageId) => PanelHost.OpenSettings(pageId);

    // ------------------------------------------------------------------ apps

    /// <summary>Reads the installed apps on a background STA thread (COM shell), then calls back on the UI thread.</summary>
    public void LoadApps(Action? then = null)
    {
        if (_appsLoading)
        {
            if (then is not null) _pendingAppCallbacks.Add(then);
            return;
        }
        _appsLoading = true;
        if (then is not null) _pendingAppCallbacks.Add(then);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            List<CommandBarApp> apps;
            try { apps = CommandBarApps.Load(); }
            catch (Exception ex)
            {
                Log.Error("Command Bar: reading apps failed", ex);
                apps = [];
            }
            dispatcher.BeginInvoke(() =>
            {
                _appsLoading = false;
                if (_controller is null) return; // stopped meanwhile
                _apps = apps;
                _appsById = apps.GroupBy(a => CommandBarRowShortcuts.AppKey(a.Id)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                _appsLoadedAt = DateTime.UtcNow;
                var callbacks = _pendingAppCallbacks.ToList();
                _pendingAppCallbacks.Clear();
                foreach (var callback in callbacks) Fieldkit.UI.Ui.Guard(callback);
                StateChanged?.Invoke();
            });
        }) { IsBackground = true, Name = "Command Bar apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private readonly List<Action> _pendingAppCallbacks = [];

    // ------------------------------------------------------------------ for the Settings pages

    /// <summary>A readable title for a stored key (an app that is gone shows its key, so it can still be removed).</summary>
    public string TitleFor(string stableKey)
    {
        if (_appsById.TryGetValue(stableKey, out var app)) return app.Name;
        if (_controller is not null)
        {
            if (_controller.Catalog.Count == 0)
            {
                var prefs = CommandBarPrefs.Load();
                _controller.SetData(prefs, catalog: CommandBarCatalog.Build(_controller, this), windowsSettings: CommandBarCatalog.WindowsSettingsEntries());
            }
            var title = _controller.TitleFor(stableKey);
            if (title != stableKey) return title;
        }
        if (stableKey.StartsWith("emoji.", StringComparison.Ordinal)) return stableKey[6..];
        return stableKey;
    }

    /// <summary>Forgets usage, query habits and this session's memory.</summary>
    public void ForgetLearning()
    {
        Settings.Remove(CommandBarKeys.Usage);
        Settings.Remove(CommandBarKeys.QueryHabits);
        _controller?.ForgetLearning();
    }

    /// <summary>Recentres the bar (Settings button).</summary>
    public static void ResetPosition() => Settings.Set(CommandBarKeys.Position, string.Empty);
}
