// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// The Utilities → Window layout popup (upstream PanelWindowLayoutView): quick switches and a
/// grid of every placement, applied to the window that was in front before the popup opened.
/// Actions can be hidden from the grid (their shortcuts keep working).
/// </summary>
internal sealed class LayoutGridPopup : OverlayWindow
{
    public const string HiddenActionsKey = "windowLayout.hiddenActions";
    private static LayoutGridPopup? _open;
    private readonly StackPanel _body = Ui.Stack(10);
    private bool _editing;

    private LayoutGridPopup() : base(noActivate: false)
    {
        Title = "Window layout";
        var scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 12, 14, 14),
        };
        var border = new Border { BorderThickness = new Thickness(1), Child = scroll };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        Content = border;
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) =>
        {
            if (_open == this) _open = null;
        };
        Rebuild();
    }

    public static void Open()
    {
        _open?.Close();
        var popup = new LayoutGridPopup();
        _open = popup;
        Native.GetCursorPos(out var cursor);
        var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        var scale = WindowHelper.GetScaleAt(cursor.X, cursor.Y);
        const double width = 360;
        var height = Math.Min(720, work.Height / scale - 24);
        var x = (int)Math.Clamp(cursor.X - width * scale / 2, work.Left + 8, work.Right - width * scale - 8);
        var y = (int)Math.Clamp(cursor.Y - height * scale - 12, work.Top + 8, work.Bottom - height * scale - 8);
        popup.PlaceAt(x, y, width, height);
        popup.Show();
        popup.Activate();
    }

    private void Rebuild()
    {
        _body.Children.Clear();
        var edit = Ui.IconButton(_editing ? Glyphs.Check : Glyphs.Settings, "Choose which layouts to show", () =>
        {
            _editing = !_editing;
            Rebuild();
        }, 14, _editing ? "Accent" : "TextSecondary");
        var close = Ui.IconButton(Glyphs.Close, "Close", Close, 12);
        var header = Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Layout, 15), Ui.Text("Window layout", 13, FontWeights.SemiBold, wrap: false)),
            Ui.Row(2, edit, close));
        _body.Children.Add(header);
        Ui.Append(_body, 10, Intro());

        var hidden = LayoutActions.ParseHidden(Settings.GetString(HiddenActionsKey, string.Empty));
        foreach (var (title, actions) in LayoutActions.Groups)
        {
            var shown = _editing ? actions : actions.Where(a => !hidden.Contains(a)).ToArray();
            if (shown.Length == 0) continue;
            var grid = new UniformGrid { Columns = 2 };
            foreach (var action in shown) grid.Children.Add(ActionButton(action, hidden));
            Ui.Append(_body, 10, Ui.Card(Ui.Stack(7, Ui.SectionHeader(title), grid), new Thickness(10, 8, 10, 10)));
        }

        var result = Ui.Text(string.Empty, 12, FontWeights.Medium, "TextSecondary");
        if (WindowLayoutEngine.LastResult is { } last) ShowResult(result, last);
        Ui.Track(result, () =>
        {
            void OnResult(LayoutResult r) => ShowResult(result, r);
            WindowLayoutEngine.ResultChanged += OnResult;
            return new ActionDisposable(() => WindowLayoutEngine.ResultChanged -= OnResult);
        });
        Ui.Append(_body, 10, result);
    }

    private static void ShowResult(TextBlock text, LayoutResult r)
    {
        text.Text = WindowLayoutEngine.ResultText(r);
        text.SetResourceReference(TextBlock.ForegroundProperty, r is LayoutResult.Done or LayoutResult.Restored ? "Green" : "Orange");
    }

    private static FrameworkElement Intro()
    {
        var caption = Ui.Caption("Arrange windows into screen sections or move and resize them with a mouse or touchpad.", 11.5);
        return Ui.Card(Ui.Stack(6,
            Ui.Check("Snap windows at screen edges", WindowLayoutService.EdgeSnapEnabledKey, false),
            Ui.Check("Shortcuts", WindowLayoutService.ShortcutsEnabledKey, true),
            Ui.Check("Move and resize by dragging", WindowLayoutService.GestureEnabledKey, false),
            caption), new Thickness(10, 8, 10, 10));
    }

    private FrameworkElement ActionButton(LayoutAction action, HashSet<LayoutAction> hidden)
    {
        var isHidden = hidden.Contains(action);
        var icon = _editing
            ? Ui.Icon(isHidden ? "\uED1A" : Glyphs.Eye, 13, "TextSecondary")
            : LayoutIcon.For(action, 16);
        var title = Ui.Text(action.Title(), 11.5, FontWeights.SemiBold, wrap: false);
        var lines = Ui.Stack(1, title);
        var shortcut = Shortcut.FromSetting(action.ShortcutKey(), action.DefaultShortcut()).ToString();
        if (!_editing && shortcut.Length > 0 && Settings.GetBool(WindowLayoutService.ShortcutsEnabledKey, true))
            lines.Children.Add(Ui.Text(shortcut, 10, brush: "TextSecondary", wrap: false));
        var content = Ui.Row(7, icon, lines);
        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(3),
            MinHeight = 34,
            Opacity = _editing && isHidden ? 0.45 : 1,
        };
        button.Click += (_, _) => Ui.Guard(() =>
        {
            if (_editing)
            {
                var set = LayoutActions.ParseHidden(Settings.GetString(HiddenActionsKey, string.Empty));
                if (!set.Add(action)) set.Remove(action);
                Settings.Set(HiddenActionsKey, LayoutActions.HiddenStorage(set));
                Rebuild();
            }
            else
            {
                WindowLayoutEngine.Apply(action);
            }
        });
        return button;
    }
}
