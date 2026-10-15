// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>Small settings controls the capture pages share.</summary>
internal static class CaptureUi
{
    /// <summary>A segmented picker bound to a string setting.</summary>
    public static FrameworkElement Segmented(string key, IReadOnlyList<(string Value, string Label)> options, string defaultValue, Action<string>? onChanged = null)
    {
        var current = Settings.GetString(key, defaultValue);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = key + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var v = value;
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = value == current, Padding = new Thickness(12, 4, 12, 4) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) =>
            {
                Settings.Set(key, v);
                onChanged?.Invoke(v);
            };
            panel.Children.Add(rb);
        }
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return border;
    }

    /// <summary>A two-option segmented picker bound to a boolean setting.</summary>
    public static FrameworkElement SegmentedBool(string key, string falseLabel, string trueLabel, bool defaultValue)
    {
        var current = Settings.GetBool(key, defaultValue);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = key + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in new[] { (false, falseLabel), (true, trueLabel) })
        {
            var v = value;
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = value == current, Padding = new Thickness(12, 4, 12, 4) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) => Settings.Set(key, v);
            panel.Children.Add(rb);
        }
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return border;
    }

    /// <summary>A segmented picker bound to an integer setting.</summary>
    public static FrameworkElement SegmentedInt(string key, IReadOnlyList<(int Value, string Label)> options, int defaultValue)
    {
        var current = Settings.GetInt(key, defaultValue);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = key + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var v = value;
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = value == current, Padding = new Thickness(12, 4, 12, 4) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) => Settings.Set(key, v);
            panel.Children.Add(rb);
        }
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return border;
    }

    public static FrameworkElement SegmentedIntRow(string title, string? caption, string key, IReadOnlyList<(int Value, string Label)> options, int defaultValue) =>
        Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)),
            SegmentedInt(key, options, defaultValue));

    /// <summary>Title (+ caption) on the left, a segmented picker on the right.</summary>
    public static FrameworkElement SegmentedRow(string title, string? caption, string key, IReadOnlyList<(string Value, string Label)> options, string defaultValue, Action<string>? onChanged = null) =>
        Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)),
            Segmented(key, options, defaultValue, onChanged));

    /// <summary>
    /// A shortcut that is off until switched on (upstream defaults): the switch fills in the suggested
    /// combination; the recorder below changes it. Empty means off, here and on the Keyboard shortcuts page.
    /// </summary>
    public static FrameworkElement ShortcutToggle(string title, string key, string suggested, string? caption = null)
    {
        var toggle = new CheckBox { IsChecked = Settings.GetString(key, string.Empty).Length > 0 };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        var recorder = new ShortcutRecorder(key, string.Empty);
        var recorderRow = Ui.Split(Ui.Caption("Shortcut"), recorder);
        recorderRow.Margin = new Thickness(0, 8, 0, 0);
        void Sync()
        {
            var on = Settings.GetString(key, string.Empty).Length > 0;
            toggle.IsChecked = on;
            recorderRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        toggle.Click += (_, _) => Settings.Set(key, toggle.IsChecked == true ? suggested : string.Empty);
        Sync();
        var head = Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), caption is null ? null : Ui.Caption(caption)), toggle);
        var stack = Ui.Stack(0, head, recorderRow);
        Ui.Track(stack, () => Settings.Observe(Sync, key));
        return stack;
    }

    /// <summary>A save folder row: path, Choose… and a reset to the default.</summary>
    public static FrameworkElement FolderRow(string title, string key, Func<string> current, string defaultFolder)
    {
        var path = Ui.Caption(current());
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.TextWrapping = TextWrapping.NoWrap;
        var choose = Ui.Button("Choose\u2026", () =>
        {
            var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(current()) ? current() : null, Title = title };
            if (dialog.ShowDialog() == true) Settings.Set(key, dialog.FolderName);
        });
        var reset = Ui.IconButton(Glyphs.Undo, "Back to " + defaultFolder, () => Settings.Remove(key), 12);
        var open = Ui.IconButton(Glyphs.Folder, "Open the folder", () =>
        {
            Directory.CreateDirectory(current());
            Process.Start(new ProcessStartInfo(current()) { UseShellExecute = true });
        }, 13);
        var row = Ui.Split(Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), path), Ui.Row(4, open, reset, choose));
        Ui.Track(row, () => Settings.Observe(() => path.Text = current(), key));
        return row;
    }

    public static FrameworkElement ActionButtons(params (string Title, string Glyph, Action Run)[] buttons)
    {
        var row = new WrapPanel();
        for (var i = 0; i < buttons.Length; i++)
        {
            var b = Ui.Button(buttons[i].Title, buttons[i].Run, accent: i == 0, glyph: buttons[i].Glyph);
            b.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(b);
        }
        return row;
    }
}
