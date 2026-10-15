// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>Settings → Files → Shelf (upstream ShelfSettings).</summary>
public static class ShelfPage
{
    private const string ExclusionsKey = "shelf.automaticExclusions";

    public static FrameworkElement Build()
    {
        var intro = Ui.Group(null,
            Ui.Stack(6,
                Ui.ToggleRow("Temporary area for dragging files",
                    "A floating spot to gather files, images and text, then drag them anywhere later.",
                    "shelf.enabled", false),
                Ui.Row(6, Ui.Icon(Glyphs.Shield, 12, "TextSecondary"), Ui.Caption("Requires no permissions."))));

        var how = Ui.Group("How to use",
            Ui.Stack(10,
                Step("1", "Open it with the shortcut, or by shaking the mouse during a drag."),
                Step("2", "Drop files, images, links or text onto it to hold them."),
                Step("3", "Drag each item back out to any app when you need it.")));

        var explorerSelection = Ui.ToggleRow("Add the File Explorer selection with the shortcut",
            "With File Explorer in front, the shortcut opens the shelf with the selected files already in it. With nothing selected, it opens as usual.",
            "shelf.shortcutAddsExplorerSelection", false);
        FollowSetting(explorerSelection, "shelf.shortcutEnabled", true);

        var openNow = Ui.Button("Open now", OpenNow, glyph: Glyphs.Download);
        openNow.HorizontalAlignment = HorizontalAlignment.Left;

        var opening = Ui.Group(null,
            Ui.ToggleRow("Shelf shortcut", null, "shelf.shortcutEnabled", true),
            Ui.ShortcutRow("Shortcut", "shelf.shortcut", ShelfService.ShortcutDefault),
            explorerSelection,
            Ui.ToggleRow("Open by shaking the mouse while dragging",
                "Shake the pointer quickly while holding an item to summon it near the cursor. Moving or resizing a window never counts.",
                "shelf.shakeToOpen", true),
            openNow);

        var afterUse = Ui.Group("After use",
            Ui.ToggleRow("Close after dropping into another app",
                "Closes the shelf when the destination accepts the items. The pin in the panel keeps it open.",
                "shelf.closeAfterDrop", false),
            Ui.ToggleRow("Remove items after dropping",
                "Items accepted by another app leave the shelf. Turn this off to keep a copy there. Pinned items always stay.",
                "shelf.removeAfterDrop", true),
            Ui.ToggleRow("Clear when closed",
                "Empties the shelf only when you click its close button. Automatic hiding keeps the items.",
                "shelf.clearOnClose", false));

        var gated = Ui.Stack(20, opening, afterUse, Exceptions());
        FollowSetting(gated, "shelf.enabled", false);

        return Ui.Page("Shelf", null, intro, how, gated);
    }

    private static void OpenNow()
    {
        if (FeatureRuntime.Service<ShelfService>() is { IsRunning: true } service) service.Summon();
        else Notifier.Show("Shelf", "Install the shelf in the Features page first.");
    }

    /// <summary>Shows <paramref name="element"/> only while a boolean setting is on.</summary>
    private static void FollowSetting(FrameworkElement element, string key, bool defaultValue)
    {
        void Sync() => element.Visibility = Settings.GetBool(key, defaultValue) ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(element, () => Settings.Observe(Sync, key));
    }

    private static FrameworkElement Step(string number, string text)
    {
        var digit = Ui.Text(number, 11, FontWeights.Bold, "TextOnAccent", wrap: false);
        digit.HorizontalAlignment = HorizontalAlignment.Center;
        digit.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Child = digit,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        var label = Ui.Text(text, 13);
        label.Margin = new Thickness(10, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(badge);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }

    private static List<string> Exclusions() =>
        ShelfRules.NormalizeExclusions(Settings.Get<List<string>>(ExclusionsKey, []));

    private static FrameworkElement Exceptions()
    {
        var list = Ui.Stack(8);
        void Rebuild()
        {
            list.Children.Clear();
            var names = Exclusions();
            if (names.Count == 0)
            {
                list.Children.Add(Ui.Caption("No apps added.", 13));
                return;
            }
            foreach (var name in names)
            {
                var row = Ui.Split(
                    Ui.Row(9, Ui.IconTile(Glyphs.Grid, 22), Ui.Text(name, 13, wrap: false)),
                    Ui.IconButton(Glyphs.Remove, "Remove", () => Settings.Set(ExclusionsKey,
                        Exclusions().Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList())));
                Ui.Append(list, 8, row);
            }
        }
        Rebuild();
        Ui.Track(list, () => Settings.Observe(Rebuild, ExclusionsKey));

        var add = Ui.Button("Add app…", AddApp, glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Group("Automatic exceptions",
            list,
            Ui.Stack(8, add, Ui.Caption("Shaking stays off for drags started in these apps. The shortcut and Open now still work.")));
    }

    private static void AddApp()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog() != true) return;
        var names = Exclusions();
        names.Add(System.IO.Path.GetFileName(dialog.FileName));
        Settings.Set(ExclusionsKey, ShelfRules.NormalizeExclusions(names));
    }
}
