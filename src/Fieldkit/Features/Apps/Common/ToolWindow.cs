// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Common;

/// <summary>
/// A themed, normal (title-barred) tool window shared by the app-management tools
/// (Packages, Uninstaller, Cleaner, App updates). One instance per key: opening an
/// already open tool brings it forward instead of creating a second copy.
/// </summary>
public class ToolWindow : Window
{
    private static readonly Dictionary<string, ToolWindow> Open = new(StringComparer.Ordinal);

    public ToolWindow(string title, double width = 720, double height = 620)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowFit.ToDisplay(this);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Shows the tool registered under <paramref name="key"/>, creating it with
    /// <paramref name="factory"/> if it is not open yet.</summary>
    public static T ShowSingle<T>(string key, Func<T> factory) where T : ToolWindow
    {
        if (Open.TryGetValue(key, out var existing) && existing.IsLoaded)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return (T)existing;
        }
        var window = factory();
        Open[key] = window;
        window.Closed += (_, _) => Open.Remove(key);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Closes the tool registered under <paramref name="key"/> (when its feature is switched off).</summary>
    public static void CloseSingle(string key)
    {
        if (Open.TryGetValue(key, out var w)) w.Close();
    }
}

/// <summary>
/// Renders any element offscreen to a PNG with the app theme applied on the host element, so it
/// works from unit tests without an <see cref="Application"/> (call on an STA thread). Elements
/// must take colours through SetResourceReference (not ThemeManager.Brush).
/// </summary>
public static class PreviewRenderer
{
    public static void Render(FrameworkElement element, string path, double width, double height, bool dark = true)
    {
        var host = new Border { Width = width, Height = height, Child = element, Padding = new Thickness(0) };
        host.Resources.MergedDictionaries.Add(LoadTheme(dark ? "Dark" : "Light"));
        host.Resources.MergedDictionaries.Add(LoadTheme("Controls"));
        host.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        TextElement.SetFontFamily(host, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        TextElement.SetFontSize(host, 13);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ResourceDictionary LoadTheme(string name) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/Fieldkit;component/UI/Themes/{name}.xaml", UriKind.Relative));
}
