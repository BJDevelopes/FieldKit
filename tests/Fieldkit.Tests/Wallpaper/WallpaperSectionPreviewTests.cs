// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Wallpaper;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Wallpaper;

public class WallpaperSectionPreviewTests
{
    /// <summary>
    /// Renders the panel section with 30 of "your pictures" (two pages) in remove mode, for a human
    /// to look at (%TEMP%\fieldkit-previews\wallpaper-*.png). Reads the current wallpaper, never sets it.
    /// Only runs against a scratch settings folder (FIELDKIT_DATA_DIR) because it writes settings.
    /// </summary>
    [Fact]
    public void Section_renders_gallery_pager_and_remove_mode()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIELDKIT_DATA_DIR"))) return;
        using var t = new TempTree();
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");
        var stills = Directory.Exists(source)
            ? Directory.GetFiles(source, "*.jpg", SearchOption.AllDirectories).Take(4).ToList()
            : [];
        for (var i = 0; i < 30; i++)
        {
            var target = Path.Combine(t.Root, "pics", $"pic{i:D2}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (stills.Count > 0) File.Copy(stills[i % stills.Count], target);
            else File.WriteAllBytes(target, [1, 2, 3]);
        }
        t.File("single.png");

        var oldSources = Settings.Get<string[]>(WallpaperKeys.Sources, []);
        var oldFilter = Settings.GetString(WallpaperKeys.Filter, "all");
        Settings.Set(WallpaperKeys.Sources, new[] { Path.Combine(t.Root, "pics"), Path.Combine(t.Root, "single.png"), Path.Combine(t.Root, "gone") });
        Settings.Set(WallpaperKeys.Filter, "own");
        try
        {
            StaRunner.Run(() =>
            {
                var refresh = WallpaperController.RefreshAsync();
                Pump(() => refresh.IsCompleted, TimeSpan.FromSeconds(20));
                Assert.Equal(31, WallpaperController.VisibleEntries.Count);

                var section = WallpaperSection.Build();
                Pump(() => false, TimeSpan.FromMilliseconds(1500)); // thumbnails decode off-thread
                PreviewRenderer.Render(section, StaRunner.PreviewPath("wallpaper-panel.png"), 336, 900);
                ((Border)section.Parent).Child = null;

                var remove = FindAll<Button>(section).First(b => Equals(b.Content, WallpaperText.RemoveAdded));
                remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(WallpaperText.DoneRemoving, remove.Content);
                Pump(() => false, TimeSpan.FromMilliseconds(800));
                PreviewRenderer.Render(section, StaRunner.PreviewPath("wallpaper-panel-removing.png"), 336, 900, dark: false);
                WallpaperController.Reset();
            });
        }
        finally
        {
            Settings.Set(WallpaperKeys.Sources, oldSources);
            Settings.Set(WallpaperKeys.Filter, oldFilter);
            Settings.Flush();
        }
    }

    private static void Pump(Func<bool> done, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }
}
