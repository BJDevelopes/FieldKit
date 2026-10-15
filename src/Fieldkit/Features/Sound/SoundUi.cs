// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>Small building blocks shared by the mixer, the priority editor and the sound settings pages.</summary>
internal static class SoundUi
{
    /// <summary>Speaker with waves (upstream speaker.wave.2.fill).</summary>
    public const string SpeakerGlyph = "";
    /// <summary>Counter-clockwise arrow (upstream arrow.counterclockwise).</summary>
    public const string ResetGlyph = "";

    /// <summary>A secondary label with a leading glyph, like upstream's "Output" / "Microphone" pickers.</summary>
    public static FrameworkElement Label(string glyph, string text)
    {
        var icon = Ui.Icon(glyph, 11.5, "TextSecondary");
        icon.Width = 16;
        return Ui.Row(6, icon, Ui.Text(text, 12, FontWeights.Medium, "TextSecondary", wrap: false));
    }

    /// <summary>A one- or two-line explanatory note with a glyph (fallbacks, errors, empty states).</summary>
    public static FrameworkElement Message(string glyph, string text, string brush = "TextSecondary")
    {
        var icon = Ui.Icon(glyph, 10.5, brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 6, 0);
        var caption = Ui.Text(text, 11, brush: brush);
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(caption);
        return dock;
    }

    /// <summary>A compact dropdown of (value, label) pairs; <paramref name="onPick"/> runs on user selection only.</summary>
    public static ComboBox Combo(IReadOnlyList<(string Value, string Label)> items, string? selected, Action<string> onPick, double width)
    {
        var combo = new ComboBox { Width = width, FontSize = 12, MinHeight = 24, Height = 24 };
        foreach (var (value, label) in items)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value, FontSize = 12, ToolTip = label });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected);
        var ready = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (ready && combo.SelectedItem is ComboBoxItem item) Ui.Guard(() => onPick((string)item.Tag));
        };
        ready = true;
        return combo;
    }

    /// <summary>"Speakers (Realtek(R) Audio) (current)" for the default device.</summary>
    public static string DeviceTitle(AudioDevice device) => device.IsDefault ? $"{device.Name} (current)" : device.Name;

    /// <summary>A slider whose fill turns orange in the boost range.</summary>
    public static Slider VolumeSlider(double maximum)
    {
        var slider = new Slider { Minimum = 0, Maximum = maximum, SmallChange = 0.01, LargeChange = 0.05, VerticalAlignment = VerticalAlignment.Center };
        slider.SetResourceReference(Control.ForegroundProperty, "Accent");
        return slider;
    }

    /// <summary>Little uppercase-free subsection title ("Output priority").</summary>
    public static TextBlock SubTitle(string text) => Ui.Text(text, 12, FontWeights.SemiBold, "TextSecondary", wrap: false);

    /// <summary>A borderless disclosure header: chevron + title; toggles <paramref name="body"/>.</summary>
    public static FrameworkElement Disclosure(string title, FrameworkElement body, bool expanded, Action<bool>? onToggle = null,
        string? glyph = null, bool chevronLeading = true)
    {
        var chevron = Ui.Icon(Glyphs.ChevronRight, 9, "TextSecondary");
        chevron.Width = 12;
        var label = Ui.Text(title, 12, FontWeights.SemiBold, "TextSecondary", wrap: false);
        UIElement? icon = glyph is null ? null : Ui.Icon(glyph, 11, "TextSecondary");
        var head = chevronLeading
            ? (FrameworkElement)Ui.Row(7, chevron, icon, label)
            : Ui.Split(Ui.Row(7, icon, label), chevron);
        var button = Ui.Clickable(head, () => { });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        body.Margin = new Thickness(chevronLeading ? 19 : 0, 8, 0, 0);
        void Apply()
        {
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new RotateTransform(expanded ? 90 : 0);
        }
        button.Click += (_, _) =>
        {
            expanded = !expanded;
            Apply();
            onToggle?.Invoke(expanded);
        };
        Apply();
        return Ui.Stack(0, button, body);
    }

    /// <summary>
    /// The percentage beside a slider: reads like a label, becomes a selected text field on
    /// click (Enter commits, Esc cancels, leaving commits), like upstream EditableVolumePercent.
    /// </summary>
    public sealed class EditablePercent : Grid
    {
        private readonly TextBlock _text;
        private readonly TextBlock _bolt;
        private readonly Button _label;
        private readonly TextBox _editor;
        private readonly int _maximum;
        private readonly Action<double> _commit;
        private int _percent;

        public EditablePercent(int maximumPercent, double width, Action<double> commit, string accessibleName)
        {
            _maximum = maximumPercent;
            _commit = commit;
            Width = width;
            _text = Ui.Text("100%", 11.5, FontWeights.Medium, "TextSecondary", wrap: false);
            _text.FontFeatures();
            _bolt = Ui.Icon(Glyphs.Bolt, 9.5, "Orange");
            _bolt.Margin = new Thickness(0, 0, 2, 0);
            _bolt.Visibility = Visibility.Collapsed;
            var row = Ui.Row(0, _bolt, _text);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            _label = Ui.Clickable(row, BeginEditing, "Click to type a level");
            _label.Padding = new Thickness(2, 1, 2, 1);
            _label.HorizontalContentAlignment = HorizontalAlignment.Right;
            System.Windows.Automation.AutomationProperties.SetName(_label, accessibleName);
            _editor = new TextBox
            {
                Visibility = Visibility.Collapsed,
                FontSize = 11.5,
                Padding = new Thickness(3, 0, 3, 0),
                MinHeight = 0,
                Height = 20,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; EndEditing(); }
            };
            _editor.LostKeyboardFocus += (_, _) => { if (_editor.IsVisible) Commit(); };
            Children.Add(_label);
            Children.Add(_editor);
        }

        public bool IsEditing => _editor.Visibility == Visibility.Visible;

        public void Show(int percent, bool boosting)
        {
            _percent = percent;
            _text.Text = $"{percent}%";
            _text.SetResourceReference(TextBlock.ForegroundProperty, boosting ? "Orange" : "TextSecondary");
            _bolt.Visibility = boosting ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BeginEditing()
        {
            _editor.Text = _percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
            _label.Visibility = Visibility.Hidden;
            _editor.Visibility = Visibility.Visible;
            _editor.Focus();
            _editor.SelectAll();
        }

        private void Commit()
        {
            if (SoundLogic.VolumeFraction(_editor.Text, _maximum) is not { } level)
            {
                System.Media.SystemSounds.Beep.Play();
                EndEditing();
                return;
            }
            EndEditing();
            _commit(level);
        }

        private void EndEditing()
        {
            _editor.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Tabular digits so percentages don't jitter while dragging.</summary>
    private static void FontFeatures(this TextBlock text) => System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
}
