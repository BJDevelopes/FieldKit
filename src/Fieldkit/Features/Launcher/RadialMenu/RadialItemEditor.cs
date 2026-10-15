// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// The sheet that edits one slice (upstream <c>RadialItemEditor</c>): action type, target, name and
/// icon. Changing the type clears targets that no longer make sense but keeps name and icon.
/// </summary>
public sealed class RadialItemEditor
{
    private readonly RadialMenuItem _item;
    private readonly bool _isNew;
    private readonly bool _allowsSubmenu;
    private readonly RadialCatalog _catalog;
    private readonly Action<RadialMenuItem> _save;
    private readonly Action? _delete;
    private readonly Action? _openChildren;
    private readonly ContentControl _payloadHost = new();
    private readonly ContentControl _iconsHost = new();
    private readonly TextBlock _message = Ui.Text(string.Empty, 12);
    private readonly ContentControl _submenuHost = new();
    private readonly Button _saveButton;
    private Window? _window;

    private RadialItemEditor(RadialMenuItem item, bool isNew, bool allowsSubmenu, RadialCatalog catalog,
        Action<RadialMenuItem> save, Action? delete, Action? openChildren)
    {
        _item = item.Clone();
        _isNew = isNew;
        _allowsSubmenu = allowsSubmenu;
        _catalog = catalog;
        _save = save;
        _delete = delete;
        _openChildren = openChildren;
        _saveButton = Ui.Button(RadialStrings.SaveButton, Save, accent: true);
    }

    public static void Show(Window? owner, RadialMenuItem item, bool isNew, bool allowsSubmenu, RadialCatalog catalog,
        Action<RadialMenuItem> save, Action? delete, Action? openChildren)
    {
        var editor = new RadialItemEditor(item, isNew, allowsSubmenu, catalog, save, delete, openChildren);
        var window = new Window
        {
            Title = isNew ? RadialStrings.AddButton : catalog.DisplayName(item),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Content = editor.Build(),
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is RadialShortcutBox { IsRecording: true }))
            {
                window.Close();
                e.Handled = true;
            }
        };
        editor._window = window;
        window.ShowDialog();
    }

    /// <summary>The editor's content (also rendered by the preview test).</summary>
    public FrameworkElement Build()
    {
        var title = Ui.Text(_isNew ? RadialStrings.AddButton : _catalog.DisplayName(_item), 15, FontWeights.SemiBold);

        var kinds = new ComboBox { MinWidth = 220 };
        foreach (var kind in AvailableKinds()) kinds.Items.Add(new ComboBoxItem { Content = RadialStrings.KindTitle(kind), Tag = kind });
        kinds.SelectedItem = kinds.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (RadialItemKind)i.Tag == _item.Kind);
        kinds.SelectionChanged += (_, _) =>
        {
            if (kinds.SelectedItem is ComboBoxItem { Tag: RadialItemKind kind }) ChangeKind(kind);
        };

        var name = PlaceholderBox(_item.Name, RadialStrings.AutomaticLabel, text => _item.Name = text);

        _message.Visibility = Visibility.Collapsed;
        var remove = _delete is null ? null : Ui.Button(RadialStrings.DeleteButton, () =>
        {
            _delete();
            _window?.Close();
        });
        remove?.SetResourceReference(Control.ForegroundProperty, "Red");
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
        if (remove is not null) buttons.Children.Add(remove);
        var right = Ui.Row(8, Ui.Button(RadialStrings.CancelButton, () => _window?.Close()), _saveButton);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        RefreshPayload();
        RefreshIcons();
        RefreshState();

        var form = Ui.Stack(12,
            Row(RadialStrings.ActionLabel, kinds),
            _payloadHost,
            Row(RadialStrings.NameLabel, name),
            Ui.Stack(8, Ui.Text(RadialStrings.IconLabel, 13, FontWeights.Medium), _iconsHost));
        return new Border
        {
            Padding = new Thickness(18),
            Child = Ui.Stack(14, title, form, _message, _submenuHost, buttons),
        };
    }

    private IEnumerable<RadialItemKind> AvailableKinds()
    {
        yield return RadialItemKind.App;
        yield return RadialItemKind.File;
        yield return RadialItemKind.Url;
        yield return RadialItemKind.Shortcut;
        if (_catalog.ToolChoices.Any() || _item.Kind == RadialItemKind.Tool) yield return RadialItemKind.Tool;
        if (_catalog.ToggleChoices.Any() || _item.Kind == RadialItemKind.QuickToggle) yield return RadialItemKind.QuickToggle;
        if (_catalog.LayoutChoices.Any() || _item.Kind == RadialItemKind.WindowLayout) yield return RadialItemKind.WindowLayout;
        yield return RadialItemKind.Media;
        if (_allowsSubmenu || _item.Kind == RadialItemKind.Submenu) yield return RadialItemKind.Submenu;
    }

    private void ChangeKind(RadialItemKind kind)
    {
        if (kind == _item.Kind) return;
        _item.Kind = kind;
        if (kind != RadialItemKind.Url) _item.CustomIconData = null;
        _item.Payload = kind switch
        {
            RadialItemKind.Tool => _catalog.ToolChoices.FirstOrDefault()?.Id ?? string.Empty,
            RadialItemKind.QuickToggle => _catalog.ToggleChoices.FirstOrDefault()?.Id ?? string.Empty,
            RadialItemKind.WindowLayout => _catalog.LayoutChoices.FirstOrDefault()?.Id ?? string.Empty,
            RadialItemKind.Media => RadialRaw.Media(RadialMediaKey.PlayPause),
            _ => string.Empty,
        };
        if (kind != RadialItemKind.Submenu) _item.Children = [];
        _message.Visibility = Visibility.Collapsed;
        RefreshPayload();
        RefreshIcons();
        RefreshState();
    }

    private void RefreshState()
    {
        _saveButton.IsEnabled = RadialMenuSupport.IsValidPayload(_item);
        if (_item.Kind == RadialItemKind.Url && _item.Payload.Trim().Length > 0 && RadialMenuSupport.NormalizedUrl(_item.Payload) is null)
            ShowMessage(RadialStrings.UrlInvalid, problem: true);
        else if (_item.Kind == RadialItemKind.Url && _message.Text == RadialStrings.UrlInvalid)
            _message.Visibility = Visibility.Collapsed;

        if (_item.Kind == RadialItemKind.Submenu)
        {
            var caption = Ui.Caption(RadialStrings.SubmenuCaption, 12);
            var open = _openChildren is null ? null : Ui.Clickable(
                Ui.Row(4, Ui.Text(RadialStrings.EditActionsButton, 12.5, FontWeights.Medium, "Accent", wrap: false),
                    Ui.Icon(Glyphs.ChevronRight, 9, "Accent")),
                () =>
                {
                    _window?.Close();
                    _openChildren();
                });
            _submenuHost.Content = Ui.Stack(4, caption, open);
        }
        else
        {
            _submenuHost.Content = null;
        }
    }

    private void ShowMessage(string text, bool problem)
    {
        _message.Text = text;
        _message.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Orange" : "TextSecondary");
        _message.Visibility = Visibility.Visible;
    }

    private void Save()
    {
        _item.Name = _item.Name.Trim();
        if (!RadialMenuSupport.IsValidPayload(_item)) return;
        _save(_item);
        _window?.Close();
    }

    // --------------------------------------------------------------- payload

    private void RefreshPayload()
    {
        _payloadHost.Content = _item.Kind switch
        {
            RadialItemKind.App => Row(RadialStrings.KindApp, Ui.Row(8,
                Ui.Button(ChooseTitle, ChooseApp),
                Ui.Button(RadialStrings.StartMenuButton, ChooseStartMenuApp))),
            RadialItemKind.File => Row(RadialStrings.KindFile, Ui.Row(8,
                Ui.Button(ChooseTitle, () => ChooseFile(folder: false)),
                Ui.Button(RadialStrings.ChooseFolderButton, () => ChooseFile(folder: true)))),
            RadialItemKind.Url => UrlEditor(),
            RadialItemKind.Shortcut => Row(RadialStrings.KindShortcut, ShortcutEditor()),
            RadialItemKind.Tool => Row(RadialStrings.ToolLabel, Picker(_catalog.ToolChoices.Select(a => (a.Id, a.Title, a.Glyph)))),
            RadialItemKind.QuickToggle => Row(RadialStrings.KindQuickToggle, Picker(_catalog.ToggleChoices.Select(t => (t.Id, t.Title, t.Glyph)))),
            RadialItemKind.WindowLayout => Row(RadialStrings.KindWindowLayout, Picker(_catalog.LayoutChoices.Select(a => (a.Id, a.Title, a.Glyph)))),
            RadialItemKind.Media => Row(RadialStrings.MediaLabel, Picker(Enum.GetValues<RadialMediaKey>()
                .Select(k => (RadialRaw.Media(k), RadialStrings.MediaTitle(k), RadialIcons.DefaultGlyph(new RadialMenuItem(RadialItemKind.Media, RadialRaw.Media(k)), _catalog))))),
            _ => null,
        };
    }

    private string ChooseTitle => _item.Payload.Length == 0 ? RadialStrings.ChooseButton : RadialCatalog.FileDisplayName(_item.Payload);

    private ComboBox Picker(IEnumerable<(string Id, string Title, string Glyph)> choices)
    {
        var combo = new ComboBox { MinWidth = 220 };
        var list = choices.ToList();
        // A stored upstream value ("screenshot") resolves to its registered action.
        var current = _item.Kind switch
        {
            RadialItemKind.Tool => _catalog.Tool(_item.Payload)?.Id,
            RadialItemKind.QuickToggle => _catalog.Toggle(_item.Payload)?.Id,
            RadialItemKind.WindowLayout => _catalog.Layout(_item.Payload)?.Id,
            _ => _item.Payload,
        } ?? _item.Payload;
        foreach (var (id, title, glyph) in list)
            combo.Items.Add(new ComboBoxItem { Content = Ui.Row(8, Ui.Icon(glyph, 13, "TextSecondary"), Ui.Text(title, 13, wrap: false)), Tag = id });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _item.Payload = id;
                RefreshIcons();
                RefreshState();
            }
        };
        return combo;
    }

    private void ChooseApp()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Apps (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(_window) != true) return;
        SetPath(dialog.FileName);
    }

    private void ChooseStartMenuApp()
    {
        var picked = RadialAppPicker.Pick(_window);
        if (picked is null) return;
        SetPath(picked.Path);
    }

    private void ChooseFile(bool folder)
    {
        if (folder)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(_window) != true) return;
            SetPath(dialog.FolderName);
        }
        else
        {
            var dialog = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
            if (dialog.ShowDialog(_window) != true) return;
            SetPath(dialog.FileName);
        }
    }

    private void SetPath(string path)
    {
        RadialIcons.Invalidate(_item.Payload);
        _item.Payload = path;
        RefreshPayload();
        RefreshIcons();
        RefreshState();
    }

    private FrameworkElement UrlEditor()
    {
        var status = Ui.Text(string.Empty, 12, wrap: false);
        status.Visibility = Visibility.Collapsed;
        Button? fetch = null;
        var box = PlaceholderBox(_item.Payload, RadialStrings.UrlPlaceholder, text =>
        {
            _item.Payload = text;
            status.Visibility = Visibility.Collapsed;
            if (fetch is not null) fetch.IsEnabled = RadialMenuSupport.NormalizedUrl(text) is not null;
            RefreshState();
        });
        fetch = Ui.Button(RadialStrings.FetchFaviconButton, async () =>
        {
            fetch!.IsEnabled = false;
            fetch.Content = RadialStrings.FetchFaviconLoading;
            var data = await RadialFavicon.FetchAsync(_item.Payload);
            fetch.Content = RadialStrings.FetchFaviconButton;
            fetch.IsEnabled = true;
            status.Visibility = Visibility.Visible;
            if (data is not null)
            {
                _item.CustomIconData = data;
                _item.SymbolName = string.Empty;
                status.Text = RadialStrings.FetchFaviconSuccess;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Green");
                RefreshIcons();
            }
            else
            {
                status.Text = RadialStrings.FetchFaviconError;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
            }
        });
        fetch.IsEnabled = RadialMenuSupport.NormalizedUrl(_item.Payload) is not null;
        return Ui.Stack(6,
            Row(RadialStrings.KindUrl, box),
            Ui.Row(8, fetch, status),
            Ui.Caption(RadialStrings.FetchFaviconDisclaimer, 12));
    }

    private FrameworkElement ShortcutEditor()
    {
        var box = new RadialShortcutBox(Shortcut.Parse(_item.Payload));
        box.Recording += () => ShowMessage(RadialStrings.ShortcutRecordingHint, problem: false);
        box.Captured += shortcut =>
        {
            _item.Payload = shortcut.ToString();
            _message.Visibility = Visibility.Collapsed;
            RefreshState();
        };
        box.Invalid += () => ShowMessage(RadialStrings.ShortcutInvalid, problem: true);
        box.Cleared += () =>
        {
            _item.Payload = string.Empty;
            RefreshState();
        };
        box.Stopped += () =>
        {
            if (_message.Text == RadialStrings.ShortcutRecordingHint) _message.Visibility = Visibility.Collapsed;
        };
        return box;
    }

    // ------------------------------------------------------------------ icons

    private void RefreshIcons()
    {
        var grid = new WrapPanel();
        grid.Children.Add(IconButton(null));
        foreach (var (symbol, _) in RadialIcons.Symbols) grid.Children.Add(IconButton(symbol));
        _iconsHost.Content = grid;
    }

    /// <summary>"Automatic" (the real app/file icon or the action's own glyph) plus the curated grid.</summary>
    private Button IconButton(string? symbol)
    {
        var selected = symbol is null ? _item.SymbolName.Length == 0 : _item.SymbolName == symbol;
        FrameworkElement content;
        if (symbol is not null)
        {
            content = Ui.Icon(RadialIcons.GlyphForSymbol(symbol) ?? Glyphs.Star, 14, "TextPrimary");
        }
        else
        {
            var probe = _item.Clone();
            probe.SymbolName = string.Empty;
            content = RadialIcons.Visual(probe, _catalog, 20, 14, false);
        }
        var face = new Border
        {
            Width = 34,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(selected ? 1.2 : 1),
            Child = content,
            Margin = new Thickness(0, 0, 6, 6),
        };
        face.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSubtle" : "ControlBackground");
        face.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "CardBorder");
        return Ui.Clickable(face, () =>
        {
            _item.SymbolName = symbol ?? string.Empty;
            RefreshIcons();
        }, symbol ?? RadialStrings.AutomaticLabel);
    }

    // ---------------------------------------------------------------- helpers

    private static Grid Row(string label, UIElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Ui.Text(label, 13, FontWeights.Medium);
        l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        if (control is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>A text box with a grey prompt while empty.</summary>
    public static FrameworkElement PlaceholderBox(string text, string placeholder, Action<string> changed, double width = 260)
    {
        var box = new TextBox { Text = text, Width = width };
        var hint = Ui.Text(placeholder, 13, brush: "TextTertiary", wrap: false);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(9, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        box.TextChanged += (_, _) =>
        {
            Sync();
            changed(box.Text);
        };
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(box);
        g.Children.Add(hint);
        return g;
    }

    /// <summary>Preview of the editor for review (STA tests).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var item = new RadialMenuItem(RadialItemKind.Url, "github.com") { SymbolName = "globe" };
        var editor = new RadialItemEditor(item, isNew: false, allowsSubmenu: true, new RadialCatalog([], [], _ => true), _ => { }, () => { }, null);
        PreviewRenderer.Render(editor.Build(), path, 460, 760, dark);
    }
}

/// <summary>
/// Click to record a key combination (the next press with a modifier, or an F-key); Esc cancels,
/// Backspace/Delete clears. Unlike the shared recorder it reports to its owner instead of a setting.
/// </summary>
internal sealed class RadialShortcutBox : Button
{
    private Shortcut _shortcut;

    public RadialShortcutBox(Shortcut shortcut, bool allowClear = true)
    {
        _shortcut = shortcut;
        AllowClear = allowClear;
        MinWidth = 130;
        Padding = new Thickness(10, 5, 10, 5);
        HorizontalAlignment = HorizontalAlignment.Left;
        Refresh();
        Click += (_, _) => Start();
        LostKeyboardFocus += (_, _) => Stop();
        PreviewKeyDown += OnKey;
    }

    public bool AllowClear { get; }
    public bool IsRecording { get; private set; }

    public event Action? Recording;
    public event Action? Stopped;
    public event Action<Shortcut>? Captured;
    public event Action? Cleared;
    public event Action? Invalid;

    public void SetShortcut(Shortcut shortcut)
    {
        _shortcut = shortcut;
        Refresh();
    }

    private void Refresh() =>
        Content = IsRecording ? RadialStrings.ShortcutPressKeys : _shortcut.IsEmpty ? RadialStrings.ShortcutNone : _shortcut.ToString();

    private IDisposable? _hotkeySuspension;

    private void Start()
    {
        // Fieldkit's own hotkeys would otherwise fire instead of being recorded.
        _hotkeySuspension ??= Hotkeys.SuspendAll();
        IsRecording = true;
        Refresh();
        Focus();
        Recording?.Invoke();
    }

    private void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _hotkeySuspension?.Dispose();
        _hotkeySuspension = null;
        Refresh();
        Stopped?.Invoke();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!IsRecording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Stop();
            return;
        }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None && AllowClear)
        {
            _shortcut = Shortcut.None;
            Stop();
            Cleared?.Invoke();
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (Shortcut.IsModifierKey(vk)) return;
        var mods = Modifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Modifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Modifiers.Win;
        if (mods == Modifiers.None && vk is not (>= 0x70 and <= 0x87))
        {
            Invalid?.Invoke();
            return;
        }
        var captured = new Shortcut(mods, vk);
        Stop();
        Captured?.Invoke(captured);
        Refresh();
    }

    /// <summary>The owner accepted the capture.</summary>
    public void Accept(Shortcut shortcut) => SetShortcut(shortcut);
}

/// <summary>A searchable list of Start menu apps (shortcuts under both Start menu folders).</summary>
internal static class RadialAppPicker
{
    public sealed class Entry(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public ImageSource? Icon => RadialIcons.FileIcon(Path);
    }

    public static List<Entry> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    result.TryAdd(name, new Entry(name, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable folders.
            }
        }
        return result.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Entry? Pick(Window? owner)
    {
        var all = StartMenuApps();
        Entry? picked = null;
        var list = new ListBox { Height = 380, ItemTemplate = Template() };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        list.ItemsSource = all;
        var window = new Window
        {
            Title = RadialStrings.StartMenuTitle,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var searchHost = RadialItemEditor.PlaceholderBox(string.Empty, RadialStrings.SearchPlaceholder, text =>
        {
            var q = text.Trim();
            list.ItemsSource = q.Length == 0 ? all : all.Where(e => e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }, 370);
        void Accept()
        {
            if (list.SelectedItem is not Entry entry) return;
            picked = entry;
            window.Close();
        }
        list.MouseDoubleClick += (_, _) => Accept();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
        };
        var buttons = Ui.Row(8, Ui.Button(RadialStrings.CancelButton, window.Close), Ui.Button(RadialStrings.ChooseButton.TrimEnd('…'), Accept, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        window.Content = new Border
        {
            Padding = new Thickness(16),
            Child = Ui.Stack(10, searchHost, list, buttons),
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(window);
        window.Loaded += (_, _) => ((Grid)searchHost).Children[0].Focus();
        window.ShowDialog();
        return picked;
    }

    private static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(FrameworkElement.WidthProperty, 20.0);
        image.SetValue(FrameworkElement.HeightProperty, 20.0);
        image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 10, 2));
        image.SetBinding(Image.SourceProperty, new Binding(nameof(Entry.Icon)) { IsAsync = false });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Entry.Name)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        panel.AppendChild(image);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }
}
