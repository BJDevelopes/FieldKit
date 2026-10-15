// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>The three switcher looks (upstream SwitcherLayout).</summary>
public enum SwitcherLayoutMode { Windows, Icons, Simple }

/// <summary>Everything the switcher panel draws for one frame.</summary>
public sealed class SwitcherViewState
{
    public required IReadOnlyList<SwitcherItem> Items { get; init; }
    public required SwitcherLayoutMode Mode { get; init; }
    public int Selected { get; set; }
    public string Query { get; set; } = string.Empty;
    public bool SearchPinned { get; set; }
    public double Scale { get; init; } = 1;
    public bool Minimal { get; init; }
    public bool Thumbnails { get; init; } = true;
    public int? MaxColumns { get; init; }
    public string? HintApps { get; init; }
    public string? HintWindows { get; init; }
}

/// <summary>
/// The switcher panel: a non-activating, top-most window centred on a display, with DWM live
/// thumbnails placed over the card placeholders. Keyboard input arrives through the service's
/// hook; the mouse works directly (click a card, hover buttons to close or minimize).
/// </summary>
internal sealed class SwitcherWindow : OverlayWindow
{
    private const double Gap = 10;
    private const double Pad = 14;
    // The search line sits in a spaced stack above the content even while collapsed.
    private const double StackSlack = 10;
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };
    private readonly List<(int Index, FrameworkElement Card, FrameworkElement Thumb)> _cards = [];
    private ThumbnailHost? _thumbs;
    private SwitcherViewState? _state;
    private int _columns = 1;
    private TextBlock? _searchText;

    public event Action<int>? ItemClicked;
    public event Action<int>? CloseClicked;
    public event Action<int>? MinimizeClicked;

    public SwitcherWindow() : base(noActivate: true)
    {
        Title = "Fieldkit switcher";
        var border = new Border { BorderThickness = new Thickness(1), Child = _scroll, Padding = new Thickness(Pad) };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        Content = border;
        _scroll.ScrollChanged += (_, _) => PlaceThumbnails();
        SourceInitialized += (_, _) => _thumbs = new ThumbnailHost(Handle);
        Closed += (_, _) => _thumbs?.Dispose();
    }

    public int Columns => _columns;

    private (FrameworkElement Content, Size Size) Build(SwitcherViewState state, double maxWidth) => state.Mode switch
    {
        SwitcherLayoutMode.Icons => BuildIcons(state, maxWidth),
        SwitcherLayoutMode.Simple => BuildSimple(state, maxWidth),
        _ => BuildGrid(state, maxWidth),
    };

    /// <summary>The panel's content laid out at its natural size, without a window (previews in tests).</summary>
    public static FrameworkElement PreviewContent(SwitcherViewState state, double maxWidth)
    {
        var window = new SwitcherWindow();
        var (content, size) = window.Build(state, maxWidth);
        window.Close();
        var root = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad),
            Width = size.Width + 2 * Pad + 2,
            Height = size.Height + 2 * Pad + 2 + StackSlack,
            Child = content,
        };
        root.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        root.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        return root;
    }

    /// <summary>Rebuilds the panel for <paramref name="state"/> and sizes it for <paramref name="workArea"/> (px).</summary>
    public void Render(SwitcherViewState state, Native.RECT workArea)
    {
        _state = state;
        var scale = WindowHelper.GetScaleAt(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2);
        var maxWidth = workArea.Width / scale * 0.9;
        var maxHeight = workArea.Height / scale * 0.85;
        var (content, size) = Build(state, maxWidth);
        _scroll.Content = content;
        var width = size.Width + 2 * Pad + 2;
        var height = Math.Min(size.Height + 2 * Pad + 2 + StackSlack, maxHeight);
        var x = workArea.Left + (int)((workArea.Width - width * scale) / 2);
        var y = workArea.Top + (int)((workArea.Height - height * scale) / 2);
        PlaceAt(x, y, width, height);
        ShowQuietly();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollSelectedIntoView();
            PlaceThumbnails();
        });
    }

    /// <summary>Moves the highlight without rebuilding (grid and simple looks).</summary>
    public void Select(int index)
    {
        if (_state is null) return;
        _state.Selected = index;
        if (_state.Mode == SwitcherLayoutMode.Windows)
        {
            foreach (var (i, card, _) in _cards) Highlight(card, i == index);
            ScrollSelectedIntoView();
        }
    }

    public void Dismiss()
    {
        _thumbs?.Clear();
        _cards.Clear();
        _scroll.Content = null;
        _state = null;
        Hide();
    }

    // ------------------------------------------------------------------ windows grid

    private (FrameworkElement, Size) BuildGrid(SwitcherViewState state, double maxWidth)
    {
        _cards.Clear();
        var cardW = 288 * state.Scale;
        var cardH = 214 * state.Scale;
        const double inner = 10;
        // Two lines of text under the thumbnail (13 + 10.5 pt with spacing), plus the 2 px selection border.
        var titleH = state.Minimal ? 0 : 42;
        var thumbW = cardW - 2 * inner - 4;
        var thumbH = cardH - 2 * inner - 4 - titleH;
        _columns = SwitcherLogic.Columns(state.Items.Count, maxWidth - 2 * Pad, cardW + Gap, state.MaxColumns);
        var grid = new UniformGrid { Columns = _columns };
        for (var i = 0; i < state.Items.Count; i++)
        {
            var item = state.Items[i];
            var index = i;
            var thumb = new Border { Width = thumbW, Height = thumbH, CornerRadius = new CornerRadius(6) };
            var fallback = AppIcons.Element(IconFor(item), 80 * state.Scale * 0.6);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            thumb.Child = fallback;
            var thumbHost = new Grid();
            thumbHost.Children.Add(thumb);
            if (!state.Minimal)
            {
                var badge = AppIcons.Element(IconFor(item), 26 * Math.Min(state.Scale, 1.3));
                badge.HorizontalAlignment = HorizontalAlignment.Left;
                badge.VerticalAlignment = VerticalAlignment.Bottom;
                badge.Margin = new Thickness(6, 0, 0, 6);
                thumbHost.Children.Add(badge);
                var status = StatusBadges(item);
                if (status is not null) thumbHost.Children.Add(status);
            }

            var stack = new StackPanel();
            stack.Children.Add(thumbHost);
            if (!state.Minimal)
            {
                var title = Ui.Text(item.IsAppEntry ? item.AppName : item.Title, 13, FontWeights.Medium, wrap: false);
                title.TextAlignment = TextAlignment.Center;
                title.Margin = new Thickness(4, 7, 4, 0);
                var subtitle = Ui.Text(item.IsAppEntry ? "No open window" : item.AppName, 10.5, brush: "TextSecondary", wrap: false);
                subtitle.TextAlignment = TextAlignment.Center;
                subtitle.Margin = new Thickness(4, 2, 4, 0);
                stack.Children.Add(title);
                stack.Children.Add(subtitle);
            }

            var cardGrid = new Grid();
            cardGrid.Children.Add(stack);
            if (!state.Minimal && !item.IsAppEntry)
            {
                var buttons = Ui.Row(2,
                    HoverButton(Glyphs.Remove, "Minimize window", () => MinimizeClicked?.Invoke(index)),
                    HoverButton(Glyphs.Close, "Close window", () => CloseClicked?.Invoke(index)));
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                buttons.VerticalAlignment = VerticalAlignment.Top;
                buttons.Margin = new Thickness(0, 2, 2, 0);
                buttons.Visibility = Visibility.Collapsed;
                cardGrid.Children.Add(buttons);
                cardGrid.MouseEnter += (_, _) => buttons.Visibility = Visibility.Visible;
                cardGrid.MouseLeave += (_, _) => buttons.Visibility = Visibility.Collapsed;
            }

            var card = new Border
            {
                Width = cardW,
                Height = cardH,
                Padding = new Thickness(inner),
                Margin = new Thickness(Gap / 2),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                Child = cardGrid,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            card.MouseLeftButtonUp += (_, _) => ItemClicked?.Invoke(index);
            card.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle && !item.IsAppEntry) CloseClicked?.Invoke(index);
            };
            Highlight(card, i == state.Selected);
            _cards.Add((i, card, thumb));
            grid.Children.Add(card);
        }
        var rows = (int)Math.Ceiling(state.Items.Count / (double)_columns);
        var content = Ui.Stack(8, SearchLine(state), grid);
        var searchHeight = state.Query.Length > 0 || state.SearchPinned ? 30 : 0;
        return (content, new Size(_columns * (cardW + Gap), rows * (cardH + Gap) + searchHeight));
    }

    private static FrameworkElement? StatusBadges(SwitcherItem item)
    {
        if (!item.IsMinimized && !item.OnOtherDesktop) return null;
        var badge = Ui.Badge(item.OnOtherDesktop ? "Other desktop" : "Minimized");
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Bottom;
        badge.Margin = new Thickness(0, 0, 4, 4);
        return badge;
    }

    private static Button HoverButton(string glyph, string tooltip, Action onClick)
    {
        var b = Ui.IconButton(glyph, tooltip, onClick, 10, "TextPrimary");
        b.Padding = new Thickness(4);
        b.SetResourceReference(BackgroundProperty, "ControlBackground");
        return b;
    }

    private static void Highlight(Border card, bool selected)
    {
        if (selected)
        {
            card.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        }
        else
        {
            card.Background = Brushes.Transparent;
            card.BorderBrush = Brushes.Transparent;
        }
    }

    private static void Highlight(FrameworkElement card, bool selected)
    {
        if (card is Border b) Highlight(b, selected);
    }

    private FrameworkElement SearchLine(SwitcherViewState state)
    {
        _searchText = Ui.Text(string.Empty, 13, FontWeights.Medium, wrap: false);
        var line = Ui.Row(8, Ui.Icon(Glyphs.Search, 13, "TextSecondary"), _searchText);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        _searchText.Text = state.Query.Length > 0 ? state.Query : "Type to search";
        if (state.Query.Length == 0) _searchText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        line.Visibility = state.Query.Length > 0 || state.SearchPinned ? Visibility.Visible : Visibility.Collapsed;
        return line;
    }

    private static ImageSource? IconFor(SwitcherItem item) =>
        item.IsAppEntry ? AppIcons.ForPath(item.AppPath) : AppIcons.ForWindow(item.Handle);

    // ------------------------------------------------------------------ large icons

    private (FrameworkElement, Size) BuildIcons(SwitcherViewState state, double maxWidth)
    {
        _cards.Clear();
        var groups = SwitcherLogic.GroupByApp(state.Items);
        var selectedGroup = Math.Max(0, groups.FindIndex(g => g.ItemIndices.Contains(state.Selected)));
        var iconScale = Math.Min(state.Scale, 1.15);
        var icon = 68 * iconScale;
        var selectedIcon = 78 * iconScale;
        var cell = selectedIcon + 24;
        _columns = SwitcherLogic.Columns(groups.Count, maxWidth - 2 * Pad, cell, state.MaxColumns);

        // Previews of the selected app's windows, above the icons.
        var group = groups.Count > 0 ? groups[selectedGroup] : null;
        var previewRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var thumbW = 220 * state.Scale;
        var thumbH = 140 * state.Scale;
        double previewHeight = 0;
        if (group is not null && state.Thumbnails)
        {
            foreach (var i in group.ItemIndices.Where(i => !state.Items[i].IsAppEntry))
            {
                var thumb = new Border { Width = thumbW, Height = thumbH };
                var index = i;
                var card = new Border
                {
                    Padding = new Thickness(6),
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(2),
                    Child = Ui.Stack(4, thumb, state.Minimal ? null : Centered(Ui.Text(state.Items[i].Title, 11.5, wrap: false), thumbW)),
                    Cursor = Cursors.Hand,
                };
                card.MouseLeftButtonUp += (_, _) => ItemClicked?.Invoke(index);
                Highlight(card, i == state.Selected);
                _cards.Add((i, card, thumb));
                previewRow.Children.Add(card);
            }
            previewHeight = previewRow.Children.Count > 0 ? thumbH + (state.Minimal ? 24 : 44) : 0;
        }

        var iconRow = new UniformGrid { Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center };
        for (var g = 0; g < groups.Count; g++)
        {
            var app = groups[g];
            var first = state.Items[app.ItemIndices[0]];
            var selected = g == selectedGroup;
            var image = AppIcons.Element(IconFor(first), selected ? selectedIcon : icon);
            var tile = new Border
            {
                Width = cell,
                Height = cell,
                CornerRadius = new CornerRadius(16),
                Child = image,
                Cursor = Cursors.Hand,
            };
            if (selected) tile.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            var firstIndex = app.ItemIndices[0];
            tile.MouseLeftButtonUp += (_, _) => ItemClicked?.Invoke(firstIndex);
            var count = app.ItemIndices.Count(i => !state.Items[i].IsAppEntry);
            var host = new Grid();
            host.Children.Add(tile);
            if (count > 1)
            {
                var badge = Ui.Badge(count.ToString(), "Accent", "TextOnAccent");
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.VerticalAlignment = VerticalAlignment.Top;
                host.Children.Add(badge);
            }
            iconRow.Children.Add(host);
        }
        var rows = (int)Math.Ceiling(groups.Count / (double)_columns);

        var selectedItem = state.Items.Count > 0 ? state.Items[Math.Clamp(state.Selected, 0, state.Items.Count - 1)] : null;
        var name = Ui.Text(selectedItem?.AppName ?? string.Empty, 14, FontWeights.SemiBold, wrap: false);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        var hints = Hints(state);

        var content = Ui.Stack(10, SearchLine(state), previewRow, iconRow, name, hints);
        var width = Math.Max(_columns * cell, Math.Min(maxWidth - 2 * Pad, (group?.ItemIndices.Count ?? 0) * (thumbW + 20)));
        var height = previewHeight + rows * cell + 30 + (hints is null ? 0 : 26) + 30;
        return (content, new Size(Math.Max(width, 280), height));
    }

    private static FrameworkElement Centered(TextBlock text, double width)
    {
        text.MaxWidth = width;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    private static FrameworkElement? Hints(SwitcherViewState state)
    {
        if (state.HintApps is null && state.HintWindows is null) return null;
        var row = Ui.Row(14,
            state.HintApps is null ? null : Ui.Row(6, Ui.Badge(state.HintApps), Ui.Text("Apps", 11.5, brush: "TextSecondary")),
            state.HintWindows is null ? null : Ui.Row(6, Ui.Badge(state.HintWindows), Ui.Text("Windows", 11.5, brush: "TextSecondary")));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        return row;
    }

    // ------------------------------------------------------------------ simple

    private (FrameworkElement, Size) BuildSimple(SwitcherViewState state, double maxWidth)
    {
        _cards.Clear();
        const double icon = 48;
        const double cell = icon + 16;
        _columns = SwitcherLogic.Columns(state.Items.Count, maxWidth - 2 * Pad, cell, state.MaxColumns);
        var selected = state.Items.Count > 0 ? state.Items[Math.Clamp(state.Selected, 0, state.Items.Count - 1)] : null;
        var title = Ui.Text(selected is null ? string.Empty : selected.IsAppEntry ? selected.AppName : selected.Title, 14, FontWeights.SemiBold, wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.MaxWidth = Math.Max(_columns * cell, 320);
        var strip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 7), Child = title, HorizontalAlignment = HorizontalAlignment.Center };
        strip.SetResourceReference(Border.BackgroundProperty, "ControlBackground");

        var row = new UniformGrid { Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < state.Items.Count; i++)
        {
            var index = i;
            var tile = new Border
            {
                Width = cell,
                Height = cell,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                Child = AppIcons.Element(IconFor(state.Items[i]), icon),
                Cursor = Cursors.Hand,
                Opacity = state.Items[i].IsMinimized ? 0.6 : 1,
            };
            tile.MouseLeftButtonUp += (_, _) => ItemClicked?.Invoke(index);
            Highlight(tile, i == state.Selected);
            row.Children.Add(tile);
        }
        var rows = (int)Math.Ceiling(state.Items.Count / (double)_columns);
        var hints = Hints(state);
        var content = Ui.Stack(10, SearchLine(state), strip, row, hints);
        return (content, new Size(Math.Max(_columns * cell, 320), 40 + rows * cell + 20 + (hints is null ? 0 : 26) + 30));
    }

    // ------------------------------------------------------------------ thumbnails

    private void ScrollSelectedIntoView()
    {
        if (_state is null) return;
        var card = _cards.FirstOrDefault(c => c.Index == _state.Selected).Card;
        card?.BringIntoView();
    }

    /// <summary>Registers/moves DWM thumbnails onto the visible placeholders, hiding any not fully in view.</summary>
    private void PlaceThumbnails()
    {
        if (_thumbs is null || _state is null || !IsVisible) return;
        if (!_state.Thumbnails)
        {
            _thumbs.Clear();
            return;
        }
        var viewport = ThumbnailHost.DeviceBounds(_scroll, this);
        var shown = new List<IntPtr>();
        foreach (var (index, _, placeholder) in _cards)
        {
            var item = _state.Items[index];
            if (item.IsAppEntry) continue;
            var rect = ThumbnailHost.DeviceBounds(placeholder, this);
            if (rect.IsEmpty || !viewport.Contains(rect))
            {
                _thumbs.Hide(item.Handle);
                continue;
            }
            if (_thumbs.Show(item.Handle, rect) is not null)
            {
                shown.Add(item.Handle);
                if (placeholder is Border b) b.Child = null; // the live thumbnail replaces the icon
            }
        }
        _thumbs.Prune(shown);
    }
}
