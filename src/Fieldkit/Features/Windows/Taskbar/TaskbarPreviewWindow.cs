// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// The Taskbar Preview panel: live DWM thumbnails of every window of the hovered app, above
/// (or beside) its taskbar button, with minimize/restore and close buttons. Never takes focus.
/// </summary>
internal sealed class TaskbarPreviewWindow : OverlayWindow
{
    private readonly WrapPanel _row = new();
    private readonly List<(AppWindow Window, Border Placeholder)> _cards = [];
    private ThumbnailHost? _thumbs;
    private Rect _screenRect;

    public event Action<AppWindow>? WindowChosen;
    public event Action<AppWindow>? CloseRequested;
    public event Action<AppWindow>? MinimizeRequested;
    public event Action? Emptied;

    public TaskbarPreviewWindow() : base(noActivate: true)
    {
        Title = "Fieldkit taskbar preview";
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = _row };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        Content = border;
        SourceInitialized += (_, _) => _thumbs = new ThumbnailHost(Handle);
        Closed += (_, _) => _thumbs?.Dispose();
    }

    public TaskbarHover? Shown { get; private set; }

    /// <summary>The panel's screen rectangle in physical pixels (for hover tests from other threads).</summary>
    public Rect ScreenRect => _screenRect;

    public void Show(TaskbarHover hover, double scale, bool minimal, bool thumbnails, string closeTooltip)
    {
        Shown = hover;
        _cards.Clear();
        _row.Children.Clear();
        _thumbs?.Clear();
        var thumbW = 200 * scale;
        var thumbH = 125 * scale;
        foreach (var w in hover.Windows)
        {
            var window = w;
            var placeholder = new Border { Width = thumbW, Height = thumbH, CornerRadius = new CornerRadius(6) };
            placeholder.Child = AppIcons.Element(AppIcons.ForWindow(w.Handle), 48);
            var stack = new StackPanel();
            if (!minimal)
            {
                var title = Ui.Text(w.Title, 12, FontWeights.Medium, wrap: false);
                title.Margin = new Thickness(6, 0, 0, 0);
                var minimize = Ui.IconButton(w.IsMinimized ? Glyphs.Add : Glyphs.Remove,
                    w.IsMinimized ? "Restore window" : "Minimize window", () => MinimizeRequested?.Invoke(window), 10);
                var close = Ui.IconButton(Glyphs.Close, closeTooltip, () => CloseRequested?.Invoke(window), 10);
                var header = new Grid { Margin = new Thickness(0, 0, 0, 6), Width = thumbW };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(AppIcons.Element(AppIcons.ForWindow(w.Handle), 16));
                Grid.SetColumn(title, 1);
                header.Children.Add(title);
                var buttons = Ui.Row(0, minimize, close);
                Grid.SetColumn(buttons, 2);
                header.Children.Add(buttons);
                stack.Children.Add(header);
            }
            var thumbHost = new Grid();
            thumbHost.Children.Add(placeholder);
            if (!minimal && (w.IsMinimized || w.OnOtherDesktop))
            {
                var badge = Ui.Badge(w.OnOtherDesktop ? "Other desktop" : "Minimized");
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.VerticalAlignment = VerticalAlignment.Bottom;
                badge.Margin = new Thickness(0, 0, 4, 4);
                thumbHost.Children.Add(badge);
            }
            stack.Children.Add(thumbHost);
            var card = new Border
            {
                Padding = new Thickness(8),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Child = stack,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = minimal ? w.Title : null,
            };
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
                WindowChosen?.Invoke(window);
            };
            card.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle) CloseRequested?.Invoke(window);
            };
            _cards.Add((w, placeholder));
            _row.Children.Add(card);
        }

        Place(hover.Button.Bounds, hover.Windows.Count, thumbW, thumbH + (minimal ? 0 : 30));
        ShowQuietly();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceThumbnails(thumbnails));
    }

    private static Button? FindButton(DependencyObject d)
    {
        for (var current = d; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button b) return b;
        return null;
    }

    /// <summary>Drops a card (closed window) and hides the panel when none is left.</summary>
    public void RemoveWindow(IntPtr hwnd)
    {
        var index = _cards.FindIndex(c => c.Window.Handle == hwnd);
        if (index < 0) return;
        _thumbs?.Hide(hwnd);
        _row.Children.RemoveAt(index);
        _cards.RemoveAt(index);
        if (_cards.Count == 0)
        {
            Dismiss();
            Emptied?.Invoke();
            return;
        }
        if (Shown is not null) Place(Shown.Button.Bounds, _cards.Count, _cards[0].Placeholder.Width, _cards[0].Placeholder.Height + 30);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceThumbnails(true));
    }

    private void Place(Rect button, int count, double cardW, double cardH)
    {
        var center = new Native.POINT((int)(button.X + button.Width / 2), (int)(button.Y + button.Height / 2));
        var monitorInfo = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST));
        var monitor = monitorInfo.rcMonitor;
        var work = monitorInfo.rcWork;
        var scale = WindowHelper.GetScaleAt(center.X, center.Y);
        var maxCards = Math.Max(1, (int)((work.Width / scale - 40) / (cardW + 20)));
        var width = Math.Min(count, maxCards) * (cardW + 20) + 18;
        var rows = (int)Math.Ceiling(count / (double)maxCards);
        var height = rows * (cardH + 20) + 18;
        _row.Width = Math.Min(count, maxCards) * (cardW + 20);
        var wPx = width * scale;
        var hPx = height * scale;
        double x, y;
        if (button.Bottom >= monitor.Bottom - button.Height * 1.5)
        {
            x = center.X - wPx / 2;
            y = button.Top - hPx - 8 * scale;
        }
        else if (button.Top <= monitor.Top + button.Height * 1.5 && button.Width > button.Height)
        {
            x = center.X - wPx / 2;
            y = button.Bottom + 8 * scale;
        }
        else if (button.Left <= monitor.Left + button.Width * 1.5)
        {
            x = button.Right + 8 * scale;
            y = center.Y - hPx / 2;
        }
        else
        {
            x = button.Left - wPx - 8 * scale;
            y = center.Y - hPx / 2;
        }
        x = Math.Clamp(x, monitor.Left + 4, Math.Max(monitor.Left + 4, monitor.Right - wPx - 4));
        y = Math.Clamp(y, monitor.Top + 4, Math.Max(monitor.Top + 4, monitor.Bottom - hPx - 4));
        _screenRect = new Rect(x, y, wPx, hPx);
        PlaceAt((int)x, (int)y, width, height);
    }

    private void PlaceThumbnails(bool thumbnails)
    {
        if (_thumbs is null || !IsVisible) return;
        if (!thumbnails)
        {
            _thumbs.Clear();
            return;
        }
        foreach (var (window, placeholder) in _cards)
        {
            var rect = ThumbnailHost.DeviceBounds(placeholder, this);
            if (rect.IsEmpty) continue;
            if (_thumbs.Show(window.Handle, rect) is not null) placeholder.Child = null;
        }
    }

    public void Dismiss()
    {
        Shown = null;
        _thumbs?.Clear();
        _cards.Clear();
        _row.Children.Clear();
        _screenRect = Rect.Empty;
        Hide();
    }
}
