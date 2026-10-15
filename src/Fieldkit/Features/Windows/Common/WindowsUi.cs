// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>Settings building blocks shared by the window pages (built on <see cref="Ui"/>).</summary>
internal static class WindowsUi
{
    /// <summary>
    /// A list of apps with icons, a remove button per row and "Add an app…" (running apps or
    /// Browse…). Rows follow the setting while shown.
    /// </summary>
    public static FrameworkElement AppListEditor(string title, string caption, string addTitle, string removeTitle,
        string settingKey, Func<IReadOnlyList<string>> keys, Action<string> add, Action<string> remove,
        Func<string, FrameworkElement?>? accessory = null)
    {
        var rows = Ui.Stack(6);
        void Rebuild()
        {
            rows.Children.Clear();
            var items = keys().OrderBy(AppNames.DisplayNameForKey, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (items.Count == 0)
            {
                rows.Children.Add(Ui.Text("No apps", 13, brush: "TextTertiary"));
                return;
            }
            foreach (var key in items)
            {
                var icon = AppIcons.Element(AppIcons.ForPath(AppNames.PathFor(key)), 20);
                var name = Ui.Text(AppNames.DisplayNameForKey(key), 13, wrap: false);
                var file = Ui.Text(key, 11.5, brush: "TextTertiary", wrap: false);
                var right = Ui.Row(6, accessory?.Invoke(key), Ui.IconButton(Glyphs.Remove, removeTitle, () => remove(key), 12));
                var row = Ui.Split(Ui.Row(10, icon, Ui.Stack(0, name, file)), right);
                Ui.Append(rows, 6, row);
            }
        }
        Rebuild();
        Ui.Track(rows, () => Settings.Observe(Rebuild, settingKey));

        var addButton = Ui.Button(addTitle, () => { }, glyph: Glyphs.Add);
        addButton.HorizontalAlignment = HorizontalAlignment.Left;
        addButton.Click += (_, _) => ShowAppPicker(addButton, add);

        return Ui.Stack(8,
            Ui.Text(title, 13.5, FontWeights.Medium),
            rows,
            addButton,
            Ui.Caption(caption));
    }

    /// <summary>A menu of running apps plus "Browse…" for any executable.</summary>
    public static void ShowAppPicker(FrameworkElement anchor, Action<string> onPick)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var apps = RunningApps();
        foreach (var (path, name) in apps)
        {
            var item = new MenuItem
            {
                Header = name,
                Icon = AppIcons.Element(AppIcons.ForPath(path), 16),
                ToolTip = path,
            };
            item.Click += (_, _) => onPick(path);
            menu.Items.Add(item);
        }
        if (apps.Count > 0) menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Applications (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (dialog.ShowDialog() == true) onPick(dialog.FileName);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    /// <summary>Apps with a window right now, one entry each, sorted by name.</summary>
    public static List<(string Path, string Name)> RunningApps()
    {
        var seen = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var w in WindowEnumerator.List(includeOtherDesktops: true))
            {
                if (w.ProcessPath.Length == 0 || seen.ContainsKey(w.AppKey)) continue;
                seen[w.AppKey] = (w.ProcessPath, w.AppName);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing running apps failed", ex);
        }
        return seen.Values.OrderBy(v => v.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A row of selectable chips bound to a string setting (upstream chip rows).</summary>
    public static FrameworkElement Chips(string key, string defaultValue, IReadOnlyList<(string Value, string Label)> choices,
        Action<string>? onChanged = null)
    {
        var panel = new WrapPanel();
        var buttons = new List<(Border Chip, TextBlock Label, string Value)>();
        void Sync()
        {
            var current = Settings.GetString(key, defaultValue);
            foreach (var (chip, label, value) in buttons)
            {
                var selected = value == current;
                chip.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
                chip.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "ControlBackground");
                label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextPrimary");
            }
        }
        foreach (var (value, text) in choices)
        {
            var label = Ui.Text(text, 12, FontWeights.Medium, wrap: false);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Child = label,
                Cursor = Cursors.Hand,
            };
            var v = value;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                Settings.Set(key, v);
                onChanged?.Invoke(v);
            };
            buttons.Add((chip, label, value));
            panel.Children.Add(chip);
        }
        Sync();
        Ui.Track(panel, () => Settings.Observe(Sync, key));
        return panel;
    }

    /// <summary>Title + caption on the left, chips underneath (upstream chipRow).</summary>
    public static FrameworkElement ChipRow(string title, string? caption, string key, string defaultValue,
        IReadOnlyList<(string Value, string Label)> choices) =>
        Ui.Stack(8,
            Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)),
            Chips(key, defaultValue, choices));

    /// <summary>Hides <paramref name="element"/> while the boolean setting differs from <paramref name="visibleWhen"/>.</summary>
    public static T VisibleWhen<T>(T element, string key, bool defaultValue, bool visibleWhen = true) where T : FrameworkElement
    {
        void Sync() => element.Visibility = Settings.GetBool(key, defaultValue) == visibleWhen ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(element, () => Settings.Observe(Sync, key));
        return element;
    }

    /// <summary>Greys out <paramref name="element"/> while the boolean setting is off.</summary>
    public static T EnabledWhen<T>(T element, string key, bool defaultValue) where T : FrameworkElement
    {
        void Sync()
        {
            var on = Settings.GetBool(key, defaultValue);
            element.IsEnabled = on;
            element.Opacity = on ? 1 : 0.45;
        }
        Sync();
        Ui.Track(element, () => Settings.Observe(Sync, key));
        return element;
    }

    /// <summary>A small coloured status line ("Active now").</summary>
    public static FrameworkElement Status(string glyph, string text, string brush) =>
        Ui.Row(6, Ui.Icon(glyph, 12, brush), Ui.Text(text, 12, FontWeights.Medium, brush));

    /// <summary>Left-aligns a button inside a stretching row.</summary>
    public static FrameworkElement Left(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    /// <summary>A row with something underneath it (one Group row, so collapsing it leaves no stray divider).</summary>
    public static FrameworkElement WithBelow(this FrameworkElement row, FrameworkElement below, double spacing = 8) =>
        Ui.Stack(spacing, row, below);

    /// <summary>A hint line with a leading icon, wrapping.</summary>
    public static FrameworkElement Note(string glyph, string text, string brush = "TextSecondary", string? iconBrush = null)
    {
        var icon = Ui.Icon(glyph, 12, iconBrush ?? brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = Ui.Text(text, 12, brush: brush);
        t.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(t, 1);
        g.Children.Add(icon);
        g.Children.Add(t);
        return g;
    }

    /// <summary>Opens a Windows Settings page (ms-settings: URI) or any shell target.</summary>
    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Opening {uri} failed", ex); }
    }

    /// <summary>The shortcut a setting holds right now, as display text ("Ctrl+Alt+Left"), or "None".</summary>
    public static string ShortcutText(string key, string defaultValue)
    {
        var text = Shortcut.FromSetting(key, defaultValue).ToString();
        return text.Length == 0 ? "None" : text;
    }

    /// <summary>A rounded tile drawn like a window/screen for layout choices.</summary>
    public static Border Sketch(double width, double height, string brush = "ControlBackground")
    {
        var b = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(3) };
        b.SetResourceReference(Border.BackgroundProperty, brush);
        return b;
    }

    internal static SolidColorBrush Alpha(string key, byte alpha)
    {
        var c = ThemeManager.Color(key);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
