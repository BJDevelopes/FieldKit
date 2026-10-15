// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Saves windows and elements as PNG for review without user interaction: the developer
/// RenderPreview hooks of the selector, editor, recorder controls and camera mirror use it.
/// </summary>
public static class PreviewRendering
{
    public static void SaveWindow(Window window, string path)
    {
        Pump(TimeSpan.FromMilliseconds(250));
        if (window.Content is FrameworkElement content) SaveElement(content, path);
    }

    public static void SaveElement(FrameworkElement element, string path, Color? background = null)
    {
        element.UpdateLayout();
        if (element.ActualWidth < 1 || element.ActualHeight < 1)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
            element.UpdateLayout();
        }
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(background ?? Color.FromRgb(40, 40, 40)), null, new Rect(0, 0, w, h));
        rtb.Render(bg);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A synthetic "screen" for previews and tests: a window with text lines and colour blocks.</summary>
    public static RasterImage SampleScreen(int width, int height)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(40, 70, 120), Color.FromRgb(120, 60, 110), 30), null, new Rect(0, 0, width, height));
            var win = new Rect(width * 0.08, height * 0.1, width * 0.6, height * 0.7);
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), win, 8, 8);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 235, 240)), null, new Rect(win.X, win.Y, win.Width, 28));
            var face = new Typeface("Segoe UI");
            for (var i = 0; i < 8; i++)
            {
                var ft = new FormattedText($"Line {i + 1}: The quick brown fox jumps over the lazy dog", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 14, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(win.X + 16, win.Y + 44 + i * 26));
            }
            dc.DrawRectangle(Brushes.OrangeRed, null, new Rect(width * 0.72, height * 0.15, width * 0.2, height * 0.2));
            dc.DrawRectangle(Brushes.SeaGreen, null, new Rect(width * 0.72, height * 0.45, width * 0.2, height * 0.2));
        }
        return RasterImage.FromVisual(dv, width, height);
    }
}
