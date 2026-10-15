// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Common;

/// <summary>Builders shared by the Mouse, Keyboard and Quit protection pages (upstream SettingsRow & co).</summary>
internal static class InputUi
{
    /// <summary>Left inset of content that belongs to a row with an icon (upstream settingsRowTextInset).</summary>
    public const double TextInset = 36;

    /// <summary>Icon, title and caption on the left, a control on the right.</summary>
    public static FrameworkElement Row(string glyph, string title, string? caption, UIElement? control)
    {
        var icon = Ui.Icon(glyph, 17);
        icon.Width = 24;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 12, 0);
        var text = Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is FrameworkElement fe)
        {
            fe.Margin = new Thickness(12, 0, 0, 0);
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 2);
            grid.Children.Add(fe);
        }
        return grid;
    }

    /// <summary>A switch row bound to a setting (icon + title + caption + toggle).</summary>
    public static FrameworkElement ToggleRow(string glyph, string title, string? caption, string key, bool defaultValue = false) =>
        Row(glyph, title, caption, Ui.Toggle(key, defaultValue));

    /// <summary>Indents content under a row's text.</summary>
    public static T Indent<T>(T element) where T : FrameworkElement
    {
        var m = element.Margin;
        element.Margin = new Thickness(m.Left + TextInset, m.Top, m.Right, m.Bottom);
        return element;
    }

    /// <summary>A card (optionally titled) whose rows are separated by dividers.</summary>
    public static FrameworkElement Card(string? title, params UIElement?[] rows) => Ui.Group(title, rows);

    /// <summary>Re-renders <paramref name="build"/> whenever one of <paramref name="keys"/> changes.</summary>
    public static FrameworkElement Live(Func<UIElement> build, params string[] keys)
    {
        var host = new ContentControl { Focusable = false };
        void Render() => host.Content = build();
        Render();
        Ui.Track(host, () => Settings.Observe(Render, keys));
        return host;
    }

    /// <summary>Label, slider and a fixed-width readout in one line (upstream sliderRow).</summary>
    public static FrameworkElement SliderLine(string title, string key, double min, double max, double defaultValue, double step,
        Func<double, string> readout)
    {
        var label = Ui.Text(title, 13, wrap: false);
        label.MinWidth = 120;
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(Settings.GetDouble(key, defaultValue), min, max),
            SmallChange = step,
            LargeChange = step * 2,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        var value = Ui.Text(readout(slider.Value), 12, FontWeights.Medium, "TextSecondary", wrap: false);
        value.Width = 64;
        value.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, e) =>
        {
            value.Text = readout(e.NewValue);
            Settings.Set(key, (int)Math.Round(e.NewValue));
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>A stepper (− value +) bound to an integer setting (upstream Stepper rows).</summary>
    public static FrameworkElement Stepper(string title, string key, int min, int max, int defaultValue, int step = 1,
        Func<int, int>? sanitize = null, bool enabled = true)
    {
        sanitize ??= v => Math.Clamp(v, min, max);
        var value = Ui.Text(string.Empty, 13, FontWeights.Medium, "TextSecondary", wrap: false);
        value.MinWidth = 56;
        value.TextAlignment = TextAlignment.Right;
        void Refresh() => value.Text = $"{sanitize(Settings.GetInt(key, defaultValue))} ms";
        Refresh();
        void Change(int delta) => Settings.Set(key, Math.Clamp(sanitize(Settings.GetInt(key, defaultValue)) + delta, min, max));
        var minus = Ui.IconButton(Glyphs.Remove, "Decrease", () => Change(-step), 12);
        var plus = Ui.IconButton(Glyphs.Add, "Increase", () => Change(step), 12);
        var row = Ui.Split(Ui.Text(title, 13), Ui.Row(4, value, minus, plus));
        row.IsEnabled = enabled;
        row.Opacity = enabled ? 1 : 0.5;
        Ui.Track(row, () => Settings.Observe(Refresh, key));
        return row;
    }

    /// <summary>A rounded chip used for key caps and choice chips (upstream modifier keycaps).</summary>
    public static Border Chip(string text, bool selected, Action? onClick, string? glyph = null, double minWidth = 0)
    {
        var content = Ui.Row(6,
            glyph is null ? null : Ui.Icon(glyph, 12, selected ? "Accent" : "TextPrimary"),
            Ui.Text(text, 12, FontWeights.Medium, selected ? "Accent" : "TextPrimary", wrap: false));
        var chip = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            MinWidth = minWidth,
            MinHeight = 28,
            Child = content,
        };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        chip.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
        chip.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBorder");
        if (onClick is not null)
        {
            chip.Cursor = Cursors.Hand;
            chip.MouseLeftButtonUp += (_, _) => Ui.Guard(onClick);
        }
        return chip;
    }

    /// <summary>A row of chips choosing one value of a string setting.</summary>
    public static FrameworkElement ChipChoice(string key, string defaultValue, params (string Value, string Label, string? Glyph)[] options)
    {
        var panel = new WrapPanel();
        void Render()
        {
            panel.Children.Clear();
            var current = Settings.GetString(key, defaultValue);
            foreach (var (value, label, glyph) in options)
            {
                var chip = Chip(label, value == current, () => Settings.Set(key, value), glyph);
                chip.Margin = new Thickness(0, 0, 8, 0);
                panel.Children.Add(chip);
            }
        }
        Render();
        Ui.Track(panel, () => Settings.Observe(Render, key));
        return panel;
    }

    /// <summary>A "More options" style disclosure: a chevron row that shows or hides its content.</summary>
    public static FrameworkElement Disclosure(string title, FrameworkElement content)
    {
        var chevron = Ui.Icon(Glyphs.ChevronRight, 10, "TextSecondary");
        var header = Ui.Row(8, chevron, Ui.Text(title, 13, FontWeights.Medium, wrap: false));
        header.Background = System.Windows.Media.Brushes.Transparent;
        header.Cursor = Cursors.Hand;
        content.Visibility = Visibility.Collapsed;
        content.Margin = new Thickness(0, 8, 0, 0);
        header.MouseLeftButtonUp += (_, _) =>
        {
            var open = content.Visibility != Visibility.Visible;
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        };
        return Ui.Stack(0, header, content);
    }

    /// <summary>The green "working now" capsule.</summary>
    public static FrameworkElement ActiveBadge(string text)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Row(6, Ui.Icon(Glyphs.Check, 11, "Green"), Ui.Text(text, 12, FontWeights.Medium, "Green", wrap: false)),
        };
        b.SetResourceReference(Border.BackgroundProperty, "GreenSubtle");
        return b;
    }

    /// <summary>A badge that is visible only while <paramref name="isActive"/> says so (checked every second).</summary>
    public static FrameworkElement LiveBadge(string text, Func<bool> isActive)
    {
        var badge = ActiveBadge(text);
        Ui.Every(badge, TimeSpan.FromSeconds(1), () => badge.Visibility = isActive() ? Visibility.Visible : Visibility.Collapsed);
        badge.Visibility = isActive() ? Visibility.Visible : Visibility.Collapsed;
        return badge;
    }

    /// <summary>A small icon followed by a wrapping caption.</summary>
    public static FrameworkElement InfoLine(string glyph, string text)
    {
        var icon = Ui.Icon(glyph, 12, "TextSecondary");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 6, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = Ui.Caption(text, 12);
        Grid.SetColumn(caption, 1);
        grid.Children.Add(icon);
        grid.Children.Add(caption);
        return grid;
    }

    /// <summary>The shared shortcut recorder.</summary>
    public static ShortcutRecorder Recorder(string key, double minWidth = 140) =>
        new(key, string.Empty) { MinWidth = minWidth };

    /// <summary>An orange explanatory line.</summary>
    public static TextBlock Warning(string text) => Ui.Text(text, 12, brush: "Orange");

    /// <summary>A small caption.</summary>
    public static TextBlock Note(string text) => Ui.Caption(text, 12);

    /// <summary>
    /// "Apps to leave alone" (upstream MouseExceptionsList / AppBundleList): the apps on a
    /// feature's own list, a remove button each, and "Add an app…" offering running apps or any
    /// program file.
    /// </summary>
    public static FrameworkElement ExceptionList(string listKeyPrefix, string caption, string title = "Apps to leave alone",
        string addTitle = "Add an app…", string? emptyText = null)
    {
        var rows = Ui.Stack(6);
        void Render()
        {
            rows.Children.Clear();
            var apps = AppExceptions.Get(listKeyPrefix);
            if (apps.Count == 0 && emptyText is not null) Ui.Append(rows, 6, Ui.Caption(emptyText, 12));
            foreach (var app in apps)
            {
                var name = app;
                var row = Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Document, 13, "TextSecondary"), Ui.Text(name, 13, wrap: false)),
                    Ui.IconButton(Glyphs.Close, "Remove", () => AppExceptions.Remove(listKeyPrefix, name), 11));
                Ui.Append(rows, 6, row);
            }
        }
        Render();
        Ui.Track(rows, () => Settings.Observe(Render, AppExceptions.Key(listKeyPrefix)));

        Button? add = null;
        add = Ui.Button(addTitle, () => OpenAddMenu(add!, listKeyPrefix), glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Stack(6, Ui.Text(title, 13, FontWeights.SemiBold), string.IsNullOrEmpty(caption) ? null : Ui.Caption(caption, 12), rows, add);
    }

    private static void OpenAddMenu(Button anchor, string listKeyPrefix)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var existing = new HashSet<string>(AppExceptions.Get(listKeyPrefix), StringComparer.OrdinalIgnoreCase);
        var running = WindowHelper.GetAltTabWindows()
            .Select(w => AppExceptions.Normalize(w.ProcessPath))
            .Where(n => n.Length > 0 && !existing.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var name in running)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => AppExceptions.Add(listKeyPrefix, name);
            menu.Items.Add(item);
        }
        if (running.Count > 0) menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Choose a program…" };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a program" };
            if (dialog.ShowDialog() == true) AppExceptions.Add(listKeyPrefix, dialog.FileName);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    /// <summary>Opens a Windows Settings page (ms-settings: URI).</summary>
    public static void OpenSystemSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Opening {uri} failed", ex); }
    }
}
