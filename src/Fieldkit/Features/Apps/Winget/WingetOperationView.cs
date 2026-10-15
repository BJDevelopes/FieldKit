// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>
/// Progress card for the running (or just finished) winget operation: icon, title, phase and
/// elapsed time, a progress bar, the latest activity line, the terminal fallback and an
/// expandable technical log. Port of upstream's HomebrewOperationStatusView.
/// </summary>
public sealed class WingetOperationView : Border
{
    private readonly WingetOperationLane _lane;
    private readonly bool _compact;
    private readonly StackPanel _root = new();
    private readonly ContentControl _top = new();
    private readonly WingetProgressBar _bar = new(null);
    private readonly TextBlock _unknown;
    private readonly StackPanel _rest = new();
    private TextBox? _logBox;
    private TextBlock? _elapsed;
    private bool _showDetails;
    private string? _lastTargetKey;
    private string? _topSignature;
    private string? _restSignature;
    private DispatcherTimer? _clock;

    public WingetOperationView(WingetOperationLane lane, bool compact = false)
    {
        _lane = lane;
        _compact = compact;
        var gap = compact ? 7 : 9;
        _unknown = Ui.Text(WingetStrings.ProgressUnknown, compact ? 10.5 : 11.5, brush: "TextTertiary", wrap: false);
        _bar.Margin = new Thickness(0, gap, 0, 0);
        _unknown.Margin = new Thickness(0, 4, 0, 0);
        _root.Children.Add(_top);
        _root.Children.Add(_bar);
        _root.Children.Add(_unknown);
        _root.Children.Add(_rest);
        Child = _root;
        Rebuild();
        Ui.Track(this, () =>
        {
            _lane.StatusChanged += OnStatus;
            _lane.LogAppended += OnLog;
            _topSignature = _restSignature = null;
            Rebuild();
            return new ActionDisposable(() =>
            {
                _lane.StatusChanged -= OnStatus;
                _lane.LogAppended -= OnLog;
                _clock?.Stop();
            });
        });
    }

    private void OnStatus() => Rebuild();

    private void OnLog(string text)
    {
        if (_logBox is null)
        {
            // The first line makes "Show details" appear.
            if (_lane.Status is not null && _restSignature is not null && !_restSignature.EndsWith("|log")) Rebuild();
            return;
        }
        _logBox.AppendText(text);
        _logBox.ScrollToEnd();
    }

    /// <summary>Updates the card in place; sections are only rebuilt when what they show changed,
    /// so the progress animation and an open log keep running smoothly.</summary>
    private void Rebuild()
    {
        var status = _lane.Status;
        Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is null)
        {
            _top.Content = null;
            _rest.Children.Clear();
            _logBox = null;
            _elapsed = null;
            _topSignature = _restSignature = null;
            _clock?.Stop();
            return;
        }
        var key = $"{status.Action}:{status.Target?.Id}:{status.StartedAt.Ticks}";
        if (key != _lastTargetKey)
        {
            _showDetails = false;
            _lastTargetKey = key;
        }

        var topSignature = $"{key}|{Title(status)}|{(status.IsActive ? PhaseText(status) : string.Empty)}|{status.Result}";
        if (topSignature != _topSignature)
        {
            _topSignature = topSignature;
            _top.Content = Header(status);
        }

        var showBar = status.IsActive || status.Result == WingetResult.Succeeded;
        _bar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar) _bar.Value = status.IsActive ? status.Progress : 1;
        _unknown.Visibility = status.IsActive && status.Progress is null ? Visibility.Visible : Visibility.Collapsed;

        var hasLog = _lane.Log.Length > 0;
        var restSignature = $"{status.LastActivity}|{status.Result}|{_lane.TerminalCommand}|{_showDetails}|{(hasLog ? "log" : "nolog")}";
        if (restSignature != _restSignature)
        {
            _restSignature = restSignature;
            BuildRest(status, hasLog);
        }

        if (status.IsActive)
        {
            _clock ??= NewClock();
            if (IsLoaded) _clock.Start();
        }
        else
        {
            _clock?.Stop();
        }
    }

    private void BuildRest(WingetOperationStatus status, bool hasLog)
    {
        _rest.Children.Clear();
        _logBox = null;
        var gap = _compact ? 7 : 9;
        if (!string.IsNullOrWhiteSpace(status.LastActivity))
        {
            var activity = Ui.Text(status.LastActivity!, _compact ? 11 : 12, brush: "TextSecondary");
            activity.MaxHeight = _compact ? 32 : 52;
            activity.TextTrimming = TextTrimming.CharacterEllipsis;
            activity.Margin = new Thickness(0, gap, 0, 0);
            _rest.Children.Add(activity);
        }
        if (status.Result == WingetResult.NeedsTerminal)
        {
            var terminal = TerminalFallback();
            terminal.Margin = new Thickness(0, gap, 0, 0);
            _rest.Children.Add(terminal);
        }
        if (!hasLog) return;
        var toggle = Ui.Row(6,
            Ui.Icon(_showDetails ? Glyphs.ChevronUp : Glyphs.ChevronDown, 10, "TextSecondary"),
            Ui.Text(_showDetails ? WingetStrings.HideDetails : WingetStrings.ShowDetails, _compact ? 11.5 : 12, FontWeights.Medium, "TextSecondary", wrap: false));
        var toggleButton = Ui.Clickable(toggle, () =>
        {
            _showDetails = !_showDetails;
            Rebuild();
        });
        toggleButton.HorizontalAlignment = HorizontalAlignment.Left;
        toggleButton.Margin = new Thickness(0, gap, 0, 0);
        _rest.Children.Add(toggleButton);
        if (_showDetails)
        {
            var log = TechnicalLog(_lane.Log);
            log.Margin = new Thickness(0, 4, 0, 0);
            _rest.Children.Add(log);
        }
    }
    private DispatcherTimer NewClock()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_elapsed is not null && _lane.Status is { } s) _elapsed.Text = ElapsedText(s);
        };
        return timer;
    }

    private FrameworkElement Header(WingetOperationStatus status)
    {
        var size = _compact ? 24.0 : 30.0;
        var circleBg = new Border { CornerRadius = new CornerRadius(size / 2), Opacity = 0.16 };
        circleBg.SetResourceReference(Border.BackgroundProperty, IconColor(status));
        var icon = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(circleBg);
        icon.Children.Add(Ui.Icon(IconGlyph(status), _compact ? 11 : 13, IconColor(status)));

        var title = Ui.Text(Title(status), _compact ? 12.5 : 13.5, FontWeights.SemiBold);
        _elapsed = Ui.Text(ElapsedText(status), _compact ? 11 : 12, brush: "TextSecondary", wrap: false);
        var subtitle = status.IsActive
            ? Ui.Row(6, Ui.Text(PhaseText(status), _compact ? 11 : 12, brush: "TextSecondary", wrap: false),
                Ui.Text("|", _compact ? 11 : 12, brush: "TextTertiary", wrap: false), _elapsed)
            : (FrameworkElement)_elapsed;
        var text = Ui.Stack(2, title, subtitle);
        text.Margin = new Thickness(10, 0, 10, 0);

        Button action = status.IsActive
            ? Ui.Button(WingetStrings.CancelOperation, _lane.Cancel)
            : Ui.Button(WingetStrings.ClearLog, _lane.ClearLog);
        action.Padding = new Thickness(9, 3, 9, 3);
        action.FontSize = 12;
        action.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }

    private FrameworkElement TerminalFallback()
    {
        var caption = Ui.Caption(WingetStrings.TerminalFallback, _compact ? 11 : 12);
        caption.MaxWidth = 520;
        var note = Ui.Row(8, Ui.Icon(WingetGlyphs.Terminal, 13, "TextSecondary"), caption);
        var command = _lane.TerminalCommand;
        if (command is null) return note;
        var open = Ui.Button(WingetStrings.OpenTerminal, () => WingetUi.OpenTerminal(command), glyph: WingetGlyphs.Terminal);
        var copy = Ui.Button(WingetStrings.Copy, () => WingetUi.CopyText(command), glyph: Glyphs.Copy);
        open.Padding = copy.Padding = new Thickness(9, 3, 9, 3);
        return Ui.Stack(6, note, Ui.Row(8, open, copy));
    }

    private FrameworkElement TechnicalLog(string log)
    {
        var header = Ui.Split(Ui.Text(WingetStrings.TechnicalLog, _compact ? 11 : 12, FontWeights.SemiBold, "TextTertiary", wrap: false),
            Ui.IconButton(Glyphs.Copy, WingetStrings.Copy, () => WingetUi.CopyText(_lane.Log), 12));
        _logBox = new TextBox
        {
            Text = log,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = _compact ? 10.5 : 11.5,
            Height = _compact ? 70 : 110,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _logBox.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        _logBox.Loaded += (_, _) => _logBox?.ScrollToEnd();
        return Ui.Stack(4, header, _logBox);
    }

    private static string ElapsedText(WingetOperationStatus s) =>
        string.Format(WingetStrings.ElapsedFormat, WingetUi.Elapsed((s.FinishedAt ?? DateTime.Now) - s.StartedAt));

    public static string Title(WingetOperationStatus s)
    {
        var name = s.Action is WingetAction.UpgradeSelected or WingetAction.UpgradeAll ? WingetStrings.AllPackages : s.Target?.Name ?? WingetStrings.AllPackages;
        return s.Result switch
        {
            WingetResult.Running => s.Action switch
            {
                WingetAction.Install => string.Format(WingetStrings.OperationInstallFormat, name),
                WingetAction.Uninstall => string.Format(WingetStrings.OperationUninstallFormat, name),
                WingetAction.Upgrade => string.Format(WingetStrings.OperationUpgradeFormat, name),
                WingetAction.UpdateSources => WingetStrings.OperationUpdateSources,
                _ => WingetStrings.OperationUpgradeAll,
            },
            WingetResult.Succeeded => s.Action switch
            {
                WingetAction.Install => string.Format(WingetStrings.OperationInstalledFormat, name),
                WingetAction.Uninstall => string.Format(WingetStrings.OperationUninstalledFormat, name),
                WingetAction.Upgrade => string.Format(WingetStrings.OperationUpgradedFormat, name),
                WingetAction.UpdateSources => WingetStrings.OperationUpdatedSources,
                _ => WingetStrings.OperationUpgradedAll,
            },
            WingetResult.Failed => string.Format(WingetStrings.OperationFailedFormat, name),
            WingetResult.Cancelled => WingetStrings.OperationCancelled,
            _ => WingetStrings.OperationTerminal,
        };
    }

    public static string PhaseText(WingetOperationStatus s)
    {
        var phase = s.Phase switch
        {
            WingetPhase.Preparing => WingetStrings.PhasePreparing,
            WingetPhase.Downloading => WingetStrings.PhaseDownloading,
            WingetPhase.Installing => WingetStrings.PhaseInstalling,
            WingetPhase.Uninstalling => WingetStrings.PhaseUninstalling,
            WingetPhase.Upgrading => WingetStrings.PhaseUpgrading,
            WingetPhase.Finalizing => WingetStrings.PhaseFinalizing,
            _ => WingetStrings.PhaseRefreshing,
        };
        if (s.Steps > 1 && s.Target is not null)
            return $"{s.Target.Name} ({string.Format(WingetStrings.OperationStepFormat, s.Step, s.Steps)}) · {phase}";
        return phase;
    }

    private static string IconGlyph(WingetOperationStatus s) => s.Result switch
    {
        WingetResult.Running => s.Action switch
        {
            WingetAction.Install => WingetGlyphs.ArrowDown,
            WingetAction.Uninstall => Glyphs.Delete,
            WingetAction.UpdateSources => WingetGlyphs.Sync,
            _ => WingetGlyphs.ArrowUp,
        },
        WingetResult.Succeeded => Glyphs.Check,
        WingetResult.Cancelled => Glyphs.Close,
        WingetResult.NeedsTerminal => WingetGlyphs.Terminal,
        _ => Glyphs.Warning,
    };

    private static string IconColor(WingetOperationStatus s) => s.Result switch
    {
        WingetResult.Running => "Accent",
        WingetResult.Succeeded => "Green",
        WingetResult.Cancelled => "TextSecondary",
        _ => "Orange",
    };
}
