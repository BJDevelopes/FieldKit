// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>
/// Renders the tool windows that --render-ui cannot reach (Quick panel, Scratchpad, Cleaning
/// Mode, brightness OSD, port rows) with sample data, for visual review. Call on an STA thread
/// with the theme dictionaries loaded.
/// </summary>
public static class ToolPreviews
{
    public static IReadOnlyList<string> RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        void Save(string name, FrameworkElement element, double width)
        {
            var path = Path.Combine(directory, name);
            RenderPreview(element, width, path);
            written.Add(path);
        }

        var tiles = new List<QuickPanelItem>
        {
            new("action:keepAwake.toggle", "keepAwake", "Keep awake", Glyphs.Moon, null, () => true),
            new("toggles", "quickToggles", "Quick toggles", Glyphs.Toggles, null),
            new("action:screenOCR", "screenOCR", "Copy text from screen", Glyphs.TextScan, null),
            new("action:colorPicker", "colorPicker", "Color picker", Glyphs.Eyedropper, null),
            new("action:clipboard", "clipboardHistory", "Clipboard", Glyphs.Clipboard, null),
            new("action:windowLayout", "windowLayout", "Window layout", Glyphs.Layout, null),
            new("action:cleaningMode.start", "cleaningMode", "Cleaning Mode", Glyphs.Keyboard, null),
            new("action:scratchpad.open", "scratchpad", "Scratchpad", Glyphs.Note, null),
        };
        var hidden = new List<QuickPanelItem>
        {
            new("action:media", "mediaTools", "Media", Glyphs.Photo, null),
            new("action:portManager.open", "portManager", "Port Manager", ProcessesUi.NetworkGlyph, null),
        };
        Save("tool-quickpanel.png", QuickPanelWindow.BuildPreview(tiles, [], editing: false, "Ctrl+Alt+Win+P"), 440);
        Save("tool-quickpanel-edit.png", QuickPanelWindow.BuildPreview(tiles, hidden, editing: true, "Ctrl+Alt+Win+P"), 440);
        Save("tool-scratchpad.png", ScratchpadWindow.BuildPreviewForTests(
            "# Groceries\n\n- **Coffee** beans\n- Milk\n\n1. Call *Sam*\n2. Book ~~flights~~ trains\n\n> Remember `Ctrl+T` opens a tab\n\nSee [the docs](https://example.com)."), 380);
        Save("tool-cleaning-blackout.png", CleaningMode.BuildPreview(compact: false, progress: 2), 900);
        Save("tool-cleaning-hud.png", CleaningMode.BuildPreview(compact: true, progress: 3), 420);
        Save("tool-brightness-osd.png", BrightnessOsd.BuildPreview(0.62), 196);
        return written;
    }

    /// <summary>Lays out <paramref name="element"/> at <paramref name="width"/> and writes it as a PNG.</summary>
    public static void RenderPreview(FrameworkElement element, double width, string path)
    {
        var host = new Border { Child = element, Padding = new Thickness(0) };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        var height = double.IsNaN(element.Height) ? double.PositiveInfinity : element.Height;
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(new Point(0, 0), new Size(width, host.DesiredSize.Height)));
        host.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(host.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(host.ActualHeight));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Log.Info($"Rendered preview {path}");
    }

}
