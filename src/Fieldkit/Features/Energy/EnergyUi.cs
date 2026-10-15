// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>Small UI pieces shared by the Energy and Quick tools surfaces.</summary>
public static class EnergyUi
{
    /// <summary>A capsule chip (Keep awake durations). Selected = accent fill.</summary>
    public static Border Chip(string text, bool selected, Action onClick, string? glyph = null, double minWidth = 0)
    {
        var label = Ui.Text(text, 11.5, FontWeights.SemiBold, selected ? "TextOnAccent" : "TextPrimary", wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        UIElement content = glyph is null ? label : Ui.Row(4, Ui.Icon(glyph, 11, selected ? "TextOnAccent" : "TextSecondary"), label);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 3, 9, 4),
            Margin = new Thickness(0, 0, 6, 6),
            MinWidth = minWidth,
            Child = content,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
        };
        if (content is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
        chip.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "ControlBackground");
        chip.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBorder");
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onClick);
        };
        return chip;
    }

    /// <summary>Icon + title (+ caption) with a small switch bound to a setting.</summary>
    public static FrameworkElement MiniToggle(string? glyph, string title, string? caption, string key, bool defaultValue, Action<bool>? onChanged = null)
    {
        var text = Ui.Stack(1, Ui.Text(title, 12.5), caption is null ? null : Ui.Text(caption, 11, brush: "TextSecondary"));
        UIElement left = glyph is null ? text : WithIcon(glyph, text);
        return Ui.Split(left, Ui.Toggle(key, defaultValue, onChanged, small: true));
    }

    public static FrameworkElement WithIcon(string glyph, UIElement content, double size = 13)
    {
        var icon = Ui.Icon(glyph, size, "TextSecondary");
        icon.Width = 20;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 6, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(icon);
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    /// <summary>A chevron header that shows/hides <paramref name="body"/> (state kept in memory only, like upstream).</summary>
    public static FrameworkElement Disclosure(string title, Func<FrameworkElement> body, string? glyph = null,
        FrameworkElement? accessory = null, bool expanded = false, double indent = 14)
    {
        var chevron = Ui.Icon(expanded ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        chevron.Width = 14;
        var titleText = Ui.Text(title, 12, FontWeights.SemiBold, "TextSecondary", wrap: false);
        var left = glyph is null ? Ui.Row(4, chevron, titleText) : Ui.Row(4, chevron, Ui.Icon(glyph, 11, "TextSecondary"), titleText);
        var header = Ui.Split(left, accessory);
        header.Background = Brushes.Transparent;
        header.Cursor = Cursors.Hand;
        var host = new ContentControl { Margin = new Thickness(indent, 8, 0, 0) };
        var stack = Ui.Stack(0, header, host);
        void Sync()
        {
            chevron.Text = expanded ? Glyphs.ChevronDown : Glyphs.ChevronRight;
            host.Content = expanded ? body() : null;
            host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
        header.MouseLeftButtonUp += (_, _) =>
        {
            expanded = !expanded;
            Sync();
        };
        Sync();
        return stack;
    }

    /// <summary>A segmented control (SegmentButton style) bound to a string setting.</summary>
    public static FrameworkElement Segmented(string key, IReadOnlyList<(string Value, string Label)> options, string defaultValue) =>
        Segmented(options, Settings.GetString(key, defaultValue), v => Settings.Set(key, v));

    /// <summary>A segmented control (SegmentButton style).</summary>
    public static FrameworkElement Segmented(IReadOnlyList<(string Value, string Label)> options, string current, Action<string> onChanged)
    {
        var group = $"seg-{Guid.NewGuid():N}";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = value == current, Padding = new Thickness(12, 3, 12, 3) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) => onChanged(value);
            row.Children.Add(rb);
        }
        return border;
    }

    /// <summary>Title (+ caption) and a dropdown bound to an integer setting.</summary>
    public static FrameworkElement IntChoiceRow(string title, string? caption, string key, IReadOnlyList<int> values,
        Func<int, string> label, int current, bool compact = false)
    {
        var combo = new ComboBox { MinWidth = compact ? 90 : 140 };
        foreach (var v in values)
        {
            var item = new ComboBoxItem { Content = label(v), Tag = v };
            combo.Items.Add(item);
            if (v == current) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int v }) Settings.Set(key, v);
        };
        var left = Ui.Stack(2, Ui.Text(title, compact ? 12.5 : 13.5, compact ? FontWeights.Normal : FontWeights.Medium),
            caption is null ? null : Ui.Caption(caption));
        return Ui.Split(left, combo);
    }

    /// <summary>A global-shortcut toggle where "on" means the shortcut setting is non-empty.</summary>
    public static FrameworkElement ShortcutToggle(string title, string? caption, string key, string defaultShortcut)
    {
        var recorder = Ui.ShortcutRow("Shortcut", key, defaultShortcut);
        void Sync() => recorder.Visibility = Shortcut.FromSetting(key, defaultShortcut).IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        var toggle = new CheckBox { IsChecked = !Shortcut.FromSetting(key, defaultShortcut).IsEmpty };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        toggle.Click += (_, _) =>
        {
            Settings.Set(key, toggle.IsChecked == true ? defaultShortcut : string.Empty);
            Sync();
        };
        var row = Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)), toggle);
        var stack = Ui.Stack(10, row, recorder);
        Ui.Track(stack, () => Settings.Observe(() =>
        {
            toggle.IsChecked = !Shortcut.FromSetting(key, defaultShortcut).IsEmpty;
            Sync();
        }, key));
        Sync();
        return stack;
    }

    /// <summary>A borderless, themed confirmation dialog. Returns true when the confirm button was pressed.</summary>
    public static bool Confirm(string title, string message, string confirmText, bool destructive = true, Window? owner = null)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
            Topmost = true,
            Owner = owner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        var confirm = Ui.Button(confirmText, () =>
        {
            result = true;
            window.Close();
        }, accent: true);
        if (destructive) confirm.SetResourceReference(Control.BackgroundProperty, "Red");
        confirm.IsDefault = true;
        var cancel = Ui.Button("Cancel", window.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(8, cancel, confirm);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = Ui.Stack(10, Ui.Title(title), string.IsNullOrEmpty(message) ? null : Ui.Caption(message), buttons);
        body.Margin = new Thickness(20, 16, 20, 18);
        window.Content = body;
        window.ShowDialog();
        return result;
    }

    /// <summary>A small accent-tinted, square-ish selectable tile (automation conditions).</summary>
    public static Border SelectTile(string glyph, string title, bool selected, Action onClick, double height = 66)
    {
        var icon = Ui.Icon(glyph, 18, selected ? "Accent" : "TextSecondary");
        var check = Ui.Icon(selected ? Glyphs.Check : string.Empty, 10, "Accent");
        check.HorizontalAlignment = HorizontalAlignment.Right;
        check.VerticalAlignment = VerticalAlignment.Top;
        var label = Ui.Text(title, 11, FontWeights.Medium, selected ? "Accent" : "TextPrimary", wrap: true);
        label.TextAlignment = TextAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.Stack(6, icon, label);
        stack.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(check);
        var tile = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(6),
            MinHeight = height,
            Child = grid,
            Cursor = Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
        tile.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBorder");
        tile.MouseLeftButtonUp += (_, _) => Ui.Guard(onClick);
        return tile;
    }

    /// <summary>Opens a context menu under <paramref name="anchor"/>.</summary>
    public static void ShowMenu(FrameworkElement anchor, IEnumerable<(string Text, Action Run)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (text, run) in items)
        {
            if (text == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => Ui.Guard(run);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
