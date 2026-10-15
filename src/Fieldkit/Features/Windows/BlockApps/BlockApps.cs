// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools.BlockApps;

/// <summary>Settings keys of Block apps. The app list itself is <c>blockApps.exceptions</c> (the shared app list control).</summary>
public static class BlockAppsKeys
{
    public const string FeatureId = "blockApps";
    public const string Enabled = "blockApps.enabled";
    public const string Mode = "blockApps.mode";        // always | schedule
    public const string Start = "blockApps.start";      // minutes after midnight
    public const string End = "blockApps.end";
    public const string Days = "blockApps.days";        // "1,2,3,4,5" (DayOfWeek numbers)
    public const int DefaultStart = 9 * 60;
    public const int DefaultEnd = 17 * 60;
    public const string DefaultDays = "1,2,3,4,5";
}

/// <summary>Pure rules of Block apps (unit tested).</summary>
public static class BlockAppsLogic
{
    /// <summary>Programs that keep Windows usable: never blocked, whatever the list says.</summary>
    private static readonly HashSet<string> NeverBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "fieldkit", "taskmgr", "dwm", "winlogon", "csrss", "lsass", "services", "svchost", "smss", "wininit",
        "sihost", "ctfmon", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
        "systemsettings", "lockapp", "logonui", "consent", "conhost", "openconsole", "textinputhost", "securityhealthsystray",
    };

    /// <summary>Whether a program may be blocked: not one Windows needs, and nothing inside the Windows folder.</summary>
    public static bool CanBlock(string name, string? path, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(name) || NeverBlock.Contains(name)) return false;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(windowsDir)) return true;
        return !path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<DayOfWeek> ParseDays(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var d) && d is >= 0 and <= 6 ? (DayOfWeek?)d : null)
            .Where(d => d is not null).Select(d => d!.Value).ToHashSet();

    public static string FormatDays(IEnumerable<DayOfWeek> days) => string.Join(",", days.Select(d => (int)d).Distinct().Order());

    /// <summary>
    /// Whether blocking is on at <paramref name="now"/>. A schedule that ends before it starts runs overnight and
    /// belongs to the day it started on (22:00–07:00 on Friday covers Saturday morning). Equal times mean all day.
    /// </summary>
    public static bool IsActive(DateTime now, string mode, int startMinutes, int endMinutes, IReadOnlySet<DayOfWeek> days)
    {
        if (mode != "schedule") return true;
        var minute = now.Hour * 60 + now.Minute;
        var today = now.DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);
        if (startMinutes == endMinutes) return days.Contains(today);
        if (startMinutes < endMinutes) return days.Contains(today) && minute >= startMinutes && minute < endMinutes;
        return minute >= startMinutes && days.Contains(today) || minute < endMinutes && days.Contains(yesterday);
    }

    /// <summary>"09:00", "17:30".</summary>
    public static string TimeLabel(int minutes) =>
        DateTime.Today.AddMinutes(minutes).ToString("t", CultureInfo.CurrentCulture);
}

/// <summary>
/// Block apps (upstream #2965): a listed app is closed as soon as it shows a window, always or on a schedule.
/// A window-show hook runs out of process, so this costs nothing while idle. Windows' own block switches (Image
/// File Execution Options, AppLocker) need administrator rights and change the whole PC; this does neither.
/// When a schedule starts, listed apps that are already open are asked to close normally (unsaved work is
/// never thrown away); new launches during the block are closed at once.
/// </summary>
internal sealed class BlockAppsService : FeatureService
{
    public override string FeatureId => BlockAppsKeys.FeatureId;
    public override IReadOnlyList<string> EnableKeys => [BlockAppsKeys.Enabled];
    public override bool EnabledByDefault => false;

    /// <summary>"Pause for 15 minutes" from the Command Bar or the command line.</summary>
    public static DateTime PausedUntil { get; set; } = DateTime.MinValue;

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly DispatcherTimer _schedule = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, DateTime> _lastNotice = new(StringComparer.OrdinalIgnoreCase);
    private Native.WinEventDelegate? _callback;
    private IntPtr _hook;
    private HashSet<string> _blocked = [];
    private IDisposable? _observer;
    private bool _wasActive;

    public BlockAppsService() => _schedule.Tick += (_, _) => Ui.Guard(CheckSchedule);

    protected override void OnStart()
    {
        Read();
        _observer = Settings.ObservePrefix("blockApps.", Read);
        _callback = OnEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _wasActive = false;
        _schedule.Start();
        CheckSchedule();
    }

    protected override void OnStop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
        _schedule.Stop();
        _observer?.Dispose();
        _observer = null;
    }

    private void Read() => _blocked = AppExceptions.Snapshot(BlockAppsKeys.FeatureId);

    private static bool Active() =>
        DateTime.Now >= PausedUntil && BlockAppsLogic.IsActive(DateTime.Now,
            Settings.GetString(BlockAppsKeys.Mode, "always"),
            Settings.GetInt(BlockAppsKeys.Start, BlockAppsKeys.DefaultStart),
            Settings.GetInt(BlockAppsKeys.End, BlockAppsKeys.DefaultEnd),
            BlockAppsLogic.ParseDays(Settings.GetString(BlockAppsKeys.Days, BlockAppsKeys.DefaultDays)));

    /// <summary>When a block begins (schedule start, switched on, pause over), open listed apps are asked to close.</summary>
    private void CheckSchedule()
    {
        var active = Active();
        if (active && !_wasActive && _blocked.Count > 0)
        {
            foreach (var w in WindowHelper.GetAltTabWindows())
            {
                var name = AppExceptions.Normalize(w.ProcessPath);
                if (!_blocked.Contains(name) || !BlockAppsLogic.CanBlock(name, w.ProcessPath, WindowsDir)) continue;
                WindowHelper.Close(w.Handle);
                Notice(name, "asked to close");
            }
        }
        _wasActive = active;
    }

    // UI thread (out-of-context WinEvents arrive on the thread that set the hook).
    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero || _blocked.Count == 0) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
        var pid = WindowHelper.GetProcessId(hwnd);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;
        var path = WindowHelper.GetProcessPath(pid);
        var name = AppExceptions.Normalize(path);
        if (!_blocked.Contains(name) || !BlockAppsLogic.CanBlock(name, path, WindowsDir) || !Active()) return;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill();
            Log.Info($"Blocked {name}");
            Notice(name, "closed");
        }
        catch (Exception ex)
        {
            // Apps running as administrator cannot be closed by a normal Fieldkit.
            Log.Warn($"Could not block {name}: {ex.Message}");
            Notice(name, "could not be closed (it may run as administrator)");
        }
    }

    private void Notice(string name, string what)
    {
        // One notice per app per minute: some apps relaunch themselves or open several windows at once.
        if (_lastNotice.TryGetValue(name, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        _lastNotice[name] = DateTime.UtcNow;
        Notifier.Show($"{name} is blocked", $"Fieldkit {what}. Change this in Settings → Block apps.");
    }
}

/// <summary>Settings → Block apps.</summary>
internal static class BlockAppsPage
{
    public static FrameworkElement Build() => Ui.Page("Block apps",
        "Keep distracting apps closed: a blocked app is closed as soon as it opens, always or during the hours you choose.",
        Ui.Group(null,
            Ui.ToggleRow("Block apps", "Apps on the list below are closed when they open. Windows' own programs can't be blocked.",
                BlockAppsKeys.Enabled, false),
            Ui.ChoiceRow("When", BlockAppsKeys.Mode, [("always", "Always"), ("schedule", "On a schedule")], "always"),
            InputUi.Live(Schedule, BlockAppsKeys.Mode)),
        Ui.Group(null, InputUi.ExceptionList(BlockAppsKeys.FeatureId,
            "When a block starts, listed apps that are already open are asked to close, so nothing unsaved is lost.",
            title: "Blocked apps", addTitle: "Add an app…", emptyText: "No apps yet.")),
        Ui.Group(null, InputUi.InfoLine(Glyphs.Info,
            "Apps that run as administrator can only be closed when Fieldkit runs as administrator too. Pause blocking for 15 minutes from the Command Bar.")));

    private static UIElement Schedule()
    {
        if (Settings.GetString(BlockAppsKeys.Mode, "always") != "schedule") return new Border();
        var times = Enumerable.Range(0, 48).Select(i => ((i * 30).ToString(CultureInfo.InvariantCulture), BlockAppsLogic.TimeLabel(i * 30))).ToList();
        var from = Ui.Choice(BlockAppsKeys.Start, times, BlockAppsKeys.DefaultStart.ToString(CultureInfo.InvariantCulture));
        var to = Ui.Choice(BlockAppsKeys.End, times, BlockAppsKeys.DefaultEnd.ToString(CultureInfo.InvariantCulture));
        from.MinWidth = to.MinWidth = 110;
        var timeRow = Ui.Split(Ui.Text("From", 13.5, FontWeights.Medium), Ui.Row(8, from, Ui.Text("to", 13, brush: "TextSecondary"), to));

        var days = new WrapPanel();
        var selected = BlockAppsLogic.ParseDays(Settings.GetString(BlockAppsKeys.Days, BlockAppsKeys.DefaultDays)).ToHashSet();
        // Monday first, like Windows' calendar in most places.
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var on = selected.Contains(day);
            var chip = new CheckBox
            {
                Content = Ui.Text(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), 12.5, wrap: false),
                IsChecked = on,
                Margin = new Thickness(0, 0, 12, 0),
            };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) selected.Add(day);
                else selected.Remove(day);
                Settings.Set(BlockAppsKeys.Days, BlockAppsLogic.FormatDays(selected));
            };
            days.Children.Add(chip);
        }
        return Ui.Stack(10, timeRow, Ui.Split(Ui.Text("Days", 13.5, FontWeights.Medium), days),
            Ui.Caption("A schedule that ends before it starts runs overnight.", 12));
    }
}

/// <summary>Block apps (a Fieldkit extra, upstream #2965).</summary>
public sealed class BlockAppsModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(BlockAppsKeys.FeatureId, Parity.Full, "Fieldkit extra, from Vorssaint issue #2965.");
        r.Service(BlockAppsKeys.FeatureId, () => new BlockAppsService());
        r.SettingsPage(new SettingsPageDef("blockApps", SettingsCategory.Utilities, "Block apps", Glyphs.Stop, 66,
            [BlockAppsKeys.FeatureId], BlockAppsPage.Build, ["block", "focus", "distraction", "parental", "schedule", "close apps"]));
        r.Action(new ToolAction("blockApps.pause", BlockAppsKeys.FeatureId, "Pause blocking apps for 15 minutes", Glyphs.Stop, () =>
        {
            BlockAppsService.PausedUntil = DateTime.Now.AddMinutes(15);
            Notifier.Show("Blocking paused", "Blocked apps can open for the next 15 minutes.");
        }));
        r.Action(new ToolAction("blockApps.resume", BlockAppsKeys.FeatureId, "Resume blocking apps", Glyphs.Stop, () =>
        {
            BlockAppsService.PausedUntil = DateTime.MinValue;
            Notifier.Show("Blocking resumed", "Blocked apps are closed again when they open.");
        }));
    }
}
