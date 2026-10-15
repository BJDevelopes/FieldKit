// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.UI;
using Glyphs = Fieldkit.UI.Glyphs;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The bar's face (port of upstream CommandBarView): one field, one list. Every row is icon, name in
/// plain words, where it lives, and its shortcut, so the bar teaches the faster way while being used.
/// Rebuilt from the controller on every change; the field itself is kept so typing never loses focus.
/// </summary>
public sealed class CommandBarView : Border
{
    public const double BarWidth = 560;
    public const double ListCeiling = 452;

    private readonly CommandBarController _c;
    private readonly Func<string> _hotkeyText;
    private readonly StackPanel _root = new();
    private readonly Grid _searchBar = new();
    private readonly ContentControl _modeChip = new() { Focusable = false };
    private readonly TextBlock _placeholder;
    private readonly FrameworkElement _compactHints;
    private readonly Button _clear;
    private readonly ContentControl _body = new() { Focusable = false };
    private bool _replacing;
    private Point? _lastPointer;
    private FrameworkElement? _selectedRow;

    public TextBox Field { get; }

    /// <summary>Renders the bar as it opens (suggestions) to a PNG, offscreen, with sample data.</summary>
    public static void RenderPreview(string path, bool dark) => CommandBarPreview.RenderHome(path, dark);

    /// <summary>Renders the bar with a query typed (apps, a window, a file, a setting) to a PNG.</summary>
    public static void RenderSearchPreview(string path, bool dark, string query = "note") => CommandBarPreview.RenderSearch(path, dark, query);

    /// <summary>Raised when the mark is pressed (drag to move the bar).</summary>
    public event Action? MarkPressed;

    /// <summary>Raised when the mark is double-clicked (recenter).</summary>
    public event Action? MarkDoubleClicked;

    public CommandBarView(CommandBarController controller, Func<string>? hotkeyText = null)
    {
        _c = controller;
        _hotkeyText = hotkeyText ?? (() => CommandBarKeys.ShortcutDisplay);
        Width = BarWidth;
        CornerRadius = new CornerRadius(18);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(BorderBrushProperty, "PanelBorder");
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        Field = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 16.5,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = PlainTextBoxTemplate(),
        };
        Field.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        Field.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextPrimary");
        Field.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "Accent");
        Field.TextChanged += (_, _) =>
        {
            UpdateFieldChrome();
            if (!_replacing) _c.SetQuery(Field.Text);
        };
        _placeholder = Ui.Text(CommandBarStrings.SearchPlaceholder, 16.5, brush: "TextTertiary", wrap: false);
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        var fieldHost = new Grid();
        fieldHost.Children.Add(_placeholder);
        fieldHost.Children.Add(Field);

        _compactHints = Ui.Row(4,
            Ui.Text("↓", 10, FontWeights.SemiBold, "TextTertiary", wrap: false),
            Ui.Text(CommandBarStrings.SuggestionsLabel, 10, brush: "TextTertiary", wrap: false),
            Ui.Text("Esc", 10, FontWeights.SemiBold, "TextTertiary", wrap: false));
        _clear = Ui.IconButton(CommandBarGlyphs.ClearField, CommandBarStrings.RemoveButton, () =>
        {
            Field.Text = string.Empty;
            Field.Focus();
        }, 13, "TextTertiary");
        _clear.Focusable = false;

        var mark = BrandMark(22);
        mark.Cursor = Cursors.SizeAll;
        mark.ToolTip = CommandBarStrings.DragHint;
        mark.Background = Brushes.Transparent;
        mark.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) MarkDoubleClicked?.Invoke();
            else MarkPressed?.Invoke();
            e.Handled = true;
        };

        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _searchBar.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        _searchBar.Margin = new Thickness(16, 13, 12, 13);
        _searchBar.MinHeight = 26;
        Place(_searchBar, mark, 0, new Thickness(0, 0, 10, 0));
        Place(_searchBar, _modeChip, 1, new Thickness(0, 0, 8, 0));
        Place(_searchBar, fieldHost, 2, new Thickness(0));
        Place(_searchBar, _compactHints, 3, new Thickness(8, 0, 4, 0));
        Place(_searchBar, _clear, 4, new Thickness(4, 0, 0, 0));

        _root.Children.Add(_searchBar);
        _root.Children.Add(_body);
        Child = _root;

        _c.Changed += Rebuild;
        _c.QueryReplaced += text =>
        {
            _replacing = true;
            Field.Text = text;
            Field.CaretIndex = text.Length;
            _replacing = false;
            UpdateFieldChrome();
        };
        Rebuild();
    }

    private static void Place(Grid grid, FrameworkElement element, int column, Thickness margin)
    {
        element.Margin = margin;
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static ControlTemplate PlainTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = host;
        return template;
    }

    private void UpdateFieldChrome()
    {
        _placeholder.Text = _c.Mode switch
        {
            CommandBarMode.Argument when _c.Entry(_c.ModeEntryId)?.NumericRange is { } r => string.Format(CommandBarStrings.ArgumentRangeFormat, r.Min, r.Max),
            CommandBarMode.Naming => CommandBarStrings.AliasPlaceholder,
            _ => CommandBarStrings.SearchPlaceholder,
        };
        _placeholder.Visibility = Field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = Field.Text.Length > 0 && _c.Mode == CommandBarMode.Search ? Visibility.Visible : Visibility.Collapsed;
        _compactHints.Visibility = _c.IsCompactHome ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ rebuild

    public void Rebuild()
    {
        UpdateFieldChrome();
        _modeChip.Content = _c.Mode is CommandBarMode.Naming or CommandBarMode.Argument && _c.Entry(_c.ModeEntryId) is { } chipEntry
            ? Capsule(chipEntry.Title, "Accent", "AccentSubtle", 12, FontWeights.SemiBold)
            : null;
        _modeChip.Visibility = _modeChip.Content is null ? Visibility.Collapsed : Visibility.Visible;

        var body = new StackPanel();
        _selectedRow = null;
        switch (_c.Mode)
        {
            case CommandBarMode.Search:
            {
                var chips = _c.CategoryChips.Count > 0 && (_c.IsShowingSuggestions || _c.ActiveCategory is not null);
                if (chips)
                {
                    body.Children.Add(Divider());
                    body.Children.Add(ChipsRow());
                }
                if (_c.Rows.Count == 0)
                {
                    if (_c.Query.Trim().Length > 0 || _c.ActiveCategory is not null)
                    {
                        body.Children.Add(Divider());
                        body.Children.Add(EmptyState());
                    }
                }
                else
                {
                    if (!chips) body.Children.Add(Divider());
                    body.Children.Add(ResultsList());
                }
                break;
            }
            case CommandBarMode.Argument:
                body.Children.Add(Divider());
                body.Children.Add(EntryCard(_c.Entry(_c.ModeEntryId), CommandBarStrings.ArgumentHint, null));
                break;
            case CommandBarMode.Confirm:
                body.Children.Add(Divider());
                body.Children.Add(ConfirmCard());
                break;
            case CommandBarMode.Actions:
                body.Children.Add(Divider());
                body.Children.Add(ActionsList());
                break;
            case CommandBarMode.Naming:
                body.Children.Add(Divider());
                body.Children.Add(EntryCard(_c.Entry(_c.ModeEntryId), _c.AliasWarning ?? CommandBarStrings.ArgumentHint, null, _c.AliasWarning is not null));
                break;
            case CommandBarMode.CapturingShortcut:
            {
                var entry = _c.Entry(_c.ModeEntryId);
                var existing = entry is null ? null : _c.RowShortcutText(entry);
                body.Children.Add(Divider());
                body.Children.Add(EntryCard(entry, _c.AliasWarning ?? CommandBarStrings.ShortcutCaptureHint, existing, _c.AliasWarning is not null));
                break;
            }
        }
        if (!_c.IsCompactHome)
        {
            body.Children.Add(Divider());
            body.Children.Add(Footer());
        }
        _body.Content = body;
        if (_selectedRow is not null)
        {
            var row = _selectedRow;
            row.Loaded += (_, _) => row.BringIntoView();
            if (row.IsLoaded) row.BringIntoView();
        }
    }

    private static Border Divider()
    {
        var b = new Border { Height = 1 };
        b.SetResourceReference(BackgroundProperty, "Separator");
        return b;
    }

    // ------------------------------------------------------------------ chips

    private FrameworkElement ChipsRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 9, 16, 9) };
        row.Children.Add(Chip(null, CommandBarStrings.CategoryAll));
        foreach (var source in _c.CategoryChips)
            row.Children.Add(Chip(source, CommandBarController.CategoryTitle(source)));
        return new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    private FrameworkElement Chip(CommandBarSource? source, string label)
    {
        var active = _c.ActiveCategory == source;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (source is { } s)
        {
            var icon = Ui.Icon(CommandBarPreferences.Glyph(s), 10, active ? "Accent" : "TextSecondary");
            icon.Margin = new Thickness(0, 0, 4, 0);
            content.Children.Add(icon);
        }
        content.Children.Add(Ui.Text(label, 11.5, active ? FontWeights.SemiBold : FontWeights.Normal, active ? "Accent" : "TextSecondary", wrap: false));
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(0, 0, 5, 0),
            BorderThickness = new Thickness(1),
            Child = content,
            Cursor = Cursors.Hand,
        };
        chip.SetResourceReference(BackgroundProperty, active ? "AccentSubtle" : "HoverOverlay");
        if (active) chip.SetResourceReference(BorderBrushProperty, "Accent");
        else chip.BorderBrush = Brushes.Transparent;
        chip.MouseLeftButtonUp += (_, _) => _c.SetCategory(source);
        return chip;
    }

    // ------------------------------------------------------------------ rows

    private FrameworkElement ResultsList()
    {
        var outer = new StackPanel();
        if (_c.IsShowingSuggestions && _c.ActiveCategory is null) outer.Children.Add(Examples());
        var list = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        for (var i = 0; i < _c.Rows.Count; i++)
        {
            if (_c.SectionTitles.TryGetValue(i, out var heading)) list.Children.Add(SectionHeader(heading));
            var row = Row(_c.Rows[i], i);
            list.Children.Add(row);
            if (i == _c.SelectedIndex) _selectedRow = row;
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = ListCeiling,
            VerticalScrollBarVisibility = _c.Rows.Count > 14 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        outer.Children.Add(scroll);
        return outer;
    }

    /// <summary>Examples say what the bar can do that a list of commands never does; clicking one fills the field.</summary>
    private FrameworkElement Examples()
    {
        var row = new WrapPanel { Margin = new Thickness(16, 10, 16, 0) };
        var label = Ui.Text(CommandBarStrings.TryTheseLabel.ToUpperInvariant(), 10, FontWeights.Bold, "TextTertiary", wrap: false);
        label.Margin = new Thickness(0, 0, 6, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var examples = new List<string> { "100 km to mi", "2+2*3" };
        if (CommandBarSystem.ReadBattery() is not null) examples.Add(CommandBarStrings.AnswerBatteryLabel.ToLowerInvariant());
        examples.Add(":fire");
        foreach (var example in examples)
        {
            var chip = Capsule(example, "TextSecondary", "HoverOverlay", 11, FontWeights.Normal);
            chip.Margin = new Thickness(0, 0, 5, 0);
            chip.Cursor = Cursors.Hand;
            var text = example;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                Field.Text = text;
                Field.CaretIndex = text.Length;
                Field.Focus();
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private static FrameworkElement SectionHeader(string title)
    {
        var t = Ui.Text(title.ToUpperInvariant(), 10, FontWeights.Bold, "TextTertiary", wrap: false);
        t.Margin = new Thickness(8, 7, 8, 3);
        return t;
    }

    private FrameworkElement Row(CommandBarEntry entry, int index)
    {
        var selected = index == _c.SelectedIndex;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconView(entry);
        grid.Children.Add(icon);

        var texts = new StackPanel { Margin = new Thickness(10, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(TitleView(entry));
        var subtitle = SubtitleView(entry);
        if (subtitle is not null) texts.Children.Add(subtitle);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        FrameworkElement? trailing = null;
        if (_c.ControlHeld && index < 9)
            trailing = Capsule($"Ctrl+{index + 1}", "Accent", "AccentSubtle", 11, FontWeights.SemiBold, 5);
        else if (entry.AnswerValue is { } value)
            trailing = Ui.Text(value, 13, FontWeights.SemiBold, "Accent", wrap: false);
        else if ((_c.RowShortcutText(entry) ?? entry.ShortcutText) is { } shortcut)
            trailing = Capsule(shortcut, "TextSecondary", "HoverOverlay", 11, FontWeights.SemiBold, 5);
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        var ret = Ui.Icon(CommandBarGlyphs.Return, 11, "TextTertiary");
        ret.Opacity = selected ? 1 : 0;
        Grid.SetColumn(ret, 3);
        grid.Children.Add(ret);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, entry.IsAnswer ? 9 : 6, 10, entry.IsAnswer ? 9 : 6),
            Margin = new Thickness(0, 0, 0, 1),
            Child = grid,
            Background = Brushes.Transparent,
        };
        if (selected) border.SetResourceReference(BackgroundProperty, "AccentSubtle");
        border.ToolTip = entry.IsAnswer ? null : entry.Title.Length > 60 ? entry.Title : null;
        border.MouseMove += (_, e) =>
        {
            var p = PointToScreenSafe(e);
            if (p is null || _lastPointer == p) return;
            _lastPointer = p;
            _c.Select(index);
        };
        border.MouseLeftButtonUp += (_, _) => _c.Run(entry);
        return border;
    }

    private Point? PointToScreenSafe(MouseEventArgs e)
    {
        try
        {
            return PresentationSource.FromVisual(this) is null ? e.GetPosition(this) : PointToScreen(e.GetPosition(this));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private FrameworkElement TitleView(CommandBarEntry entry)
    {
        if (entry.IsAnswer)
        {
            var t = Ui.Text(entry.Title, 18, FontWeights.SemiBold, wrap: false);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }
        var offsets = _c.HighlightOffsets(entry);
        var block = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        if (offsets.Count == 0)
        {
            block.Text = entry.Title;
            return block;
        }
        var title = entry.Title;
        var i = 0;
        while (i < title.Length)
        {
            var hit = offsets.Contains(i);
            var start = i;
            while (i < title.Length && offsets.Contains(i) == hit) i++;
            var run = new Run(title[start..i]);
            if (hit)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                run.FontWeight = FontWeights.Bold;
            }
            block.Inlines.Add(run);
        }
        return block;
    }

    private static FrameworkElement? SubtitleView(CommandBarEntry entry)
    {
        if (entry.NeedsSetupFeature is { } feature)
            return Ui.Text(string.Format(CommandBarStrings.NeedsSetupFormat, feature), 11.5, brush: "Orange", wrap: false);
        if (entry.Subtitle.Length == 0) return null;
        var t = Ui.Text(entry.Subtitle, 11.5, brush: "TextSecondary", wrap: false);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    /// <summary>A row's icon: glyphs sit on a tinted plate; app, file, window and emoji icons stand alone.</summary>
    public static FrameworkElement IconView(CommandBarEntry entry)
    {
        var host = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement content;
        switch (entry.IconKind)
        {
            case CommandBarIconKind.Shell when CommandBarIcons.For(entry.IconPath) is { } shell:
                content = Image(shell);
                break;
            case CommandBarIconKind.Window when AppIcons.ForWindow(entry.WindowHandle) is { } window:
                content = Image(window);
                break;
            case CommandBarIconKind.Image when entry.Image is not null:
                content = Image(entry.Image);
                break;
            case CommandBarIconKind.Emoji:
            {
                var emoji = new TextBlock
                {
                    Text = entry.Glyph,
                    FontSize = 19,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                emoji.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                content = emoji;
                break;
            }
            case CommandBarIconKind.Color when entry.Swatch is { } swatch:
            {
                var b = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromArgb(swatch.A8, swatch.R8, swatch.G8, swatch.B8)),
                };
                b.SetResourceReference(BorderBrushProperty, "CardBorder");
                content = b;
                break;
            }
            default:
            {
                var plate = new Border { CornerRadius = new CornerRadius(8), Child = Ui.Icon(entry.Glyph, 14, entry.IsActive ? "Accent" : "TextPrimary") };
                plate.SetResourceReference(BackgroundProperty, entry.IsActive ? "AccentSubtle" : "HoverOverlay");
                content = plate;
                break;
            }
        }
        host.Children.Add(content);
        if (entry.IsActive)
        {
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
            };
            dot.SetResourceReference(BackgroundProperty, "Green");
            dot.SetResourceReference(BorderBrushProperty, "PanelBackground");
            host.Children.Add(dot);
        }
        return host;
    }

    private static FrameworkElement Image(ImageSource source)
    {
        var img = new System.Windows.Controls.Image { Source = source, Width = 26, Height = 26, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private static Border Capsule(string text, string foreground, string background, double size, FontWeight weight, double radius = 9)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(7, 2, 7, 3),
            Child = Ui.Text(text, size, weight, foreground, wrap: false),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(BackgroundProperty, background);
        return b;
    }

    // ------------------------------------------------------------------ empty, argument, confirm, actions

    private FrameworkElement EmptyState()
    {
        var mark = BrandMark(26);
        mark.Opacity = 0.3;
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Ui.Text(CommandBarStrings.NoResultsTitle, 13, brush: "TextSecondary", wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var home = Capsule(CommandBarStrings.NoResultsAction, "Accent", "AccentSubtle", 12, FontWeights.SemiBold, 11);
        home.Cursor = Cursors.Hand;
        home.Padding = new Thickness(11, 4, 11, 5);
        home.MouseLeftButtonUp += (_, _) => _c.GoHome();
        var actions = Ui.Row(6, home);
        var query = _c.Query.Trim();
        if (query.Length > 0 && _c.ActiveCategory is null)
        {
            var web = Capsule(CommandBarStrings.WebSearchAction, "TextSecondary", "HoverOverlay", 12, FontWeights.SemiBold, 11);
            web.Cursor = Cursors.Hand;
            web.Padding = new Thickness(11, 4, 11, 5);
            web.MouseLeftButtonUp += (_, _) =>
            {
                CommandBarSystem.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
                _c.Close();
            };
            Ui.Append(actions, 6, web);
        }
        actions.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.Stack(9, mark, title, actions);
        stack.Margin = new Thickness(0, 24, 0, 24);
        return stack;
    }

    private static FrameworkElement EntryCard(CommandBarEntry? entry, string hint, string? badge, bool warning = false)
    {
        var stack = new StackPanel { Margin = new Thickness(17, 12, 17, 12) };
        if (entry is not null)
        {
            var title = Ui.Text(entry.Title, 13.5, FontWeights.Medium, wrap: false);
            title.Margin = new Thickness(10, 0, 0, 0);
            title.VerticalAlignment = VerticalAlignment.Center;
            var head = Ui.Split(Ui.Row(0, IconView(entry), title), badge is null ? null : Capsule(badge, "Accent", "AccentSubtle", 12, FontWeights.SemiBold));
            stack.Children.Add(head);
        }
        var hintText = Ui.Text(hint, 11.5, brush: warning ? "Orange" : "TextTertiary");
        hintText.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(hintText);
        return stack;
    }

    private FrameworkElement ConfirmCard()
    {
        var entry = _c.Entry(_c.ModeEntryId);
        if (entry is null) return new Border();
        var texts = Ui.Stack(2, Ui.Text(entry.ConfirmationPrompt ?? entry.Title, 13.5, FontWeights.SemiBold), Ui.Text(CommandBarStrings.ConfirmHint, 11.5, brush: "TextSecondary"));
        texts.Margin = new Thickness(10, 0, 10, 0);
        texts.VerticalAlignment = VerticalAlignment.Center;
        var cancel = Ui.Button(CommandBarStrings.CancelButton, _c.StepBack);
        cancel.Focusable = false;
        var confirm = Ui.Button(CommandBarStrings.ConfirmButton, _c.RunSelected, accent: true);
        confirm.Focusable = false;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(IconView(entry));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var buttons = Ui.Row(6, cancel, confirm);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(12),
            Child = grid,
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0x45, 0x3A)),
        };
        return card;
    }

    private FrameworkElement ActionsList()
    {
        var stack = new StackPanel();
        var header = Ui.Text(CommandBarStrings.ActionsTitle.ToUpperInvariant(), 10, FontWeights.Bold, "TextTertiary", wrap: false);
        header.Margin = new Thickness(16, 10, 16, 2);
        stack.Children.Add(header);
        var list = new StackPanel { Margin = new Thickness(8) };
        var actions = _c.ActionRows();
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var selected = i == _c.ActionIndex;
            var brush = action.IsDestructive ? "Red" : "TextPrimary";
            FrameworkElement glyph;
            if (action.Id.StartsWith("emojiSkinTone.", StringComparison.Ordinal))
            {
                // A tone is shown as its colour (WPF draws emoji without colour).
                var tone = CommandBarEmoji.ParseTone(action.Id["emojiSkinTone.".Length..]);
                glyph = new Border
                {
                    Child = new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(CommandBarSettingsPage.ToneColor(tone)) },
                };
            }
            else
            {
                glyph = Ui.Icon(action.Glyph, 14, brush);
            }
            glyph.Width = 30;
            var title = Ui.Text(action.Title, 13.5, FontWeights.Medium, brush, wrap: false);
            title.Margin = new Thickness(10, 0, 0, 0);
            var ret = Ui.Icon(CommandBarGlyphs.Return, 11, "TextTertiary");
            ret.Opacity = selected ? 1 : 0;
            var row = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 1),
                Child = Ui.Split(Ui.Row(0, glyph, title), ret),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            if (selected) row.SetResourceReference(BackgroundProperty, "AccentSubtle");
            row.MouseLeftButtonUp += (_, _) => _c.RunAction(action);
            list.Children.Add(row);
            if (selected) _selectedRow = row;
        }
        stack.Children.Add(list);
        return stack;
    }


    // ------------------------------------------------------------------ footer

    private FrameworkElement Footer()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var hotkey = _hotkeyText();
        if (hotkey.Length > 0)
        {
            var kb = Ui.Icon(Glyphs.Keyboard, 10, "TextTertiary");
            kb.Margin = new Thickness(0, 0, 5, 0);
            left.Children.Add(kb);
            left.Children.Add(Hint(hotkey, bold: true));
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(UIElement e)
        {
            if (e is FrameworkElement fe && right.Children.Count > 0) fe.Margin = new Thickness(6, 0, 0, 0);
            right.Children.Add(e);
        }
        if (_c.CanOpenActions)
        {
            Add(Hint("Ctrl+K", bold: true));
            Add(Hint(CommandBarStrings.ActionsHint, bold: false));
        }
        Add(Hint(_c.IsShowingSuggestions && _c.CategoryChips.Count > 0 ? "↑↓ ←→" : "↑↓", bold: true));
        if (_c.SelectedEntry?.Id == "math.result" && _c.Mode == CommandBarMode.Search)
        {
            Add(Hint("Tab", bold: true));
            Add(Hint(CommandBarStrings.ReuseHint, bold: false));
        }
        if (_c.SelectedEntry?.CanReveal == true && _c.Mode == CommandBarMode.Search)
        {
            Add(Hint("Ctrl+↵", bold: true));
            Add(Hint("File Explorer", bold: false));
        }
        Add(Ui.Icon(CommandBarGlyphs.Return, 10, "TextTertiary"));
        Add(Hint("Esc", bold: true));
        var grid = Ui.Split(left, right);
        grid.Margin = new Thickness(16, 8, 16, 9);
        return grid;
    }

    private static TextBlock Hint(string text, bool bold) =>
        Ui.Text(text, 10.5, bold ? FontWeights.SemiBold : FontWeights.Normal, "TextTertiary", wrap: false);

    // ------------------------------------------------------------------ the mark

    /// <summary>Fieldkit's mark (four tiles, one in the accent colour): the bar's face and its drag handle.</summary>
    public static Grid BrandMark(double size)
    {
        var grid = new Grid { Width = size, Height = size, Opacity = 0.85 };
        var gap = size * 0.12;
        var tile = (size - gap) / 2;
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 2; col++)
        {
            var b = new Border
            {
                Width = tile,
                Height = tile,
                CornerRadius = new CornerRadius(tile * 0.3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(col * (tile + gap), row * (tile + gap), 0, 0),
            };
            b.SetResourceReference(BackgroundProperty, row == 0 && col == 1 ? "Accent" : "TextPrimary");
            grid.Children.Add(b);
        }
        return grid;
    }
}
