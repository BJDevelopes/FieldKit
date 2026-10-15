// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>
/// The Packages tool: search, installed list with update badges, package details and the live
/// operation card (layout of upstream's HomebrewSettings: toolbar, list on the left, details on
/// the right). Every install, update and removal goes through a confirmation sheet; uninstall
/// shows what will be removed first.
/// </summary>
public sealed class PackagesView : Border
{
    public const string SearchSourceKey = "homebrew.searchSource";

    private readonly PackageManager _m;
    private readonly SheetHost _sheet;
    private readonly ContentControl _body = new();
    private readonly TextBox _query = new();
    private readonly ContentControl _filterHost = new();
    private readonly ContentControl _summaryHost = new();
    private readonly ContentControl _bannerHost = new();
    private readonly ContentControl _listHost = new();
    private readonly ContentControl _detailHost = new();
    private readonly Button _searchButton;
    private readonly Button _refreshButton;
    private readonly Button _sourcesButton;
    private readonly FrameworkElement _main;
    private InstalledFilter _filter = InstalledFilter.All;
    private bool _laneWasRunning;

    public PackagesView(PackageManager manager)
    {
        _m = manager;
        _searchButton = Ui.Button(PackagesStrings.SearchButton, Search, glyph: Glyphs.Search);
        _refreshButton = new Button { Content = Ui.Icon(Glyphs.Refresh, 14, "TextPrimary"), Padding = new Thickness(9, 6, 9, 6) };
        _refreshButton.Click += (_, _) => Ui.Guard(() => _m.RefreshInstalled());
        _sourcesButton = Ui.Button(PackagesStrings.UpdateSources, () => Confirm(WingetAction.UpdateSources, null), glyph: WingetGlyphs.Sync);
        _main = BuildMain();

        var header = Ui.Row(10, Ui.Icon(Glyphs.Package, 17), Ui.Text(PackagesStrings.Name, 15, FontWeights.SemiBold, wrap: false));
        header.Margin = new Thickness(18, 12, 18, 12);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        var divider = Ui.Divider(0);
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);
        Grid.SetRow(_body, 2);
        root.Children.Add(_body);

        _sheet = new SheetHost(root);
        Child = _sheet;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        Rebuild();
        Ui.Track(this, () =>
        {
            _m.Changed += Rebuild;
            _m.Lane.StatusChanged += OnLaneStatus;
            var consent = Settings.Observe(Rebuild, WingetConsent.Key);
            _laneWasRunning = _m.Lane.IsRunning;
            Rebuild();
            return new ActionDisposable(() =>
            {
                _m.Changed -= Rebuild;
                _m.Lane.StatusChanged -= OnLaneStatus;
                consent.Dispose();
            });
        });
    }

    private void OnLaneStatus()
    {
        var running = _m.Lane.IsRunning;
        if (running == _laneWasRunning) return;
        _laneWasRunning = running;
        Rebuild();
    }

    // ----------------------------------------------------------------- layout

    private FrameworkElement BuildMain()
    {
        _query.MinWidth = 160;
        _query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Search();
        };
        var search = WithPlaceholder(_query, PackagesStrings.SearchPlaceholder);
        var source = SourcePicker();
        source.Margin = new Thickness(8, 0, 0, 0);
        _searchButton.Margin = new Thickness(8, 0, 0, 0);
        _refreshButton.Margin = new Thickness(8, 0, 0, 0);
        _refreshButton.ToolTip = PackagesStrings.CheckPackages;
        _sourcesButton.Margin = new Thickness(8, 0, 0, 0);

        var line1 = new Grid();
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.Children.Add(search);
        AddAt(line1, source, 1);
        AddAt(line1, _searchButton, 2);
        AddAt(line1, _refreshButton, 3);
        AddAt(line1, _sourcesButton, 4);

        var line2 = Ui.Split(_filterHost, _summaryHost);
        var toolbar = Ui.Stack(10, line1, line2, _bannerHost);
        toolbar.Margin = new Thickness(16, 14, 16, 12);

        var listScroll = new ScrollViewer
        {
            Content = _listHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10, 12, 10, 12),
        };
        var detailScroll = new ScrollViewer
        {
            Content = _detailHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 16, 18, 16),
        };
        var operation = new WingetOperationView(_m.Lane)
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 14),
        };
        operation.SetResourceReference(BorderBrushProperty, "Separator");
        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.Children.Add(detailScroll);
        Grid.SetRow(operation, 1);
        detail.Children.Add(operation);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(listScroll);
        var vertical = new Border { Width = 1 };
        vertical.SetResourceReference(Border.BackgroundProperty, "Separator");
        AddAt(columns, vertical, 1);
        AddAt(columns, detail, 2);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.Children.Add(toolbar);
        var divider = Ui.Divider(0);
        Grid.SetRow(divider, 1);
        main.Children.Add(divider);
        Grid.SetRow(columns, 2);
        main.Children.Add(columns);
        return main;
    }

    private static void AddAt(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static FrameworkElement WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = Ui.Text(placeholder, 13, brush: "TextTertiary", wrap: false);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(11, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    private FrameworkElement SourcePicker()
    {
        var combo = Ui.Choice(SearchSourceKey,
        [
            (WingetPackageRules.WingetSource, PackagesStrings.WingetSource),
            (WingetPackageRules.StoreSource, PackagesStrings.StoreSource),
            (string.Empty, PackagesStrings.AllSources),
        ], WingetPackageRules.WingetSource);
        combo.MinWidth = 150;
        return combo;
    }

    // ---------------------------------------------------------------- refresh

    private void Rebuild()
    {
        if (_m.WingetMissing)
        {
            _body.Content = WingetUi.MissingState(() => _m.RefreshInstalled());
            return;
        }
        if (!ReferenceEquals(_body.Content, _main)) _body.Content = _main;
        var busy = _m.IsBusy;
        _searchButton.IsEnabled = !busy || _m.IsSearching;
        _refreshButton.IsEnabled = !busy;
        _sourcesButton.IsEnabled = !busy;
        _filterHost.Content = FilterPicker();
        _summaryHost.Content = OutdatedSummary();
        _bannerHost.Content = Banner();
        _listHost.Content = PackageList();
        _detailHost.Content = DetailPane();
    }

    private FrameworkElement FilterPicker()
    {
        var installed = _m.Installed;
        return WingetUi.Segmented(
        [
            (nameof(InstalledFilter.All), $"{PackagesStrings.All} {installed.Count}"),
            (nameof(InstalledFilter.Winget), $"{PackagesStrings.WingetSource} {PackageRules.Count(installed, InstalledFilter.Winget)}"),
            (nameof(InstalledFilter.Store), $"{PackagesStrings.StoreSource} {PackageRules.Count(installed, InstalledFilter.Store)}"),
            (nameof(InstalledFilter.Other), $"{PackagesStrings.Other} {PackageRules.Count(installed, InstalledFilter.Other)}"),
        ], _filter.ToString(), value =>
        {
            _filter = Enum.Parse<InstalledFilter>(value);
            if (_m.Selected is { } s && !_m.SearchResults.Contains(s) && !PackageRules.Matches(s, _filter)) _m.ClearSelection();
            else Rebuild();
        }, 12);
    }

    private FrameworkElement OutdatedSummary()
    {
        if (_m.IsLoadingInstalled)
            return Ui.Row(6, Ui.Icon(WingetGlyphs.Hourglass, 12, "TextSecondary"), Ui.Text(PackagesStrings.Updates, 12.5, brush: "TextSecondary", wrap: false));
        var count = _m.OutdatedCount;
        if (count == 0)
            return Ui.Row(6, Ui.Icon(WingetGlyphs.CheckCircle, 13, "TextSecondary"), Ui.Text($"{PackagesStrings.Updates} 0", 12.5, FontWeights.Medium, "TextSecondary", wrap: false));
        var all = Ui.Button(PackagesStrings.UpgradeAll, () => Confirm(WingetAction.UpgradeAll, null), glyph: WingetGlyphs.ArrowUp);
        all.Padding = new Thickness(10, 4, 10, 4);
        all.IsEnabled = !_m.IsBusy && PackageRules.UpgradeAllTargets(_m.Installed).Count > 0;
        return Ui.Row(10,
            Ui.Row(6, Ui.Icon(WingetGlyphs.ArrowUp, 13, "Orange"), Ui.Text($"{PackagesStrings.Updates} {count}", 12.5, FontWeights.SemiBold, "Orange", wrap: false)),
            all);
    }

    private FrameworkElement? Banner()
    {
        if (!_m.StoreNeedsConsent || WingetConsent.Accepted) return null;
        var row = Ui.Split(
            Ui.Row(10, Ui.Icon(WingetGlyphs.Store, 15, "Orange"), Ui.Caption(WingetStrings.ConsentStoreNotice, 12.5)),
            WingetUi.Link(WingetStrings.ConsentReview, () => _sheet.Show(_sheet.Consent(() => _m.RefreshInstalled()))));
        return Ui.Card(row, new Thickness(12, 8, 12, 8), 10);
    }

    // ------------------------------------------------------------------- list

    private FrameworkElement PackageList()
    {
        var stack = Ui.Stack(14);
        if (_m.IsSearching)
        {
            Ui.Append(stack, 14, Section(PackagesStrings.SearchResults, null, Message(PackagesStrings.Loading, loading: true)));
        }
        else if (_m.SearchResults.Count > 0)
        {
            Ui.Append(stack, 14, Section(PackagesStrings.SearchResults, _m.SearchResults.Count,
                Ui.Stack(3, _m.SearchResults.Select(i => (UIElement)Row(i)).ToArray())));
        }
        else if (_m.HasSearched)
        {
            Ui.Append(stack, 14, Section(PackagesStrings.SearchResults, 0, Message(PackagesStrings.SearchEmpty)));
        }

        var visible = PackageRules.Visible(_m.Installed, _filter);
        UIElement installed = _m.IsLoadingInstalled && _m.Installed.Count == 0
            ? Message(PackagesStrings.Loading, loading: true)
            : visible.Count == 0
                ? Message(PackagesStrings.NoPackages)
                : Ui.Stack(3, visible.Select(i => (UIElement)Row(i)).ToArray());
        Ui.Append(stack, 14, Section(PackagesStrings.Installed, visible.Count, installed));
        return stack;
    }

    private static FrameworkElement Section(string title, int? count, UIElement content)
    {
        var header = Ui.Row(6, Ui.Text(title, 12, FontWeights.SemiBold, "TextSecondary", wrap: false), count is null ? null : WingetUi.Count(count.Value));
        header.Margin = new Thickness(6, 0, 6, 0);
        return Ui.Stack(7, header, content);
    }

    private static FrameworkElement Message(string text, bool loading = false)
    {
        var caption = Ui.Caption(text, 12);
        var row = loading ? Ui.Row(8, Ui.Icon(WingetGlyphs.Hourglass, 12, "TextSecondary"), caption) : (FrameworkElement)caption;
        row.Margin = new Thickness(6, 5, 6, 5);
        return row;
    }

    private static string RowGlyph(PackageItem item) =>
        item.Origin == WingetOrigin.Store || item.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) ? WingetGlyphs.Store
        : item.Origin == WingetOrigin.Local ? WingetGlyphs.AppWindow
        : Glyphs.Package;

    private FrameworkElement Row(PackageItem item)
    {
        var selected = _m.Selected?.Key == item.Key;
        var icon = Ui.Icon(RowGlyph(item), 13, "TextSecondary");
        icon.Width = 18;
        var name = Ui.Text(item.Name, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
        var sub = Ui.Text(item.VersionText ?? item.Id, 11, brush: "TextSecondary", wrap: false);
        var text = Ui.Stack(1, name, sub);
        text.Margin = new Thickness(8, 0, 8, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        AddAt(grid, text, 1);
        AddAt(grid, RowIndicator(item), 2);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 6, 6, 6),
            CornerRadius = new CornerRadius(7),
            Cursor = Cursors.Hand,
            ToolTip = item.Id,
        };
        if (selected)
        {
            row.SetResourceReference(BackgroundProperty, "AccentSubtle");
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.MouseEnter += (_, _) => row.SetResourceReference(BackgroundProperty, "HoverOverlay");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        row.MouseLeftButtonUp += (_, _) => _m.Select(item);
        row.ContextMenu = RowMenu(item);
        return row;
    }

    private FrameworkElement RowIndicator(PackageItem item)
    {
        var status = _m.Lane.Status;
        if (status is { IsActive: true } && status.Concerns(item.Id))
            return Ui.Icon(WingetGlyphs.Hourglass, 12, "Accent");
        var busy = _m.IsBusy || !item.CanAct;
        if (item.HasUpdate)
        {
            var b = Ui.IconButton(WingetGlyphs.ArrowUp, $"{PackagesStrings.Upgrade}: {item.VersionSummary}", () => Confirm(WingetAction.Upgrade, item), 13, "Orange");
            b.IsEnabled = !busy;
            return b;
        }
        if (item.IsInstalled) return Ui.Icon(WingetGlyphs.CheckCircle, 13, "Green");
        var install = Ui.IconButton(WingetGlyphs.ArrowDown, PackagesStrings.Install, () => Confirm(WingetAction.Install, item), 13, "Accent");
        install.IsEnabled = !busy;
        return install;
    }

    private ContextMenu RowMenu(PackageItem item)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => Ui.Guard(action);
            menu.Items.Add(mi);
        }
        var busy = _m.IsBusy || !item.CanAct;
        Add(PackagesStrings.DetailsTitle, () => _m.Select(item));
        if (item.HasUpdate) Add(PackagesStrings.Upgrade, () => Confirm(WingetAction.Upgrade, item), !busy);
        if (item.IsInstalled) Add(PackagesStrings.Uninstall, () => Confirm(WingetAction.Uninstall, item), !busy);
        else Add(PackagesStrings.Install, () => Confirm(WingetAction.Install, item), !busy);
        return menu;
    }

    // ---------------------------------------------------------------- details

    private FrameworkElement DetailPane()
    {
        var stack = Ui.Stack(14);
        if (_m.IsLoadingDetails) Ui.Append(stack, 14, Message(PackagesStrings.Loading, loading: true));
        Ui.Append(stack, 14, _m.Selected is { } item ? PackageDetail(item) : EmptyDetail());
        if (!string.IsNullOrWhiteSpace(_m.ErrorMessage))
        {
            var error = Ui.Row(8, Ui.Icon(Glyphs.Warning, 13, "Orange"), Ui.Text(_m.ErrorMessage!, 12, brush: "Orange"));
            ((FrameworkElement)error.Children[0]).VerticalAlignment = VerticalAlignment.Top;
            Ui.Append(stack, 14, error);
        }
        return stack;
    }

    private static FrameworkElement EmptyDetail() =>
        Ui.Stack(8,
            Ui.Row(10, Ui.Icon(Glyphs.Package, 18), Ui.Text(PackagesStrings.DetailsTitle, 18, FontWeights.SemiBold, wrap: false)),
            Ui.Caption(PackagesStrings.NoSelection, 13));

    private FrameworkElement PackageDetail(PackageItem item)
    {
        var details = _m.Details is { } d && string.Equals(d.Id, item.Id, StringComparison.OrdinalIgnoreCase) ? d : null;
        var app = _m.SelectedApp;
        var title = Ui.Text(item.Name, 18, FontWeights.SemiBold);
        title.MaxWidth = 460;
        var heading = Ui.Row(10, title, StatusPill(item));
        var id = Ui.Text(item.Id, 12, brush: "TextSecondary");
        id.FontFamily = new FontFamily("Cascadia Mono, Consolas");

        var stack = Ui.Stack(12, heading, id);
        if (details is { Description.Length: > 0 })
        {
            var desc = Ui.Text(details.Description, 13);
            desc.MaxWidth = 560;
            desc.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Append(stack, 12, desc);
        }
        else if (!item.HasSource && item.IsInstalled)
        {
            Ui.Append(stack, 12, Ui.Caption(PackagesStrings.LocalDetails, 12.5));
        }

        var rows = new List<UIElement?>
        {
            DetailRow(PackagesStrings.Version, item.InstalledVersion ?? item.LatestVersion ?? details?.Version ?? "-"),
        };
        var latest = item.Available ?? (item.IsInstalled ? null : item.LatestVersion ?? details?.Version);
        if (item.HasUpdate && latest is not null) rows.Add(DetailRow(PackagesStrings.LatestVersion, latest));
        rows.Add(DetailRow(PackagesStrings.Source, PackageRules.SourceLabel(item.Source)));
        var publisher = details?.Publisher is { Length: > 0 } p ? p : app?.Publisher ?? string.Empty;
        if (publisher.Length > 0) rows.Add(DetailRow(PackagesStrings.Publisher, publisher));
        if (details?.License is { Length: > 0 } license) rows.Add(DetailRow(PackagesStrings.License, license));
        if (app is { InstallLocation.Length: > 0 }) rows.Add(DetailRow(PackagesStrings.InstallLocation, app.InstallLocation));
        var links = new List<UIElement>();
        if (details?.Homepage is { Length: > 0 } home && IsWebLink(home))
            links.Add(WingetUi.Link(PackagesStrings.Homepage, () => WingetUi.OpenUri(home), 12.5));
        if (details?.ReleaseNotesUrl is { Length: > 0 } notes && IsWebLink(notes))
            links.Add(WingetUi.Link(PackagesStrings.ReleaseNotes, () => WingetUi.OpenUri(notes), 12.5));
        if (links.Count > 0) rows.Add(Ui.Row(16, links.ToArray()));
        Ui.Append(stack, 12, Ui.Stack(7, rows.ToArray()));

        if (item.IsPinned)
            Ui.Append(stack, 12, Ui.Row(8, Ui.Icon(Glyphs.Pin, 12, "TextSecondary"), Ui.Caption(PackagesStrings.PinnedNote, 12)));
        if (!item.CanAct) Ui.Append(stack, 12, Ui.Caption(PackagesStrings.NotActionable, 12));
        Ui.Append(stack, 12, DetailActions(item));
        return stack;
    }

    private static bool IsWebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static FrameworkElement DetailRow(string title, string value)
    {
        var v = Ui.Text(value, 12.5);
        v.TextAlignment = TextAlignment.Right;
        v.MaxWidth = 380;
        return Ui.Split(Ui.Text(title, 12.5, brush: "TextSecondary", wrap: false), v);
    }

    private FrameworkElement StatusPill(PackageItem item)
    {
        var status = _m.Lane.Status;
        if (status is { IsActive: true } && status.Concerns(item.Id))
            return WingetUi.Pill(WingetOperationView.PhaseText(status), "Accent");
        if (item.HasUpdate) return WingetUi.Pill(PackagesStrings.UpdateAvailableBadge, "Orange");
        return item.IsInstalled
            ? WingetUi.Pill(PackagesStrings.InstalledBadge, "Green")
            : WingetUi.Pill(PackagesStrings.NotInstalledBadge, "TextSecondary");
    }

    private FrameworkElement DetailActions(PackageItem item)
    {
        var status = _m.Lane.Status;
        if (status is { IsActive: true } && status.Concerns(item.Id))
            return Ui.Row(8, Ui.Icon(WingetGlyphs.Hourglass, 13, "TextSecondary"),
                Ui.Text(WingetOperationView.PhaseText(status), 13, FontWeights.SemiBold, "TextSecondary", wrap: false));
        var enabled = !_m.IsBusy && item.CanAct;
        var buttons = new List<UIElement>();
        if (item.IsInstalled)
        {
            if (item.HasUpdate) buttons.Add(Ui.Button(PackagesStrings.Upgrade, () => Confirm(WingetAction.Upgrade, item), accent: true, glyph: WingetGlyphs.ArrowUp));
            buttons.Add(WingetUi.DestructiveButton(PackagesStrings.Uninstall, () => Confirm(WingetAction.Uninstall, item), Glyphs.Delete));
        }
        else
        {
            buttons.Add(Ui.Button(PackagesStrings.Install, () => Confirm(WingetAction.Install, item), accent: true, glyph: WingetGlyphs.ArrowDown));
        }
        foreach (var b in buttons) ((Button)b).IsEnabled = enabled;
        return Ui.Row(8, buttons.ToArray());
    }

    // ---------------------------------------------------------------- actions

    private void Search()
    {
        var query = _query.Text;
        var source = Settings.GetString(SearchSourceKey, WingetPackageRules.WingetSource);
        void Run()
        {
            _m.ClearSelection();
            _m.Search(query, source);
        }
        if (query.Trim().Length > 0 && PackageRules.SearchNeedsConsent(source)) _sheet.WithConsent(Run);
        else Run();
    }

    /// <summary>Every action asks first; the agreement sheet comes before the first one that needs it.</summary>
    private void Confirm(WingetAction action, PackageItem? item)
    {
        if (_m.IsBusy && action != WingetAction.UpdateSources) return;
        if (action == WingetAction.UpdateSources)
        {
            ShowConfirmation(action, item, null);
            return;
        }
        _sheet.WithConsent(() => _ = ConfirmAsync(action, item));
    }

    private async Task ConfirmAsync(WingetAction action, PackageItem? item)
    {
        try
        {
            UIElement? detail = action switch
            {
                WingetAction.Uninstall when item is not null => await UninstallReview(item),
                WingetAction.UpgradeAll => UpgradeAllReview(),
                _ when item is not null => Summary(item),
                _ => null,
            };
            ShowConfirmation(action, item, detail);
        }
        catch (Exception ex)
        {
            Log.Error("Preparing the package confirmation failed", ex);
        }
    }

    private void ShowConfirmation(WingetAction action, PackageItem? item, UIElement? detail)
    {
        var glyph = action switch
        {
            WingetAction.Install => WingetGlyphs.ArrowDown,
            WingetAction.Uninstall => Glyphs.Delete,
            WingetAction.UpdateSources => WingetGlyphs.Sync,
            _ => WingetGlyphs.ArrowUp,
        };
        _sheet.Show(_sheet.Confirmation(PackageRules.ConfirmTitle(action), PackageRules.ConfirmBody(action, item), detail,
            PackageRules.ActionTitle(action), action == WingetAction.Uninstall, () => _m.Perform(action, item), glyph));
    }

    private static FrameworkElement InfoCard(string? heading, IEnumerable<UIElement?> rows, string? note = null)
    {
        var stack = Ui.Stack(6, rows.ToArray());
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return Ui.Stack(6,
            heading is null ? null : Ui.Text(heading, 12, FontWeights.SemiBold, "TextSecondary", wrap: false),
            card,
            note is null ? null : Ui.Caption(note, 12));
    }

    private static FrameworkElement Summary(PackageItem item)
    {
        var version = item.HasUpdate ? item.VersionSummary : item.LatestVersion ?? item.InstalledVersion ?? "-";
        return InfoCard(null,
        [
            DetailRow(PackagesStrings.Details, item.Name),
            DetailRow("Id", item.Id),
            DetailRow(PackagesStrings.Version, version),
            DetailRow(PackagesStrings.Source, PackageRules.SourceLabel(item.Source)),
        ]);
    }

    private async Task<FrameworkElement> UninstallReview(PackageItem item)
    {
        var app = await _m.FindAppAsync(item);
        return UninstallReviewCard(item, app);
    }

    /// <summary>What an uninstall will remove: the package and, when found, the app's folder and size.</summary>
    public static FrameworkElement UninstallReviewCard(PackageItem item, InstalledApp? app)
    {
        var rows = new List<UIElement?>
        {
            DetailRow(PackagesStrings.Details, item.Name),
            DetailRow("Id", item.Id),
            DetailRow(PackagesStrings.Version, item.InstalledVersion ?? "-"),
            DetailRow(PackagesStrings.Source, PackageRules.SourceLabel(item.Source)),
        };
        if (app is not null)
        {
            if (app.Publisher.Length > 0) rows.Add(DetailRow(PackagesStrings.Publisher, app.Publisher));
            if (app.InstallLocation.Length > 0) rows.Add(DetailRow(PackagesStrings.InstallLocation, app.InstallLocation));
            if (app.EstimatedSizeBytes > 0) rows.Add(DetailRow(PackagesStrings.Size, ByteFormat.Format(app.EstimatedSizeBytes)));
        }
        return InfoCard(PackagesStrings.WhatWillBeRemoved, rows, PackagesStrings.UninstallNote);
    }

    private FrameworkElement UpgradeAllReview()
    {
        var targets = PackageRules.UpgradeAllTargets(_m.Installed);
        var rows = targets.Take(12).Select(t => (UIElement?)DetailRow(t.Name, t.VersionSummary)).ToList();
        if (targets.Count > 12) rows.Add(Ui.Caption($"+{targets.Count - 12}", 12));
        var skipped = _m.Installed.Any(i => i.HasUpdate && i.IsPinned);
        return InfoCard($"{PackagesStrings.WillBeUpdated} ({targets.Count})", rows, skipped ? PackagesStrings.PinnedSkipped : null);
    }
}
