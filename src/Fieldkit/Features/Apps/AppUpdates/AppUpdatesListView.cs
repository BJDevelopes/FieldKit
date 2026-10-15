// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>
/// The update list shared by the App updates window and its Settings page (port of upstream's
/// AppUpdatesListView): last-check line with Check now, selectable rows with icons and
/// "1.2 → 1.3", per-row Update/Open, Update N, update rules, incomplete-check notes and the
/// live operation card.
/// </summary>
public sealed class AppUpdatesListView : Border
{
    private readonly AppUpdatesController _c;
    private readonly Func<SheetHost?> _sheet;
    private readonly bool _compact;
    private readonly StackPanel _root = new();
    private readonly WingetOperationView _operation;
    private bool _rulesExpanded;
    private bool _laneWasRunning;

    public AppUpdatesListView(AppUpdatesController controller, Func<SheetHost?> sheet, bool compact = false)
    {
        _c = controller;
        _sheet = sheet;
        _compact = compact;
        _operation = new WingetOperationView(controller.Lane, compact);
        Child = _root;
        Rebuild();
        Ui.Track(this, () =>
        {
            _c.Changed += Rebuild;
            _c.Lane.StatusChanged += OnLane;
            var settings = Settings.Observe(Rebuild, WingetConsent.Key, AppUpdatesController.IncludeStoreKey);
            var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            clock.Tick += (_, _) => Rebuild();
            clock.Start();
            _laneWasRunning = _c.Lane.IsRunning;
            Rebuild();
            return new ActionDisposable(() =>
            {
                _c.Changed -= Rebuild;
                _c.Lane.StatusChanged -= OnLane;
                settings.Dispose();
                clock.Stop();
            });
        });
    }

    private void OnLane()
    {
        var running = _c.Lane.IsRunning;
        if (running == _laneWasRunning) return;
        _laneWasRunning = running;
        Rebuild();
    }

    private bool StoreCoverageIncomplete => AppUpdatesController.IncludeStore && _c.StoreNotChecked && !WingetConsent.Accepted;

    private bool CoverageIncomplete => _c.HasCheckedThisSession && !_c.IsChecking && (StoreCoverageIncomplete || _c.CheckFailed);

    private void Rebuild()
    {
        // The operation card keeps its own state; detach it before clearing so it can be re-added.
        _root.Children.Clear();
        var gap = _compact ? 8.0 : 12.0;
        Ui.Append(_root, gap, SummaryRow());
        if (_c.Items.Count == 0)
        {
            Ui.Append(_root, gap, EmptyState());
        }
        else
        {
            if (_c.SelectableCount > 0) Ui.Append(_root, gap, SelectionBar());
            Ui.Append(_root, gap, List());
            if (_c.SelectableCount > 0) Ui.Append(_root, gap, UpdateButton());
        }
        if (_c.Rules.Count > 0) Ui.Append(_root, gap, RulesSection());
        if (CoverageIncomplete) Ui.Append(_root, gap, IncompleteCheck());
        _operation.Margin = new Thickness(0);
        Ui.Append(_root, gap, _operation);
        if (!string.IsNullOrWhiteSpace(_c.LastError) && !_c.IsChecking)
        {
            var error = Ui.Row(8, Ui.Icon(Glyphs.Warning, 13, "Orange"), Ui.Text(_c.LastError!, _compact ? 11.5 : 12, brush: "Orange"));
            Ui.Append(_root, gap, error);
        }
    }

    private FrameworkElement SummaryRow()
    {
        var line = Ui.Text(AppUpdateRules.LastCheckLine(_c.LastCheck, DateTime.Now), _compact ? 12 : 13, brush: "TextSecondary", wrap: false);
        Button check = _c.IsChecking
            ? Ui.Button(AppUpdateStrings.Checking, () => { }, glyph: WingetGlyphs.Hourglass)
            : Ui.Button(AppUpdateStrings.CheckNow, () => _c.Check(), glyph: Glyphs.Refresh);
        check.Padding = new Thickness(10, 4, 10, 4);
        check.IsEnabled = !_c.IsBusy;
        return Ui.Split(line, check);
    }

    private FrameworkElement EmptyState()
    {
        var stack = Ui.Stack(6);
        if (_c.HasCheckedThisSession && !_c.IsChecking)
        {
            var info = CoverageIncomplete || _c.Rules.Count > 0;
            var text = CoverageIncomplete ? AppUpdateStrings.PartialUpToDate
                : _c.Rules.Count == 0 ? AppUpdateStrings.UpToDate : AppUpdateStrings.NoVisibleUpdates;
            Ui.Append(stack, 6, Ui.Row(7,
                Ui.Icon(info ? Glyphs.Info : WingetGlyphs.CheckCircle, 13, info ? "TextSecondary" : "Green"),
                Ui.Text(text, _compact ? 12.5 : 13, FontWeights.Medium, info ? "TextSecondary" : "Green", wrap: false)));
        }
        Ui.Append(stack, 6, Ui.Caption(AppUpdateStrings.CoverageNote, _compact ? 11.5 : 12));
        if (_c.HasCheckedThisSession && !_c.PackageManagerAvailable)
        {
            Ui.Append(stack, 6, Ui.Caption(AppUpdateStrings.PackageMissing, _compact ? 11.5 : 12));
            Ui.Append(stack, 6, WingetUi.Link(WingetStrings.GetAppInstaller, () =>
            {
                if (!WingetUi.OpenUri(WingetCommands.AppInstallerStoreUri)) WingetUi.OpenUri(WingetCommands.AppInstallerWebUrl);
            }, 12));
        }
        return stack;
    }

    private FrameworkElement IncompleteCheck()
    {
        var stack = Ui.Stack(4, Ui.Row(7, Ui.Icon(Glyphs.Warning, 13, "Orange"),
            Ui.Text(AppUpdateStrings.IncompleteCheck, _compact ? 12 : 12.5, FontWeights.Medium, "Orange", wrap: false)));
        if (_c.CheckFailed) Ui.Append(stack, 4, Ui.Caption(AppUpdateStrings.CheckUnavailable, _compact ? 11.5 : 12));
        if (StoreCoverageIncomplete)
        {
            Ui.Append(stack, 4, Ui.Caption(AppUpdateStrings.StoreNotChecked, _compact ? 11.5 : 12));
            Ui.Append(stack, 4, Ui.Row(16,
                WingetUi.Link(WingetStrings.ConsentReview, () => _sheet()?.Show(_sheet()!.Consent(() => _c.Check())), 12),
                WingetUi.Link(AppUpdateStrings.OpenStore, () => _c.OpenStore(null), 12)));
        }
        return stack;
    }

    private FrameworkElement SelectionBar()
    {
        var all = WingetUi.Link(AppUpdateStrings.SelectAll, _c.SelectAll, _compact ? 12 : 12.5);
        var none = WingetUi.Link(AppUpdateStrings.ClearSelection, _c.SelectNone, _compact ? 12 : 12.5);
        all.IsEnabled = _c.SelectedCount != _c.SelectableCount;
        all.Opacity = all.IsEnabled ? 1 : 0.45;
        none.IsEnabled = _c.SelectedCount > 0;
        none.Opacity = none.IsEnabled ? 1 : 0.45;
        return Ui.Row(14, all, none);
    }

    private FrameworkElement List()
    {
        var rows = Ui.Stack(_compact ? 5 : 6, _c.Items.Select(i => (UIElement)Row(i)).ToArray());
        if (!_compact) return rows;
        return new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 4 * 46 + 3 * 5,
        };
    }

    private FrameworkElement Row(AppUpdateItem item)
    {
        FrameworkElement check;
        if (item.IsSelectable)
        {
            var cb = new CheckBox { IsChecked = _c.Selection.Contains(item.Id), VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Name };
            cb.Click += (_, _) => _c.Toggle(item);
            check = cb;
        }
        else
        {
            check = new Border { Width = 26, Height = 18 }; // same footprint as the checkbox template
        }
        var icon = AppIcons.Element(_c.IconFor(item), _compact ? 24 : 28, item.Source == AppUpdateSource.Store ? WingetGlyphs.Store : Glyphs.Package);
        var name = Ui.Text(item.Name, _compact ? 12.5 : 13.5, FontWeights.Medium, wrap: false);
        var nameRow = Ui.Row(6, name, item.Source switch
        {
            AppUpdateSource.Store => WingetUi.Pill(AppUpdateStrings.StoreBadge, "Accent", 10),
            AppUpdateSource.Unknown => WingetUi.Pill(AppUpdateStrings.UnknownBadge, "Accent", 10),
            _ => null,
        });
        var versions = Ui.Text(item.VersionSummary, _compact ? 11 : 12, brush: "TextSecondary", wrap: false);
        var text = Ui.Stack(1, nameRow, versions);
        text.Margin = new Thickness(10, 0, 10, 0);

        var action = ActionButton(item);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(check);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, _compact ? 6 : 8, 10, _compact ? 6 : 8),
            ContextMenu = RowMenu(item),
        };
        row.SetResourceReference(BackgroundProperty, "HoverOverlay");
        return row;
    }

    private Button ActionButton(AppUpdateItem item)
    {
        var status = _c.Lane.Status;
        if (status is { IsActive: true } && status.Concerns(item.PackageId))
        {
            var busy = Ui.Button(WingetOperationView.PhaseText(status).Split(" · ")[^1], () => { });
            busy.IsEnabled = false;
            busy.Padding = new Thickness(10, 4, 10, 4);
            return busy;
        }
        Button b;
        switch (item.Source)
        {
            case AppUpdateSource.Unknown:
                b = Ui.Button(AppUpdateStrings.OpenApp, () => _ = _c.OpenAppAsync(item));
                b.ToolTip = _c.AppFor(item) is null ? AppUpdateStrings.OpenAppMissing : AppUpdateStrings.OpenAppHint;
                b.IsEnabled = _c.AppFor(item) is not null;
                break;
            case AppUpdateSource.Store:
                b = Ui.Button(AppUpdateStrings.UpdateOne, () => RunWithConsent(() => _c.Update(item)));
                b.ToolTip = AppUpdateStrings.StoreHint;
                b.IsEnabled = !_c.IsBusy;
                break;
            default:
                b = Ui.Button(AppUpdateStrings.UpdateOne, () => RunWithConsent(() => _c.Update(item)));
                b.ToolTip = AppUpdateStrings.UpdateOne;
                b.IsEnabled = !_c.IsBusy;
                break;
        }
        b.Padding = new Thickness(12, 4, 12, 4);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    private ContextMenu RowMenu(AppUpdateItem item)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => Ui.Guard(action);
            menu.Items.Add(mi);
        }
        if (!AppUpdateRules.IsUncomparable(item.LatestVersion))
            Add(string.Format(AppUpdateStrings.SkipVersionFormat, item.LatestVersion), () => _c.SkipVersion(item), !_c.IsBusy);
        Add(AppUpdateStrings.ExcludeApp, () => _c.ExcludeApp(item), !_c.IsBusy);
        if (_c.AppFor(item) is not null) Add(AppUpdateStrings.OpenApp, () => _ = _c.OpenAppAsync(item));
        if (item.Source == AppUpdateSource.Store) Add(AppUpdateStrings.OpenInStore, () => _c.OpenStore(item));
        return menu;
    }

    private FrameworkElement UpdateButton()
    {
        var b = Ui.Button(string.Format(AppUpdateStrings.UpdateSelectedFormat, _c.SelectedCount), ConfirmSelected, accent: true);
        b.IsEnabled = _c.SelectedCount > 0 && !_c.IsBusy;
        b.HorizontalAlignment = _compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        b.Padding = new Thickness(16, 6, 16, 6);
        b.FontWeight = FontWeights.SemiBold;
        return b;
    }

    /// <summary>"Update N" shows the apps it will update before anything runs.</summary>
    private void ConfirmSelected()
    {
        var selected = _c.Items.Where(i => i.IsSelectable && _c.Selection.Contains(i.Id)).ToList();
        if (selected.Count == 0) return;
        RunWithConsent(() =>
        {
            var sheet = _sheet();
            if (sheet is null)
            {
                _c.UpdateSelected();
                return;
            }
            var rows = selected.Take(12).Select(i =>
            {
                var v = Ui.Text(i.VersionSummary, 12.5, brush: "TextSecondary", wrap: false);
                return (UIElement)Ui.Split(Ui.Text(i.Name, 12.5, wrap: false), v);
            }).ToList();
            if (selected.Count > 12) rows.Add(Ui.Caption($"+{selected.Count - 12}", 12));
            var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = Ui.Stack(6, rows.ToArray()) };
            card.SetResourceReference(BackgroundProperty, "ControlBackground");
            sheet.Show(sheet.Confirmation(AppUpdateStrings.ConfirmTitle, AppUpdateStrings.ConfirmBody, card,
                string.Format(AppUpdateStrings.UpdateSelectedFormat, selected.Count), false, () => _c.UpdateSelected(), WingetGlyphs.ArrowUp));
        });
    }

    private void RunWithConsent(Action action)
    {
        var sheet = _sheet();
        if (sheet is null)
        {
            if (WingetConsent.Accepted) action();
            return;
        }
        sheet.WithConsent(action);
    }

    private FrameworkElement RulesSection()
    {
        var chevron = Ui.Icon(_rulesExpanded ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        var header = Ui.Row(8, chevron, Ui.Text(AppUpdateStrings.RulesTitle, _compact ? 12 : 13, FontWeights.Medium, wrap: false), WingetUi.Count(_c.Rules.Count));
        header.Background = System.Windows.Media.Brushes.Transparent;
        header.Cursor = Cursors.Hand;
        header.MouseLeftButtonUp += (_, _) =>
        {
            _rulesExpanded = !_rulesExpanded;
            Rebuild();
        };
        if (!_rulesExpanded) return header;
        var rows = _c.Rules.Select(rule =>
        {
            var left = Ui.Stack(1,
                Ui.Text(rule.Name, 12.5, FontWeights.Medium, wrap: false),
                Ui.Text(rule.Version is null ? AppUpdateStrings.ExcludedApp : string.Format(AppUpdateStrings.SkippedVersionFormat, rule.Version),
                    11.5, brush: "TextSecondary", wrap: false));
            left.ToolTip = rule.PackageId;
            var remove = Ui.Button(AppUpdateStrings.RemoveRule, () => _c.RemoveRule(rule));
            remove.Padding = new Thickness(10, 3, 10, 3);
            remove.IsEnabled = !_c.IsBusy;
            return (UIElement)Ui.Split(left, remove);
        }).ToArray();
        var body = Ui.Stack(8, [Ui.Caption(AppUpdateStrings.RulesHint, _compact ? 11.5 : 12), .. rows]);
        body.Margin = new Thickness(18, 0, 0, 0);
        return Ui.Stack(8, header, body);
    }
}
