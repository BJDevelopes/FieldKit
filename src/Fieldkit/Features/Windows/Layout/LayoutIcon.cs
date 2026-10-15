// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// A tiny screen with the area an action fills, drawn from the real geometry (the counterpart
/// of upstream's rectangle.*half.inset.filled symbols). Display moves and Restore use glyphs.
/// </summary>
internal static class LayoutIcon
{
    // Segoe Fluent Icons: Forward, Back, Undo, FullScreen (checked with --render-glyph-range).
    private const string GlyphNextDisplay = "\uE72A";
    private const string GlyphPreviousDisplay = "\uE72B";
    private const string GlyphRestore = "\uE7A7";
    private const string GlyphFullScreen = "\uE740";

    public static FrameworkElement For(LayoutAction action, double width = 18, string brush = "Accent")
    {
        var glyph = action switch
        {
            LayoutAction.NextDisplay => GlyphNextDisplay,
            LayoutAction.PreviousDisplay => GlyphPreviousDisplay,
            LayoutAction.Restore => GlyphRestore,
            LayoutAction.FullScreen => GlyphFullScreen,
            _ => null,
        };
        if (glyph is not null)
        {
            var icon = Ui.Icon(glyph, width * 0.8, brush);
            icon.Width = width;
            return icon;
        }

        var height = Math.Round(width * 0.68);
        var screen = new Rect(0, 0, 160, 100);
        var current = new Rect(40, 25, 80, 50);
        var r = LayoutGeometry.RectFor(action, current, screen, marginPercent: 10);
        var canvas = new Canvas { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
        var outline = new Rectangle { Width = width, Height = height, RadiusX = 2, RadiusY = 2, StrokeThickness = 1.2 };
        outline.SetResourceReference(Shape.StrokeProperty, brush);
        canvas.Children.Add(outline);
        const double inset = 2.2;
        var sx = (width - 2 * inset) / screen.Width;
        var sy = (height - 2 * inset) / screen.Height;
        var fill = new Rectangle
        {
            Width = Math.Max(1.5, r.Width * sx - 0.6),
            Height = Math.Max(1.5, r.Height * sy - 0.6),
            RadiusX = 1,
            RadiusY = 1,
        };
        fill.SetResourceReference(Shape.FillProperty, brush);
        Canvas.SetLeft(fill, inset + r.X * sx + 0.3);
        Canvas.SetTop(fill, inset + r.Y * sy + 0.3);
        canvas.Children.Add(fill);
        return canvas;
    }
}
