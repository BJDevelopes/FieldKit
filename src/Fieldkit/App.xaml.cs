// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Shell;
using Fieldkit.Interop;
using Fieldkit.UI;
using Fieldkit.UI.Panel;
using Fieldkit.UI.Prefs;

namespace Fieldkit;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Fieldkit.SingleInstance";
    private const string ShowEventName = @"Local\Fieldkit.ShowPanel";
    private const string FirstRunKey = "app.firstRunDone";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private TaskbarButton? _taskbarButton;
    private PanelWindow? _panel;
    private SettingsWindow? _settings;

    public static App Instance => (App)Current;

    public static void Quit() => Instance.Dispatcher.BeginInvoke(() => Instance.ShutdownApp());

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            // The app only ends on an explicit Shutdown: a failed start must not linger as a windowless process.
            Log.Error("Startup failed", ex);
            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        var args = e.Args;

        // Tooling modes that never show UI.
        if (args.Contains("--write-parity"))
        {
            var index = Array.IndexOf(args, "--write-parity");
            var path = index + 1 < args.Length ? args[index + 1] : "PARITY.md";
            FeatureRuntime.Initialize();
            File.WriteAllText(path, ParityReport.Markdown());
            Shutdown(0);
            return;
        }
        // The installer's uninstaller: take back what Fieldkit registered for this user (Start with Windows and
        // the browser picker). Settings and histories stay in %APPDATA%\Fieldkit.
        if (args.Contains("--uninstall-cleanup"))
        {
            LaunchAtLogin.Set(false);
            try { Features.Launcher.BrowserPicker.Browsers.Unregister(); }
            catch (Exception ex) { Log.Warn($"Removing the browser registration failed: {ex.Message}"); }
            Shutdown(0);
            return;
        }
        // Only known switches start the app normally; anything else (a mistyped tooling command,
        // stray positional arguments) exits instead of launching hooks on the developer's desktop.
        string[] launchSwitches = ["--background", "--elevated-restart"];
        string[] toolingSwitches = ["--render-ui", "--render-glyphs", "--render-glyph-range", "--write-icon", "--write-parity", "--selftest", "--export-site"];
        if (args.Contains("--list-actions"))
        {
            CommandChannel.AttachParentConsole();
            FeatureRuntime.Initialize();
            foreach (var line in CommandChannel.DescribeActions()) Console.WriteLine(line);
            Shutdown(0);
            return;
        }
        string? pendingAction = null;
        var actionIndex = Array.IndexOf(args, "--action");
        if (actionIndex >= 0)
        {
            if (actionIndex + 1 >= args.Length)
            {
                CommandChannel.AttachParentConsole();
                Console.WriteLine("usage: Fieldkit.exe --action <id>   (Fieldkit.exe --list-actions shows the ids)");
                Shutdown(2);
                return;
            }
            pendingAction = args[actionIndex + 1];
            // A running instance does the work; otherwise this process starts Fieldkit and runs it after startup.
            if (CommandChannel.Send("action " + pendingAction) is { } reply)
            {
                CommandChannel.AttachParentConsole();
                Console.WriteLine(reply);
                Shutdown(reply == "ok" ? 0 : 1);
                return;
            }
            args = args.Where((_, i) => i != actionIndex && i != actionIndex + 1).Append("--background").ToArray();
        }
        // A link from Windows (Fieldkit chosen as the default browser): the running instance shows the picker.
        string? pendingUrl = null;
        var urlIndex = Array.IndexOf(args, "--open-url");
        if (urlIndex >= 0)
        {
            if (urlIndex + 1 >= args.Length)
            {
                Shutdown(2);
                return;
            }
            pendingUrl = args[urlIndex + 1];
            if (CommandChannel.Send("open-url " + pendingUrl) is not null)
            {
                Shutdown(0);
                return;
            }
            args = args.Where((_, i) => i != urlIndex && i != urlIndex + 1).Append("--background").ToArray();
        }
        if (args.Length > 0 && !args.Any(toolingSwitches.Contains) && !args.All(launchSwitches.Contains))
        {
            Console.Error.WriteLine($"Unknown arguments: {string.Join(' ', args)}");
            Shutdown(2);
            return;
        }

        // Developer tooling never falls through to a normal launch, even with malformed arguments.
        if (args.Any(a => a.StartsWith("--render-", StringComparison.Ordinal) || a is "--write-icon" or "--write-parity" or "--selftest")
            && !args.Contains("--render-ui") && !args.Contains("--render-glyphs") && !args.Contains("--write-icon") && !args.Contains("--export-site")
            && !args.Contains("--write-parity") && !args.Contains("--selftest") && !args.Contains("--render-glyph-range"))
        {
            Shutdown(2);
            return;
        }
        if (args.Contains("--render-glyph-range"))
        {
            var index = Array.IndexOf(args, "--render-glyph-range");
            try
            {
                UiSnapshots.RenderGlyphRange(Convert.ToInt32(args[index + 1], 16), Convert.ToInt32(args[index + 2], 16), args[index + 3]);
                Shutdown(0);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException or ArgumentException)
            {
                Console.Error.WriteLine("usage: --render-glyph-range <fromHex> <toHex> <out.png>");
                Shutdown(2);
            }
            return;
        }
        if (args.Contains("--write-icon"))
        {
            var index = Array.IndexOf(args, "--write-icon");
            UiSnapshots.WriteAppIcon(index + 1 < args.Length ? args[index + 1] : "Fieldkit.ico");
            Shutdown(0);
            return;
        }
        if (args.Contains("--render-glyphs"))
        {
            var index = Array.IndexOf(args, "--render-glyphs");
            UiSnapshots.RenderGlyphSheet(index + 1 < args.Length ? args[index + 1] : "glyphs.png");
            Shutdown(0);
            return;
        }
        if (args.Contains("--export-site"))
        {
            // Developer tooling: the real UI as data for the project website (see SiteExport). Dark theme, everything installed.
            var index = Array.IndexOf(args, "--export-site");
            var dir = index + 1 < args.Length ? args[index + 1] : "site/data";
            Settings.Set(ThemeManager.AppearanceKey, "dark");
            foreach (var f in FeatureCatalog.All) f.SetAvailable(true);
            ThemeManager.Initialize();
            FeatureRuntime.Initialize();
            SiteExport.Export(dir);
            Settings.Flush();
            Shutdown(0);
            return;
        }
        if (args.Contains("--render-ui"))
        {
            var index = Array.IndexOf(args, "--render-ui");
            var dir = index + 1 < args.Length ? args[index + 1] : "ui-snapshots";
            var themeIndex = Array.IndexOf(args, "--theme");
            var onlyIndex = Array.IndexOf(args, "--only");
            Settings.Set(ThemeManager.AppearanceKey, themeIndex >= 0 && themeIndex + 1 < args.Length ? args[themeIndex + 1] : "dark");
            if (args.Contains("--all-features"))
                foreach (var f in FeatureCatalog.All) f.SetAvailable(true);
            ThemeManager.Initialize();
            FeatureRuntime.Initialize();
            UiSnapshots.Render(dir, onlyIndex >= 0 && onlyIndex + 1 < args.Length ? args[onlyIndex + 1] : null);
            Settings.Flush();
            Shutdown(0);
            return;
        }
        if (args.Contains("--selftest"))
        {
            Shutdown(SelfTest() ? 0 : 1);
            return;
        }

        if (!AcquireSingleInstance(args.Contains("--elevated-restart")))
        {
            // Another instance is running: ask it to show its panel and leave. This launch has the
            // user's foreground rights; pass them on so the panel can take focus.
            Native.AllowSetForegroundWindow(-1); // ASFW_ANY
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown(0);
            return;
        }

        Log.Info($"{AppInfo.Name} {AppInfo.Version} starting");
        ThemeManager.Initialize();
        FirstRun();
        FeatureRuntime.Initialize();

        _panel = new PanelWindow();
        _panel.SettingsRequested += () => OpenSettings(null);
        _panel.QuitRequested += ShutdownApp;
        PanelHost.HideAction = () => _panel.HidePanel();
        PanelHost.ShowAction = id => _panel.ShowPanel(id);
        PanelHost.OpenSettingsAction = id => OpenSettings(id);

        _tray = new TrayIcon();
        _tray.LeftClick += () => _panel.Toggle();
        _tray.OpenSettings += () => OpenSettings(null);
        _tray.Quit += ShutdownApp;
        _tray.Show();

        _taskbarButton = new TaskbarButton();
        _taskbarButton.Clicked += () => _panel.Toggle();
        _taskbarButton.OpenSettings += () => OpenSettings(null);
        _taskbarButton.Quit += ShutdownApp;
        _taskbarButton.Start();

        FeatureRuntime.Start();
        ListenForShowRequests();
        CommandChannel.Listen();
        if (pendingUrl is not null)
        {
            var url = pendingUrl;
            Dispatcher.BeginInvoke(() => Log.Info($"Startup link: {CommandChannel.OpenUrl?.Invoke(url) ?? "no browser picker"}"),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (pendingAction is not null)
        {
            var id = pendingAction;
            Dispatcher.BeginInvoke(() => Log.Info($"Startup action {id}: {CommandChannel.RunAction(id)}"),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Windows may silently drop low-level hooks around these events; put them back.
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                or Microsoft.Win32.SessionSwitchReason.RemoteConnect or Microsoft.Win32.SessionSwitchReason.SessionLogon)
                InputHooks.Reinstall();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) InputHooks.Reinstall();
        };

        if (!args.Contains("--background") && !Settings.GetBool("app.welcomed", false))
        {
            Settings.Set("app.welcomed", true);
            _tray.ShowBalloon($"{AppInfo.Name} is running", "Click the icon in the notification area to open the panel.", false);
            OpenSettings("features");
        }
    }

    /// <summary>A clean install starts from the small Essentials set, like upstream.</summary>
    private static void FirstRun()
    {
        if (Settings.GetBool(FirstRunKey, false)) return;
        var essentials = FeatureBundle.All.First(b => b.Id == "essential").Features;
        foreach (var f in FeatureCatalog.All)
        {
            if (!Settings.Has(f.AvailabilityKey)) f.SetAvailable(essentials.Contains(f.Id));
        }
        Settings.Set(FirstRunKey, true);
    }

    public void OpenSettings(string? pageId)
    {
        _panel?.HidePanel();
        if (_settings is null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (pageId is not null) _settings.Navigate(pageId);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _instanceMutex = new Mutex(false, InstanceMutexName);
        try
        {
            if (_instanceMutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero)) return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _panel?.ShowPanel());
        }) { IsBackground = true, Name = "Fieldkit show requests" };
        thread.Start();
    }

    private void ShutdownApp()
    {
        Log.Info("Shutting down");
        try
        {
            FeatureRuntime.Shutdown();
            InputHooks.Shutdown();
            Settings.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed", ex);
        }
        _tray?.Dispose();
        _taskbarButton?.Stop();
        _panel?.Close();
        _settings?.Close();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        Shutdown(0);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// The installer works through its queue: a few installed features are uninstalled and installed again, one per
    /// turn of the UI thread, and it must end idle with each in the state asked for.
    /// </summary>
    private static bool SelfTestInstaller()
    {
        FeatureInstaller.ShowNotice = false;
        var sample = FeatureCatalog.All.Where(f => f.IsAvailable && !f.Beta).Take(4).ToList();
        if (sample.Count == 0) return true;

        bool Run(bool available)
        {
            var done = false;
            var frame = new DispatcherFrame();
            void OnChanged()
            {
                if (FeatureInstaller.Busy) return;
                done = true;
                frame.Continue = false;
            }
            FeatureInstaller.Changed += OnChanged;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timeout.Tick += (_, _) => frame.Continue = false;
            timeout.Start();
            FeatureInstaller.Run(sample.Select(f => (f.Id, available)).ToList());
            if (FeatureInstaller.Busy) Dispatcher.PushFrame(frame);
            else done = true;
            timeout.Stop();
            FeatureInstaller.Changed -= OnChanged;
            return done && sample.All(f => f.IsAvailable == available);
        }

        var passed = Run(false) && Run(true);
        Log.Info($"Selftest: the installer cycled {sample.Count} features: {(passed ? "ok" : "failed")}");
        return passed;
    }

    /// <summary>
    /// Health check used by CI: every module registers, every surface builds and every
    /// service can start and stop. Exit code 0 on success.
    /// </summary>
    private static bool SelfTest()
    {
        var ok = true;
        try
        {
            ThemeManager.Initialize();
            FeatureRuntime.Initialize();
            var r = FeatureRuntime.Registry;
            foreach (var section in r.PanelSections)
            {
                try { section.Build(); }
                catch (Exception ex) { ok = false; Console.Error.WriteLine($"panel section {section.Id}: {ex.Message}"); }
            }
            foreach (var page in r.SettingsPages)
            {
                try { page.Build(); }
                catch (Exception ex) { ok = false; Console.Error.WriteLine($"settings page {page.Id}: {ex.Message}"); }
            }
            if (!SelfTestInstaller()) { ok = false; Console.Error.WriteLine("feature installer: did not reach the requested state"); }
            var (full, partial, native, planned) = ParityReport.Counts();
            Log.Info($"Selftest: {r.PanelSections.Count} sections, {r.SettingsPages.Count} pages; parity {full}/{partial}/{native}/{planned}");
        }
        catch (Exception ex)
        {
            Log.Error("Selftest failed", ex);
            ok = false;
        }
        File.WriteAllText(AppInfo.DataPath("selftest.txt"), ok ? "ok" : "failed");
        return ok;
    }
}
