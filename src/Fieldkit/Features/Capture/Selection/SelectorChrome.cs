// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>The mode bar and key hints of the capture selector (upstream UnifiedCaptureGuideContent).</summary>
internal static class SelectorChrome
{
    internal const string ZoomGlyph = "\uE71E";
    internal const string StackGlyph = "\uE8CB";

    public static string ToolGlyph(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => Glyphs.Record,
        CaptureTool.Text => Glyphs.TextScan,
        CaptureTool.Color => Glyphs.Eyedropper,
        _ => Glyphs.Camera,
    };

    public static FrameworkElement Build(CaptureSelector owner)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (owner.ShowMenu && owner.Tools.Count > 1)
        {
            var palette = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var tool in owner.Tools) palette.Children.Add(ModeButton(owner, tool));
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Plate(palette, new Thickness(4), 14));
            var esc = EscHint(owner, 56);
            esc.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(esc);
            stack.Children.Add(row);
        }

        var guide = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var subtitle = Ui.Text(owner.Subtitle, 11, FontWeights.Medium, "TextSecondary", wrap: false);
        subtitle.MaxWidth = 560;
        subtitle.VerticalAlignment = VerticalAlignment.Center;
        guide.Children.Add(subtitle);
        if (owner.Tools.Count > 1) guide.Children.Add(KeyHint(Glyphs.Keyboard, owner.Tools.Count == 2 ? "1\u20132" : $"1\u2013{owner.Tools.Count}"));
        if (owner.Tool != CaptureTool.Color)
        {
            if (!owner.ScrollingOn) guide.Children.Add(KeyHint(Glyphs.Maximize, "\u21B5"));
            if (owner.Tool == CaptureTool.Screenshot) guide.Children.Add(KeyHint(StackGlyph, owner.ScrollingOn ? "S on" : "S"));
            guide.Children.Add(KeyHint(ZoomGlyph, owner.LoupeOn ? "Z on" : "Z"));
            if (owner.LoupeOn) guide.Children.Add(KeyHint(Glyphs.Copy, "C"));
            if (owner.LastRegion is not null) guide.Children.Add(KeyHint(Glyphs.Refresh, "R"));
        }
        var guideRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        guideRow.Children.Add(Plate(guide, new Thickness(12, 2, 6, 2), 15, 30));
        if (owner.Tool is CaptureTool.Screenshot or CaptureTool.Recording && !owner.ScrollingOn)
        {
            var full = ChromeButton(Glyphs.Maximize, "Full screen", owner.CaptureFullScreen);
            full.Margin = new Thickness(8, 0, 0, 0);
            guideRow.Children.Add(full);
        }
        if (owner.Tool == CaptureTool.Screenshot)
        {
            var scrolling = ChromeButton(StackGlyph, owner.ScrollingOn ? "Scrolling on" : "Scrolling capture", () => owner.SetScrolling(!owner.ScrollingOn), owner.ScrollingOn);
            scrolling.Margin = new Thickness(8, 0, 0, 0);
            guideRow.Children.Add(scrolling);
        }
        if (!(owner.ShowMenu && owner.Tools.Count > 1))
        {
            var esc = EscHint(owner, 30);
            esc.Margin = new Thickness(8, 0, 0, 0);
            guideRow.Children.Add(esc);
        }
        stack.Children.Add(guideRow);

        if (owner.Tool == CaptureTool.Recording)
        {
            var audio = new StackPanel { Orientation = Orientation.Horizontal };
            audio.Children.Add(AudioToggle(Glyphs.Volume, "PC sound", CaptureSettings.RecorderSystemAudio, true));
            var mic = AudioToggle(Glyphs.Microphone, "Microphone", CaptureSettings.RecorderMicrophone, false);
            mic.Margin = new Thickness(6, 0, 0, 0);
            audio.Children.Add(mic);
            var plate = Plate(audio, new Thickness(4), 16);
            plate.HorizontalAlignment = HorizontalAlignment.Center;
            plate.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(plate);
        }
        return stack;
    }

    private static Border Plate(UIElement child, Thickness padding, double radius, double height = double.NaN)
    {
        var b = new Border
        {
            Child = child,
            Padding = padding,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            Height = height,
            Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = 0.3, Direction = 270 },
        };
        b.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        b.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        return b;
    }

    private static FrameworkElement ModeButton(CaptureSelector owner, CaptureTool tool)
    {
        var selected = owner.Tool == tool;
        var digit = new Border
        {
            Width = 19,
            Height = 19,
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = ((int)tool + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = selected ? Brushes.White : null,
            },
        };
        if (selected) digit.SetResourceReference(Border.BackgroundProperty, "Accent");
        else digit.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        if (!selected) ((TextBlock)digit.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var icon = Ui.Icon(ToolGlyph(tool), 14, selected ? "Accent" : "TextPrimary");
        icon.Margin = new Thickness(6, 0, 0, 0);
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(digit);
        top.Children.Add(icon);
        var title = Ui.Text(CaptureSettings.ShortTitle(tool), 10.5, selected ? FontWeights.SemiBold : FontWeights.Medium, selected ? "TextPrimary" : "TextSecondary", wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 3, 0, 0);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(top);
        content.Children.Add(title);
        var card = new Border
        {
            Width = 116,
            Height = 48,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = $"{CaptureSettings.Title(tool)} ({(int)tool + 1})",
        };
        if (selected)
        {
            card.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        }
        else
        {
            card.Background = Brushes.Transparent;
            card.BorderBrush = Brushes.Transparent;
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        }
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            owner.SelectTool(tool);
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return card;
    }

    private static FrameworkElement KeyHint(string glyph, string key)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Icon(glyph, 10, "TextSecondary"));
        var t = Ui.Text(key, 10.5, FontWeights.SemiBold, "TextSecondary", wrap: false);
        t.Margin = new Thickness(5, 0, 0, 0);
        row.Children.Add(t);
        var b = new Border
        {
            Child = row,
            Padding = new Thickness(8, 0, 8, 0),
            Height = 24,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.VerticalAlignment = VerticalAlignment.Center;
        b.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return b;
    }

    private static Border EscHint(CaptureSelector owner, double height)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ui.Icon(Glyphs.Close, 10, "TextSecondary"));
        var t = Ui.Text("esc", 10.5, FontWeights.SemiBold, "TextSecondary", wrap: false);
        t.Margin = new Thickness(5, 0, 0, 0);
        row.Children.Add(t);
        var plate = Plate(row, new Thickness(10, 0, 10, 0), 14, height);
        plate.Cursor = Cursors.Hand;
        plate.ToolTip = "Esc cancels";
        plate.MouseLeftButtonDown += (_, e) => e.Handled = true;
        plate.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            owner.KeyPressed(Key.Escape, ModifierKeys.None);
        };
        return plate;
    }

    private static Border ChromeButton(string glyph, string text, Action onClick, bool active = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ui.Icon(glyph, 12, active ? "Accent" : "TextPrimary"));
        var t = Ui.Text(text, 11.5, FontWeights.SemiBold, wrap: false);
        t.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(t);
        var plate = Plate(row, new Thickness(12, 0, 12, 0), 15, 30);
        plate.Cursor = Cursors.Hand;
        if (active) plate.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        plate.MouseLeftButtonDown += (_, e) => e.Handled = true;
        plate.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onClick);
        };
        return plate;
    }

    private static Border AudioToggle(string glyph, string text, string key, bool defaultValue)
    {
        var on = Settings.GetBool(key, defaultValue);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.Icon(on ? glyph : (glyph == Glyphs.Microphone ? "\uEC54" : Glyphs.Mute), 12, on ? "Accent" : "TextSecondary");
        row.Children.Add(icon);
        var t = Ui.Text(text, 11.5, FontWeights.SemiBold, on ? "TextPrimary" : "TextSecondary", wrap: false);
        t.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(t);
        var b = new Border
        {
            Child = row,
            Padding = new Thickness(12, 0, 12, 0),
            Height = 28,
            CornerRadius = new CornerRadius(12),
            Cursor = Cursors.Hand,
        };
        if (on) b.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        else b.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var value = !Settings.GetBool(key, defaultValue);
            Settings.Set(key, value);
            icon.Text = value ? glyph : (glyph == Glyphs.Microphone ? "\uEC54" : Glyphs.Mute);
            icon.SetResourceReference(TextBlock.ForegroundProperty, value ? "Accent" : "TextSecondary");
            t.SetResourceReference(TextBlock.ForegroundProperty, value ? "TextPrimary" : "TextSecondary");
            b.SetResourceReference(Border.BackgroundProperty, value ? "AccentSubtle" : "ControlBackground");
        };
        return b;
    }
}
