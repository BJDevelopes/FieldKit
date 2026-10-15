// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;
using Glyphs = Fieldkit.UI.Glyphs;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// The floating clipboard history (upstream ClipboardQuickPanelView): search, pinned and recent
/// entries, Ctrl+1…Ctrl+9, multi-select, a preview sidebar with editing, and "Clear unpinned".
/// Picking an entry copies it and pastes it into the window the user came from.
/// </summary>
public sealed class ClipboardHistoryWindow : FloatingWindow
{
    public const double CompactWidth = 560;
    public const double CompactHeight = 420;
    public const double MinCompactWidth = 560;
    public const double MinCompactHeight = 300;
    public const double PreviewWidth = 280;
    public const double PreviewExtraHeight = 80;
    private const double CardsHeight = 318;

    private static ClipboardHistoryWindow? _instance;

    private readonly Func<IReadOnlyList<ClipboardEntry>> _source;
    private readonly bool _isPreview;
    private readonly TextBox _search = new();
    private readonly TextBlock _searchPlaceholder;
    private readonly ListBox _list = new();
    private readonly Border _previewHost = new() { Width = PreviewWidth };
    private readonly Border _previewDivider = new() { Width = 1 };
    private readonly ContentControl _contentHost = new();
    private readonly StackPanel _footerLeft = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _count;
    private readonly Button _previewButton;
    private readonly HashSet<Guid> _batch = [];
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveSizeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private List<ClipboardEntry> _filtered = [];
    private Guid? _selectedId;
    private bool _selectionVisible;
    private Guid? _previewId;
    private Guid? _hoverCandidate;
    private bool _editing;
    private IntPtr _target;
    private bool _cards;
    private bool _sizing;

    /// <summary>Raised whenever selection, batch or hover changes, so realized rows can restyle.</summary>
    private event Action? RowStateChanged;

    private ClipboardHistoryWindow(Func<IReadOnlyList<ClipboardEntry>> source, bool isPreview)
    {
        _source = source;
        _isPreview = isPreview;
        Title = "Clipboard history";
        CloseOnEscape = false; // Esc backs out one layer at a time (selection, then the window)

        _searchPlaceholder = Ui.Text("Search copied text", 13, brush: "TextTertiary", wrap: false);
        _searchPlaceholder.IsHitTestVisible = false;
        _searchPlaceholder.Margin = new Thickness(9, 0, 0, 0);
        _searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _search.Padding = new Thickness(6, 5, 6, 5);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _selectedId = null;
            _selectionVisible = false;
            Refresh(scrollToTop: true);
        };
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        searchHost.Children.Add(_searchPlaceholder);

        _previewButton = new Button { Padding = new Thickness(8, 4, 8, 4), ToolTip = "Preview" };
        _previewButton.Click += (_, _) => TogglePreview();
        var close = HeaderButton(Glyphs.Close, "Close", Dismiss, 11);

        var toolbar = new Grid { Margin = new Thickness(12, 9, 12, 9) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(searchHost);
        Grid.SetColumn(_previewButton, 1);
        _previewButton.Margin = new Thickness(8, 0, 0, 0);
        toolbar.Children.Add(_previewButton);
        Grid.SetColumn(close, 2);
        close.Margin = new Thickness(6, 0, 0, 0);
        toolbar.Children.Add(close);

        ConfigureList();

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.Children.Add(_contentHost);
        _previewDivider.SetResourceReference(Border.BackgroundProperty, "Separator");
        Grid.SetColumn(_previewDivider, 1);
        middle.Children.Add(_previewDivider);
        Grid.SetColumn(_previewHost, 2);
        middle.Children.Add(_previewHost);

        _count = Ui.Text("0", 11, FontWeights.Medium, "TextSecondary", wrap: false);
        var countRow = Ui.Row(5, Ui.Icon(Glyphs.Clipboard, 11, "TextSecondary"), _count);
        var footer = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_footerLeft);
        Grid.SetColumn(countRow, 1);
        footer.Children.Add(countRow);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(root, toolbar, 0);
        AddRow(root, Ui.Divider(0), 1);
        AddRow(root, middle, 2);
        AddRow(root, Ui.Divider(0), 3);
        AddRow(root, footer, 4);
        Body = root;

        PreviewKeyDown += OnKey;
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_editing || _hoverCandidate is null) return;
            _previewId = _hoverCandidate;
            RefreshPreview();
        };
        _saveSizeTimer.Tick += (_, _) =>
        {
            _saveSizeTimer.Stop();
            SaveSize();
        };
        SizeChanged += (_, _) =>
        {
            if (!_sizing && IsVisible && !_cards) _saveSizeTimer.Start();
        };
        if (!isPreview) ClipboardHistory.Changed += OnHistoryChanged;
        Closed += (_, _) => ClipboardHistory.Changed -= OnHistoryChanged;
    }

    private static void AddRow(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private bool PreviewOn => _previewOverride ?? Settings.GetBool(ClipboardHistory.PreviewKey, false);

    // ------------------------------------------------------------ show/hide

    public static void Toggle()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.Dismiss();
            return;
        }
        Open();
    }

    public static void Open()
    {
        var target = PasteHelper.CaptureTarget();
        _instance ??= new ClipboardHistoryWindow(() => ClipboardHistory.Entries, isPreview: false);
        _instance.Present(target);
    }

    public static void HideIfOpen() => _instance?.Dismiss();

    private void Present(IntPtr target)
    {
        _target = target;
        _cards = Settings.GetString(ClipboardHistory.LayoutKey, "list") == "cards";
        _search.Text = string.Empty;
        _batch.Clear();
        _selectedId = null;
        _selectionVisible = false;
        _editing = false;
        _previewId = null;
        ApplySize();
        Refresh(scrollToTop: true);
        Opacity = 0;
        Show();
        UpdateLayout();
        Place();
        Opacity = 1;
        Activate();
        _search.Focus();
        Keyboard.Focus(_search);
    }

    public override void Dismiss()
    {
        _hoverTimer.Stop();
        _editing = false;
        Hide();
    }

    private void ApplySize()
    {
        _sizing = true;
        try
        {
            if (_cards)
            {
                Native.GetCursorPos(out var cursor);
                var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
                var scale = ScaleAt(cursor.X, cursor.Y);
                ResizeMode = ResizeMode.NoResize;
                MinWidth = MinHeight = 0;
                Width = work.Width / scale - 32 + 2 * ShadowMargin;
                Height = Math.Min(CardsHeight, work.Height / scale - 16) + 2 * ShadowMargin;
            }
            else
            {
                var preview = PreviewOn;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                var w = Settings.GetDouble(ClipboardHistory.WindowWidthKey, 0);
                var h = Settings.GetDouble(ClipboardHistory.WindowHeightKey, 0);
                if (!(w >= MinCompactWidth)) w = CompactWidth;
                if (!(h >= MinCompactHeight)) h = CompactHeight;
                MinWidth = MinCompactWidth + (preview ? PreviewWidth : 0) + 2 * ShadowMargin;
                MinHeight = MinCompactHeight + (preview ? PreviewExtraHeight : 0) + 2 * ShadowMargin;
                Width = w + (preview ? PreviewWidth : 0) + 2 * ShadowMargin;
                Height = h + (preview ? PreviewExtraHeight : 0) + 2 * ShadowMargin;
            }
        }
        finally
        {
            _sizing = false;
        }
    }

    private void SaveSize()
    {
        if (_cards) return;
        var preview = PreviewOn;
        var w = ActualWidth - 2 * ShadowMargin - (preview ? PreviewWidth : 0);
        var h = ActualHeight - 2 * ShadowMargin - (preview ? PreviewExtraHeight : 0);
        if (w >= MinCompactWidth) Settings.Set(ClipboardHistory.WindowWidthKey, Math.Round(w));
        if (h >= MinCompactHeight) Settings.Set(ClipboardHistory.WindowHeightKey, Math.Round(h));
    }

    /// <summary>List: near the caret of the app behind (or the pointer). Cards: along the bottom of the screen.</summary>
    private void Place()
    {
        Native.GetCursorPos(out var cursor);
        var scale = ScaleAt(cursor.X, cursor.Y);
        if (_cards)
        {
            var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
            MoveCardTo(work.Left + (int)(16 * scale), work.Bottom - (int)((ActualHeight - 2 * ShadowMargin + 8) * scale));
            return;
        }
        var caret = CaretPosition();
        var anchor = caret ?? cursor;
        var workArea = WindowHelper.GetWorkAreaAt(anchor.X, anchor.Y);
        var cardW = (ActualWidth - 2 * ShadowMargin) * scale;
        var cardH = (ActualHeight - 2 * ShadowMargin) * scale;
        var x = caret is null ? anchor.X - cardW / 2 : anchor.X - 24 * scale;
        var y = anchor.Y + 10 * scale;
        if (y + cardH > workArea.Bottom && anchor.Y - cardH - 34 * scale >= workArea.Top) y = anchor.Y - cardH - 34 * scale;
        MoveCardTo((int)x, (int)y);
    }

    // ---------------------------------------------------------------- list

    private void ConfigureList()
    {
        var template = new ControlTemplate(typeof(ListBox));
        var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
        scroller.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroller.SetValue(ScrollViewer.FocusableProperty, false);
        scroller.SetValue(ScrollViewer.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        scroller.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        template.VisualTree = scroller;
        _list.Template = template;
        _list.Focusable = false;
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Standard);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);

        var containerTemplate = new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        };
        var containerStyle = new Style(typeof(ListBoxItem));
        containerStyle.Setters.Add(new Setter(TemplateProperty, containerTemplate));
        containerStyle.Setters.Add(new Setter(FocusableProperty, false));
        containerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = containerStyle;

        var factory = new FrameworkElementFactory(typeof(ContentControl));
        factory.SetBinding(ContentControl.ContentProperty, new Binding { Converter = new RowConverter(this) });
        factory.SetValue(FocusableProperty, false);
        _list.ItemTemplate = new DataTemplate { VisualTree = factory };
    }

    private void OnHistoryChanged()
    {
        if (!IsVisible) return;
        var valid = ClipboardHistory.Entries.Select(e => e.Id).ToHashSet();
        _batch.RemoveWhere(id => !valid.Contains(id));
        Refresh(scrollToTop: false);
    }

    private IReadOnlyList<ClipboardEntry> Entries => _source();

    private string Query => _search.Text.Trim();

    private void Refresh(bool scrollToTop)
    {
        var entries = Entries;
        if (Query.Length == 0)
        {
            _filtered = entries.ToList();
        }
        else
        {
            var candidates = entries.Select((e, i) => new SearchCandidate(i, e.SearchableText("Image"), e.IsPinned)).ToList();
            _filtered = ClipboardSearch.Ranked(candidates, Query).Select(i => entries[i]).ToList();
        }
        var items = new List<object>();
        if (Query.Length == 0 && !_cards)
        {
            var pinned = _filtered.Where(e => e.IsPinned).ToList();
            var recent = _filtered.Where(e => !e.IsPinned).ToList();
            if (pinned.Count > 0)
            {
                items.Add(new HeaderItem("Pinned", false));
                items.AddRange(pinned.Select(e => new EntryItem(e)));
            }
            if (recent.Count > 0)
            {
                items.Add(new HeaderItem("Recent", pinned.Count > 0));
                items.AddRange(recent.Select(e => new EntryItem(e)));
            }
            // Ctrl+1…9 follow the order shown.
            _filtered = [.. pinned, .. recent];
        }
        else
        {
            items.AddRange(_filtered.Select(e => new EntryItem(e)));
        }

        if (_cards)
        {
            _list.ItemsPanel = new ItemsPanelTemplate(Factory(typeof(VirtualizingStackPanel), Orientation.Horizontal));
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.Padding = new Thickness(16, 12, 16, 12);
        }
        else
        {
            _list.ItemsPanel = new ItemsPanelTemplate(Factory(typeof(VirtualizingStackPanel), Orientation.Vertical));
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            _list.Padding = new Thickness(10, 6, 10, 6);
        }
        _list.ItemsSource = items;
        _contentHost.Content = _filtered.Count == 0
            ? EmptyState(entries.Count == 0 ? "No saved text" : "No results")
            : _list;
        if (scrollToTop && items.Count > 0) _list.ScrollIntoView(items[0]);
        _count.Text = entries.Count.ToString(CultureInfo.CurrentCulture);
        if (_previewId is Guid p && _filtered.All(e => e.Id != p)) _previewId = null;
        RefreshFooter();
        RefreshPreviewChrome();
        RefreshPreview();
        RowStateChanged?.Invoke();
    }

    private static FrameworkElementFactory Factory(Type type, Orientation orientation)
    {
        var f = new FrameworkElementFactory(type);
        f.SetValue(VirtualizingStackPanel.OrientationProperty, orientation);
        return f;
    }

    private static FrameworkElement EmptyState(string message)
    {
        var icon = Ui.Icon(Glyphs.Clipboard, 27, "TextTertiary");
        var text = Ui.Text(message, 12, FontWeights.Medium, "TextSecondary", wrap: false);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.Stack(10, icon, text);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Children = { stack }, MinHeight = 120 };
    }

    // ---------------------------------------------------------------- footer

    private void RefreshFooter()
    {
        _footerLeft.Children.Clear();
        var batch = BatchEntries();
        if (batch.Count > 0)
        {
            var paste = Ui.Button($"Paste {batch.Count}", () => PrimaryAction(paste: true), accent: true);
            var copy = Ui.Button($"Copy {batch.Count}", () => PrimaryAction(paste: false));
            var delete = Ui.Button($"Delete {batch.Count}", RemoveSelected);
            delete.SetResourceReference(ForegroundProperty, "Red");
            var clear = Ui.Button("Clear selection", () =>
            {
                _batch.Clear();
                RefreshFooter();
                RowStateChanged?.Invoke();
            });
            foreach (var b in new[] { paste, copy, delete, clear }) Small(b);
            Ui.Append(_footerLeft, 8, paste);
            Ui.Append(_footerLeft, 8, copy);
            Ui.Append(_footerLeft, 8, delete);
            Ui.Append(_footerLeft, 8, clear);
        }
        else
        {
            var clearRecent = Ui.Button("Clear unpinned", ConfirmClearRecent, glyph: Glyphs.Delete);
            Small(clearRecent);
            clearRecent.IsEnabled = Entries.Any(e => !e.IsPinned);
            _footerLeft.Children.Add(clearRecent);
        }
    }

    private static void Small(Button b)
    {
        b.Padding = new Thickness(9, 3, 9, 4);
        b.FontSize = 12;
    }

    private void ConfirmClearRecent()
    {
        var snapshot = ClipboardHistoryLogic.RecentSnapshot(Entries);
        if (snapshot.Count == 0) return;
        var ok = WithoutAutoDismiss(() => ConfirmDialog.Show(this, $"Clear unpinned ({snapshot.Count})?",
            "Pinned items stay, and so does anything copied after this. This can’t be undone.", "Clear unpinned"));
        if (ok) ClipboardHistory.ClearRecent(snapshot);
    }

    // ------------------------------------------------------------ selection

    private ClipboardEntry? SelectedEntry =>
        _filtered.FirstOrDefault(e => e.Id == _selectedId) ?? _filtered.FirstOrDefault();

    private int SelectedIndex
    {
        get
        {
            var i = _filtered.FindIndex(e => e.Id == _selectedId);
            return i < 0 ? 0 : i;
        }
    }

    private List<ClipboardEntry> BatchEntries() => Entries.Where(e => _batch.Contains(e.Id)).ToList();

    private List<ClipboardEntry> PrimaryEntries()
    {
        var batch = BatchEntries();
        if (batch.Count > 0) return batch;
        return SelectedEntry is { } e ? [e] : [];
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0)
        {
            _selectedId = null;
            _selectionVisible = false;
            return;
        }
        var index = _selectionVisible ? Math.Clamp(SelectedIndex + delta, 0, _filtered.Count - 1) : SelectedIndex;
        _selectedId = _filtered[index].Id;
        _selectionVisible = true;
        if (!_editing) _previewId = _selectedId;
        ScrollToSelection();
        RowStateChanged?.Invoke();
        RefreshPreview();
    }

    private void ScrollToSelection()
    {
        if (_list.ItemsSource is not List<object> items) return;
        var item = items.OfType<EntryItem>().FirstOrDefault(i => i.Entry.Id == _selectedId);
        if (item is not null) _list.ScrollIntoView(item);
    }

    private void ToggleBatch(ClipboardEntry entry)
    {
        _selectedId = entry.Id;
        if (!_batch.Remove(entry.Id)) _batch.Add(entry.Id);
        RefreshFooter();
        RowStateChanged?.Invoke();
    }

    private void ExtendBatch(ClipboardEntry entry)
    {
        var target = _filtered.FindIndex(e => e.Id == entry.Id);
        if (target < 0) return;
        foreach (var e in ClipboardHistoryLogic.RangeSelection(_filtered, SelectedIndex, target)) _batch.Add(e.Id);
        _selectedId = entry.Id;
        _selectionVisible = true;
        RefreshFooter();
        RowStateChanged?.Invoke();
    }

    private void SelectAll()
    {
        foreach (var e in _filtered) _batch.Add(e.Id);
        RefreshFooter();
        RowStateChanged?.Invoke();
    }

    // -------------------------------------------------------------- actions

    /// <summary>Copies the batch (or the highlighted entry) and pastes it into the app behind, or only copies.</summary>
    private void PrimaryAction(bool paste)
    {
        var entries = PrimaryEntries();
        if (entries.Count == 0) return;
        Run(entries, paste);
    }

    private void Run(IReadOnlyList<ClipboardEntry> entries, bool paste)
    {
        if (_isPreview) return;
        var target = _target;
        Dismiss();
        ClipboardHistory.Copy(entries, paste && target != IntPtr.Zero, target);
    }

    private void ActivateEntry(ClipboardEntry entry)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) ToggleBatch(entry);
        else if (mods.HasFlag(ModifierKeys.Shift)) ExtendBatch(entry);
        else if (_batch.Contains(entry.Id)) PrimaryAction(paste: true);
        else Run([entry], paste: true);
    }

    private void RemoveSelected()
    {
        var entries = PrimaryEntries();
        if (entries.Count == 0) return;
        var position = SelectedIndex;
        foreach (var e in entries) _batch.Remove(e.Id);
        ClipboardHistory.Remove(entries.Select(e => e.Id));
        if (_isPreview) return;
        _selectedId = _filtered.Count == 0 ? null : _filtered[Math.Min(position, _filtered.Count - 1)].Id;
        RowStateChanged?.Invoke();
    }

    private void TogglePreview()
    {
        Settings.Set(ClipboardHistory.PreviewKey, !PreviewOn);
        if (!_editing) _previewId ??= SelectedEntry?.Id;
        ApplySize();
        RefreshPreviewChrome();
        RefreshPreview();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_editing)
        {
            if (e.Key == Key.Escape)
            {
                _editing = false;
                RefreshPreview();
                e.Handled = true;
            }
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Escape:
                if (_batch.Count > 0)
                {
                    _batch.Clear();
                    RefreshFooter();
                    RowStateChanged?.Invoke();
                }
                else
                {
                    Dismiss();
                }
                e.Handled = true;
                return;
            case Key.Space when _selectionVisible && mods == ModifierKeys.None:
                TogglePreview();
                e.Handled = true;
                return;
            case Key.Enter:
                if (mods == ModifierKeys.Control) ToggleBatchSelected();
                else if (mods == ModifierKeys.Shift) PrimaryAction(paste: false);
                else if (mods == ModifierKeys.None) PrimaryAction(paste: true);
                else return;
                e.Handled = true;
                return;
            case Key.Down:
            case Key.N when mods == ModifierKeys.Control:
            case Key.Right when _cards && mods == ModifierKeys.None:
                MoveSelection(1);
                e.Handled = true;
                return;
            case Key.Up:
            case Key.P when mods == ModifierKeys.Control:
            case Key.Left when _cards && mods == ModifierKeys.None:
                MoveSelection(-1);
                e.Handled = true;
                return;
            case Key.C when mods == ModifierKeys.Control && _batch.Count > 0:
                PrimaryAction(paste: false);
                e.Handled = true;
                return;
            case Key.A when mods == ModifierKeys.Control && (_batch.Count > 0 || Query.Length == 0):
                SelectAll();
                e.Handled = true;
                return;
            case Key.P when mods == ModifierKeys.Alt:
                if (SelectedEntry is { } pinTarget) ClipboardHistory.TogglePin(pinTarget.Id);
                e.Handled = true;
                return;
            case Key.Delete or Key.Back when mods == ModifierKeys.Alt || (mods == ModifierKeys.Control && _batch.Count > 0):
                RemoveSelected();
                e.Handled = true;
                return;
        }
        if (mods == ModifierKeys.Control && key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
            if (index < _filtered.Count) Run([_filtered[index]], paste: true);
            e.Handled = true;
        }
    }

    private void ToggleBatchSelected()
    {
        if (SelectedEntry is { } entry) ToggleBatch(entry);
    }

    // -------------------------------------------------------------- preview

    private void RefreshPreviewChrome()
    {
        var on = PreviewOn && !_cards;
        _previewHost.Visibility = _previewDivider.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var label = Ui.Row(6, Ui.Icon(on ? Glyphs.Eye : Glyphs.Eye, 12, on ? "Accent" : "TextSecondary"),
            Ui.Text("Preview", 12, brush: on ? "Accent" : "TextSecondary", wrap: false));
        _previewButton.Content = label;
        _previewButton.Visibility = _cards ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshPreview()
    {
        if (!PreviewOn || _cards)
        {
            _previewHost.Child = null;
            return;
        }
        var entry = Entries.FirstOrDefault(e => e.Id == (_previewId ?? SelectedEntry?.Id));
        _previewHost.Child = ClipboardPreviewPane.Build(entry, _editing,
            onClose: TogglePreview,
            onEdit: () =>
            {
                _editing = true;
                RefreshPreview();
            },
            onCancel: () =>
            {
                _editing = false;
                RefreshPreview();
            },
            onSave: draft =>
            {
                if (entry is not null && ClipboardHistory.UpdateText(entry.Id, draft))
                {
                    _editing = false;
                    RefreshPreview();
                }
            },
            onCopy: () =>
            {
                if (entry is not null) Run([entry], paste: false);
            });
    }

    // ----------------------------------------------------------------- rows

    private sealed record HeaderItem(string Title, bool Divider);

    private sealed record EntryItem(ClipboardEntry Entry);

    private sealed class RowConverter(ClipboardHistoryWindow owner) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            HeaderItem h => owner.BuildHeader(h),
            EntryItem e => owner._cards ? owner.BuildCard(e.Entry) : owner.BuildRow(e.Entry),
            _ => null,
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private FrameworkElement BuildHeader(HeaderItem header)
    {
        var title = Ui.Text(header.Title.ToUpperInvariant(), 10, FontWeights.SemiBold, "TextSecondary", wrap: false);
        title.Margin = new Thickness(8, 6, 8, 6);
        if (!header.Divider) return title;
        var divider = Ui.Divider(5);
        divider.Margin = new Thickness(6, 5, 6, 5);
        return Ui.Stack(0, divider, title);
    }

    private int? ShortcutIndex(ClipboardEntry entry)
    {
        var i = _filtered.Take(9).ToList().FindIndex(e => e.Id == entry.Id);
        return i < 0 ? null : i;
    }

    private FrameworkElement BuildRow(ClipboardEntry entry)
    {
        var tokens = ClipboardSearch.Tokens(Query);
        var marker = new ContentControl { Width = 25, Height = 25, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        var content = EntryContent(entry, tokens, card: false);
        content.Margin = new Thickness(9, 0, 8, 0);
        content.VerticalAlignment = VerticalAlignment.Center;
        var trailing = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Focusable = false };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(marker);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var row = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 48,
            Child = grid,
            Background = Brushes.Transparent,
            ToolTip = SourceTooltip(entry),
        };
        var hovered = false;
        void Style()
        {
            var batch = _batch.Contains(entry.Id);
            var selected = _selectionVisible && _selectedId == entry.Id;
            if (batch || selected) row.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            else if (hovered) row.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
            else row.Background = Brushes.Transparent;
            marker.Content = Marker(entry, batch, hovered);
            trailing.Content = hovered ? HoverActions(entry) : TimeAndShortcut(entry);
        }
        Style();
        Wire(row, entry, v =>
        {
            hovered = v;
            Style();
        }, Style);
        return row;
    }

    private FrameworkElement BuildCard(ClipboardEntry entry)
    {
        var tokens = ClipboardSearch.Tokens(Query);
        var marker = new ContentControl { Width = 25, Height = 25, Focusable = false };
        var app = SourceName(entry);
        var header = Ui.Row(6, marker, Ui.Text(app ?? KindLabel(entry), 11, FontWeights.SemiBold, wrap: false));
        var body = EntryContent(entry, tokens, card: true);
        body.Margin = new Thickness(0, 8, 0, 8);
        var footer = new ContentControl { Focusable = false };
        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        stack.Children.Add(header);
        stack.Children.Add(footer);
        stack.Children.Add(body);
        var card = new Border
        {
            Width = 220,
            Margin = new Thickness(0, 0, 12, 0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = stack,
            ToolTip = SourceTooltip(entry),
        };
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        var hovered = false;
        void Style()
        {
            var batch = _batch.Contains(entry.Id);
            var selected = _selectionVisible && _selectedId == entry.Id;
            card.SetResourceReference(Border.BackgroundProperty, batch || selected ? "AccentSubtle" : hovered ? "ControlBackground" : "CardBackground");
            marker.Content = Marker(entry, batch, hovered);
            footer.Content = hovered ? HoverActions(entry) : TimeAndShortcut(entry, horizontal: true);
        }
        Style();
        Wire(card, entry, v =>
        {
            hovered = v;
            Style();
        }, Style);
        return card;
    }

    private void Wire(Border element, ClipboardEntry entry, Action<bool> setHovered, Action restyle)
    {
        element.MouseEnter += (_, _) =>
        {
            setHovered(true);
            _hoverCandidate = entry.Id;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        };
        element.MouseLeave += (_, _) =>
        {
            setHovered(false);
            if (_hoverCandidate == entry.Id) _hoverTimer.Stop();
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is not null) return;
            ActivateEntry(entry);
            e.Handled = true;
        };
        element.ContextMenu = EntryMenu(entry);
        void OnState() => restyle();
        element.Loaded += (_, _) =>
        {
            RowStateChanged -= OnState;
            RowStateChanged += OnState;
            restyle();
        };
        element.Unloaded += (_, _) => RowStateChanged -= OnState;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private ContextMenu EntryMenu(ClipboardEntry entry)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => Ui.Guard(action);
            return item;
        }
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var current = Entries.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
            var canReorder = Query.Length == 0;
            menu.Items.Add(Item("Paste", () => Run([current], paste: true)));
            menu.Items.Add(Item("Copy", () => Run([current], paste: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(current.IsPinned ? "Unpin" : "Pin", () => ClipboardHistory.TogglePin(current.Id)));
            menu.Items.Add(Item("Move up", () => ClipboardHistory.Move(current.Id, MoveDirection.Up),
                canReorder && ClipboardHistory.CanMove(current.Id, MoveDirection.Up)));
            menu.Items.Add(Item("Move down", () => ClipboardHistory.Move(current.Id, MoveDirection.Down),
                canReorder && ClipboardHistory.CanMove(current.Id, MoveDirection.Down)));
            menu.Items.Add(new Separator());
            var delete = Item("Delete item", () => ClipboardHistory.Remove([current.Id]));
            delete.SetResourceReference(ForegroundProperty, "Red");
            menu.Items.Add(delete);
        };
        menu.Items.Add(new MenuItem { Header = "Paste" }); // placeholder so the menu opens
        return menu;
    }

    private UIElement Marker(ClipboardEntry entry, bool batch, bool hovered)
    {
        if (batch) return Ui.Icon(CheckCircleGlyph, 16, "Accent");
        if (hovered) return Ui.Icon(CircleGlyph, 16, "Accent");
        if (entry.Kind == ClipboardEntryKind.Files && entry.FilePaths.Count >= 1 && !ClipboardHistoryLogic.IsImageFileName(entry.FilePaths[0])
            && AppIcons.ForPath(entry.FilePaths.FirstOrDefault(File.Exists)) is { } icon)
            return AppIcons.Element(icon, 20);
        var glyph = entry.IsPinned ? Glyphs.Pin : entry.Kind switch
        {
            ClipboardEntryKind.Image => Glyphs.Photo,
            ClipboardEntryKind.Files when entry.FilePaths.Any(ClipboardHistoryLogic.IsImageFileName) => Glyphs.Photo,
            ClipboardEntryKind.Files => Glyphs.Folder,
            _ => Glyphs.Document,
        };
        return Ui.Icon(glyph, 12, entry.IsPinned ? "Accent" : "TextSecondary");
    }

    private FrameworkElement TimeAndShortcut(ClipboardEntry entry, bool horizontal = false)
    {
        var time = Ui.Text(entry.CopiedAt.ToString("t", CultureInfo.CurrentCulture), 10, brush: "TextTertiary", wrap: false);
        var index = ShortcutIndex(entry);
        var shortcut = index is int i ? Ui.Text($"Ctrl+{i + 1}", 10, FontWeights.SemiBold, "TextSecondary", wrap: false) : null;
        if (horizontal)
        {
            var row = new DockPanel();
            row.Children.Add(time);
            if (shortcut is not null)
            {
                DockPanel.SetDock(shortcut, Dock.Right);
                shortcut.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(shortcut);
            }
            return row;
        }
        time.HorizontalAlignment = HorizontalAlignment.Right;
        var stack = Ui.Stack(3, time, shortcut);
        if (shortcut is not null) shortcut.HorizontalAlignment = HorizontalAlignment.Right;
        return stack;
    }

    private FrameworkElement HoverActions(ClipboardEntry entry)
    {
        var copy = Ui.IconButton(Glyphs.Copy, "Copy", () => Run([entry], paste: false), 11);
        var more = Ui.IconButton(MoreGlyph, "More", () => { }, 11);
        more.Click += (_, _) =>
        {
            var menu = EntryMenu(entry);
            menu.PlacementTarget = more;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return Ui.Row(4, copy, more);
    }

    private FrameworkElement EntryContent(ClipboardEntry entry, IReadOnlyList<string> tokens, bool card)
    {
        switch (entry.Kind)
        {
            case ClipboardEntryKind.Image:
            {
                var bytes = ClipboardHistoryStore.ReadImage(entry.ImageFile);
                var bitmap = bytes is null ? null : ClipboardImageCodec.Decode(bytes, card ? 400 : 240);
                var caption = Ui.Text($"Image · {entry.DimensionsLabel}", 11.5, brush: "TextSecondary", wrap: false);
                if (bitmap is null) return caption;
                var size = ClipboardImageCodec.Fit(bitmap.PixelWidth, bitmap.PixelHeight, card ? 196 : 220, card ? 150 : 56);
                var image = new Image { Source = bitmap, Width = size.Width, Height = size.Height, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return Ui.Stack(4, image, caption);
            }
            case ClipboardEntryKind.Files:
            {
                var title = entry.FilePaths.Count == 1 ? entry.FileNames.FirstOrDefault() ?? entry.Preview : $"{entry.FilePaths.Count} files";
                var detail = entry.FilePaths.Count == 1
                    ? Path.GetDirectoryName(entry.FilePaths[0]) ?? string.Empty
                    : string.Join(", ", entry.FileNames);
                var t = Highlighted(title, tokens, 12.5);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.TextWrapping = TextWrapping.NoWrap;
                var d = Ui.Text(detail, 10.5, brush: "TextTertiary", wrap: card);
                d.TextTrimming = TextTrimming.CharacterEllipsis;
                if (card) d.MaxHeight = 90;
                var stack = Ui.Stack(2, t, d);
                stack.ToolTip = string.Join("\n", entry.FilePaths);
                return stack;
            }
            default:
            {
                var preview = card ? ClipboardHistoryLogic.PreviewText(entry.Text, keepLineBreaks: true) : entry.Preview;
                if (preview.Length > 500) preview = preview[..500] + "…";
                var text = Highlighted(preview, tokens, 12.5);
                text.TextWrapping = TextWrapping.Wrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.LineHeight = 17;
                text.MaxHeight = card ? 17 * 8 : 34;
                return text;
            }
        }
    }

    private static TextBlock Highlighted(string text, IReadOnlyList<string> tokens, double size)
    {
        var tb = Ui.Text(string.Empty, size);
        if (tokens.Count == 0)
        {
            tb.Text = text;
            return tb;
        }
        var position = 0;
        foreach (var (start, length) in ClipboardSearch.HighlightRanges(text, tokens))
        {
            if (start > position) tb.Inlines.Add(new Run(text[position..start]));
            var match = new Run(text.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
            match.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            tb.Inlines.Add(match);
            position = start + length;
        }
        if (position < text.Length) tb.Inlines.Add(new Run(text[position..]));
        return tb;
    }

    private static string? SourceName(ClipboardEntry entry)
    {
        if (string.IsNullOrEmpty(entry.SourcePath)) return null;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(entry.SourcePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription;
        }
        catch
        {
            // fall back to the file name
        }
        return Path.GetFileNameWithoutExtension(entry.SourcePath);
    }

    private static string? SourceTooltip(ClipboardEntry entry) =>
        SourceName(entry) is { } name ? $"Copied from {name} · {entry.CopiedAt.ToString("g", CultureInfo.CurrentCulture)}" : null;

    private static string KindLabel(ClipboardEntry entry) => entry.Kind switch
    {
        ClipboardEntryKind.Image => "Image",
        ClipboardEntryKind.Files => entry.FilePaths.Count == 1 ? "File" : $"{entry.FilePaths.Count} files",
        _ => "Text",
    };

    // Segoe Fluent Icons not in UI/Glyphs (verified with --render-glyph-range).
    internal const string CircleGlyph = "\uEA3A";
    internal const string CheckCircleGlyph = "\uEC61";
    internal const string MoreGlyph = "\uE712";

    // -------------------------------------------------------------- preview

    /// <summary>Renders the window with sample entries to a PNG (for visual checks without a desktop session).</summary>
    public static void RenderPreview(string path, IReadOnlyList<ClipboardEntry> sample, bool preview, bool cards = false, string query = "")
    {
        var window = new ClipboardHistoryWindow(() => sample, isPreview: true)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            HideOnDeactivate = false,
        };
        PreviewRendering.ApplyTheme(window);
        window._cards = cards;
        window._previewOverride = preview;
        window.Width = (cards ? 1100 : CompactWidth + (preview ? PreviewWidth : 0)) + 2 * window.ShadowMargin;
        window.Height = (cards ? CardsHeight : CompactHeight + (preview ? PreviewExtraHeight : 0)) + 2 * window.ShadowMargin;
        window._search.Text = query;
        window.Refresh(scrollToTop: true);
        window._selectedId = sample.Skip(1).FirstOrDefault()?.Id;
        window._selectionVisible = true;
        window._previewId = window._selectedId;
        window.Show();
        window.RefreshPreviewChrome();
        window.RefreshPreview();
        window.RowStateChanged?.Invoke();
        PreviewRendering.Save(window, path);
        window.Close();
    }

    private bool? _previewOverride;
}
