// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>
/// The Scratchpad: a floating, resizable pad with up to 12 tabs, autosave, Markdown
/// formatting marks and preview, find, copy, clear and save-as-file.
/// </summary>
public sealed class ScratchpadWindow : Window
{
    public const string CloseOnClickOutsideKey = "scratchpad.closeOnClickOutside";
    public const string TextSizeKey = "scratchpad.textSize";
    public const string BackgroundKey = "scratchpad.background";
    public const string ShortcutKey = "scratchpad.shortcut";
    public const string DefaultShortcut = "Ctrl+Alt+Win+N";

    private const string TextFormatGlyph = "\uE8D2";
    private const string SaveGlyph = "\uE74E";
    private const string MoreGlyph = "\uE712";

    private static ScratchpadWindow? _instance;
    private static bool _placed;

    private readonly TextBox _editor = new();
    private readonly FlowDocumentScrollViewer _preview = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _tabScroll;
    private readonly Border _banner;
    private readonly Border _formatBar;
    private readonly Border _background;
    private readonly TextBlock _placeholder;
    private readonly Grid _findBar;
    private readonly TextBox _find = new() { MinWidth = 140 };
    private readonly StackPanel _footer;
    private Button _pin = null!;
    private Button _formatToggle = null!;
    private Button _previewToggle = null!;
    private Button _copy = null!;
    private bool _pinned;
    private bool _previewing;
    private bool _formatting;
    private bool _dialogOpen;
    private bool _loadingText;

    // ---- Instance management ---------------------------------------------------------

    /// <summary>Shortcut behaviour: hidden → show; visible but not focused → focus; focused → hide.</summary>
    public static void Toggle()
    {
        if (_instance is { IsVisible: true })
        {
            if (_instance.IsActive) _instance.HidePad();
            else _instance.FocusPad();
            return;
        }
        Open();
    }

    public static void Open()
    {
        if (!ScratchpadStore.EnsureLoaded())
        {
            Notifier.Show("Scratchpad", "Your notes could not be opened. They were left unchanged.", warning: true);
            return;
        }
        _instance ??= new ScratchpadWindow();
        _instance.ShowPad();
    }

    public static void CloseInstance()
    {
        ScratchpadStore.SaveNow();
        _instance?.Close();
        _instance = null;
    }

    private ScratchpadWindow()
    {
        Title = "Scratchpad";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 380;
        Height = 300;
        MinWidth = 280;
        MinHeight = 220;
        ResizeMode = ResizeMode.CanResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            UseAeroCaptionButtons = false,
        });

        // Header (drag handle) -------------------------------------------------------
        var title = Ui.Text("Scratchpad", 12, FontWeights.SemiBold, "TextSecondary", wrap: false);
        title.VerticalAlignment = VerticalAlignment.Center;
        _pin = Chrome(Ui.IconButton(Glyphs.Pin, "Keep open", TogglePin, 12));
        var close = Chrome(Ui.IconButton(Glyphs.Close, "Close", HidePad, 11));
        var header = Ui.Split(title, Ui.Row(2, _pin, close));
        header.Height = 34;
        header.Margin = new Thickness(12, 0, 6, 0);

        // Tab bar ------------------------------------------------------------------
        _tabScroll = new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _tabScroll.PreviewMouseWheel += (_, e) =>
        {
            _tabScroll.ScrollToHorizontalOffset(_tabScroll.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        };
        var add = Ui.IconButton(Glyphs.Add, "New scratchpad", NewPad, 11);
        Button? more = null;
        more = Ui.IconButton(MoreGlyph, "Scratchpad actions", () => EnergyUi.ShowMenu(more!,
        [
            ("Rename scratchpad", () => RenamePad(ScratchpadStore.Selected.Id)),
            ("Close scratchpad", () => ClosePad(ScratchpadStore.Selected.Id)),
        ]), 11);
        var tabRow = new Grid { Height = 32, Margin = new Thickness(8, 0, 6, 0) };
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabRow.Children.Add(_tabScroll);
        var tabButtons = Ui.Row(0, add, more);
        Grid.SetColumn(tabButtons, 1);
        tabRow.Children.Add(tabButtons);
        add.Tag = "add";

        // Banner ------------------------------------------------------------------
        _banner = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            Child = Ui.Row(6, Ui.Icon(Glyphs.Warning, 12, "Orange"),
                Ui.Text("Your notes could not be saved. Copy them elsewhere before quitting.", 11, brush: "Orange")),
            Visibility = Visibility.Collapsed,
        };

        // Editor and preview ------------------------------------------------------------
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.BorderThickness = new Thickness(0);
        _editor.Background = Brushes.Transparent;
        _editor.Padding = new Thickness(5, 4, 5, 4);
        _editor.VerticalContentAlignment = VerticalAlignment.Top;
        _editor.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _editor.TextChanged += (_, _) =>
        {
            if (!_loadingText) ScratchpadStore.UpdateText(_editor.Text);
            UpdateEmptyState();
        };
        _placeholder = Ui.Text("Type anything. It saves by itself.", 13, brush: "TextTertiary");
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(9, 6, 9, 0);
        _preview.Visibility = Visibility.Collapsed;
        _preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var editorHost = new Grid();
        editorHost.Children.Add(_editor);
        editorHost.Children.Add(_placeholder);
        editorHost.Children.Add(_preview);

        // Find bar -------------------------------------------------------------------
        _findBar = new Grid { Margin = new Thickness(8, 2, 8, 2), Visibility = Visibility.Collapsed };
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _find.ToolTip = "Find";
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindNext(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFind();
                e.Handled = true;
            }
        };
        _findBar.Children.Add(_find);
        var findButtons = Ui.Row(0,
            Ui.IconButton(Glyphs.ChevronUp, "Previous match", () => FindNext(-1), 10),
            Ui.IconButton(Glyphs.ChevronDown, "Next match", () => FindNext(1), 10),
            Ui.IconButton(Glyphs.Close, "Close", CloseFind, 10));
        Grid.SetColumn(findButtons, 1);
        _findBar.Children.Add(findButtons);

        // Format bar --------------------------------------------------------------
        var formatButtons = Ui.Row(2,
            FormatButton("B", "Bold", FormatMark.Bold, FontWeights.Bold),
            FormatButton("I", "Italic", FormatMark.Italic, FontWeights.Normal, italic: true),
            FormatButton("S", "Strikethrough", FormatMark.Strikethrough, FontWeights.Normal, strike: true),
            FormatButton("H", "Heading", FormatMark.Heading, FontWeights.Bold),
            FormatIcon(Glyphs.List, "Bulleted list", FormatMark.Bullet),
            FormatButton("1.", "Numbered list", FormatMark.Numbered, FontWeights.SemiBold),
            FormatButton("❝", "Quote", FormatMark.Quote, FontWeights.Normal),
            FormatIcon("\uE943", "Code", FormatMark.Code),
            FormatIcon(Glyphs.Link, "Link", FormatMark.Link));
        _formatBar = new Border
        {
            Height = 30,
            Padding = new Thickness(8, 0, 8, 0),
            Child = new ScrollViewer { Content = formatButtons, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Visibility = Visibility.Collapsed,
        };

        // Footer ----------------------------------------------------------------------
        _formatToggle = Ui.IconButton(TextFormatGlyph, "Formatting", () =>
        {
            _formatting = !_formatting;
            UpdateModes();
        }, 13);
        _previewToggle = Ui.IconButton(Glyphs.Eye, "Show formatting", TogglePreview, 13);
        _copy = Ui.IconButton(Glyphs.Copy, "Copy all", CopyAll, 13);
        var save = Ui.IconButton(SaveGlyph, "Save as file", SaveAsFile, 13);
        var clear = Ui.IconButton(Glyphs.Delete, "Clear", Clear, 13);
        _footer = Ui.Row(2, _formatToggle, _previewToggle, _copy, save);
        var footer = Ui.Split(_footer, clear);
        footer.Height = 36;
        footer.Margin = new Thickness(8, 0, 8, 0);

        // Layout ------------------------------------------------------------------------
        var layout = new Grid();
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = h });
        var divider = Ui.Divider(0);
        Place(layout, header, 0);
        Place(layout, Ui.Stack(0, tabRow, divider), 1);
        Place(layout, _banner, 2);
        Place(layout, _findBar, 3);
        Place(layout, editorHost, 4);
        Place(layout, _formatBar, 5);
        Place(layout, footer, 6);

        _background = new Border { CornerRadius = new CornerRadius(14) };
        _background.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        var frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)),
            Child = layout,
        };
        var root = new Grid();
        root.Children.Add(_background);
        root.Children.Add(frame);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Deactivated += (_, _) =>
        {
            if (!_pinned && !_dialogOpen && IsVisible) HidePad();
        };
        PreviewKeyDown += OnKey;
        ScratchpadStore.SaveStateChanged += UpdateBanner;
        var appearance = Settings.Observe(ApplyAppearance, TextSizeKey, BackgroundKey);
        var pinning = Settings.Observe(() =>
        {
            _pinned = !Settings.GetBool(CloseOnClickOutsideKey, true);
            UpdatePin();
        }, CloseOnClickOutsideKey);
        Closed += (_, _) =>
        {
            ScratchpadStore.SaveStateChanged -= UpdateBanner;
            appearance.Dispose();
            pinning.Dispose();
        };
    }

    private static T Chrome<T>(T element) where T : UIElement
    {
        WindowChrome.SetIsHitTestVisibleInChrome(element, true);
        return element;
    }

    private static void Place(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    // ---- Show / hide ---------------------------------------------------------------------

    private void ShowPad()
    {
        _pinned = !Settings.GetBool(CloseOnClickOutsideKey, true);
        _previewing = false;
        ApplyAppearance();
        LoadSelected();
        RebuildTabs();
        UpdatePin();
        UpdateBanner();
        Opacity = 0;
        Show();
        if (!_placed || !OnAnyScreen())
        {
            PlaceUnderPointer();
            _placed = true;
        }
        FocusPad();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)));
    }

    private void FocusPad()
    {
        WindowHelper.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        _editor.Focus();
        _editor.CaretIndex = _editor.Text.Length;
    }

    public void HidePad()
    {
        ScratchpadStore.UpdateText(_editor.Text);
        ScratchpadStore.SaveNow();
        CloseFind();
        Hide();
    }

    private bool OnAnyScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var r);
        return WindowHelper.GetMonitors().Any(m => r.Left < m.Bounds.Right && r.Right > m.Bounds.Left && r.Top < m.Bounds.Bottom && r.Bottom > m.Bounds.Top);
    }

    private void PlaceUnderPointer()
    {
        Native.GetCursorPos(out var p);
        var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
        var scale = WindowHelper.GetScaleAt(p.X, p.Y);
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var inset = (int)(16 * scale);
        var x = Math.Clamp(work.Left + (work.Width - w) / 2, work.Left + inset, Math.Max(work.Left + inset, work.Right - w - inset));
        var y = Math.Clamp(work.Top + (int)((work.Height - h) * 0.42), work.Top + inset, Math.Max(work.Top + inset, work.Bottom - h - inset));
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
    }

    private void ApplyAppearance()
    {
        var size = ScratchpadLogic.ClampTextSize(Settings.GetDouble(TextSizeKey, 13));
        _editor.FontSize = size;
        _placeholder.FontSize = size;
        _background.Opacity = 0.86 + 0.14 * Math.Clamp(Settings.GetDouble(BackgroundKey, 0), 0, 1);
        if (_previewing) _preview.Document = MarkdownLite.ToFlowDocument(_editor.Text, size);
    }

    // ---- Tabs ------------------------------------------------------------------------------

    private void RebuildTabs()
    {
        _tabs.Children.Clear();
        var doc = ScratchpadStore.Document!;
        var many = doc.Pads.Count > 1;
        foreach (var pad in doc.Pads)
        {
            var selected = pad.Id == doc.SelectedId;
            var name = Ui.Text(pad.Name, 11, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? "TextPrimary" : "TextSecondary", wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            var closeTab = Ui.IconButton(Glyphs.Close, "Close scratchpad", () => ClosePad(pad.Id), 8);
            closeTab.Padding = new Thickness(3);
            closeTab.Visibility = many && selected ? Visibility.Visible : Visibility.Collapsed;
            var content = Ui.Row(2, name, closeTab);
            var tab = new Border
            {
                Height = 24,
                MinWidth = 46,
                MaxWidth = 130,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 0, 4, 0),
                Margin = new Thickness(0, 4, 4, 4),
                Child = content,
                Cursor = Cursors.Hand,
                Tag = pad.Id,
            };
            if (selected) tab.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            else tab.Background = Brushes.Transparent;
            tab.MouseEnter += (_, _) => closeTab.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            tab.MouseLeave += (_, _) => closeTab.Visibility = many && selected ? Visibility.Visible : Visibility.Collapsed;
            tab.MouseLeftButtonUp += (_, _) => SelectPad(pad.Id);
            tab.ContextMenu = new ContextMenu();
            var rename = new MenuItem { Header = "Rename scratchpad" };
            rename.Click += (_, _) => RenamePad(pad.Id);
            var closeItem = new MenuItem { Header = "Close scratchpad", IsEnabled = many };
            closeItem.Click += (_, _) => ClosePad(pad.Id);
            tab.ContextMenu.Items.Add(rename);
            tab.ContextMenu.Items.Add(closeItem);
            _tabs.Children.Add(tab);
            if (selected) tab.Loaded += (_, _) => tab.BringIntoView();
        }
    }

    private void SaveCurrent()
    {
        ScratchpadStore.UpdateText(_editor.Text);
        ScratchpadStore.SaveNow();
    }

    private void LoadSelected()
    {
        _loadingText = true;
        _editor.Text = ScratchpadStore.Selected.Text;
        _editor.CaretIndex = _editor.Text.Length;
        _loadingText = false;
        UpdateModes();
    }

    private void SelectPad(string id)
    {
        SaveCurrent();
        ScratchpadStore.Select(id);
        LoadSelected();
        RebuildTabs();
        _editor.Focus();
    }

    private void NewPad()
    {
        SaveCurrent();
        if (ScratchpadStore.AddPad() is null) return;
        LoadSelected();
        RebuildTabs();
        _editor.Focus();
    }

    private void RenamePad(string id)
    {
        var pad = ScratchpadStore.Document?.Pads.FirstOrDefault(p => p.Id == id);
        if (pad is null) return;
        _dialogOpen = true;
        var name = Prompt("Rename scratchpad", pad.Name);
        _dialogOpen = false;
        if (name is not null) ScratchpadStore.Rename(id, name);
        RebuildTabs();
        Activate();
    }

    private void ClosePad(string id)
    {
        var doc = ScratchpadStore.Document;
        var pad = doc?.Pads.FirstOrDefault(p => p.Id == id);
        if (doc is null || pad is null || doc.Pads.Count <= 1) return;
        SaveCurrent();
        if (pad.Text.Length > 0)
        {
            _dialogOpen = true;
            var ok = EnergyUi.Confirm("Close scratchpad", $"Delete “{pad.Name}” and everything in it?", "Close scratchpad", owner: this);
            _dialogOpen = false;
            Activate();
            if (!ok) return;
        }
        ScratchpadStore.Close(id);
        LoadSelected();
        RebuildTabs();
    }

    private string? Prompt(string title, string initial)
    {
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 260 };
        var window = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
        };
        window.SetResourceReference(BackgroundProperty, "WindowBackground");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        var saveButton = Ui.Button("Save", () =>
        {
            result = box.Text;
            window.Close();
        }, accent: true);
        saveButton.IsDefault = true;
        var cancel = Ui.Button("Cancel", window.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(8, cancel, saveButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = Ui.Stack(12, Ui.Title(title, 14), box, buttons);
        body.Margin = new Thickness(18);
        window.Content = body;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return result;
    }

    // ---- Modes, actions ------------------------------------------------------------------

    private void UpdateModes()
    {
        var empty = _editor.Text.Length == 0;
        if (empty) _previewing = false;
        _editor.Visibility = _previewing ? Visibility.Hidden : Visibility.Visible;
        _preview.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
        if (_previewing) _preview.Document = MarkdownLite.ToFlowDocument(_editor.Text, _editor.FontSize);
        _formatBar.Visibility = _formatting && !_previewing ? Visibility.Visible : Visibility.Collapsed;
        _formatToggle.IsEnabled = !_previewing;
        _formatToggle.Content = Ui.Icon(TextFormatGlyph, 13, _formatting && !_previewing ? "Accent" : "TextSecondary");
        _previewToggle.Content = Ui.Icon(_previewing ? Glyphs.Edit : Glyphs.Eye, 13, _previewing ? "Accent" : "TextSecondary");
        _previewToggle.ToolTip = _previewing ? "Edit text" : "Show formatting";
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var empty = _editor.Text.Length == 0;
        _placeholder.Visibility = empty && !_previewing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in _footer.Children.OfType<Button>().Skip(1))
        {
            child.IsEnabled = !empty;
            child.Opacity = empty ? 0.5 : 1;
        }
    }

    private void UpdatePin()
    {
        _pin.Content = Ui.Icon(Glyphs.Pin, 12, _pinned ? "Accent" : "TextSecondary");
        _pin.ToolTip = _pinned ? "Close when I click outside" : "Keep open";
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        UpdatePin();
    }

    private void UpdateBanner() => _banner.Visibility = ScratchpadStore.SaveFailed ? Visibility.Visible : Visibility.Collapsed;

    private void TogglePreview()
    {
        if (_editor.Text.Length == 0) return;
        _previewing = !_previewing;
        if (_previewing) CloseFind();
        UpdateModes();
        if (!_previewing) _editor.Focus();
    }

    private void CopyAll()
    {
        if (_editor.Text.Length == 0) return;
        try { System.Windows.Clipboard.SetText(_editor.Text); }
        catch (Exception ex) { Log.Warn($"Copy failed: {ex.Message}"); return; }
        _copy.Content = Ui.Row(4, Ui.Icon(Glyphs.Check, 12, "Green"), Ui.Text("Copied", 11, brush: "Green", wrap: false));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _copy.Content = Ui.Icon(Glyphs.Copy, 13, "TextSecondary");
        };
        t.Start();
    }

    private void Clear()
    {
        if (_editor.Text.Length == 0) return;
        _previewing = false;
        _editor.SelectAll();
        _editor.SelectedText = string.Empty; // through the control, so Ctrl+Z brings it back
        UpdateModes();
        _editor.Focus();
    }

    private void SaveAsFile()
    {
        if (_editor.Text.Length == 0) return;
        SaveCurrent();
        var dialog = new SaveFileDialog
        {
            FileName = ScratchpadLogic.SuggestedFileName(ScratchpadStore.Selected.Name, DateTime.Now),
            Filter = "Text (*.txt)|*.txt|Markdown (*.md)|*.md",
            DefaultExt = ".txt",
            AddExtension = true,
        };
        _dialogOpen = true;
        var ok = dialog.ShowDialog(this) == true;
        _dialogOpen = false;
        Activate();
        if (!ok) return;
        try { File.WriteAllText(dialog.FileName, _editor.Text, new UTF8Encoding(false)); }
        catch (Exception ex)
        {
            Log.Error("Saving the scratchpad as a file failed", ex);
            Notifier.Show("Scratchpad", "The file could not be saved", warning: true);
        }
    }

    private Button FormatButton(string text, string tooltip, FormatMark mark, FontWeight weight, bool italic = false, bool strike = false)
    {
        var label = Ui.Text(text, 12.5, weight, "TextSecondary", wrap: false);
        if (italic) label.FontStyle = FontStyles.Italic;
        if (strike) label.TextDecorations = TextDecorations.Strikethrough;
        var b = Ui.Clickable(label, () => ApplyMark(mark), tooltip);
        b.Width = 26;
        b.Height = 26;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return b;
    }

    private Button FormatIcon(string glyph, string tooltip, FormatMark mark)
    {
        var b = Ui.IconButton(glyph, tooltip, () => ApplyMark(mark), 12);
        b.Width = 26;
        b.Height = 26;
        return b;
    }

    /// <summary>Applies a mark through the TextBox's own edit so one Ctrl+Z reverts it.</summary>
    private void ApplyMark(FormatMark mark)
    {
        var old = _editor.Text;
        var edit = ScratchpadFormat.Apply(mark, old, _editor.SelectionStart, _editor.SelectionLength);
        var prefix = 0;
        while (prefix < old.Length && prefix < edit.Text.Length && old[prefix] == edit.Text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < old.Length - prefix && suffix < edit.Text.Length - prefix &&
               old[old.Length - 1 - suffix] == edit.Text[edit.Text.Length - 1 - suffix]) suffix++;
        _editor.Select(prefix, old.Length - prefix - suffix);
        _editor.SelectedText = edit.Text.Substring(prefix, edit.Text.Length - prefix - suffix);
        _editor.Select(edit.SelectionStart, edit.SelectionLength);
        _editor.Focus();
        ScratchpadStore.UpdateText(_editor.Text);
        ScratchpadStore.SaveNow();
    }

    // ---- Find ------------------------------------------------------------------------------

    private void OpenFind()
    {
        if (_previewing)
        {
            _previewing = false;
            UpdateModes();
        }
        _findBar.Visibility = Visibility.Visible;
        if (_editor.SelectionLength > 0 && !_editor.SelectedText.Contains('\n')) _find.Text = _editor.SelectedText;
        _find.Focus();
        _find.SelectAll();
    }

    private void CloseFind()
    {
        if (_findBar.Visibility != Visibility.Visible) return;
        _findBar.Visibility = Visibility.Collapsed;
        _editor.Focus();
    }

    private void FindNext(int direction)
    {
        var query = _find.Text;
        if (query.Length == 0) return;
        var text = _editor.Text;
        int index;
        if (direction > 0)
        {
            var from = Math.Min(text.Length, _editor.SelectionStart + Math.Max(1, _editor.SelectionLength));
            index = text.IndexOf(query, from, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }
        else
        {
            var from = _editor.SelectionStart - 1;
            index = from < 0 ? -1 : text.LastIndexOf(query, from, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) index = text.LastIndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }
        if (index < 0) return;
        _editor.Select(index, query.Length);
        var line = _editor.GetLineIndexFromCharacterIndex(index);
        if (line >= 0) _editor.ScrollToLine(line);
    }

    // ---- Keyboard -------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrlOnly = Keyboard.Modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift);
        if (e.Key == Key.Escape)
        {
            if (_findBar.Visibility == Visibility.Visible) CloseFind();
            else HidePad();
            e.Handled = true;
            return;
        }
        if (!ctrlOnly) return;
        switch (e.Key)
        {
            case Key.T when Keyboard.Modifiers == ModifierKeys.Control:
                NewPad();
                e.Handled = true;
                break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control:
                if (ScratchpadStore.Document!.Pads.Count <= 1) HidePad();
                else ClosePad(ScratchpadStore.Selected.Id);
                e.Handled = true;
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                OpenFind();
                e.Handled = true;
                break;
            case Key.G:
                FindNext(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Renders the pad with sample content (no file I/O, no global state) for previews.</summary>
    public static FrameworkElement BuildPreviewForTests(string sampleText)
    {
        var window = new ScratchpadWindow();
        window._loadingText = true;
        window._editor.Text = sampleText;
        window._loadingText = false;
        window._tabs.Children.Clear();
        foreach (var name in new[] { "Scratchpad 1", "Ideas", "Meeting" })
        {
            var t = new Border { Height = 24, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 4, 4, 4),
                Child = Ui.Text(name, 11, name == "Ideas" ? FontWeights.SemiBold : FontWeights.Normal, wrap: false) };
            if (name == "Ideas") t.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            window._tabs.Children.Add(t);
        }
        window._formatting = true;
        window.UpdateModes();
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        window.Close();
        content.Width = 380;
        content.Height = 300;
        return content;
    }
}
