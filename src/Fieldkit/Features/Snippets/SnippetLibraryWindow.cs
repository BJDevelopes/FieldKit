// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Snippets;

/// <summary>
/// The quick snippet menu (upstream SnippetLibraryView): a search field, snippets grouped by folder,
/// arrows / Enter / Ctrl+1…9 to insert, Esc to close. Picking one types it where the cursor was.
/// </summary>
public sealed class SnippetLibraryWindow : FloatingWindow
{
    private static SnippetLibraryWindow? _instance;

    private readonly Func<IReadOnlyList<TextSnippet>> _source;
    private readonly bool _isPreview;
    private readonly TextBox _search = new();
    private readonly TextBlock _placeholder;
    private readonly Button _clear;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
    private readonly ContentControl _content = new();
    private List<TextSnippet> _rows = [];
    private Guid? _selectedId;
    private IntPtr _target;
    private readonly Dictionary<Guid, Border> _rowViews = [];

    /// <summary>True while the menu is on screen (typed triggers do not expand meanwhile).</summary>
    public static bool IsOpen { get; private set; }

    private SnippetLibraryWindow(Func<IReadOnlyList<TextSnippet>> source, bool isPreview) : base(cornerRadius: 22)
    {
        _source = source;
        _isPreview = isPreview;
        Title = "Snippets";
        Width = 460 + 2 * ShadowMargin;
        SizeToContent = SizeToContent.Height;

        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.FontSize = 15;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.SetResourceReference(ForegroundProperty, "TextPrimary");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "Accent");
        _placeholder = Ui.Text("Search snippets", 15, brush: "TextTertiary", wrap: false);
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(3, 0, 0, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _clear = Ui.IconButton(ClearGlyph, "Clear", () => _search.Text = string.Empty, 12, "TextTertiary");
        var field = new Grid { Children = { _search, _placeholder } };
        var searchBar = new DockPanel { Margin = new Thickness(16, 13, 12, 13) };
        var magnifier = Ui.Icon(Glyphs.Search, 15, "TextSecondary");
        magnifier.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(magnifier, Dock.Left);
        DockPanel.SetDock(_clear, Dock.Right);
        searchBar.Children.Add(magnifier);
        searchBar.Children.Add(_clear);
        searchBar.Children.Add(field);
        _search.TextChanged += (_, _) => Refresh();

        var hint = Ui.Text("↩ inserts · esc closes", 12, brush: "TextTertiary", wrap: false);
        var manage = Ui.Clickable(Ui.Text("Manage snippets", 12, brush: "TextSecondary", wrap: false), () =>
        {
            Dismiss();
            PanelHost.OpenSettings("snippets");
        });
        var footer = new DockPanel { Margin = new Thickness(16, 9, 16, 10) };
        DockPanel.SetDock(manage, Dock.Right);
        footer.Children.Add(manage);
        footer.Children.Add(hint);

        _scroll.Content = _content;
        var root = new DockPanel();
        DockPanel.SetDock(searchBar, Dock.Top);
        var topDivider = Ui.Divider(0);
        DockPanel.SetDock(topDivider, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var bottomDivider = Ui.Divider(0);
        DockPanel.SetDock(bottomDivider, Dock.Bottom);
        root.Children.Add(searchBar);
        root.Children.Add(topDivider);
        root.Children.Add(footer);
        root.Children.Add(bottomDivider);
        root.Children.Add(_scroll);
        Body = root;
        PreviewKeyDown += OnKey;
        IsVisibleChanged += (_, _) => IsOpen = !_isPreview && IsVisible;
    }

    public static void Toggle()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.Dismiss();
            return;
        }
        var target = PasteHelper.CaptureTarget();
        _instance ??= new SnippetLibraryWindow(() => SnippetStore.All, isPreview: false);
        _instance._target = target;
        _instance._search.Text = string.Empty;
        _instance._selectedId = null;
        _instance.Refresh();
        _instance.Opacity = 0;
        _instance.Show();
        _instance.UpdateLayout();
        // Spotlight-style: centered on the pointer's screen, a bit above the middle.
        _instance.CenterOnPointerScreen(0.38);
        _instance.Opacity = 1;
        _instance.Activate();
        _instance._search.Focus();
    }

    public static void HideIfOpen() => _instance?.Dismiss();

    private void Refresh()
    {
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var all = _source();
        var sections = SnippetEngine.LibrarySections(all, _search.Text);
        _rows = SnippetEngine.LibraryRows(sections);
        if (_rows.All(r => r.Id != _selectedId)) _selectedId = _rows.FirstOrDefault()?.Id;
        _rowViews.Clear();
        if (!SnippetEngine.LibrarySections(all, string.Empty).Any())
        {
            _content.Content = Empty(Glyphs.Note,
                "Nothing to show yet. Add snippets, or turn on “Show in the quick menu” for the ones you use most.");
            return;
        }
        if (_rows.Count == 0)
        {
            _content.Content = Empty(null, "No snippet matches the search.");
            return;
        }
        var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        var index = 0;
        foreach (var section in sections)
        {
            if (section.Folder.Length > 0)
            {
                var header = Ui.Row(7, Ui.Icon(Glyphs.Folder, 13, "TextSecondary"),
                    Ui.Text(section.Folder, 13, FontWeights.SemiBold, "TextSecondary", wrap: false));
                header.Margin = new Thickness(8, 10, 8, 4);
                stack.Children.Add(header);
            }
            foreach (var snippet in section.Snippets)
            {
                stack.Children.Add(Row(snippet, index));
                index++;
            }
        }
        _content.Content = stack;
        Restyle();
    }

    private static FrameworkElement Empty(string? glyph, string message)
    {
        var text = Ui.Text(message, 13, brush: "TextSecondary");
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 360;
        var stack = Ui.Stack(10, glyph is null ? null : Ui.Icon(glyph, 28, "TextSecondary"), text);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(0, 28, 0, 28);
        return stack;
    }

    private Border Row(TextSnippet snippet, int index)
    {
        var name = Ui.Text(snippet.DisplayName, 15, FontWeights.Medium, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var trigger = Ui.Badge(snippet.Trigger);
        ((TextBlock)trigger.Child).FontFamily = new FontFamily("Cascadia Mono, Consolas");
        ((TextBlock)trigger.Child).FontSize = 12;
        var preview = Ui.Text(SnippetEngine.Preview(snippet.Replacement), 12.5, brush: "TextSecondary", wrap: false);
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        var enter = Ui.Icon(ReturnGlyph, 13, "TextSecondary");
        enter.Visibility = Visibility.Hidden;
        var shortcut = index < 9 ? Ui.Text($"Ctrl+{index + 1}", 10.5, brush: "TextTertiary", wrap: false) : null;
        var right = Ui.Row(8, shortcut, enter);
        var top = Ui.Row(8, name, trigger);
        var text = Ui.Stack(2, top, preview);
        var grid = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        right.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(right);
        grid.Children.Add(text);
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 12, 8),
            Child = grid,
            Background = Brushes.Transparent,
            Tag = enter,
            Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) =>
        {
            _selectedId = snippet.Id;
            Restyle();
        };
        row.MouseLeftButtonUp += (_, _) => Insert(snippet);
        _rowViews[snippet.Id] = row;
        return row;
    }

    private void Restyle()
    {
        foreach (var (id, row) in _rowViews)
        {
            var selected = id == _selectedId;
            if (selected) row.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            else row.Background = Brushes.Transparent;
            if (row.Tag is UIElement enter) enter.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        var index = _rows.FindIndex(r => r.Id == _selectedId);
        index = Math.Clamp(index < 0 ? 0 : index + delta, 0, _rows.Count - 1);
        _selectedId = _rows[index].Id;
        Restyle();
        if (_rowViews.TryGetValue(_rows[index].Id, out var view)) view.BringIntoView();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Down:
            case Key.N when mods == ModifierKeys.Control:
                Move(1);
                e.Handled = true;
                return;
            case Key.Up:
            case Key.P when mods == ModifierKeys.Control:
                Move(-1);
                e.Handled = true;
                return;
            case Key.Enter:
                if (_rows.FirstOrDefault(r => r.Id == _selectedId) is { } snippet) Insert(snippet);
                e.Handled = true;
                return;
        }
        // Plain digits belong to the search text; only Ctrl+1…9 insert by position.
        if (mods == ModifierKeys.Control && key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
            if (index < _rows.Count) Insert(_rows[index]);
            e.Handled = true;
        }
    }

    private void Insert(TextSnippet snippet)
    {
        if (_isPreview) return;
        var target = _target;
        Dismiss();
        if (target == IntPtr.Zero)
        {
            // Opened over Fieldkit itself: typing would land in our own fields.
            SystemSounds.Beep.Play();
            return;
        }
        WindowHelper.Activate(target);
        _ = PasteHelper.SendWhenReleasedAsync(() => _ = InsertAsync(snippet));
    }

    private static async Task InsertAsync(TextSnippet snippet)
    {
        if (await SnippetFocus.IsPasswordAsync().ConfigureAwait(false))
        {
            SystemSounds.Beep.Play();
            return;
        }
        await TextSnippetService.Insert(snippet.Replacement, 0, string.Empty, 0).ConfigureAwait(false);
    }

    // Segoe Fluent Icons not in UI/Glyphs (verified with --render-glyph-range).
    private const string ReturnGlyph = "\uE751";
    private const string ClearGlyph = "\uEA39";

    /// <summary>Renders the menu with sample snippets to a PNG (visual check without a desktop session).</summary>
    public static void RenderPreview(string path, IReadOnlyList<TextSnippet> sample, string query = "", bool light = false)
    {
        var window = new SnippetLibraryWindow(() => sample, isPreview: true)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            HideOnDeactivate = false,
        };
        PreviewRendering.ApplyTheme(window, light ? true : null);
        window._search.Text = query;
        window.Refresh();
        window.Show();
        PreviewRendering.Save(window, path);
        window.Close();
    }
}

/// <summary>The snippet menu's shortcut, while "Open snippets from a menu" is on.</summary>
public sealed class SnippetLibraryService : FeatureService
{
    public const string EnabledKey = "textSnippets.libraryEnabled";
    public const string ShortcutKey = "textSnippets.libraryShortcut";
    public const string DefaultShortcut = "Ctrl+Alt+Shift+L";

    private IDisposable? _hotkey;

    public override string FeatureId => TextSnippetService.Id;

    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    public override bool EnabledByDefault => false;

    protected override void OnStart() => _hotkey = Hotkeys.Bind(ShortcutKey, DefaultShortcut, SnippetLibraryWindow.Toggle);

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        SnippetLibraryWindow.HideIfOpen();
    }
}
