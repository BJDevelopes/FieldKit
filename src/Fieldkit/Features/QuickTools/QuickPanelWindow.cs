// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Shell;
using Fieldkit.Interop;
using Fieldkit.UI;
using Fieldkit.UI.Panel;

namespace Fieldkit.Features.QuickTools;

/// <summary>
/// The Quick panel (upstream QuickLauncher): a floating 3-column grid of favourite tools,
/// summoned by a shortcut, with keyboard navigation, 1–9 shortcuts and an edit mode to hide,
/// add back and drag tiles around.
/// </summary>
public sealed class QuickPanelWindow : Window
{
    private const int Columns = 3;
    private const string TuneGlyph = "\uE9E9";

    private static QuickPanelWindow? _instance;

    private readonly ContentControl _body = new();
    private readonly Button _edit;
    private readonly TextBlock _shortcut = Ui.Text(string.Empty, 9.5, brush: "TextTertiary", wrap: false);
    private List<QuickPanelItem> _shown = [];
    private readonly List<Action<bool>> _painters = [];
    private int _selected;
    private bool _editing;
    private bool _hostingToggles;
    private Point _dragStart;
    private (List<QuickPanelItem>, List<QuickPanelItem>)? _previewItems;

    public static void ToggleInstance()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.HidePanel();
            return;
        }
        _instance ??= new QuickPanelWindow();
        _instance.ShowPanel();
    }

    public static void CloseInstance()
    {
        _instance?.Close();
        _instance = null;
    }

    private QuickPanelWindow()
    {
        Title = "Quick panel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 420 + 20;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var close = Ui.IconButton(Glyphs.Close, "Close", HidePanel, 12);
        _edit = Ui.IconButton(TuneGlyph, "Use the tune button to choose, hide and drag the tools around.", ToggleEdit, 13);
        var brand = PanelWindow.BrandMark(26);
        brand.HorizontalAlignment = HorizontalAlignment.Center;
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(brand);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        _edit.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(close);
        header.Children.Add(_edit);

        var footer = Ui.Split(Ui.Row(5, Ui.Icon(Glyphs.Keyboard, 10, "TextTertiary"), _shortcut), Ui.Text("Esc", 9.5, brush: "TextTertiary"));
        footer.Margin = new Thickness(2, 12, 2, 0);

        var root = new Border
        {
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(10),
            Child = Ui.Stack(0, header, _body, footer),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 },
        };
        root.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Deactivated += (_, _) =>
        {
            // A working window (hosted tool or edit mode) survives its own dialogs.
            if (!_editing && !_hostingToggles) HidePanel();
        };
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            if (_instance == this) _instance = null;
        };
    }

    private void ShowPanel()
    {
        _editing = false;
        _hostingToggles = false;
        _selected = 0;
        _shortcut.Text = Shortcut.FromSetting(QuickPanelItems.ShortcutKey, QuickPanelItems.DefaultShortcut).ToString();
        Rebuild();
        Opacity = 0;
        Show();
        UpdateLayout();
        Place();
        WindowHelper.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        Focus();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)));
    }

    public void HidePanel()
    {
        _editing = false;
        _hostingToggles = false;
        Hide();
    }

    /// <summary>On the display under the pointer, a bit above centre (upstream 0.62 rule), 16 px inside.</summary>
    private void Place()
    {
        Native.GetCursorPos(out var p);
        var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
        var scale = WindowHelper.GetScaleAt(p.X, p.Y);
        var w = (int)Math.Round(ActualWidth * scale);
        var h = (int)Math.Round(ActualHeight * scale);
        var x = work.Left + (work.Width - w) / 2;
        var y = work.Top + (int)((work.Height - h) * 0.38);
        var inset = (int)(16 * scale);
        x = Math.Clamp(x, work.Left + inset, Math.Max(work.Left + inset, work.Right - w - inset));
        y = Math.Clamp(y, work.Top + inset, Math.Max(work.Top + inset, work.Bottom - h - inset));
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }

    private void ToggleEdit()
    {
        _editing = !_editing;
        _hostingToggles = false;
        Rebuild();
    }

    // ---- Content ---------------------------------------------------------------------

    private void Rebuild()
    {
        _edit.Content = Ui.Icon(_editing ? Glyphs.Check : TuneGlyph, 13, _editing ? "Accent" : "TextSecondary");
        if (_hostingToggles)
        {
            _body.Content = BuildHostedToggles();
            return;
        }
        var (shown, hidden) = _previewItems ?? QuickPanelItems.Resolve();
        _shown = shown;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, shown.Count - 1));
        _painters.Clear();
        var stack = Ui.Stack(12);
        if (shown.Count == 0 && !_editing)
        {
            stack.Children.Add(Ui.Caption("All tools are hidden. Use the tune button to add them back."));
        }
        else
        {
            var grid = new UniformGrid { Columns = Columns };
            for (var i = 0; i < shown.Count; i++)
            {
                var tile = Tile(shown[i], i);
                tile.Margin = new Thickness(5);
                grid.Children.Add(tile);
            }
            stack.Children.Add(grid);
        }
        if (_editing && hidden.Count > 0)
        {
            var header = Ui.Text("ADD BACK", 9, FontWeights.Bold, "TextSecondary", wrap: false);
            var chips = new WrapPanel();
            foreach (var item in hidden)
            {
                var chip = Energy.EnergyUi.Chip(item.Title, false, () =>
                {
                    QuickPanelItems.Show(item.Id);
                    Rebuild();
                }, Glyphs.Add);
                chips.Children.Add(chip);
            }
            Ui.Append(stack, 12, Ui.Stack(6, header, chips));
        }
        _body.Content = stack;
    }

    private FrameworkElement Tile(QuickPanelItem item, int index)
    {
        var selected = index == _selected && !_editing;
        var active = item.IsActive?.Invoke() == true;
        var icon = Ui.Icon(item.Glyph, 19, active ? "Accent" : "TextPrimary");
        var iconBox = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(12), Child = icon };
        iconBox.SetResourceReference(Border.BackgroundProperty, active ? "AccentSubtle" : "ControlBackground");
        var iconGrid = new Grid { Width = 54, Height = 50, HorizontalAlignment = HorizontalAlignment.Center };
        iconBox.HorizontalAlignment = HorizontalAlignment.Center;
        iconBox.VerticalAlignment = VerticalAlignment.Bottom;
        iconGrid.Children.Add(iconBox);
        Border? badge = null;
        if (index < 9 && !_editing)
        {
            badge = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Text((index + 1).ToString(), 9, FontWeights.Bold, "TextSecondary", wrap: false),
            };
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
            iconGrid.Children.Add(badge);
        }
        if (active)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, "Green");
            iconGrid.Children.Add(dot);
        }
        if (_editing)
        {
            var remove = Ui.IconButton("\uE738", "Hide from panel", () =>
            {
                QuickPanelItems.Hide(item.Id);
                Rebuild();
            }, 9, "TextOnAccent");
            remove.Width = remove.Height = 18;
            remove.Padding = new Thickness(0);
            remove.SetResourceReference(Control.BackgroundProperty, "Red");
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            remove.VerticalAlignment = VerticalAlignment.Top;
            iconGrid.Children.Add(remove);
        }
        var label = Ui.Text(item.Title, 10.5, FontWeights.Medium);
        label.TextAlignment = TextAlignment.Center;
        label.MaxHeight = 30;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Border
        {
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(6, 8, 6, 8),
            Child = Ui.Stack(6, iconGrid, label),
            Cursor = Cursors.Hand,
            MinHeight = 98,
            AllowDrop = _editing,
            Tag = item.Id,
        };
        void Paint(bool on)
        {
            tile.SetResourceReference(Border.BackgroundProperty, on ? "AccentSubtle" : "CardBackground");
            tile.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "CardBorder");
            if (badge is null) return;
            badge.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "BadgeBackground");
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, on ? "TextOnAccent" : "TextSecondary");
        }
        Paint(selected);
        _painters.Add(Paint);
        tile.MouseEnter += (_, _) =>
        {
            if (_editing) return;
            Select(index);
        };
        tile.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (!_editing) Open(item);
        };
        tile.MouseMove += (_, e) =>
        {
            if (!_editing || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - _dragStart;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            DragDrop.DoDragDrop(tile, item.Id, DragDropEffects.Move);
        };
        tile.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(string)) is not string dragged || dragged == item.Id) return;
            QuickPanelItems.MoveBefore(dragged, item.Id);
            Rebuild();
        };
        return tile;
    }

    private FrameworkElement BuildHostedToggles()
    {
        var back = Ui.IconButton(Glyphs.Previous, "Back", () =>
        {
            _hostingToggles = false;
            Rebuild();
        }, 12);
        var head = Ui.Split(Ui.Row(6, back, Ui.Icon(Glyphs.Toggles, 13), Ui.Text("Quick toggles", 13, FontWeights.SemiBold)), null);
        var grid = new UniformGrid { Columns = 3 };
        foreach (var t in FeatureRuntime.Registry.QuickToggles.OrderBy(t => t.Order))
        {
            var tile = CoreModule.QuickToggleTile(t);
            tile.Margin = new Thickness(4);
            grid.Children.Add(tile);
        }
        return Ui.Stack(10, head, new ScrollViewer { Content = grid, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private void Open(QuickPanelItem item)
    {
        if (item.IsTogglesTile)
        {
            _hostingToggles = true;
            Rebuild();
            return;
        }
        if (item.Action is null) return;
        if (item.Action.Id == "keepAwake.toggle")
        {
            Ui.Guard(item.Action.Run);
            Rebuild();
            return;
        }
        HidePanel();
        var run = item.Action.Run;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Ui.Guard(run);
        };
        t.Start();
    }

    private void Select(int index)
    {
        if (index == _selected || index < 0 || index >= _painters.Count) return;
        if (_selected < _painters.Count) _painters[_selected](false);
        _selected = index;
        _painters[index](true);
    }

    /// <summary>The panel content for sample tiles, detached from any window (for previews and tests).</summary>
    public static FrameworkElement BuildPreview(List<QuickPanelItem> shown, List<QuickPanelItem> hidden, bool editing, string shortcut)
    {
        var w = new QuickPanelWindow { _previewItems = (shown, hidden), _editing = editing };
        w._shortcut.Text = shortcut;
        w.Rebuild();
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        w.Close();
        content.Width = 420;
        return content;
    }

    // ---- Keyboard --------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            if (_hostingToggles) { _hostingToggles = false; Rebuild(); }
            else if (_editing) { _editing = false; Rebuild(); }
            else HidePanel();
            return;
        }
        if (_editing || _hostingToggles || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var count = _shown.Count;
        var next = key switch
        {
            Key.Left => QuickToolsLogic.GridMove(_selected, count, Columns, -1, 0),
            Key.Right => QuickToolsLogic.GridMove(_selected, count, Columns, 1, 0),
            Key.Up => QuickToolsLogic.GridMove(_selected, count, Columns, 0, -1),
            Key.Down => QuickToolsLogic.GridMove(_selected, count, Columns, 0, 1),
            _ => -1,
        };
        if (next >= 0)
        {
            e.Handled = true;
            Select(next);
            return;
        }
        if (key == Key.Enter && count > 0)
        {
            e.Handled = true;
            Open(_shown[_selected]);
            return;
        }
        var digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : -1;
        var index = QuickToolsLogic.DigitIndex(digit, count);
        if (index >= 0)
        {
            e.Handled = true;
            Open(_shown[index]);
        }
    }
}
