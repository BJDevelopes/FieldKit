// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>Small UI pieces shared by the cleaner window, the messaging window and the settings page.</summary>
internal static class CleanerUi
{
    /// <summary>A dropdown bound to an integer setting.</summary>
    public static ComboBox IntChoice(string key, IReadOnlyList<(int Value, string Label)> options, int defaultValue, Action<int>? onChanged = null)
    {
        var combo = new ComboBox { MinWidth = 70 };
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        var current = Settings.GetInt(key, defaultValue);
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current)
                             ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem item) return;
            Settings.Set(key, (int)item.Tag);
            onChanged?.Invoke((int)item.Tag);
        };
        return combo;
    }

    /// <summary>A dropdown not bound to a setting.</summary>
    public static ComboBox Combo<T>(IReadOnlyList<(T Value, string Label)> options, T selected, Action<T> onChanged)
    {
        var combo = new ComboBox { MinWidth = 70 };
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected))
                             ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value }) onChanged(value);
        };
        return combo;
    }

    /// <summary>The small rounded card of the cleaner's idle state (upstream: 9 pt radius, faint fill).</summary>
    public static Border SoftCard(UIElement child, double maxWidth = 400)
    {
        var card = Ui.Card(child, new Thickness(12, 11, 12, 11), 9);
        card.MaxWidth = maxWidth;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        return card;
    }

    public static TextBlock Small(string text, string brush = "TextSecondary", double size = 11.5) => Ui.Text(text, size, brush: brush);

    /// <summary>A checkbox not bound to a setting.</summary>
    public static CheckBox Check(bool isChecked, Action<bool> onChanged, string? label = null, bool enabled = true)
    {
        var cb = new CheckBox { IsChecked = isChecked, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        if (label is not null) cb.Content = Ui.Text(label, 13);
        cb.Click += (_, _) => Ui.Guard(() => onChanged(cb.IsChecked == true));
        return cb;
    }

    /// <summary>Selects an item in File Explorer (or opens a folder).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Reveal {path} failed: {ex.Message}");
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Open {path} failed: {ex.Message}");
        }
    }

    /// <summary>A themed question with custom answers. Returns the index of the chosen button, or -1 when closed.</summary>
    public static int Ask(Window? owner, string title, string message, params string[] buttons)
    {
        var result = -1;
        var window = new ToolWindow(title, 440, 220) { SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, MinHeight = 0 };
        if (owner is not null && owner.IsLoaded)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = Ui.Button(buttons[i], () =>
            {
                result = index;
                window.Close();
            }, accent: i == 0);
            b.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(b);
        }
        var body = Ui.Stack(14, Ui.Title(title, 16), Ui.Caption(message, 13), row);
        body.Margin = new Thickness(20);
        window.Content = body;
        window.ShowDialog();
        return result;
    }

    /// <summary>An icon followed by wrapping text (a Label with an icon, upstream).</summary>
    public static FrameworkElement IconLine(string glyph, TextBlock text, string brush = "TextSecondary")
    {
        var dock = new DockPanel();
        var icon = Ui.Icon(glyph, 14, brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(text);
        return dock;
    }

    /// <summary>The orange "Experimental" capsule.</summary>
    public static Border ExperimentalBadge()
    {
        var b = Ui.Badge(S.OrgExperimental.ToUpperInvariant(), "BadgeBackground", "Orange");
        return b;
    }

    public static SolidColorBrush Transparent => Brushes.Transparent;
}
