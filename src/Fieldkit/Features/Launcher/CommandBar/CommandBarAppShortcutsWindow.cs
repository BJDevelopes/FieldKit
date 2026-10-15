// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// App shortcuts (port of upstream CommandBarAppShortcutsView): every app with the name it answers to,
/// its own global shortcut (works even when the bar is closed) and whether it is pinned.
/// </summary>
public sealed class CommandBarAppShortcutsWindow : ToolWindow
{
    private const string WindowKey = "commandBar.appShortcuts";

    private enum Filter { All, Pinned, Shortcuts }

    private readonly TextBox _search = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _message;
    private Filter _filter = Filter.All;
    private string? _recordingKey;
    private IDisposable? _hotkeySuspension;

    private static CommandBarService? Service => FeatureRuntime.Service<CommandBarService>();

    private CommandBarAppShortcutsWindow() : base(CommandBarStrings.AppCenterTitle, 820, 600)
    {
        var placeholder = Ui.Text(CommandBarStrings.SearchAppsPlaceholder, 13, brush: "TextTertiary", wrap: false);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var searchHost = new Grid { MinWidth = 280 };
        searchHost.Children.Add(_search);
        searchHost.Children.Add(placeholder);
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Render();
        };

        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (filter, label) in new[] { (Filter.All, CommandBarStrings.CategoryAll), (Filter.Pinned, CommandBarStrings.PinnedTitle), (Filter.Shortcuts, CommandBarStrings.AppShortcutsFilter) })
        {
            var button = new RadioButton { GroupName = "commandBarAppFilter", Content = label, IsChecked = filter == Filter.All, Margin = new Thickness(0, 0, 2, 0) };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            var value = filter;
            button.Checked += (_, _) =>
            {
                _filter = value;
                Render();
            };
            filters.Children.Add(button);
        }

        _message = Ui.Caption(CommandBarStrings.ShortcutCaptureHint, 12);
        var done = Ui.Button(CommandBarStrings.DoneButton, Close, accent: true);

        var header = new Grid { Margin = new Thickness(12, 0, 12, 4) };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(170), new GridLength(200), new GridLength(70) })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        AddCell(header, Ui.Text(CommandBarStrings.SourceApps, 12, FontWeights.SemiBold, "TextSecondary"), 0);
        AddCell(header, Ui.Text(CommandBarStrings.AppAliasLabel, 12, FontWeights.SemiBold, "TextSecondary"), 1);
        AddCell(header, Ui.Text(CommandBarStrings.AppShortcutLabel, 12, FontWeights.SemiBold, "TextSecondary"), 2);
        AddCell(header, Ui.Text(CommandBarStrings.PinnedTitle, 12, FontWeights.SemiBold, "TextSecondary"), 3);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = Ui.Stack(6, Ui.Title(CommandBarStrings.AppCenterTitle, 22), Ui.Caption(CommandBarStrings.AppCenterCaption, 13));
        top.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(top);
        var controls = Ui.Row(12, searchHost, filters);
        controls.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);
        Grid.SetRow(header, 2);
        root.Children.Add(header);
        var listCard = Ui.Card(scroll, new Thickness(4));
        Grid.SetRow(listCard, 3);
        root.Children.Add(listCard);
        var footer = Ui.Split(_message, done);
        footer.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            if (_recordingKey is not null) Service?.SuspendHotkeys(false);
            _hotkeySuspension?.Dispose();
            _hotkeySuspension = null;
        };
        void OnAvailability()
        {
            if (!FeatureCatalog.IsAvailable(CommandBarKeys.FeatureId)) Close();
        }
        FeatureRuntime.AvailabilityChanged += OnAvailability;
        Closed += (_, _) => FeatureRuntime.AvailabilityChanged -= OnAvailability;
        if (Service is { } service)
        {
            void OnState() => Dispatcher.BeginInvoke(Render);
            service.StateChanged += OnState;
            Closed += (_, _) => service.StateChanged -= OnState;
            service.LoadApps();
        }
        Render();
    }

    public static void ShowWindow() => ShowSingle(WindowKey, () => new CommandBarAppShortcutsWindow());

    private static void AddCell(Grid grid, FrameworkElement element, int column)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void Render()
    {
        _list.Children.Clear();
        var service = Service;
        var shortcuts = CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty));
        var aliases = CommandBarPreferences.DecodeAliases(Settings.GetString(CommandBarKeys.Aliases, string.Empty));
        var pins = CommandBarPreferences.DecodePins(Settings.GetString(CommandBarKeys.Pins, string.Empty)).ToHashSet(StringComparer.Ordinal);
        var query = CommandBarSearch.Normalize(_search.Text);
        var apps = (service?.Apps ?? []).GroupBy(a => CommandBarRowShortcuts.AppKey(a.Id)).Select(g => g.First())
            .Where(a =>
            {
                var key = CommandBarRowShortcuts.AppKey(a.Id);
                var included = _filter == Filter.All || (_filter == Filter.Pinned && pins.Contains(key)) || (_filter == Filter.Shortcuts && shortcuts.ContainsKey(key));
                return included && (query.Length == 0 || CommandBarSearch.Normalize(a.Name + " " + aliases.GetValueOrDefault(key)).Contains(query, StringComparison.Ordinal));
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (apps.Count == 0)
        {
            var empty = Ui.Caption(service?.AppsLoading == true ? CommandBarStrings.StillLooking : CommandBarStrings.NoResultsTitle, 13);
            empty.Margin = new Thickness(12, 16, 12, 16);
            _list.Children.Add(empty);
            return;
        }
        foreach (var app in apps) _list.Children.Add(AppRow(app, shortcuts, aliases, pins));
    }

    private FrameworkElement AppRow(CommandBarApp app, Dictionary<string, Shortcut> shortcuts, Dictionary<string, string> aliases, HashSet<string> pins)
    {
        var key = CommandBarRowShortcuts.AppKey(app.Id);
        var grid = new Grid { Margin = new Thickness(8, 3, 8, 3), MinHeight = 34 };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(170), new GridLength(200), new GridLength(70) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var icon = CommandBarIcons.For(CommandBarIcons.AppsFolder(app.Id));
        var iconElement = AppIcons.Element(icon, 24, CommandBarGlyphs.Apps);
        var name = Ui.Text(app.Name, 13, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = app.Name;
        var nameCell = new DockPanel();
        iconElement.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(iconElement, Dock.Left);
        nameCell.Children.Add(iconElement);
        nameCell.Children.Add(name);
        AddCell(grid, nameCell, 0);

        var alias = new TextBox { Text = aliases.GetValueOrDefault(key) ?? string.Empty, Margin = new Thickness(6, 0, 10, 0), ToolTip = CommandBarStrings.AliasPlaceholder };
        void SaveAlias()
        {
            var current = CommandBarPreferences.DecodeAliases(Settings.GetString(CommandBarKeys.Aliases, string.Empty));
            var typed = alias.Text.Trim();
            if (typed == (current.GetValueOrDefault(key) ?? string.Empty)) return;
            if (typed.Length > 0 && CommandBarPreferences.RowUsingAlias(typed, current, key) is { } owner)
            {
                Report(app.Name, string.Format(CommandBarStrings.AliasTakenFormat, Service?.TitleFor(owner) ?? owner));
                alias.Text = current.GetValueOrDefault(key) ?? string.Empty;
                return;
            }
            Settings.Set(CommandBarKeys.Aliases, CommandBarPreferences.EncodeAliases(CommandBarPreferences.SettingAlias(typed, key, current)));
            Report(app.Name, null);
        }
        alias.LostKeyboardFocus += (_, _) => SaveAlias();
        alias.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SaveAlias();
        };
        AddCell(grid, alias, 1);

        var shortcut = shortcuts.TryGetValue(key, out var s) ? s : (Shortcut?)null;
        var recording = _recordingKey == key;
        var recorder = new Button
        {
            Content = recording ? CommandBarStrings.ShortcutPressKeys : shortcut?.ToString() ?? CommandBarStrings.AppShortcutRecord,
            MinWidth = 140,
            Padding = new Thickness(8, 4, 8, 4),
        };
        recorder.Click += (_, _) =>
        {
            if (_recordingKey is null) Service?.SuspendHotkeys(true);
            _hotkeySuspension ??= Interop.Hotkeys.SuspendAll();
            _recordingKey = key;
            _message.Text = CommandBarStrings.ShortcutCaptureHint;
            _message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Render();
        };
        var refused = Service?.RefusedRowShortcutKeys.Contains(key) == true;
        var shortcutCell = Ui.Row(4, recorder,
            refused ? Ui.Icon(Glyphs.Warning, 13, "Orange") : null,
            shortcut is null ? null : Ui.IconButton(Glyphs.Close, CommandBarStrings.ActionShortcutRemove, () => SetShortcut(app.Name, key, null)));
        AddCell(grid, shortcutCell, 2);
        if (recording) Dispatcher.BeginInvoke(() => recorder.Focus());

        var pinned = pins.Contains(key);
        var pin = Ui.IconButton(pinned ? CommandBarGlyphs.Pin : CommandBarGlyphs.Unpin, pinned ? CommandBarStrings.ActionUnpin : CommandBarStrings.ActionPin, () =>
        {
            var next = CommandBarPreferences.TogglingPin(key, CommandBarPreferences.DecodePins(Settings.GetString(CommandBarKeys.Pins, string.Empty)));
            Settings.Set(CommandBarKeys.Pins, CommandBarPreferences.EncodePins(next));
            Render();
        }, 14, pinned ? "Accent" : "TextTertiary");
        pin.HorizontalAlignment = HorizontalAlignment.Center;
        AddCell(grid, pin, 3);
        return grid;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingKey is not { } key) return;
        e.Handled = true;
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        var appName = Service?.TitleFor(key) ?? key;
        if (pressed == Key.Escape)
        {
            StopRecording();
            return;
        }
        if (pressed is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            StopRecording();
            SetShortcut(appName, key, null);
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(pressed);
        if (Shortcut.IsModifierKey(vk)) return;
        var mods = Modifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Modifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Modifiers.Win;
        var shortcut = new Shortcut(mods, vk);
        if (!CommandBarRowShortcuts.IsUsable(shortcut))
        {
            Report(appName, CommandBarStrings.ShortcutInvalid);
            return;
        }
        StopRecording();
        SetShortcut(appName, key, shortcut);
    }

    private void StopRecording()
    {
        _recordingKey = null;
        Service?.SuspendHotkeys(false);
        _hotkeySuspension?.Dispose();
        _hotkeySuspension = null;
        Render();
    }

    private void SetShortcut(string appName, string key, Shortcut? shortcut)
    {
        var current = CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty));
        if (shortcut is { } s && CommandBarRowShortcuts.AssignmentIssue(s, key, current) is { } issue)
        {
            if (issue.Issue == CommandBarRowShortcuts.Issue.Full)
            {
                Report(appName, string.Format(CommandBarStrings.RowShortcutsLimitFormat, CommandBarRowShortcuts.Limit));
                return;
            }
            if (issue.Issue == CommandBarRowShortcuts.Issue.Invalid)
            {
                Report(appName, CommandBarStrings.ShortcutInvalid);
                return;
            }
        }
        Settings.Set(CommandBarKeys.RowShortcuts, CommandBarRowShortcuts.Encode(CommandBarRowShortcuts.Setting(shortcut, key, current)));
        Dispatcher.BeginInvoke(() =>
        {
            Report(appName, Service?.RefusedRowShortcutKeys.Contains(key) == true ? CommandBarStrings.ShortcutUnavailable : null);
            Render();
        });
    }

    private void Report(string appName, string? error)
    {
        _message.Text = error is null ? CommandBarStrings.ShortcutCaptureHint : $"{appName}: {error}";
        _message.SetResourceReference(TextBlock.ForegroundProperty, error is null ? "TextSecondary" : "Orange");
    }
}
