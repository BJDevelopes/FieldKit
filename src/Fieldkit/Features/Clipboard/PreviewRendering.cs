// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Renders tool windows that <c>--render-ui</c> cannot reach (history, shelf, snippet library, …) to
/// PNG with sample data, from an STA unit test. Loads the theme into the element itself, so it works
/// without the app's <see cref="Application"/>.
/// </summary>
public static class PreviewRendering
{
    /// <summary>Merges the palette (dark unless <paramref name="light"/>) and control styles into <paramref name="element"/>.</summary>
    public static void ApplyTheme(FrameworkElement element, bool? light = null)
    {
        if (Application.Current?.Resources.MergedDictionaries.Count > 0 && light is null) return;
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // registers pack:// outside an Application
        var palette = light == true ? "Light" : "Dark";
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Fieldkit;component/UI/Themes/{palette}.xaml"),
        });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Fieldkit;component/UI/Themes/Controls.xaml"),
        });
    }

    /// <summary>Lets layout and bindings settle, then saves the window's content as PNG.</summary>
    public static void Save(Window window, string path)
    {
        Pump(TimeSpan.FromMilliseconds(300));
        var element = (FrameworkElement)window.Content;
        element.UpdateLayout();
        // The card sits inside its shadow margin; RenderTargetBitmap includes that offset.
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth + element.Margin.Left + element.Margin.Right));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight + element.Margin.Top + element.Margin.Bottom));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(90, 90, 96)), null, new Rect(0, 0, w, h));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
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
}
