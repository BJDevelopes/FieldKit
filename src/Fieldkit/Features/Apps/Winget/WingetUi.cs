// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>Segoe Fluent Icons code points used by the package tools (checked with --render-glyph-range).</summary>
public static class WingetGlyphs
{
    public const string ArrowUp = "";
    public const string ArrowDown = "";
    public const string CheckCircle = "";
    public const string Sync = "";
    public const string Store = "";
    public const string Terminal = "";
    public const string AppWindow = "";
    public const string OpenExternal = "";
    public const string Hourglass = "";
}

/// <summary>Small building blocks shared by the Packages and App updates windows.</summary>
public static class WingetUi
{
    /// <summary>A coloured capsule ("Installed", "Update available", "winget").</summary>
    public static FrameworkElement Pill(string text, string colorKey, double size = 10.5)
    {
        var bg = new Border { CornerRadius = new CornerRadius(8), Opacity = 0.15 };
        bg.SetResourceReference(Border.BackgroundProperty, colorKey);
        var label = Ui.Text(text, size, FontWeights.SemiBold, colorKey, wrap: false);
        label.Margin = new Thickness(7, 1.5, 7, 2);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(bg);
        grid.Children.Add(label);
        return grid;
    }

    /// <summary>A plain count capsule next to a section title.</summary>
    public static FrameworkElement Count(int count) => Ui.Badge(count.ToString(System.Globalization.CultureInfo.CurrentCulture));

    /// <summary>Clickable accent-coloured text.</summary>
    public static FrameworkElement Link(string text, Action onClick, double size = 12.5)
    {
        var tb = Ui.Text(text, size, FontWeights.Medium, "Accent", wrap: false);
        tb.Cursor = Cursors.Hand;
        tb.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
        tb.MouseLeave += (_, _) => tb.TextDecorations = null;
        tb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onClick);
        };
        return tb;
    }

    /// <summary>Opens a web or store link with the default handler.</summary>
    public static bool OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening {uri} failed: {ex.Message}");
            return false;
        }
    }

    public static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn($"Copy failed: {ex.Message}"); }
    }

    /// <summary>Opens a console window running <paramref name="command"/> so winget can ask its questions there.</summary>
    public static void OpenTerminal(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k " + command) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening a terminal failed: {ex.Message}");
        }
    }

    /// <summary>A rounded segmented control (one RadioButton per option).</summary>
    public static FrameworkElement Segmented(IReadOnlyList<(string Value, string Label)> options, string selected,
        Action<string> onSelect, double fontSize = 12.5)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton
            {
                Content = label,
                FontSize = fontSize,
                IsChecked = value == selected,
            };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Padding = new Thickness(11, 4, 11, 4);
            var v = value;
            rb.Checked += (_, _) => Ui.Guard(() => onSelect(v));
            row.Children.Add(rb);
        }
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BackgroundProperty, "TabBarBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        return border;
    }

    /// <summary>A button styled for a destructive action (red fill).</summary>
    public static Button DestructiveButton(string text, Action onClick, string? glyph = null)
    {
        var b = Ui.Button(text, onClick, accent: true, glyph: glyph);
        b.SetResourceReference(Control.BackgroundProperty, "Red");
        b.SetResourceReference(Control.BorderBrushProperty, "Red");
        return b;
    }

    /// <summary>A text-and-icon button sized for toolbars.</summary>
    public static Button ToolbarButton(string text, string glyph, Action onClick, bool accent = false)
    {
        var b = Ui.Button(text, onClick, accent, glyph);
        b.Padding = new Thickness(10, 5, 10, 5);
        return b;
    }

    /// <summary>"12s", "3min", "1h 4min" like upstream's elapsed text.</summary>
    public static string Elapsed(TimeSpan span)
    {
        var seconds = Math.Max(0, (int)span.TotalSeconds);
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}min";
        return $"{minutes / 60}h {minutes % 60}min";
    }

    /// <summary>The "winget not found" state with a way to get App Installer.</summary>
    public static FrameworkElement MissingState(Action refresh, bool compact = false)
    {
        var opened = false;
        var caption = Ui.Caption(WingetStrings.GetAppInstallerCaption, compact ? 11.5 : 12.5);
        caption.TextAlignment = TextAlignment.Center;
        var get = Ui.Button(WingetStrings.GetAppInstaller, () =>
        {
            if (!OpenUri(WingetCommands.AppInstallerStoreUri)) OpenUri(WingetCommands.AppInstallerWebUrl);
            opened = true;
            caption.Text = opened ? WingetStrings.GetAppInstallerOpened : WingetStrings.GetAppInstallerCaption;
        }, accent: true, glyph: WingetGlyphs.Store);
        get.HorizontalAlignment = HorizontalAlignment.Center;
        var refreshButton = Ui.Button(WingetStrings.Refresh, () =>
        {
            WingetCli.Redetect();
            refresh();
        }, glyph: Glyphs.Refresh);
        refreshButton.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Ui.Text(WingetStrings.MissingTitle, compact ? 14 : 17, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var body = Ui.Caption(WingetStrings.MissingBody, compact ? 12 : 13);
        body.TextAlignment = TextAlignment.Center;
        body.MaxWidth = 380;
        var icon = Ui.Icon(Glyphs.Package, compact ? 30 : 44, "TextSecondary");
        var stack = Ui.Stack(12, icon, title, body, get, caption, refreshButton);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.Margin = new Thickness(28);
        return stack;
    }
}

/// <summary>Thin progress bar: determinate fill, or a sliding segment while the percentage is unknown.</summary>
public sealed class WingetProgressBar : Grid
{
    private readonly Border _fill;
    private readonly TranslateTransform _slide = new();
    private double? _value;

    public WingetProgressBar(double? value)
    {
        Height = 5;
        ClipToBounds = true;
        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBackground");
        _fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _slide };
        _fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        Children.Add(track);
        Children.Add(_fill);
        SizeChanged += (_, _) => Apply();
        Loaded += (_, _) => Apply();
        Unloaded += (_, _) =>
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _animatedWidth = -1;
        };
        Value = value;
    }

    public double? Value
    {
        get => _value;
        set
        {
            _value = value;
            Apply();
        }
    }

    private void Apply()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width is > 0 and not double.NaN ? Width : 0;
        if (_value is { } v)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _animatedWidth = -1;
            _slide.X = 0;
            _fill.Width = Math.Max(0, width * Math.Clamp(v, 0, 1));
            return;
        }
        _fill.Width = Math.Max(36, width * 0.34);
        if (width <= 0 || !IsLoaded)
        {
            _animatedWidth = -1;
            _slide.X = width * 0.2;
            return;
        }
        if (Math.Abs(_animatedWidth - width) < 1) return; // already sliding at this size
        _animatedWidth = width;
        var animation = new DoubleAnimation(-0.35 * width, 1.05 * width, TimeSpan.FromSeconds(1.15))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private double _animatedWidth = -1;
}

/// <summary>
/// A layer that shows modal sheets (confirmations, consent, review screens) over a window's
/// content, the in-window counterpart of upstream's confirmation dialogs.
/// </summary>
public sealed class SheetHost : Grid
{
    private readonly Grid _overlay;
    private readonly Border _card;
    private Action? _onDismiss;

    public SheetHost(UIElement content)
    {
        Children.Add(content);
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)) };
        backdrop.MouseLeftButtonUp += (_, _) => Close();
        _card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            MaxWidth = 500,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _card.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        _card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 };
        _overlay = new Grid { Visibility = Visibility.Collapsed };
        _overlay.Children.Add(backdrop);
        _overlay.Children.Add(_card);
        Children.Add(_overlay);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsOpen)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    public bool IsOpen => _overlay.Visibility == Visibility.Visible;

    public void Show(FrameworkElement body, Action? onDismiss = null)
    {
        _onDismiss = onDismiss;
        _card.Child = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 560,
        };
        _overlay.Visibility = Visibility.Visible;
        _card.Focusable = true;
        _card.Focus();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _overlay.Visibility = Visibility.Collapsed;
        _card.Child = null;
        var dismiss = _onDismiss;
        _onDismiss = null;
        dismiss?.Invoke();
    }

    /// <summary>Standard sheet content: title, message, optional detail and right-aligned buttons.</summary>
    public FrameworkElement Confirmation(string title, string message, UIElement? detail, string actionLabel,
        bool destructive, Action onConfirm, string? glyph = null)
    {
        var heading = Ui.Text(title, 16, FontWeights.SemiBold);
        var body = Ui.Caption(message, 13);
        var cancel = Ui.Button(WingetStrings.Cancel, Close);
        cancel.MinWidth = 90;
        Button confirm = destructive
            ? WingetUi.DestructiveButton(actionLabel, () => { Close(); onConfirm(); }, glyph)
            : Ui.Button(actionLabel, () => { Close(); onConfirm(); }, accent: true, glyph: glyph);
        confirm.MinWidth = 110;
        var buttons = Ui.Row(8, cancel, confirm);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 6, 0, 0);
        return Ui.Stack(10, heading, body, detail, buttons);
    }

    /// <summary>The one-time consent sheet; <paramref name="onAgree"/> runs after the agreement is stored.</summary>
    public FrameworkElement Consent(Action onAgree)
    {
        var heading = Ui.Row(10, Ui.Icon(Glyphs.Shield, 20), Ui.Text(WingetStrings.ConsentTitle, 16, FontWeights.SemiBold));
        var links = Ui.Row(16,
            WingetUi.Link(WingetStrings.ConsentWingetTerms, () => WingetUi.OpenUri(WingetCommands.WingetLicenseUrl)),
            WingetUi.Link(WingetStrings.ConsentStoreTerms, () => WingetUi.OpenUri(WingetCommands.StoreTermsUrl)));
        var cancel = Ui.Button(WingetStrings.Cancel, Close);
        cancel.MinWidth = 90;
        var agree = Ui.Button(WingetStrings.ConsentAgree, () =>
        {
            WingetConsent.Accept();
            Close();
            onAgree();
        }, accent: true);
        var buttons = Ui.Row(8, cancel, agree);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 6, 0, 0);
        return Ui.Stack(10, heading, Ui.Caption(WingetStrings.ConsentBody, 13), Ui.Caption(WingetStrings.ConsentDetail, 12.5), links, buttons);
    }

    /// <summary>Runs <paramref name="action"/> right away when the agreement exists, otherwise after the consent sheet.</summary>
    public void WithConsent(Action action)
    {
        if (WingetConsent.Accepted)
        {
            action();
            return;
        }
        Show(Consent(action));
    }
}
