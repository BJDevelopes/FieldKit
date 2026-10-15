// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Wallpaper;

/// <summary>
/// Settings → Utilities → Wallpaper. Upstream's settings section (show on all displays, open
/// Wallpaper settings) plus your pictures, picture position and automatic changes.
/// </summary>
public static class WallpaperPage
{
    private static readonly IReadOnlyList<(string Value, string Label)> FitOptions =
    [
        ("fill", "Fill"),
        ("fit", "Fit"),
        ("stretch", "Stretch"),
        ("tile", "Tile"),
        ("center", "Center"),
        ("span", "Span"),
    ];

    public static FrameworkElement Build()
    {
        var openSettings = Ui.Button(WallpaperText.OpenSystemSettings, WallpaperController.OpenSystemSettings, glyph: Glyphs.Settings);
        openSettings.HorizontalAlignment = HorizontalAlignment.Left;

        var general = Ui.Group(null,
            Ui.ToggleRow(WallpaperText.ApplyAllDisplays,
                "On: every display gets the picture. Off: only the display under the pointer. Windows keeps one " +
                "wallpaper per display, not one per virtual desktop.",
                WallpaperKeys.ApplyAllDisplays, true),
            Ui.ChoiceRow("Picture position", WallpaperKeys.Fit, FitOptions, "fill",
                "Fill crops the picture to cover the screen. Windows uses one position for every display."),
            openSettings);

        var sourcesList = Ui.Stack(8);
        void RenderSources()
        {
            sourcesList.Children.Clear();
            var sources = WallpaperController.SourceInfos.ToList();
            if (sources.Count == 0)
            {
                sourcesList.Children.Add(Ui.Caption(WallpaperText.EmptyOwn));
                return;
            }
            foreach (var source in sources) Ui.Append(sourcesList, 8, SourceRow(source));
        }
        RenderSources();
        Ui.Track(sourcesList, () => Settings.Observe(RenderSources, WallpaperKeys.Sources));

        var addRow = Ui.Row(8,
            Ui.Button(WallpaperText.AddImage, WallpaperController.AddImages, glyph: Glyphs.Photo),
            Ui.Button(WallpaperText.AddFolder, WallpaperController.AddFolder, glyph: Glyphs.Folder));
        var yours = Ui.Group(WallpaperText.FilterOwn,
            Ui.Caption("Pictures and folders you add appear in the panel gallery. Folders include their subfolders; " +
                       "hidden files and linked folders are skipped."),
            sourcesList,
            addRow);

        var nextChange = Ui.Caption(string.Empty);
        Ui.Every(nextChange, TimeSpan.FromSeconds(15), () =>
        {
            var service = FeatureRuntime.Service<WallpaperRotationService>();
            nextChange.Text = service is { IsRunning: true, NextChangeUtc: { } next }
                ? $"Next change at {next.ToLocalTime():t}."
                : "Automatic changes are off.";
        });

        var actions = Ui.Row(8,
            Ui.Button(WallpaperText.PreviousPage, () => _ = WallpaperController.PreviousAsync(), glyph: Glyphs.Previous),
            Ui.Button(WallpaperText.NextPage, () => _ = WallpaperController.NextAsync(), glyph: Glyphs.Next),
            Ui.Button("Shuffle", () => _ = WallpaperController.ShuffleAsync(), glyph: WallpaperSection.ShuffleGlyph));

        var rotation = Ui.Group("Change automatically",
            Ui.ToggleRow("Change picture automatically",
                "Moves to the next picture on a timer while Fieldkit runs. Turning it off keeps the current wallpaper.",
                WallpaperKeys.Rotate, false),
            Ui.ChoiceRow("Change every", WallpaperKeys.RotateInterval,
                RotationSchedule.Intervals.Select(i => (i.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Label)).ToList(),
                RotationSchedule.DefaultMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Ui.ChoiceRow("Pictures to use", WallpaperKeys.RotateSource, WallpaperSection.FilterOptions, "all",
                "Also used by Next and Shuffle."),
            Ui.ToggleRow("Shuffle", "Random order, without repeats until every picture has been shown.",
                WallpaperKeys.Shuffle, true),
            Ui.ToggleRow("Different picture on each display", $"Needs “{WallpaperText.ApplyAllDisplays}”.",
                WallpaperKeys.DifferentPerDisplay, false),
            Ui.Stack(8, actions, nextChange));

        return Ui.Page(WallpaperText.PageTitle, WallpaperText.PanelDescription,
            general,
            yours,
            rotation,
            Ui.Caption("Next, Previous and Shuffle can get global shortcuts in Keyboard shortcuts."));
    }

    private static FrameworkElement SourceRow(WallpaperSourceInfo source)
    {
        var glyph = source.IsReachable ? (source.IsFolder ? Glyphs.Folder : Glyphs.Photo) : Glyphs.Warning;
        var icon = Ui.Icon(glyph, 16, source.IsReachable ? "Accent" : "Orange");
        icon.Width = 24;
        var title = Ui.Text(source.Title, 13.5, FontWeights.Medium, wrap: false);
        var detail = Ui.Text(source.IsReachable ? source.Path : $"{WallpaperText.SourceUnavailable} · {source.Path}",
            12, brush: "TextSecondary", wrap: false);
        var text = Ui.Stack(1, title, detail);
        text.Margin = new Thickness(10, 0, 0, 0);
        var left = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        left.Children.Add(icon);
        left.Children.Add(text);
        return Ui.Split(left, Ui.IconButton(Glyphs.Delete, WallpaperText.RemoveAdded,
            () => WallpaperController.RemoveSource(source)));
    }
}
