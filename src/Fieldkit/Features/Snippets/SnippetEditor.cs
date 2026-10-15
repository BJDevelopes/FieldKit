// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.UI;

namespace Fieldkit.Features.Snippets;

/// <summary>The New / Edit snippet sheet (upstream SnippetEditor) and its date/time builder.</summary>
public static class SnippetEditor
{
    public const string VariablesHint = "Variables: {{date}}, {{time}}, {{datetime}}, {{clipboard}}, {{cursor}}";
    public const string VariablesCaption = "They become the date, the time and the copied text at the moment of expansion; {{cursor}} is where the cursor ends up.";
    public const string FormatCaption = "A format after a colon picks how they look, like {{date:yyyy-MM-dd}}, or use the date/time button above. A -tz(...) part sets the timezone, like {{date-tz(America/New_York):yyyy-MM-dd}}.";

    /// <summary>Shows the editor modally; <paramref name="save"/> or <paramref name="delete"/> run when the user picks them.</summary>
    public static void Show(Window? owner, TextSnippet snippet, bool isNew, IReadOnlyList<TextSnippet> others,
        Action<TextSnippet> save, Action? delete)
    {
        var window = new FloatingWindow { HideOnDeactivate = false, Width = 470, SizeToContent = SizeToContent.Height, Title = isNew ? "New snippet" : "Edit snippet" };
        var name = new TextBox { Text = snippet.Name };
        var trigger = new TextBox { Text = snippet.Trigger, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        var expansion = new ComboBox { MinWidth = 220 };
        expansion.Items.Add(new ComboBoxItem { Content = "After space, Tab or Enter", Tag = SnippetExpansion.AfterDelimiter });
        expansion.Items.Add(new ComboBoxItem { Content = "Right away", Tag = SnippetExpansion.Immediate });
        expansion.SelectedIndex = snippet.Expansion == SnippetExpansion.Immediate ? 1 : 0;
        var ignoreCase = new CheckBox { Content = Ui.Text("Ignore capitalization", 13), IsChecked = snippet.IgnoresCase };
        var folder = new TextBox { Text = snippet.Folder };
        var suggestions = SnippetEngine.FolderSuggestions(others);
        var folderMenu = Ui.IconButton(Glyphs.Folder, "Folders", () => { }, 13);
        folderMenu.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        folderMenu.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = folderMenu, Placement = PlacementMode.Bottom };
            foreach (var s in suggestions)
            {
                var item = new MenuItem { Header = s };
                item.Click += (_, _) => folder.Text = s;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        var showInLibrary = new CheckBox { Content = Ui.Text("Show in the quick menu", 13), IsChecked = snippet.ShowsInLibrary };
        var replacement = new TextBox
        {
            Text = snippet.Replacement,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var dateButton = Ui.Button("Insert date/time", () => { }, glyph: Glyphs.Calendar);
        void SyncDateButton()
        {
            var token = SnippetVariables.TokenAt(replacement.Text, replacement.CaretIndex);
            dateButton.Content = Ui.Row(8, Ui.Icon(Glyphs.Calendar, 14, "TextPrimary"),
                Ui.Text(token is null ? "Insert date/time" : "Edit date/time", 13, wrap: false));
        }
        replacement.SelectionChanged += (_, _) => SyncDateButton();
        SyncDateButton();
        dateButton.Click += (_, _) =>
        {
            var token = SnippetVariables.TokenAt(replacement.Text, replacement.CaretIndex);
            var tag = DateBuilder(window, token);
            if (tag is null) return;
            if (token is not null)
            {
                replacement.Text = replacement.Text.Remove(token.Start, token.Length).Insert(token.Start, tag);
                replacement.CaretIndex = token.Start + tag.Length;
            }
            else
            {
                var at = replacement.SelectionStart;
                var length = replacement.SelectionLength;
                replacement.Text = replacement.Text.Remove(at, length).Insert(at, tag);
                replacement.CaretIndex = at + tag.Length;
            }
            replacement.Focus();
        };

        var problem = Ui.Text(string.Empty, 12, brush: "Orange");
        var saveButton = Ui.Button("Save", () =>
        {
            var saved = snippet with
            {
                Name = name.Text.Trim(),
                Trigger = SnippetEngine.SanitizedTrigger(trigger.Text),
                Replacement = replacement.Text.Replace("\r\n", "\n"),
                Expansion = (SnippetExpansion)((ComboBoxItem)expansion.SelectedItem).Tag,
                IgnoresCase = ignoreCase.IsChecked == true,
                Folder = SnippetEngine.SanitizedFolder(folder.Text),
                ShowsInLibrary = showInLibrary.IsChecked == true,
            };
            save(saved);
            window.Close();
        }, accent: true);
        void Validate()
        {
            var message = SnippetEngine.Validate(trigger.Text, replacement.Text, ignoreCase.IsChecked == true, others);
            problem.Text = message ?? string.Empty;
            problem.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            saveButton.IsEnabled = message is null;
        }
        trigger.TextChanged += (_, _) => Validate();
        replacement.TextChanged += (_, _) => Validate();
        ignoreCase.Click += (_, _) => Validate();
        Validate();

        var cancel = Ui.Button("Cancel", window.Close);
        cancel.IsCancel = true;
        var buttons = new DockPanel { LastChildFill = false };
        if (delete is not null)
        {
            var deleteButton = Ui.Button("Delete", () =>
            {
                delete();
                window.Close();
            });
            deleteButton.SetResourceReference(Control.ForegroundProperty, "Red");
            buttons.Children.Add(deleteButton);
        }
        var right = Ui.Row(8, cancel, saveButton);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        void Field(string label, UIElement control)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (label.Length > 0)
            {
                var l = Ui.Text(label, 13, wrap: false);
                l.VerticalAlignment = VerticalAlignment.Center;
                l.Margin = new Thickness(0, 4, 12, 4);
                Grid.SetRow(l, row);
                form.Children.Add(l);
            }
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
            row++;
        }
        Field("Name", CleanUrlViews.Labeled(name, "Personal email"));
        Field("Trigger", CleanUrlViews.Labeled(trigger, ";email"));
        Field("Expand", expansion);
        Field(string.Empty, ignoreCase);
        var folderRow = new DockPanel();
        DockPanel.SetDock(folderMenu, Dock.Right);
        folderMenu.Margin = new Thickness(6, 0, 0, 0);
        folderRow.Children.Add(folderMenu);
        folderRow.Children.Add(CleanUrlViews.Labeled(folder, "Work"));
        Field("Folder", folderRow);
        Field(string.Empty, showInLibrary);

        var textHeader = new DockPanel();
        DockPanel.SetDock(dateButton, Dock.Right);
        textHeader.Children.Add(dateButton);
        textHeader.Children.Add(Ui.Text("Text", 13, FontWeights.Medium, wrap: false));
        var body = Ui.Stack(10,
            Ui.Title(isNew ? "New snippet" : "Edit snippet", 16),
            form,
            textHeader,
            CleanUrlViews.Labeled(replacement, "myemail@example.com"),
            Ui.Caption(VariablesHint, 12),
            Ui.Caption(FormatCaption, 12),
            problem,
            buttons);
        body.Margin = new Thickness(20, 18, 20, 18);
        window.Body = body;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.Loaded += (_, _) => (isNew ? name : replacement).Focus();
        window.ShowDialog();
    }

    /// <summary>The date/time builder (upstream DateVariableBuilder). Returns the tag text, or null when cancelled.</summary>
    public static string? DateBuilder(Window? owner, DateToken? initial)
    {
        var culture = CultureInfo.CurrentCulture;
        var window = new FloatingWindow { HideOnDeactivate = false, Width = 400, SizeToContent = SizeToContent.Height, Title = "Date and time" };
        var kind = initial?.Kind ?? DateVariableKind.DateTime;
        var style = initial is null ? DateVariableStyle.Iso8601 : SnippetVariables.MatchingStyle(initial.Pattern, kind, culture);
        var custom = style == DateVariableStyle.Custom ? initial?.Pattern ?? string.Empty : string.Empty;
        string? zone = initial?.TimeZoneId;
        string? result = null;

        var kindRow = Ui.Row(6);
        var styleBox = new ComboBox { MinWidth = 180 };
        foreach (var (s, label) in new[]
                 {
                     (DateVariableStyle.Short, "Short"), (DateVariableStyle.Medium, "Medium"), (DateVariableStyle.Long, "Long"),
                     (DateVariableStyle.Full, "Full"), (DateVariableStyle.Iso8601, "ISO 8601"), (DateVariableStyle.Custom, "Custom"),
                 })
            styleBox.Items.Add(new ComboBoxItem { Content = label, Tag = s });
        var localeNote = Ui.Caption("A named style saves the format your PC’s region uses right now.", 12);
        var zoneLabel = Ui.Text(string.Empty, 13, wrap: false);
        var clearZone = Ui.IconButton(Glyphs.Close, "Clear timezone", () => { }, 10);
        var zoneSearch = new TextBox();
        var zoneState = Ui.Icon(Glyphs.Check, 12, "Green");
        var zoneList = new StackPanel();
        var zoneScroll = new ScrollViewer { Content = zoneList, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var pattern = new TextBox { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Text = custom };
        var patternRow = Ui.Stack(4, Ui.Text("Pattern", 12, brush: "TextSecondary"), pattern);
        var preview = Ui.Text(string.Empty, 13, wrap: false);
        preview.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        var confirm = Ui.Button(initial is null ? "Insert" : "Update", () => { }, accent: true);
        var cancel = Ui.Button("Cancel", window.Close);
        cancel.IsCancel = true;

        void Refresh()
        {
            kindRow.Children.Clear();
            foreach (var (k, label) in new[] { (DateVariableKind.Date, "Date"), (DateVariableKind.Time, "Time"), (DateVariableKind.DateTime, "DateTime") })
            {
                var button = new RadioButton { Content = label, IsChecked = k == kind, GroupName = "kind", Padding = new Thickness(10, 4, 10, 4) };
                button.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
                var captured = k;
                button.Checked += (_, _) =>
                {
                    kind = captured;
                    Refresh();
                };
                kindRow.Children.Add(button);
            }
            localeNote.Visibility = style is DateVariableStyle.Short or DateVariableStyle.Medium or DateVariableStyle.Long or DateVariableStyle.Full
                ? Visibility.Visible : Visibility.Collapsed;
            patternRow.Visibility = style == DateVariableStyle.Custom ? Visibility.Visible : Visibility.Collapsed;
            zoneLabel.Text = zone ?? "Device default";
            zoneLabel.SetResourceReference(TextBlock.ForegroundProperty, zone is null ? "TextSecondary" : "TextPrimary");
            clearZone.Visibility = zone is null ? Visibility.Collapsed : Visibility.Visible;
            var query = zoneSearch.Text.Trim();
            var matches = SnippetVariables.MatchingZones(query);
            var typed = SnippetVariables.ResolvedZone(query);
            zoneState.Visibility = query.Length > 0 && (typed is not null || matches.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            zoneState.Text = typed is not null ? Glyphs.Check : Glyphs.Close;
            zoneState.SetResourceReference(TextBlock.ForegroundProperty, typed is not null ? "Green" : "Red");
            zoneState.ToolTip = typed is not null ? "Valid timezone" : "Unrecognized timezone";
            zoneList.Children.Clear();
            foreach (var id in matches.Take(200))
            {
                var item = Ui.Clickable(Ui.Text(id, 12.5, wrap: false), () =>
                {
                    zone = id;
                    zoneSearch.Text = string.Empty;
                    Refresh();
                });
                item.HorizontalContentAlignment = HorizontalAlignment.Left;
                item.Padding = new Thickness(4, 2, 4, 2);
                zoneList.Children.Add(item);
            }
            zoneScroll.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            preview.Text = SnippetVariables.PreviewText(kind, style, pattern.Text, zone, DateTime.UtcNow, culture);
            confirm.IsEnabled = style != DateVariableStyle.Custom || pattern.Text.Length > 0;
        }
        styleBox.SelectedItem = styleBox.Items.OfType<ComboBoxItem>().First(i => (DateVariableStyle)i.Tag == style);
        styleBox.SelectionChanged += (_, _) =>
        {
            style = (DateVariableStyle)((ComboBoxItem)styleBox.SelectedItem).Tag;
            if (style == DateVariableStyle.Custom && pattern.Text.Length == 0)
                pattern.Text = SnippetVariables.ResolvedPattern(kind, DateVariableStyle.Iso8601, string.Empty, culture);
            Refresh();
        };
        clearZone.Click += (_, _) =>
        {
            zone = null;
            Refresh();
        };
        zoneSearch.TextChanged += (_, _) => Refresh();
        zoneSearch.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (SnippetVariables.ResolvedZone(zoneSearch.Text.Trim()) is { } typed)
            {
                zone = typed;
                zoneSearch.Text = string.Empty;
            }
            e.Handled = true;
            Refresh();
        };
        pattern.TextChanged += (_, _) => Refresh();
        confirm.Click += (_, _) =>
        {
            result = SnippetVariables.TagText(kind, style, pattern.Text, zone, culture);
            window.Close();
        };
        Refresh();

        var zoneHeader = new DockPanel();
        DockPanel.SetDock(clearZone, Dock.Right);
        zoneHeader.Children.Add(clearZone);
        zoneHeader.Children.Add(zoneLabel);
        var searchRow = new DockPanel();
        DockPanel.SetDock(zoneState, Dock.Right);
        zoneState.Margin = new Thickness(6, 0, 0, 0);
        searchRow.Children.Add(zoneState);
        searchRow.Children.Add(CleanUrlViews.Labeled(zoneSearch, "Search timezones"));
        var previewRow = new DockPanel();
        DockPanel.SetDock(preview, Dock.Right);
        previewRow.Children.Add(preview);
        previewRow.Children.Add(Ui.Text("Preview", 12, brush: "TextSecondary", wrap: false));
        var buttons = Ui.Row(8, cancel, confirm);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = Ui.Stack(12,
            Ui.Split(Ui.Text("Type", 13, wrap: false), kindRow),
            Ui.Split(Ui.Text("Style", 13, wrap: false), styleBox),
            localeNote,
            Ui.Stack(4, Ui.Text("Timezone", 12, brush: "TextSecondary"), zoneHeader, searchRow, zoneScroll),
            patternRow,
            previewRow,
            buttons);
        body.Margin = new Thickness(18, 16, 18, 16);
        window.Body = body;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.ShowDialog();
        return result;
    }
}
