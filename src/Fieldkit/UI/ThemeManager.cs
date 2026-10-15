// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>Applies the light or dark palette ("general.appearance": system | dark | light).</summary>
public static class ThemeManager
{
    public const string AppearanceKey = "general.appearance";
    private static ResourceDictionary? _palette;

    public static bool IsDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        Apply();
        Settings.Observe(Apply, AppearanceKey);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }

    public static void Apply()
    {
        var mode = Settings.GetString(AppearanceKey, "system");
        var dark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemUsesDarkApps(),
        };
        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? "pack://application:,,,/UI/Themes/Dark.xaml" : "pack://application:,,,/UI/Themes/Light.xaml"),
        };
        // Remove every palette (including the one App.xaml declares for the designer):
        // later merged dictionaries win lookups, so only one may be present.
        foreach (var existing in dictionaries.Where(d =>
                     d.Source?.OriginalString.Contains("Themes/Dark", StringComparison.OrdinalIgnoreCase) == true ||
                     d.Source?.OriginalString.Contains("Themes/Light", StringComparison.OrdinalIgnoreCase) == true).ToList())
            dictionaries.Remove(existing);
        dictionaries.Insert(0, palette);
        _palette = palette;
        var changed = IsDark != dark;
        IsDark = dark;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        if (changed) ThemeChanged?.Invoke();
    }

    /// <summary>Dark/light native title bar and rounded corners for a standard window.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Magenta;

    public static Color Color(string key) => Brush(key) is SolidColorBrush s ? s.Color : Colors.Magenta;
}
