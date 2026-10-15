// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>Kill Process and Port Manager views (Settings → Processes and ports, and the Port Manager window).</summary>
public static class ProcessesUi
{
    public const string GroupRelatedKey = "killProcess.groupRelated";
    public const string SortByKey = "killProcess.sortBy";
    public const string SortAscendingKey = "killProcess.sortAscending";
    public const string IncludeUdpKey = "portManager.includeUdp";
    public const string NetworkGlyph = "\uE968";
    /// <summary>Circled X (EA39).</summary>
    public const string KillGlyph = "\uEA39";
    private const string MoreGlyph = "\uE712";
    private const int MaxRows = 250;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static FrameworkElement BuildPage() => Ui.Page("Processes and ports",
        "Find what is running or listening on a port, and end it.",
        FeatureCatalog.IsAvailable("killProcess") ? KillProcessGroup() : null,
        FeatureCatalog.IsAvailable("portManager") ? Ui.Group("Port Manager", PortList(maxListHeight: 420)) : null);

    // ---- Shared bits -------------------------------------------------------------------

    public static FrameworkElement SearchBox(string placeholder, Action<string> onChanged, out TextBox box)
    {
        var tb = new TextBox { Padding = new Thickness(24, 5, 8, 5) };
        box = tb;
        var hint = Ui.Text(placeholder, 12.5, brush: "TextTertiary", wrap: false);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(28, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        var icon = Ui.Icon(Glyphs.Search, 12, "TextTertiary");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(9, 0, 0, 0);
        icon.IsHitTestVisible = false;
        tb.TextChanged += (_, _) =>
        {
            hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            onChanged(tb.Text);
        };
        var grid = new Grid();
        grid.Children.Add(tb);
        grid.Children.Add(icon);
        grid.Children.Add(hint);
        return grid;
    }

    private static void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn($"Copy failed: {ex.Message}"); }
    }

    private static Window? OwnerOf(DependencyObject element) => Window.GetWindow(element);

    // ---- Kill Process ------------------------------------------------------------------

    private static FrameworkElement KillProcessGroup()
    {
        var sampler = new ProcessSampler();
        var rowsHost = new StackPanel();
        var count = Ui.Text(string.Empty, 12, brush: "TextSecondary", wrap: false);
        var spinner = Ui.Text("Refreshing…", 11, brush: "TextTertiary", wrap: false);
        spinner.Visibility = Visibility.Collapsed;
        var query = string.Empty;
        List<ProcessRow> all = [];
        var refreshing = false;
        var headerHost = new ContentControl();
        FrameworkElement? root = null;

        void Render()
        {
            var sort = ParseSort(Settings.GetString(SortByKey, "cpu"));
            var ascending = Settings.GetBool(SortAscendingKey, false);
            var rows = Settings.GetBool(GroupRelatedKey, true) ? ProcessLogic.Grouped(all) : all;
            var filtered = ProcessLogic.Sorted(rows.Where(r => ProcessLogic.Matches(r, query)), sort, ascending);
            count.Text = $"Processes: {filtered.Count}";
            headerHost.Content = ColumnHeader(sort, ascending);
            rowsHost.Children.Clear();
            if (filtered.Count == 0)
            {
                rowsHost.Children.Add(Ui.Caption(all.Count == 0 ? "Loading…" : "No Processes Found"));
                return;
            }
            foreach (var row in filtered.Take(MaxRows)) rowsHost.Children.Add(ProcessRowView(row, all, Refresh));
            if (filtered.Count > MaxRows)
                rowsHost.Children.Add(Ui.Text($"Showing {MaxRows} of {filtered.Count}. Type to filter.", 11, brush: "TextTertiary"));
        }

        void Refresh()
        {
            if (refreshing) return;
            refreshing = true;
            spinner.Visibility = Visibility.Visible;
            var first = all.Count == 0;
            Task.Run(() =>
            {
                if (first)
                {
                    // Two samples a moment apart so CPU usage is meaningful on the first paint.
                    sampler.Sample();
                    Thread.Sleep(400);
                }
                return sampler.Sample();
            }).ContinueWith(t =>
            {
                Ui.OnUi(() =>
                {
                    refreshing = false;
                    spinner.Visibility = Visibility.Collapsed;
                    if (t.IsCompletedSuccessfully) all = t.Result;
                    Render();
                });
            }, TaskScheduler.Default);
        }

        var search = SearchBox("Filter by name", q =>
        {
            query = q;
            Render();
        }, out _);
        var refresh = Ui.IconButton(Glyphs.Refresh, "Refresh", Refresh, 12);
        var top = Ui.Split(Ui.Row(8, spinner), Ui.Row(8, count, refresh));
        var list = new ScrollViewer { Content = rowsHost, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root = Ui.Stack(10,
            Ui.ToggleRow("Group related processes", "Groups processes started from the same program under one row.", GroupRelatedKey, true, _ => Render()),
            top, search, Ui.Divider(0), headerHost, Ui.Divider(0), list);
        Ui.Track(root, () => Settings.Observe(Render, SortByKey, SortAscendingKey));
        // Refresh every 3 s while the page is visible and its window is active.
        Ui.Every(root, TimeSpan.FromSeconds(3), () =>
        {
            if (all.Count == 0 || Window.GetWindow(root) is { IsActive: true }) Refresh();
        });
        return Ui.Group("Kill Process", root);
    }

    private static ProcessSort ParseSort(string s) => s switch
    {
        "memory" => ProcessSort.Memory,
        "name" => ProcessSort.Name,
        "pid" => ProcessSort.Pid,
        _ => ProcessSort.Cpu,
    };

    private static Grid Columns()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        return g;
    }

    private static void Put(Grid g, UIElement e, int column)
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    private static FrameworkElement ColumnHeader(ProcessSort sort, bool ascending)
    {
        var g = Columns();
        void Header(string title, ProcessSort s, int column, bool right)
        {
            var text = Ui.Text(title, 11.5, FontWeights.SemiBold, sort == s ? "TextPrimary" : "TextSecondary", wrap: false);
            var content = sort == s ? Ui.Row(3, text, Ui.Icon(ascending ? Glyphs.ChevronUp : Glyphs.ChevronDown, 8, "TextSecondary")) : (FrameworkElement)text;
            var button = Ui.Clickable(content, () =>
            {
                var key = s.ToString().ToLowerInvariant();
                if (sort == s) Settings.Set(SortAscendingKey, !ascending);
                else
                {
                    Settings.Set(SortAscendingKey, ProcessLogic.NaturalAscending(s));
                    Settings.Set(SortByKey, key);
                }
            });
            button.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Put(g, button, column);
        }
        Header("Process", ProcessSort.Name, 1, false);
        Header("CPU", ProcessSort.Cpu, 2, true);
        Header("Memory", ProcessSort.Memory, 3, true);
        Header("PID", ProcessSort.Pid, 4, true);
        return g;
    }

    private static FrameworkElement ProcessRowView(ProcessRow row, IReadOnlyList<ProcessRow> all, Action refresh)
    {
        var g = Columns();
        g.Margin = new Thickness(0, 3, 0, 3);
        var icon = AppIcons.Element(row.Path.Length > 0 ? AppIcons.ForPath(row.Path) : null, 22, Glyphs.Settings);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Put(g, icon, 0);
        var title = row.Count > 1 ? $"{row.Name}  ·  Processes: {row.Count}" : row.Name;
        var name = Ui.Text(title, 12.5, FontWeights.Medium, wrap: false);
        var path = Ui.Text(row.Path.Length > 0 ? row.Path : "—", 10.5, brush: "TextTertiary", wrap: false);
        Put(g, Ui.Stack(1, name, path), 1);
        TextBlock Num(string text)
        {
            var t = Ui.Text(text, 11, brush: "TextSecondary", wrap: false);
            t.FontFamily = Mono;
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }
        Put(g, Num($"{row.Cpu:0.0}%"), 2);
        Put(g, Num(ProcessLogic.FormatMemory(row.Memory)), 3);
        Put(g, Num(row.Pid.ToString()), 4);

        var kill = Ui.Button("Kill", () => _ = KillAsync(g, row, refresh));
        kill.Padding = new Thickness(10, 3, 10, 3);
        kill.IsEnabled = !row.Protected;
        Button? more = null;
        more = Ui.IconButton(MoreGlyph, "More", () =>
        {
            var items = new List<(string, Action)>();
            if (!row.Protected)
            {
                items.Add(("Force Kill", () => _ = ForceKillAsync(g, row, refresh)));
                items.Add(($"Kill All “{row.Name}”", () => _ = KillAllAsync(g, row, all, refresh)));
                items.Add(("Kill Process Tree", () => _ = KillTreeAsync(g, row, all, refresh)));
                if (row.Path.Length > 0) items.Add(("Restart", () => _ = RestartAsync(row, refresh)));
                items.Add(("-", () => { }));
            }
            items.Add(("Copy PID", () => Copy(row.Pid.ToString())));
            if (row.Path.Length > 0) items.Add(("Copy Path", () => Copy(row.Path)));
            EnergyUi.ShowMenu(more!, items);
        }, 12);
        var actions = Ui.Row(4, kill, more);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        Put(g, actions, 5);
        g.ToolTip = row.Path.Length > 0 ? row.Path : null;
        return g;
    }

    // ---- Kill flows ----------------------------------------------------------------------

    private static async Task KillAsync(DependencyObject anchor, ProcessRow row, Action refresh)
    {
        if (!EnergyUi.Confirm($"Kill {row.Name}?", row.Path, "Kill", owner: OwnerOf(anchor))) return;
        await EndGracefully(anchor, row.Name, row.Members.Select(pid => (pid, CreateTimeOf(pid, row))).ToList());
        Later(refresh);
    }

    private static async Task ForceKillAsync(DependencyObject anchor, ProcessRow row, Action refresh)
    {
        if (!EnergyUi.Confirm($"Force Kill {row.Name}?", row.Path, "Force Kill", owner: OwnerOf(anchor))) return;
        await Task.Run(() => ForceAll(row.Members.Select(pid => (pid, CreateTimeOf(pid, row)))));
        Later(refresh);
    }

    private static async Task KillAllAsync(DependencyObject anchor, ProcessRow row, IReadOnlyList<ProcessRow> all, Action refresh)
    {
        if (!EnergyUi.Confirm($"Kill all “{row.Name}” processes?", string.Empty, "Kill", owner: OwnerOf(anchor))) return;
        var targets = all.Where(r => !r.Protected && string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.Pid, r.CreateTime)).ToList();
        await EndGracefully(anchor, row.Name, targets);
        Later(refresh);
    }

    private static async Task KillTreeAsync(DependencyObject anchor, ProcessRow row, IReadOnlyList<ProcessRow> all, Action refresh)
    {
        if (!EnergyUi.Confirm($"Kill {row.Name} and all its child processes?", row.Path, "Kill Process Tree", owner: OwnerOf(anchor))) return;
        var parents = all.ToDictionary(r => r.Pid, r => r.ParentPid);
        var created = all.ToDictionary(r => r.Pid, r => r.CreateTime);
        var protectedPids = all.Where(r => r.Protected).Select(r => r.Pid).ToHashSet();
        var tree = ProcessLogic.Descendants(row.Pid, parents).Where(p => !protectedPids.Contains(p)).ToList();
        tree.Reverse(); // deepest first, the root last
        tree.Add(row.Pid);
        await Task.Run(() => ForceAll(tree.Select(p => (p, created.GetValueOrDefault(p)))));
        Later(refresh);
    }

    private static async Task RestartAsync(ProcessRow row, Action refresh)
    {
        var created = row.CreateTime;
        if (!ProcessKiller.CloseGracefully(row.Pid))
        {
            Notifier.Show("Kill Process", $"{row.Name} has no window to close, so it cannot be restarted safely.", warning: true);
            return;
        }
        if (!await ProcessKiller.WaitForExit(row.Pid, created, TimeSpan.FromSeconds(10))) return;
        try { Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Restart of {row.Path} failed: {ex.Message}"); }
        Later(refresh);
    }

    private static long CreateTimeOf(uint pid, ProcessRow row) => pid == row.Pid ? row.CreateTime : ProcessSnapshot.Take().FirstOrDefault(s => s.Pid == pid)?.CreateTime ?? 0;

    /// <summary>Asks each process to close through its windows; anything still running (or windowless) is offered a force kill.</summary>
    private static async Task EndGracefully(DependencyObject anchor, string name, List<(uint Pid, long CreateTime)> targets)
    {
        var closing = targets.Where(t => ProcessKiller.CloseGracefully(t.Pid)).ToList();
        var windowless = targets.Except(closing).ToList();
        var stubborn = new List<(uint, long)>(windowless);
        foreach (var t in closing)
        {
            if (!await ProcessKiller.WaitForExit(t.Pid, t.CreateTime, TimeSpan.FromSeconds(3))) stubborn.Add(t);
        }
        if (stubborn.Count == 0) return;
        var message = windowless.Count == targets.Count
            ? $"{name} has no window to close. Ending it may lose unsaved work."
            : $"{name} is still running. Ending it may lose unsaved work.";
        if (!EnergyUi.Confirm($"Force Kill {name}?", message, "Force Kill", owner: OwnerOf(anchor))) return;
        await Task.Run(() => ForceAll(stubborn));
    }

    private static void ForceAll(IEnumerable<(uint Pid, long CreateTime)> targets)
    {
        var failed = targets.Where(t => !ProcessKiller.Terminate(t.Pid, t.CreateTime)).ToList();
        if (failed.Count > 0)
            Notifier.Show("Couldn’t Kill Process", "The process may have already exited or require additional privileges.", warning: true);
    }

    private static void Later(Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Ui.Guard(action);
        };
        t.Start();
    }

    // ---- Port Manager --------------------------------------------------------------------

    /// <summary>The listening-ports list with filter, refresh and stop buttons.</summary>
    public static FrameworkElement PortList(double maxListHeight)
    {
        var rows = new StackPanel();
        var open = Ui.Text(string.Empty, 12, FontWeights.SemiBold, "TextSecondary", wrap: false);
        var error = Ui.Row(6, Ui.Icon(Glyphs.Warning, 12, "Orange"), Ui.Text("Could not read listening ports. Try refreshing.", 12, brush: "Orange"));
        error.Visibility = Visibility.Collapsed;
        List<PortEntry> entries = [];
        var query = string.Empty;
        var loading = false;

        void Render()
        {
            var shown = entries.Where(e => PortLogic.Matches(e, query)).ToList();
            open.Text = $"{shown.Count} open";
            rows.Children.Clear();
            if (shown.Count == 0)
            {
                rows.Children.Add(Ui.Stack(4, Ui.Icon(NetworkGlyph, 22, "TextTertiary"),
                    Ui.Text("No listening ports found", 13, FontWeights.Medium),
                    Ui.Caption("Try refreshing or changing your search.")));
                ((FrameworkElement)rows.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
                ((FrameworkElement)rows.Children[0]).Margin = new Thickness(0, 16, 0, 16);
                return;
            }
            foreach (var e in shown) Ui.Append(rows, 6, PortRow(e, Refresh));
        }

        void Refresh()
        {
            if (loading) return;
            loading = true;
            var udp = Settings.GetBool(IncludeUdpKey, false);
            Task.Run(() => PortTable.Read(udp)).ContinueWith(t => Ui.OnUi(() =>
            {
                loading = false;
                error.Visibility = t.IsCompletedSuccessfully ? Visibility.Collapsed : Visibility.Visible;
                if (t.IsCompletedSuccessfully) entries = t.Result;
                Render();
            }), TaskScheduler.Default);
        }

        var search = SearchBox("Filter by port, process, or PID", q =>
        {
            query = q;
            Render();
        }, out _);
        var refresh = Ui.IconButton(Glyphs.Refresh, "Refresh", Refresh, 12);
        var head = Ui.Split(Ui.Row(8, Ui.Icon(NetworkGlyph, 14), Ui.Text("Listening ports", 13, FontWeights.SemiBold)), open);
        var udpToggle = EnergyUi.MiniToggle(null, "Include UDP", null, IncludeUdpKey, false, _ => Refresh());
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(search);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(refresh, 1);
        searchRow.Children.Add(refresh);
        var scroll = new ScrollViewer { Content = rows, MaxHeight = maxListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = Ui.Stack(10, head, searchRow, udpToggle, error, scroll);
        root.Loaded += (_, _) => Refresh();
        if (root.IsLoaded) Refresh();
        Render();
        return root;
    }

    private static FrameworkElement PortRow(PortEntry e, Action refresh)
    {
        var port = Ui.Text(e.Port.ToString(), 13.5, FontWeights.SemiBold, wrap: false);
        port.FontFamily = Mono;
        var line1 = Ui.Row(6, port, Ui.Badge(e.Protocol));
        if (PortLogic.ListensOnAllInterfaces(e.Host))
        {
            var badge = Ui.Badge("All interfaces", "BadgeBackground", "Orange");
            badge.ToolTip = "Listening on every network interface, so other devices on the network may be able to connect.";
            line1.Children.Add(badge);
            badge.Margin = new Thickness(6, 0, 0, 0);
        }
        var details = Ui.Text($"PID {e.Pid}  •  {e.Address}", 10.5, brush: "TextTertiary", wrap: false);
        details.FontFamily = Mono;
        var text = Ui.Stack(2, line1, Ui.Text(e.ProcessName, 12, wrap: false), details);
        var icon = Ui.Icon(NetworkGlyph, 16);
        icon.Width = 24;
        icon.VerticalAlignment = VerticalAlignment.Top;
        var left = Ui.Row(8, icon, text);
        FrameworkElement? right = null;
        if (FeatureCatalog.IsAvailable("killProcess"))
        {
            var protectedProcess = ProcessLogic.IsProtected(e.Pid, e.ProcessName, (uint)Environment.ProcessId);
            var enabled = e.CreateTime != 0 && !protectedProcess;
            var kill = Ui.Button("Kill", () => _ = StopPortAsync(e, force: false, refresh));
            kill.Padding = new Thickness(10, 3, 10, 3);
            kill.IsEnabled = enabled;
            var force = Ui.IconButton(Glyphs.Bolt, "Force Kill", () => _ = StopPortAsync(e, force: true, refresh), 12);
            force.IsEnabled = enabled;
            right = Ui.Row(4, kill, force);
        }
        var row = Ui.Split(left, right);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        var menu = new ContextMenu();
        void Item(string header, Action run)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => Ui.Guard(run);
            menu.Items.Add(mi);
        }
        Item("Copy port", () => Copy(e.Port.ToString()));
        Item("Copy PID", () => Copy(e.Pid.ToString()));
        Item("Copy address:port", () => Copy(e.Address));
        if (PortLogic.BrowserUrl(e) is { } url)
        {
            menu.Items.Add(new Separator());
            Item("Open in browser", () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        }
        card.ContextMenu = menu;
        return card;
    }

    private static async Task StopPortAsync(PortEntry e, bool force, Action refresh)
    {
        if (!EnergyUi.Confirm($"Terminate {e.ProcessName}?", $"This closes port {e.Port} by terminating PID {e.Pid}.", force ? "Force Kill" : "Kill")) return;
        if (!force && ProcessKiller.CloseGracefully(e.Pid) && await ProcessKiller.WaitForExit(e.Pid, e.CreateTime, TimeSpan.FromSeconds(3)))
        {
            refresh();
            return;
        }
        await Task.Run(() => ForceAll([(e.Pid, e.CreateTime)]));
        refresh();
    }
}

/// <summary>The Port Manager tool window opened from the Utilities tab.</summary>
public sealed class PortManagerWindow : Window
{
    private static PortManagerWindow? _instance;

    public static void Open()
    {
        _instance ??= new PortManagerWindow();
        _instance.Show();
        _instance.Activate();
    }

    public static void CloseInstance() => _instance?.Close();

    private PortManagerWindow()
    {
        Title = "Port Manager";
        Width = 460;
        Height = 560;
        MinWidth = 380;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowFit.ToDisplay(this);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var settings = Ui.IconButton(Glyphs.Settings, "Settings…", () => PanelHost.OpenSettings("processes"), 13);
        var header = Ui.Split(Ui.Row(8, Ui.Icon(ProcessesUi.NetworkGlyph, 16), Ui.Title("Port Manager")), settings);
        var body = Ui.Stack(12, header, Ui.Card(ProcessesUi.PortList(maxListHeight: 2000)));
        body.Margin = new Thickness(16);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) => _instance = null;
    }
}
