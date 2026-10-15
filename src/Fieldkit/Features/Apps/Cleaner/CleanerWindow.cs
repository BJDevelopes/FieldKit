// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner.Messaging;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>The Cleaner tool window (upstream CleanerView, non-compact form).</summary>
public sealed class CleanerWindow : ToolWindow
{
    public const string Key = "cleaner";

    public CleanerWindow() : base(S.Name, 600, 700)
    {
        var view = new CleanerView(JunkCleaner.Shared, interactive: true);
        Content = view;
        Closed += (_, _) =>
        {
            // Closing means cancel: a scan left running would come back on the next open.
            if (JunkCleaner.Shared.State == JunkCleaner.Phase.Scanning) JunkCleaner.Shared.Reset();
            view.Detach();
        };
    }

    public static void Open() => ShowSingle(Key, () => new CleanerWindow());

    /// <summary>Renders the cleaner in a given state with realistic sample data (for review in tests).</summary>
    /// <param name="state">"idle", "scanning", "results" or "done".</param>
    public static void RenderPreview(string path, bool dark, string state = "results")
    {
        var paths = SamplePaths();
        var cleaner = state switch
        {
            "idle" => JunkCleaner.ForPreview(JunkCleaner.Phase.Idle, []),
            "scanning" => JunkCleaner.ForPreview(JunkCleaner.Phase.Scanning, [], CleanerCategory.Caches),
            "done" => JunkCleaner.ForPreview(JunkCleaner.Phase.Done, [], freed: 2_740_000_000, failed: 2),
            "empty" => JunkCleaner.ForPreview(JunkCleaner.Phase.Results, []),
            _ => JunkCleaner.ForPreview(JunkCleaner.Phase.Results, SampleItems(paths)),
        };
        var view = new CleanerView(cleaner, interactive: false, paths);
        view.Expand(DisplayGroup.SafeCaches);
        view.Expand(DisplayGroup.Leftovers);
        view.Rebuild();
        PreviewRenderer.Render(view, path, 600, 700, dark);
    }

    internal static CleanerPaths SamplePaths() => new(
        @"C:\Users\alex\AppData\Local", @"C:\Users\alex\AppData\Roaming", @"C:\Users\alex\AppData\LocalLow",
        @"C:\Users\alex\AppData\Local\Temp", @"C:\Users\alex", @"C:\Users\alex\Pictures\Screenshots", @"C:\Windows");

    internal static List<CleanerItem> SampleItems(CleanerPaths p)
    {
        CleanerItem Item(string path, string name, CleanerCategory c, long size, bool rec, string? blocked = null) => new()
        {
            Path = path, Name = name, Category = c, Size = size, Recommended = rec, Include = rec && blocked is null, BlockedBy = blocked,
        };
        return
        [
            Item($@"{p.LocalAppData}\Google\Chrome\User Data\Default\Cache", "Google Chrome (Default) · Cache", CleanerCategory.Caches, 812_000_000, true),
            Item($@"{p.RoamingAppData}\discord\Cache", "discord · Cache", CleanerCategory.Caches, 356_000_000, true),
            Item($@"{p.LocalAppData}\Microsoft\Edge\User Data\Default\Code Cache", "Microsoft Edge (Default) · Code Cache", CleanerCategory.Caches, 141_000_000, true, "Microsoft Edge"),
            Item($@"{p.RoamingAppData}\Code\CachedData", "Visual Studio Code · CachedData", CleanerCategory.Caches, 96_400_000, true),
            Item($@"{p.LocalAppData}\D3DSCache", "DirectX shader cache · D3DSCache", CleanerCategory.Caches, 210_000_000, false),
            Item($@"{p.LocalAppData}\Microsoft\Windows\INetCache", "Windows internet cache · INetCache", CleanerCategory.Caches, 48_000_000, false),
            Item($@"{p.Temp}\setup-4F2A", "setup-4F2A", CleanerCategory.Temp, 420_000_000, true),
            Item($@"{p.Temp}\chrome_installer.log", "chrome_installer.log", CleanerCategory.Temp, 1_200_000, true),
            Item($@"{p.LocalAppData}\CrashDumps", "Crash dumps", CleanerCategory.Logs, 188_000_000, true),
            Item($@"{p.LocalAppData}\Microsoft\Windows\WER\ReportArchive", "Windows Error Reporting archive", CleanerCategory.Logs, 12_000_000, true),
            Item($@"{p.LocalAppData}\npm-cache\_cacache", "npm · _cacache", CleanerCategory.Developer, 1_350_000_000, true),
            Item($@"{p.LocalAppData}\NuGet\v3-cache", "NuGet · v3-cache", CleanerCategory.Developer, 260_000_000, true),
            Item(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\OldUpdater", "OldUpdater", CleanerCategory.LoginItems, 0, true),
            Item($@"{p.RoamingAppData}\OldPhotoEditor", "OldPhotoEditor", CleanerCategory.Leftovers, 734_000_000, false),
            Item($@"{p.LocalAppData}\RetiredGame", "RetiredGame", CleanerCategory.Leftovers, 98_000_000, false),
            Item($@"{p.Screenshots}\Screenshot 2026-07-02 101533.png", "Screenshot 2026-07-02 101533.png", CleanerCategory.Screenshots, 2_400_000, false),
            new CleanerItem { Path = "Recycle Bin", Name = S.CatTrash, Category = CleanerCategory.Trash, Size = 3_100_000_000, Kind = CleanerItemKind.RecycleBin, Detail = "214 items" },
        ];
    }
}

/// <summary>The cleaner's content: idle, scanning, results, cleaning and done faces.</summary>
internal sealed class CleanerView : ContentControl
{
    private readonly JunkCleaner _cleaner;
    private readonly bool _interactive;
    private readonly CleanerPaths _paths;
    private readonly HashSet<DisplayGroup> _expanded = [];
    private readonly List<Action> _refreshers = [];
    private JunkCleaner.Phase? _builtPhase;
    private int _builtCount = -1;

    public CleanerView(JunkCleaner cleaner, bool interactive, CleanerPaths? paths = null)
    {
        _cleaner = cleaner;
        _interactive = interactive;
        _paths = paths ?? CleanerPaths.Current();
        if (interactive) _cleaner.Changed += OnChanged;
        Rebuild();
    }

    public void Detach() => _cleaner.Changed -= OnChanged;

    public void Expand(DisplayGroup group) => _expanded.Add(group);

    private void OnChanged()
    {
        if (_builtPhase == _cleaner.State && _builtCount == _cleaner.Items.Count && _cleaner.State == JunkCleaner.Phase.Results)
        {
            foreach (var r in _refreshers) r();
            return;
        }
        Rebuild();
    }

    public void Rebuild()
    {
        _refreshers.Clear();
        _builtPhase = _cleaner.State;
        _builtCount = _cleaner.Items.Count;
        Content = _cleaner.State switch
        {
            JunkCleaner.Phase.Idle => Scrolling(IdleState()),
            JunkCleaner.Phase.Scanning => Scrolling(BusyState(S.Scanning, _cleaner.ScanningCategory is { } c ? GroupTitleForCategory(c) : null, true)),
            JunkCleaner.Phase.Cleaning => Scrolling(BusyState(S.Cleaning, null, false)),
            JunkCleaner.Phase.Done => Scrolling(DoneState()),
            _ => ResultsState(),
        };
    }

    private static ScrollViewer Scrolling(FrameworkElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private static string GroupTitleForCategory(CleanerCategory c) => c == CleanerCategory.Caches ? S.CatCaches : CleanerRules.ScanTitle(c);

    // ------------------------------------------------------------------ idle

    private FrameworkElement IdleState()
    {
        var glyph = Ui.Icon(CleanerGlyphs.Broom, 46, "TextSecondary");
        var title = Ui.Text(S.IntroTitle, 17, FontWeights.SemiBold);
        title.TextAlignment = TextAlignment.Center;
        var caption = Ui.Caption(S.IntroCaption, 12);
        caption.TextAlignment = TextAlignment.Center;
        caption.MaxWidth = 380;
        var scan = Ui.Button(S.Scan, () => _ = _cleaner.ScanAsync(attended: true), accent: true);
        scan.Padding = new Thickness(26, 7, 26, 7);
        scan.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = Ui.Stack(16, glyph, title, caption, scan, CleanerSettingsPage.ScheduleCard(compactCaption: true),
            ScreenshotsCard(), MessagingCard());
        foreach (var child in stack.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var card in stack.Children.OfType<Border>().Where(b => b.MaxWidth < double.PositiveInfinity)) card.Width = 400;
        stack.Margin = new Thickness(28, 36, 28, 28);
        return stack;
    }

    private static Border ScreenshotsCard()
    {
        var choices = new List<(int, string)> { (0, S.ScheduleOff) };
        var current = CleanerRules.SanitizedScreenshotAgeDays(Settings.GetInt(CleanerKeys.ScreenshotAgeDays, CleanerRules.DefaultScreenshotAgeDays));
        var values = CleanerRules.ScreenshotAgeChoices.ToList();
        if (current > 0 && !values.Contains(current)) values.Add(current);
        choices.AddRange(values.Order().Select(d => (d, string.Format(CultureInfo.CurrentCulture, S.ScreenshotsAfterFormat, d))));
        var combo = CleanerUi.IntChoice(CleanerKeys.ScreenshotAgeDays, choices, CleanerRules.DefaultScreenshotAgeDays);
        var head = Ui.Split(Ui.Row(8, Ui.Icon(CleanerGlyphs.Camera, 14, "TextSecondary"), Ui.Text(S.CatScreenshots, 12.5, FontWeights.Medium)), combo);
        return CleanerUi.SoftCard(Ui.Stack(6, head, CleanerUi.Small(S.ScreenshotsSettingCaption)));
    }

    private static Border MessagingCard()
    {
        var holder = new Border();
        void Build()
        {
            if (!Settings.GetBool(MessagingKeys.Enabled, false))
            {
                var head = Ui.Split(Ui.Row(8, Ui.Icon(CleanerGlyphs.Download, 14, "TextSecondary"), Ui.Text(S.MsgTitle, 12.5, FontWeights.Medium)),
                    Ui.Toggle(MessagingKeys.Enabled, false, small: true));
                holder.Child = Ui.Stack(6, head, CleanerUi.Small(S.MsgHubDescription));
                return;
            }
            var summary = Settings.GetBool(MessagingKeys.Automatic, false)
                ? MessagingService.NextFire is { } next ? CleanerRules.RelativeDate(next, DateTime.Now) : S.MsgAutomatic
                : S.ScheduleOff;
            var row = Ui.Split(Ui.Row(8, Ui.Icon(CleanerGlyphs.Download, 14, "TextSecondary"), Ui.Text(S.MsgTitle, 12.5, FontWeights.Medium)),
                CleanerUi.Small(summary));
            var manage = Ui.Button(S.MsgManageButton, MessagingWindow.Open);
            manage.HorizontalAlignment = HorizontalAlignment.Left;
            holder.Child = Ui.Stack(8, row, CleanerUi.Small(MessagingWindow.LastRunLine(), "TextTertiary"), manage);
        }
        Build();
        Ui.Track(holder, () => Settings.Observe(Build, MessagingKeys.Enabled, MessagingKeys.Automatic));
        return CleanerUi.SoftCard(holder);
    }

    // ------------------------------------------------------------------ busy

    private FrameworkElement BusyState(string message, string? detail, bool cancellable)
    {
        var glyph = Ui.Icon(CleanerGlyphs.Broom, 54, "TextSecondary");
        var stack = Ui.Stack(16, glyph, Ui.Text(message, 13, brush: "TextSecondary"),
            detail is null ? null : Ui.Text(detail, 11.5, brush: "TextTertiary"),
            cancellable ? Ui.Button(S.Cancel, _cleaner.Reset) : null);
        foreach (var child in stack.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(28, 160, 28, 28);
        return stack;
    }

    // ------------------------------------------------------------------ done

    private FrameworkElement DoneState()
    {
        var check = Ui.Icon(CleanerGlyphs.CheckCircle, 54, "Green");
        var stack = Ui.Stack(14, check, Ui.Text(S.DoneTitle, 20, FontWeights.Bold),
            Ui.Text(string.Format(S.FreedFormat, ByteFormat.Format(_cleaner.LastFreed)), 13, brush: "TextSecondary"),
            Centered(Ui.Text(S.DoneNote, 11.5, brush: "TextTertiary"), 340),
            _cleaner.LastFailed > 0 ? Centered(Ui.Text(S.SomeFailed, 11.5, brush: "Orange"), 340) : null,
            Ui.Button(S.Again, _cleaner.Reset));
        foreach (var child in stack.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(28, 140, 28, 28);
        return stack;
    }

    private static TextBlock Centered(TextBlock tb, double maxWidth)
    {
        tb.TextAlignment = TextAlignment.Center;
        tb.MaxWidth = maxWidth;
        return tb;
    }

    // --------------------------------------------------------------- results

    private FrameworkElement ResultsState()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = ResultsHeader();
        grid.Children.Add(header);
        var div1 = Ui.Divider(0);
        Grid.SetRow(div1, 1);
        grid.Children.Add(div1);

        if (_cleaner.Items.Count == 0)
        {
            var empty = Ui.Stack(10, Ui.Icon(CleanerGlyphs.CheckCircle, 34, "Green"), Ui.Text(S.NothingFound, 13, brush: "TextSecondary"));
            foreach (var child in empty.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(empty, 2);
            grid.Children.Add(empty);
            return grid;
        }

        var list = Ui.Stack(4);
        list.Margin = new Thickness(16, 10, 16, 16);
        AddSection(list, S.SafeSection, Enum.GetValues<DisplayGroup>().Where(CleanerRules.IsSafe));
        AddSection(list, S.OptionalSection, Enum.GetValues<DisplayGroup>().Where(g => !CleanerRules.IsSafe(g)));
        var scroll = Scrolling(list);
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        var div2 = Ui.Divider(0);
        Grid.SetRow(div2, 3);
        grid.Children.Add(div2);
        var footer = ResultsFooter();
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);
        return grid;
    }

    private FrameworkElement ResultsHeader()
    {
        var found = Ui.Text(string.Empty, 11.5, brush: "TextSecondary");
        void Refresh() => found.Text = $"{ByteFormat.Format(_cleaner.TotalSize)} {S.FoundTitle}";
        Refresh();
        _refreshers.Add(Refresh);
        var left = Ui.Row(12, Ui.Icon(CleanerGlyphs.Broom, 22, "TextSecondary"), Ui.Stack(2, Ui.Text(S.Name, 15, FontWeights.SemiBold), found));
        var close = Ui.IconButton(CleanerGlyphs.CloseCircle, S.Cancel, _cleaner.Reset, 16);
        var row = Ui.Split(left, close);
        row.Margin = new Thickness(16);
        return row;
    }

    private void AddSection(StackPanel list, string header, IEnumerable<DisplayGroup> groups)
    {
        var visible = groups.Where(g => _cleaner.ItemsIn(g).Any()).ToList();
        if (visible.Count == 0) return;
        var h = Ui.Text(header, 11.5, FontWeights.SemiBold, "TextSecondary");
        h.Margin = new Thickness(2, list.Children.Count == 0 ? 0 : 14, 0, 4);
        list.Children.Add(h);
        foreach (var g in visible)
        {
            list.Children.Add(GroupRow(g));
            list.Children.Add(Ui.Divider(2));
        }
    }

    private FrameworkElement GroupRow(DisplayGroup group)
    {
        var items = _cleaner.ItemsIn(group).ToList();
        var check = CleanerUi.Check(false, v => _cleaner.SetInclude(group, v), enabled: items.Any(i => !i.IsBlocked));
        var size = Ui.Text(ByteFormat.Format(items.Sum(i => i.Size)), 11.5, brush: "TextSecondary", wrap: false);
        void Refresh()
        {
            var selectable = items.Where(i => !i.IsBlocked).ToList();
            check.IsChecked = selectable.Count > 0 && selectable.All(i => i.Include);
        }
        Refresh();
        _refreshers.Add(Refresh);

        var expanded = _expanded.Contains(group);
        var chevron = Ui.Icon(expanded ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        chevron.Width = 12;
        var icon = Ui.Icon(GroupGlyph(group), 15, "TextSecondary");
        icon.Width = 22;
        var text = Ui.Stack(1, Ui.Text(GroupTitle(group), 13, FontWeights.Medium), Ui.Text(GroupCaption(group), 11, brush: "TextSecondary"));
        text.Margin = new Thickness(10, 0, 10, 0);

        var row = new Grid { Background = CleanerUi.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        check.Margin = new Thickness(6, 0, 8, 0);
        void Place(UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            row.Children.Add(e);
        }
        Place(chevron, 0);
        Place(check, 1);
        Place(icon, 2);
        Place(text, 3);
        size.VerticalAlignment = VerticalAlignment.Center;
        Place(size, 4);
        // The checkbox handles its own mouse-up, so only clicks elsewhere on the row expand it.
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (!_expanded.Add(group)) _expanded.Remove(group);
            Rebuild();
        };

        if (!expanded) return row;
        var body = Ui.Stack(2);
        body.Margin = new Thickness(40, 2, 0, 6);
        if (group == DisplayGroup.Leftovers) body.Children.Add(CleanerUi.Small(S.LeftoversNote, "Orange", 11));
        if (group == DisplayGroup.LoginItems) body.Children.Add(CleanerUi.Small(S.LoginItemsNote, "TextSecondary", 11));
        foreach (var item in items) Ui.Append(body, 2, ItemRow(item));
        return Ui.Stack(0, row, body);
    }

    private FrameworkElement ItemRow(CleanerItem item)
    {
        var check = CleanerUi.Check(item.Include, v => _cleaner.SetInclude(item, v), enabled: !item.IsBlocked);
        void Refresh() => check.IsChecked = item.Include;
        _refreshers.Add(Refresh);
        var name = Ui.Text(item.Name, 12, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = item.IsBlocked
            ? Ui.Text(string.Format(S.CloseToCleanFormat, item.BlockedBy), 10.5, brush: "Orange", wrap: false)
            : Ui.Text(SubLine(item), 10.5, brush: "TextTertiary", wrap: false);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Stack(1, name, sub);
        text.Margin = new Thickness(10, 0, 10, 0);
        var size = Ui.Text(ByteFormat.Format(item.Size), 11, brush: "TextSecondary", wrap: false);
        size.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { Background = CleanerUi.Transparent, Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(check);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        if (item.Kind is CleanerItemKind.Path or CleanerItemKind.FolderContents)
        {
            var menu = new ContextMenu();
            var reveal = new MenuItem { Header = S.Reveal };
            reveal.Click += (_, _) => CleanerUi.Reveal(item.Path);
            menu.Items.Add(reveal);
            row.ContextMenu = menu;
            row.ToolTip = item.Path;
        }
        return row;
    }

    private string SubLine(CleanerItem item) => item.Kind switch
    {
        CleanerItemKind.RunValue => item.Detail,
        CleanerItemKind.RecycleBin => item.Detail,
        _ => CleanerRules.PrettyPath(System.IO.Path.GetDirectoryName(item.Path) ?? item.Path, _paths),
    };

    private FrameworkElement ResultsFooter()
    {
        var selected = Ui.Text(string.Empty, 12, brush: "TextSecondary", wrap: false);
        var clean = Ui.Button(string.Empty, () => _ = CleanClicked(), accent: true);
        clean.Padding = new Thickness(18, 7, 18, 7);
        void Refresh()
        {
            selected.Text = string.Format(S.SelectedFormat, _cleaner.SelectedCount, _cleaner.Items.Count);
            clean.Content = string.Format(S.CleanSizeFormat, ByteFormat.Format(_cleaner.SelectedSize));
            clean.IsEnabled = _cleaner.SelectedCount > 0;
        }
        Refresh();
        _refreshers.Add(Refresh);
        var cancel = Ui.Button(S.Cancel, _cleaner.Reset);
        var right = Ui.Row(8, cancel, clean);
        var row = Ui.Split(selected, right);
        row.Margin = new Thickness(16, 12, 16, 12);
        return row;
    }

    private async Task CleanClicked()
    {
        if (!_interactive) return;
        var includesTrash = _cleaner.Items.Any(i => i.Include && i.Kind == CleanerItemKind.RecycleBin);
        if (includesTrash && CleanerUi.Ask(Window.GetWindow(this), S.ConfirmTrashTitle, S.ConfirmTrashBody, S.CatTrash, S.Cancel) != 0) return;
        await _cleaner.CleanSelectedAsync();
    }

    // ---------------------------------------------------------------- groups

    internal static string GroupTitle(DisplayGroup g) => g switch
    {
        DisplayGroup.LoginItems => S.CatLoginItems,
        DisplayGroup.Temp => S.CatTemp,
        DisplayGroup.SafeCaches => S.CatCaches,
        DisplayGroup.Logs => S.CatLogs,
        DisplayGroup.Developer => S.CatDeveloper,
        DisplayGroup.Leftovers => S.CatLeftovers,
        DisplayGroup.OtherCaches => S.CatOtherCaches,
        DisplayGroup.SystemCaches => S.CatSystemCaches,
        DisplayGroup.DeviceBackups => S.CatDeviceBackups,
        DisplayGroup.Screenshots => S.CatScreenshots,
        DisplayGroup.Trash => S.CatTrash,
        _ => g.ToString(),
    };

    internal static string GroupCaption(DisplayGroup g) => g switch
    {
        DisplayGroup.LoginItems => S.LoginItemsCaption,
        DisplayGroup.Temp => string.Format(S.TempCaptionFormat,
            CleanerRules.SanitizedTempAgeDays(Settings.GetInt(CleanerKeys.TempAgeDays, CleanerRules.DefaultTempAgeDays))),
        DisplayGroup.SafeCaches => S.CachesCaption,
        DisplayGroup.Logs => S.LogsCaption,
        DisplayGroup.Developer => S.DeveloperCaption,
        DisplayGroup.Leftovers => S.LeftoversCaption,
        DisplayGroup.OtherCaches => S.OtherCachesCaption,
        DisplayGroup.SystemCaches => S.SystemCachesCaption,
        DisplayGroup.DeviceBackups => S.DeviceBackupsCaption,
        DisplayGroup.Screenshots => string.Format(S.ScreenshotsCaptionFormat,
            CleanerRules.SanitizedScreenshotAgeDays(Settings.GetInt(CleanerKeys.ScreenshotAgeDays, CleanerRules.DefaultScreenshotAgeDays))),
        DisplayGroup.Trash => S.TrashNote,
        _ => string.Empty,
    };

    internal static string GroupGlyph(DisplayGroup g) => g switch
    {
        DisplayGroup.LoginItems => CleanerGlyphs.Power,
        DisplayGroup.Temp => CleanerGlyphs.History,
        DisplayGroup.SafeCaches => CleanerGlyphs.Archive,
        DisplayGroup.Logs => CleanerGlyphs.Diagnostic,
        DisplayGroup.Developer => CleanerGlyphs.DevTools,
        DisplayGroup.Leftovers => CleanerGlyphs.Puzzle,
        DisplayGroup.OtherCaches => CleanerGlyphs.Drive,
        DisplayGroup.SystemCaches => CleanerGlyphs.Sync,
        DisplayGroup.DeviceBackups => CleanerGlyphs.Phone,
        DisplayGroup.Screenshots => CleanerGlyphs.Camera,
        DisplayGroup.Trash => CleanerGlyphs.Trash,
        _ => CleanerGlyphs.Page,
    };
}
