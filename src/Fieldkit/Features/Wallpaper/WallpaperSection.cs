// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.UI;

namespace Fieldkit.Features.Wallpaper;

/// <summary>
/// The Wallpaper tab of the panel, laid out like upstream <c>WallpaperSection</c>: filter,
/// "show on all displays", add image / add folder / remove, a 3-column gallery paged by 24,
/// a pager with "Open Wallpaper settings" and the last error. Windows additions: what each
/// display shows now, and previous / shuffle / next in the section header.
/// </summary>
public static class WallpaperSection
{
    public const string ShuffleGlyph = "";
    public const string ChevronLeftGlyph = "";

    private const double ThumbHeight = 54;
    private const double GridSpacing = 8;
    private const int ColumnCount = 3;
    private const int ThumbDecodeWidth = 220; // ~104 px cells at up to 200 % scaling

    public static readonly IReadOnlyList<(string Value, string Label)> FilterOptions =
    [
        ("all", WallpaperText.FilterAll),
        ("own", WallpaperText.FilterOwn),
        ("system", WallpaperText.FilterSystem),
    ];

    /// <summary>Previous / shuffle / next, at the right of the section title.</summary>
    public static FrameworkElement HeaderAccessory() => Ui.Row(0,
        Ui.IconButton(Glyphs.Previous, WallpaperText.PreviousWallpaper, () => _ = WallpaperController.PreviousAsync(), 12),
        Ui.IconButton(ShuffleGlyph, WallpaperText.ShuffleWallpaper, () => _ = WallpaperController.ShuffleAsync(), 12),
        Ui.IconButton(Glyphs.Next, WallpaperText.NextWallpaper, () => _ = WallpaperController.NextAsync(), 12));

    public static FrameworkElement Build()
    {
        var page = 1;
        var removing = false;
        Action render = () => { };

        var filter = Ui.Choice(WallpaperKeys.Filter, FilterOptions, "all", _ =>
        {
            page = 1;
            WallpaperThumbnails.BeginGeneration();
            render();
        });
        filter.MinWidth = 150;
        filter.HorizontalAlignment = HorizontalAlignment.Left;

        var allDisplays = Ui.Split(Ui.Text(WallpaperText.ApplyAllDisplays, 13),
            Ui.Toggle(WallpaperKeys.ApplyAllDisplays, true, small: true));

        var addImage = SmallButton(WallpaperText.AddImage, WallpaperController.AddImages);
        var addFolder = SmallButton(WallpaperText.AddFolder, WallpaperController.AddFolder);
        var removeButton = SmallButton(WallpaperText.RemoveAdded, () =>
        {
            removing = !removing;
            render();
        });
        var buttons = Ui.Row(8, addImage, addFolder, removeButton);

        var nowShowing = new StackPanel { Orientation = Orientation.Horizontal };
        var nowRow = Ui.Split(Ui.Caption("Now showing", 12), nowShowing);

        var chips = new WrapPanel();
        var gallery = new ContentControl();
        var pagerLeft = new StackPanel { Orientation = Orientation.Horizontal };
        var openSettings = Ui.Clickable(Ui.Text(WallpaperText.OpenSystemSettings, 12, brush: "TextSecondary", wrap: false),
            WallpaperController.OpenSystemSettings);
        var pager = Ui.Split(pagerLeft, openSettings, 8);
        pager.Height = 24;
        var status = Ui.Text(string.Empty, 12, brush: "Orange");

        var controls = Ui.Stack(8, filter, allDisplays, buttons);
        var root = Ui.Stack(10, controls, nowRow, chips, gallery, pager, status);

        render = () =>
        {
            var sources = WallpaperController.SourceInfos.ToList();
            if (sources.Count == 0) removing = false;
            addImage.IsEnabled = addFolder.IsEnabled = !removing;
            removeButton.Visibility = sources.Count > 0 || removing ? Visibility.Visible : Visibility.Collapsed;
            removeButton.Content = removing ? WallpaperText.DoneRemoving : WallpaperText.RemoveAdded;

            RenderNowShowing(nowShowing);
            nowRow.Visibility = nowShowing.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var ownIds = new HashSet<string>(
                WallpaperController.Entries.Where(e => e.Source == WallpaperSource.Own).Select(e => e.Id),
                WallpaperSupport.PathComparer);
            var removable = WallpaperSources.RemovableChips(sources, ownIds, WallpaperController.IsLoading);
            chips.Children.Clear();
            foreach (var source in removable) chips.Children.Add(Chip(source));
            chips.Visibility = removing && removable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var items = WallpaperController.VisibleEntries;
            var pageCount = WallpaperSupport.PageCount(items.Count);
            page = WallpaperSupport.ClampedPage(page, items.Count);
            gallery.Content = Gallery(items, page, removing);

            pagerLeft.Children.Clear();
            if (pageCount > 1)
            {
                var current = page;
                var prev = Ui.IconButton(ChevronLeftGlyph, WallpaperText.PreviousPage, () => ChangePage(current - 1), 11);
                prev.IsEnabled = current > 1;
                var next = Ui.IconButton(Glyphs.ChevronRight, WallpaperText.NextPage, () => ChangePage(current + 1), 11);
                next.IsEnabled = current < pageCount;
                var label = Ui.Text($"{current} / {pageCount}", 12, brush: "TextSecondary", wrap: false);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(6, 0, 6, 0);
                pagerLeft.Children.Add(prev);
                pagerLeft.Children.Add(label);
                pagerLeft.Children.Add(next);
            }

            status.Text = WallpaperController.LastError ?? string.Empty;
            status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        };

        void ChangePage(int target)
        {
            page = target;
            WallpaperThumbnails.BeginGeneration();
            render();
        }

        void OnChanged() => render();
        Ui.Track(root, () =>
        {
            WallpaperController.Changed += OnChanged;
            var filterObserver = Core.Settings.Observe(OnChanged, WallpaperKeys.Filter);
            _ = WallpaperController.RefreshAsync(); // your folders can change on disk
            return new ActionDisposable(() =>
            {
                WallpaperController.Changed -= OnChanged;
                filterObserver.Dispose();
                WallpaperThumbnails.BeginGeneration();
            });
        });
        render();
        return root;
    }

    private static Button SmallButton(string text, Action onClick)
    {
        var b = Ui.Button(text, onClick);
        b.Padding = new Thickness(10, 4, 10, 4);
        b.FontSize = 12.5;
        return b;
    }

    private static FrameworkElement Gallery(IReadOnlyList<WallpaperEntry> items, int page, bool removing)
    {
        var rows = WallpaperSupport.GalleryRows(items.Count, ColumnCount);
        var height = rows * ThumbHeight + Math.Max(0, rows - 1) * GridSpacing;
        if (items.Count == 0)
        {
            var message = Ui.Caption(WallpaperController.IsLoading ? "Loading…" : WallpaperText.Empty(WallpaperController.Filter), 12);
            message.HorizontalAlignment = HorizontalAlignment.Center;
            message.VerticalAlignment = VerticalAlignment.Center;
            return new Grid { Height = height, Children = { message } };
        }

        var grid = new UniformGrid { Columns = ColumnCount, VerticalAlignment = VerticalAlignment.Top };
        var slice = WallpaperSupport.PageSlice(items, page);
        for (var i = 0; i < slice.Count; i++)
        {
            var cell = Cell(slice[i], removing);
            cell.Margin = new Thickness(
                i % ColumnCount == 0 ? 0 : GridSpacing / 2,
                i >= ColumnCount ? GridSpacing : 0,
                i % ColumnCount == ColumnCount - 1 ? 0 : GridSpacing / 2,
                0);
            grid.Children.Add(cell);
        }
        return new Grid { Height = height, Children = { grid } };
    }

    private static FrameworkElement Cell(WallpaperEntry entry, bool removing)
    {
        var applied = WallpaperController.IsShowing(entry.Path);
        var thumb = new Border
        {
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            ToolTip = entry.Title,
            Cursor = removing ? Cursors.Arrow : Cursors.Hand,
            Child = Ui.Icon(Glyphs.Photo, 16, "TextSecondary"),
        };
        thumb.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        if (applied) thumb.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else thumb.BorderBrush = Brushes.Transparent;
        AutomationName(thumb, entry.Title);

        WallpaperThumbnails.Load(entry.Path, ThumbDecodeWidth, image =>
        {
            if (image is null) return;
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            thumb.Child = null;
        });
        thumb.MouseEnter += (_, _) => thumb.Opacity = removing ? 1 : 0.85;
        thumb.MouseLeave += (_, _) => thumb.Opacity = 1;
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (removing || WallpaperController.IsApplying) return;
            Ui.Guard(() => WallpaperController.Apply(entry));
        };

        if (!removing || entry.Source != WallpaperSource.Own) return thumb;

        var badge = RemoveBadge(18, () => WallpaperController.RemoveEntry(entry));
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, -4, 2, 0); // stays inside the panel's clip on the right column
        return new Grid { Children = { thumb, badge } };
    }

    private static FrameworkElement Chip(WallpaperSourceInfo source)
    {
        var glyph = source.IsReachable ? (source.IsFolder ? Glyphs.Folder : Glyphs.Photo) : Glyphs.Warning;
        var title = Ui.Text(source.Title, 12, wrap: false);
        title.MaxWidth = 180;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(0, 0, 8, 6),
            ToolTip = source.IsReachable ? source.Path : $"{source.Path} ({WallpaperText.SourceUnavailable})",
            Child = Ui.Row(6, Ui.Icon(glyph, 11, "TextSecondary"), title,
                RemoveBadge(16, () => WallpaperController.RemoveSource(source))),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        return chip;
    }

    private static FrameworkElement RemoveBadge(double size, Action onRemove)
    {
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Cursor = Cursors.Hand,
            ToolTip = WallpaperText.RemoveAdded,
            Child = Ui.Icon(Glyphs.Close, size * 0.45, "TextOnAccent"),
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Red");
        AutomationName(badge, WallpaperText.RemoveAdded);
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Ui.Guard(onRemove);
        };
        return badge;
    }

    /// <summary>A mini thumbnail per display of what it shows now (Windows addition).</summary>
    private static void RenderNowShowing(StackPanel host)
    {
        host.Children.Clear();
        var monitors = WallpaperController.Monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        for (var i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var name = string.IsNullOrEmpty(monitor.Wallpaper) ? "No picture" : WallpaperSupport.Title(monitor.Wallpaper);
            var mini = new Border
            {
                Width = 44,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                ToolTip = $"Display {i + 1}: {name}",
                Child = Ui.Icon(Glyphs.Photo, 11, "TextSecondary"),
            };
            mini.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            if (!string.IsNullOrEmpty(monitor.Wallpaper))
            {
                WallpaperThumbnails.Load(monitor.Wallpaper, 96, image =>
                {
                    if (image is null) return;
                    mini.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    mini.Child = null;
                });
            }
            host.Children.Add(mini);
        }
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
