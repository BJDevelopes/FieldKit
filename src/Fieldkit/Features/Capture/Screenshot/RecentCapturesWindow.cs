// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>The capture history palette (upstream RecentCapturesView / RecentCapturesWindowView).</summary>
public sealed class RecentCapturesWindow : Window
{
    private static RecentCapturesWindow? _current;
    private readonly StackPanel _list = new();

    public static void Open()
    {
        if (_current is not null)
        {
            _current.Activate();
            return;
        }
        PanelHost.Hide();
        _current = new RecentCapturesWindow();
        _current.Show();
        _current.Activate();
    }

    public static void CloseCurrent() => _current?.Close();

    private RecentCapturesWindow()
    {
        Title = "Recent captures";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 470;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = BuildContent();
        Reload();
        RecentCaptures.Changed += OnChanged;
        Closed += (_, _) =>
        {
            RecentCaptures.Changed -= OnChanged;
            _current = null;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Deactivated += (_, _) =>
        {
            if (IsVisible && OwnedWindows.Count == 0) Close();
        };
    }

    private void OnChanged() => Ui.OnUi(Reload);

    private FrameworkElement BuildContent()
    {
        var clear = Ui.IconButton(Glyphs.Delete, "Clear history", () =>
        {
            if (MessageBox.Show(this, "Clear history", "Recent captures", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                RecentCaptures.Clear();
        }, 13);
        var close = Ui.IconButton(Glyphs.Close, "Close", Close, 12);
        var header = Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Clock, 14, "TextPrimary"), Ui.Text("Recent captures", 13, FontWeights.SemiBold)), Ui.Row(2, clear, close));
        var scroll = new ScrollViewer { Content = _list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = Ui.Stack(10, header, scroll);
        var border = new Border
        {
            Child = stack,
            Padding = new Thickness(14),
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        return border;
    }

    private void Reload()
    {
        _list.Children.Clear();
        _list.Children.Add(BuildList());
    }

    /// <summary>The list itself, also used by the settings preview.</summary>
    public static FrameworkElement BuildList()
    {
        var entries = RecentCaptures.Entries;
        if (entries.Count == 0)
        {
            var empty = Ui.Text("Take a screenshot or save a recording to find it here.", 12, brush: "TextTertiary");
            empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(12, 30, 12, 30);
            return Ui.Card(empty);
        }
        return Ui.Stack(7, entries.Select(e => (UIElement)Row(e)).ToArray());
    }

    private static FrameworkElement Row(RecentCapture entry)
    {
        var thumb = RecentCaptures.Thumbnail(entry);
        var preview = new Border
        {
            Width = 104,
            Height = 68,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Child = thumb is null
                ? Ui.Icon(entry.Kind == RecentCaptureKind.Screenshot ? Glyphs.Photo : "\uE714", 20, "TextTertiary")
                : new Image { Source = thumb, Stretch = Stretch.Uniform },
        };
        preview.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        var title = Ui.Text(entry.Kind == RecentCaptureKind.Screenshot ? "Screenshot" : "Recording", 12.5, FontWeights.SemiBold, wrap: false);
        var name = entry.RecordingPath is null ? null : Ui.Text(Path.GetFileName(entry.RecordingPath), 11, brush: "TextSecondary", wrap: false);
        var time = Ui.Text(Relative(entry.CreatedAt), 11, brush: "TextTertiary", wrap: false);
        var open = Ui.Button(entry.Kind == RecentCaptureKind.Screenshot ? "Restore" : "Open", () => OpenEntry(entry), accent: true);
        open.Padding = new Thickness(10, 2, 10, 2);
        var remove = Ui.IconButton(Glyphs.Delete, "Remove from history", () => RecentCaptures.Remove(entry), 12);
        var info = Ui.Stack(4, title, name, time, Ui.Row(6, open, remove));
        info.Margin = new Thickness(10, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(preview);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return Ui.Card(grid, new Thickness(8));
    }

    private static void OpenEntry(RecentCapture entry)
    {
        CloseCurrent();
        try
        {
            if (entry.Kind == RecentCaptureKind.Screenshot && entry.ImageFile is not null)
                ScreenshotEditorWindow.Open(RasterImage.DecodeFile(entry.ImageFile));
            else if (entry.RecordingPath is not null && File.Exists(entry.RecordingPath))
                Process.Start(new ProcessStartInfo(entry.RecordingPath) { UseShellExecute = true });
            else
                Hud.Show(Glyphs.Warning, "The file is no longer there");
        }
        catch (Exception ex)
        {
            Log.Error("Opening a recent capture failed", ex);
        }
    }

    private static string Relative(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return when.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }
}
