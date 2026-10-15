// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>The busy, done and confirmation states of the uninstaller window.</summary>
public static class StatusViews
{
    /// <summary>Upstream busyState: spinner, message, the target app and (while scanning) Cancel.</summary>
    public static FrameworkElement Busy(string message, string? appName, ImageSource? icon, string? detail = null,
        Action? onCancel = null, string? secondaryText = null, Action? onSecondary = null)
    {
        var stack = Ui.Stack(14, UninstallerUi.Spinner(34), Center(Ui.Text(message, 13.5, brush: "TextSecondary", wrap: false)));
        if (appName is not null)
        {
            var row = Ui.Row(8, UninstallerUi.AppIcon(icon, 18), Ui.Text(appName, 13, FontWeights.Medium, wrap: false));
            Ui.Append(stack, 14, Center(row));
        }
        if (!string.IsNullOrEmpty(detail))
        {
            var d = Ui.Caption(detail, 12);
            d.TextAlignment = TextAlignment.Center;
            d.MaxWidth = 420;
            Ui.Append(stack, 14, Center(d));
        }
        var buttons = Ui.Row(8,
            onCancel is null ? null : UninstallerUi.PlainButton(UninstallerText.Cancel, onCancel),
            onSecondary is null || secondaryText is null ? null : UninstallerUi.PlainButton(secondaryText, onSecondary));
        if (buttons.Children.Count > 0) Ui.Append(stack, 18, Center(buttons));
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(28);
        return stack;
    }

    /// <summary>Upstream doneState: a tick (or a warning when something stayed), "Done!", the space recovered,
    /// what failed, and "Uninstall another".</summary>
    public static FrameworkElement Done(long freed, IReadOnlyList<(Leftover Item, string Reason)> failed, string? backupFolder, Action onAnother)
    {
        var ok = failed.Count == 0;
        var glyph = Ui.Icon(ok ? UninstallerGlyphs.CheckCircle : Glyphs.Warning, 54, ok ? "Green" : "Orange");
        var title = Ui.Text(UninstallerText.DoneTitle, 20, FontWeights.Bold, wrap: false);
        var freedText = Ui.Text(string.Format(UninstallerText.FreedFormat, ByteFormat.Format(freed)), 13, brush: "TextSecondary", wrap: false);
        var stack = Ui.Stack(14, Center(glyph), Center(title), Center(freedText));
        if (!ok)
        {
            var lines = Ui.Stack(4, Ui.Text(UninstallerText.SomeFailed, 12.5, FontWeights.Medium));
            foreach (var (item, reason) in failed.Take(3))
            {
                var line = Ui.Text($"{item.Name} — {reason}", 11.5, brush: "TextSecondary");
                line.ToolTip = item.Path;
                Ui.Append(lines, 4, line);
            }
            if (failed.Count > 3) Ui.Append(lines, 4, Ui.Caption(string.Format(UninstallerText.FailedMoreFormat, failed.Count - 3), 11.5));
            var warn = Ui.Icon(Glyphs.Warning, 14, "Orange");
            warn.VerticalAlignment = VerticalAlignment.Top;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lines.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(lines, 1);
            g.Children.Add(warn);
            g.Children.Add(lines);
            var card = Ui.Card(g, new Thickness(12, 10, 12, 10), 10);
            card.Width = 400;
            Ui.Append(stack, 14, Center(card));
        }
        if (!string.IsNullOrEmpty(backupFolder))
        {
            var note = Ui.Caption($"Registry keys were exported to {backupFolder} before removal.", 11.5);
            note.TextAlignment = TextAlignment.Center;
            note.MaxWidth = 420;
            Ui.Append(stack, 14, Center(note));
        }
        Ui.Append(stack, 20, Center(AccentButton(UninstallerText.Another, onAnother)));
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(28);
        return stack;
    }

    /// <summary>A modal confirmation card drawn over the window.</summary>
    public static FrameworkElement Confirm(string title, string body, string action, Action onConfirm, Action onCancel)
    {
        var backdrop = new Border();
        backdrop.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        backdrop.Opacity = 0.75;
        var text = Ui.Stack(8, Ui.Text(title, 16, FontWeights.SemiBold), Ui.Caption(body, 12.5));
        var buttons = Ui.Row(8, UninstallerUi.PlainButton(UninstallerText.Cancel, onCancel), UninstallerUi.DangerButton(action, onConfirm));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var card = Ui.Card(Ui.Stack(18, text, buttons), new Thickness(20, 18, 20, 16), 14);
        card.Width = 440;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 };
        var grid = new Grid();
        grid.Children.Add(backdrop);
        grid.Children.Add(card);
        return grid;
    }

    private static Button AccentButton(string text, Action onClick)
    {
        var b = Ui.Button(text, onClick, accent: true);
        b.Padding = new Thickness(16, 8, 16, 8);
        return b;
    }

    private static T Center<T>(T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        return e;
    }
}
