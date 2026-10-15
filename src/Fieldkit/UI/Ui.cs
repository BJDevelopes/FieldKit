// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>
/// Code-first UI builders so every feature looks the same without XAML boilerplate.
/// Colours are DynamicResource keys from UI/Themes (TextPrimary, TextSecondary, Accent,
/// CardBackground, ...), so light/dark switching just works.
///
/// Controls bound to a setting key read it on creation and write it on change; they also
/// follow external changes to that key while loaded.
/// </summary>
public static class Ui
{
    // ------------------------------------------------------------------ text

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null,
        string brush = "TextPrimary", bool wrap = true)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    /// <summary>Card / row title (15 semibold).</summary>
    public static TextBlock Title(string text, double size = 15) => Text(text, size, FontWeights.SemiBold);

    /// <summary>Secondary explanatory text (12.5, wraps).</summary>
    public static TextBlock Caption(string text, double size = 12.5) => Text(text, size, brush: "TextSecondary");

    /// <summary>The small uppercase header above a panel section ("VOLUME MIXER").</summary>
    public static TextBlock SectionHeader(string text)
    {
        var tb = Text(text.ToUpperInvariant(), 12, FontWeights.SemiBold, "TextSecondary", wrap: false);
        tb.Margin = new Thickness(0, 0, 0, 2);
        return tb;
    }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public static TextBlock Icon(string glyph, double size = 16, string brush = "Accent")
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    /// <summary>A small rounded label ("Listens to the keyboard", "Beta", key caps).</summary>
    public static Border Badge(string text, string background = "BadgeBackground", string foreground = "TextSecondary")
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, 11, FontWeights.Medium, foreground, wrap: false),
        };
        b.SetResourceReference(Border.BackgroundProperty, background);
        return b;
    }

    // ------------------------------------------------------------ containers

    public static Border Card(UIElement child, Thickness? padding = null, double radius = 12)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(14, 12, 14, 12),
            Child = child,
        };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        b.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        return b;
    }

    /// <summary>Vertical stack with uniform spacing (WPF StackPanel has none).</summary>
    public static StackPanel Stack(double spacing, params UIElement?[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        AddSpaced(sp, spacing, vertical: true, children);
        return sp;
    }

    /// <summary>Horizontal stack with uniform spacing, vertically centered.</summary>
    public static StackPanel Row(double spacing, params UIElement?[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        AddSpaced(sp, spacing, vertical: false, children);
        foreach (var c in sp.Children.OfType<FrameworkElement>()) c.VerticalAlignment = VerticalAlignment.Center;
        return sp;
    }

    /// <summary>Appends to a stack created by <see cref="Stack"/>/<see cref="Row"/> keeping its spacing.</summary>
    public static void Append(StackPanel panel, double spacing, UIElement child)
    {
        if (child is FrameworkElement fe && panel.Children.Count > 0)
        {
            var m = fe.Margin;
            fe.Margin = panel.Orientation == Orientation.Vertical
                ? new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom)
                : new Thickness(m.Left + spacing, m.Top, m.Right, m.Bottom);
        }
        panel.Children.Add(child);
    }

    private static void AddSpaced(StackPanel sp, double spacing, bool vertical, UIElement?[] children)
    {
        foreach (var child in children)
        {
            if (child is null) continue;
            if (child is FrameworkElement fe && sp.Children.Count > 0)
            {
                var m = fe.Margin;
                fe.Margin = vertical
                    ? new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom)
                    : new Thickness(m.Left + spacing, m.Top, m.Right, m.Bottom);
            }
            sp.Children.Add(child);
        }
    }

    /// <summary>Left content stretches, right content sizes to fit (classic "label ... control" row).</summary>
    public static Grid Split(UIElement left, UIElement? right, double gap = 12)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        if (right is not null)
        {
            Grid.SetColumn(right, 1);
            if (right is FrameworkElement fe)
            {
                fe.Margin = new Thickness(gap, 0, 0, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            g.Children.Add(right);
        }
        return g;
    }

    /// <summary>Equal-width columns.</summary>
    public static UniformGrid Columns(int count, double gap, params UIElement[] children)
    {
        var g = new UniformGrid { Columns = count };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(i % count == 0 ? 0 : gap / 2, i >= count ? gap : 0, i % count == count - 1 ? 0 : gap / 2, 0);
            g.Children.Add(children[i]);
        }
        return g;
    }

    public static Border Divider(double vertical = 8)
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, vertical, 0, vertical) };
        b.SetResourceReference(Border.BackgroundProperty, "Separator");
        return b;
    }

    /// <summary>A rounded square icon tile (Features hub rows, app icons placeholder).</summary>
    public static Border IconTile(string glyph, double size = 36, string background = "ControlBackground", string foreground = "TextPrimary")
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Child = Icon(glyph, size * 0.45, foreground),
        };
        b.SetResourceReference(Border.BackgroundProperty, background);
        return b;
    }

    // ---------------------------------------------------------------- buttons

    public static Button Button(string text, Action onClick, bool accent = false, string? glyph = null)
    {
        var b = new Button
        {
            Content = glyph is null ? text : Row(8, Icon(glyph, 14, accent ? "TextOnAccent" : "TextPrimary"), Text(text, 13, wrap: false)),
        };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        b.Click += (_, _) => Guard(onClick);
        return b;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick, double size = 14, string brush = "TextSecondary")
    {
        var b = new Button { Content = Icon(glyph, size, brush), ToolTip = tooltip, Padding = new Thickness(5) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        b.Click += (_, _) => Guard(onClick);
        return b;
    }

    /// <summary>Wraps any content in a borderless clickable surface.</summary>
    public static Button Clickable(UIElement content, Action onClick, string? tooltip = null)
    {
        var b = new Button { Content = content, ToolTip = tooltip, Padding = new Thickness(0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        b.Click += (_, _) => Guard(onClick);
        return b;
    }

    // -------------------------------------------------------- setting-bound

    /// <summary>A toggle switch bound to a boolean setting.</summary>
    public static CheckBox Toggle(string key, bool defaultValue, Action<bool>? onChanged = null, bool small = false)
    {
        var cb = new CheckBox { IsChecked = Settings.GetBool(key, defaultValue) };
        cb.SetResourceReference(FrameworkElement.StyleProperty, small ? "ToggleSwitchSmall" : "ToggleSwitch");
        var updating = false;
        cb.Click += (_, _) =>
        {
            updating = true;
            var value = cb.IsChecked == true;
            Settings.Set(key, value);
            updating = false;
            onChanged?.Invoke(value);
        };
        Track(cb, () => Settings.Observe(() =>
        {
            if (!updating) cb.IsChecked = Settings.GetBool(key, defaultValue);
        }, key));
        return cb;
    }

    /// <summary>A labelled checkbox bound to a boolean setting.</summary>
    public static CheckBox Check(string text, string key, bool defaultValue, Action<bool>? onChanged = null)
    {
        var cb = new CheckBox { Content = Text(text, 13), IsChecked = Settings.GetBool(key, defaultValue) };
        cb.Click += (_, _) =>
        {
            var value = cb.IsChecked == true;
            Settings.Set(key, value);
            onChanged?.Invoke(value);
        };
        Track(cb, () => Settings.Observe(() => cb.IsChecked = Settings.GetBool(key, defaultValue), key));
        return cb;
    }

    /// <summary>Settings row: title (+ description) on the left, switch on the right.</summary>
    public static FrameworkElement ToggleRow(string title, string? description, string key, bool defaultValue, Action<bool>? onChanged = null)
    {
        var left = Stack(2, Text(title, 13.5, FontWeights.Medium), description is null ? null : Caption(description));
        return Split(left, Toggle(key, defaultValue, onChanged));
    }

    /// <summary>
    /// Controls-tab card: icon, title, description and a switch (with optional indented
    /// sub-toggles revealed while on).
    /// </summary>
    public static FrameworkElement ToggleCard(string glyph, string title, string description, string key, bool defaultValue,
        IEnumerable<ControlSubToggle>? subToggles = null)
    {
        var icon = Icon(glyph, 22);
        icon.Width = 28;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = Stack(2, Title(title, 15), Caption(description, 13));
        text.Margin = new Thickness(12, 0, 0, 0);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(icon);
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        var toggle = Toggle(key, defaultValue);
        toggle.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(toggle, 2);
        head.Children.Add(toggle);

        var card = Card(head, new Thickness(14, 12, 14, 12));
        var subs = subToggles?.ToList();
        if (subs is null || subs.Count == 0) return card;

        var subPanel = Stack(10, subs.Select(s =>
        {
            var row = Split(Stack(1, Text(s.Title, 13, FontWeights.Medium), Caption(s.Description, 12)),
                Toggle(s.SettingKey, s.DefaultValue, small: true));
            row.Margin = new Thickness(62, 0, 8, 0);
            return (UIElement)row;
        }).ToArray());
        subPanel.Margin = new Thickness(0, 8, 0, 4);
        void Sync() => subPanel.Visibility = Settings.GetBool(key, defaultValue) ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        var outer = Stack(0, card, subPanel);
        Track(outer, () => Settings.Observe(Sync, key));
        return outer;
    }

    /// <summary>Title + slider + value label bound to a numeric setting.</summary>
    public static FrameworkElement SliderRow(string title, string key, double min, double max, double defaultValue,
        Func<double, string>? format = null, double step = 1, string? description = null, Action<double>? onChanged = null)
    {
        format ??= v => v.ToString("0");
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Settings.GetDouble(key, defaultValue),
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
        };
        var value = Text(format(slider.Value), 13, FontWeights.Medium, "TextSecondary", wrap: false);
        value.MinWidth = 52;
        value.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            Settings.Set(key, e.NewValue);
            onChanged?.Invoke(e.NewValue);
        };
        var header = Split(Text(title, 13.5, FontWeights.Medium), value);
        return Stack(4, header, slider, description is null ? null : Caption(description));
    }

    /// <summary>Title + dropdown bound to a string setting.</summary>
    public static FrameworkElement ChoiceRow(string title, string key, IReadOnlyList<(string Value, string Label)> options,
        string defaultValue, string? description = null, Action<string>? onChanged = null)
    {
        var combo = Choice(key, options, defaultValue, onChanged);
        combo.MinWidth = 160;
        var left = Stack(2, Text(title, 13.5, FontWeights.Medium), description is null ? null : Caption(description));
        return Split(left, combo);
    }

    public static ComboBox Choice(string key, IReadOnlyList<(string Value, string Label)> options, string defaultValue,
        Action<string>? onChanged = null)
    {
        var combo = new ComboBox();
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        void Select()
        {
            var current = Settings.GetString(key, defaultValue);
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                                 ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        Select();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                Settings.Set(key, (string)item.Tag);
                onChanged?.Invoke((string)item.Tag);
            }
        };
        return combo;
    }

    /// <summary>Title + shortcut recorder bound to a shortcut setting ("Ctrl+Alt+V").</summary>
    public static FrameworkElement ShortcutRow(string title, string key, string defaultShortcut, string? description = null)
    {
        var left = Stack(2, Text(title, 13.5, FontWeights.Medium), description is null ? null : Caption(description));
        return Split(left, new ShortcutRecorder(key, defaultShortcut));
    }

    /// <summary>A text box bound to a string setting (saved on every edit).</summary>
    public static TextBox TextInput(string key, string defaultValue = "", string? placeholder = null, bool multiline = false)
    {
        var tb = new TextBox
        {
            Text = Settings.GetString(key, defaultValue),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 80 : 0,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            ToolTip = placeholder,
        };
        tb.TextChanged += (_, _) => Settings.Set(key, tb.Text);
        return tb;
    }

    // -------------------------------------------------------------- composites

    /// <summary>Utilities-tab row: icon, title, description, optional key-cap shortcut, chevron.</summary>
    public static FrameworkElement NavRow(string glyph, string title, string description, Action onClick, string? shortcut = null)
    {
        var icon = Icon(glyph, 22);
        icon.Width = 28;
        var text = Stack(2, Title(title, 15), Caption(description, 13));
        text.Margin = new Thickness(12, 0, 0, 0);
        var right = Row(10,
            string.IsNullOrEmpty(shortcut) ? null : Badge(shortcut),
            Icon(Glyphs.ChevronRight, 12, "TextSecondary"));
        right.Margin = new Thickness(10, 0, 0, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        var card = Card(g);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) => Guard(onClick);
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        return card;
    }

    /// <summary>A Settings group: optional header above a card of rows separated by dividers.</summary>
    public static FrameworkElement Group(string? header, params UIElement?[] rows)
    {
        var inner = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (!first) inner.Children.Add(Divider(10));
            inner.Children.Add(row);
            first = false;
        }
        var card = Card(inner, new Thickness(16, 14, 16, 14));
        if (header is null) return card;
        var h = Text(header, 15, FontWeights.SemiBold);
        h.Margin = new Thickness(2, 0, 0, 8);
        return Stack(0, h, card);
    }

    /// <summary>A scrolling Settings page with a title, optional intro and spaced content.</summary>
    public static FrameworkElement Page(string title, string? intro, params UIElement?[] content)
    {
        var header = Text(title, 26, FontWeights.SemiBold);
        header.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var stack = Stack(20, Stack(6, header, intro is null ? null : Caption(intro, 13.5)));
        foreach (var c in content)
        {
            if (c is not null) Append(stack, 20, c);
        }
        stack.Margin = new Thickness(32, 24, 32, 32);
        stack.MaxWidth = 860;
        stack.HorizontalAlignment = HorizontalAlignment.Left;
        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    // --------------------------------------------------------------- lifetime

    /// <summary>
    /// Subscribes while <paramref name="owner"/> is loaded: <paramref name="subscribe"/> runs on
    /// Loaded (and immediately if already loaded) and its result is disposed on Unloaded.
    /// </summary>
    public static void Track(FrameworkElement owner, Func<IDisposable?> subscribe)
    {
        IDisposable? token = null;
        if (owner.IsLoaded) token = subscribe();
        owner.Loaded += (_, _) =>
        {
            token?.Dispose();
            token = subscribe();
        };
        owner.Unloaded += (_, _) =>
        {
            token?.Dispose();
            token = null;
        };
    }

    /// <summary>Runs <paramref name="tick"/> every <paramref name="interval"/> while <paramref name="owner"/> is loaded
    /// (and once immediately on load).</summary>
    public static void Every(FrameworkElement owner, TimeSpan interval, Action tick)
    {
        Track(owner, () =>
        {
            Guard(tick);
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += (_, _) => Guard(tick);
            timer.Start();
            return new ActionDisposable(timer.Stop);
        });
    }

    /// <summary>Runs an action, logging (not throwing) failures.</summary>
    public static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("UI action failed", ex); }
    }

    /// <summary>Runs on the UI thread (no-op marshal if already there).</summary>
    public static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) Guard(action);
        else d.BeginInvoke(() => Guard(action));
    }
}

/// <summary>An IDisposable from a lambda.</summary>
public sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() { _dispose?.Invoke(); _dispose = null; }
}

/// <summary>
/// Click to record: the next key combination becomes the shortcut, Esc cancels,
/// Backspace/Delete clears. Saved as text to a setting key.
/// </summary>
public sealed class ShortcutRecorder : Button
{
    private readonly string _key;
    private readonly string _default;
    private bool _recording;
    private IDisposable? _suspension;

    public ShortcutRecorder(string key, string defaultShortcut)
    {
        _key = key;
        _default = defaultShortcut;
        // Implicit styles do not apply to subclasses; wear the app's Button style explicitly.
        SetResourceReference(StyleProperty, typeof(Button));
        MinWidth = 120;
        Padding = new Thickness(10, 5, 10, 5);
        Refresh();
        Click += (_, _) => StartRecording();
        Ui.Track(this, () =>
        {
            Hotkeys.UnavailableChanged += Refresh;
            return new ActionDisposable(() => Hotkeys.UnavailableChanged -= Refresh);
        });
        LostKeyboardFocus += (_, _) => StopRecording();
        PreviewKeyDown += OnKey;
    }

    private void Refresh()
    {
        var text = Shortcut.FromSetting(_key, _default).ToString();
        var taken = !_recording && Hotkeys.IsUnavailable(_key);
        Content = _recording ? "Type shortcut…" : string.IsNullOrEmpty(text) ? "Record shortcut" : taken ? $"{text}  ⚠" : text;
        ToolTip = taken ? "Another app already uses this shortcut. Record a different one." : null;
    }

    private void StartRecording()
    {
        // Fieldkit's own hotkeys would otherwise fire instead of being recorded.
        _suspension ??= Hotkeys.SuspendAll();
        _recording = true;
        Refresh();
        Focus();
    }

    private void StopRecording()
    {
        _suspension?.Dispose();
        _suspension = null;
        _recording = false;
        Refresh();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Settings.Set(_key, string.Empty);
            StopRecording();
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (Shortcut.IsModifierKey(vk)) return;
        var mods = Modifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Modifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Modifiers.Win;
        if (mods == Modifiers.None && vk is not (>= 0x70 and <= 0x87)) return; // need a modifier unless F-key
        Settings.Set(_key, new Shortcut(mods, vk).ToString());
        StopRecording();
    }
}
