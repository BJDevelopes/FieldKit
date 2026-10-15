// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>Settings → Command Bar (port of upstream CommandBarSettings): every option that maps to Windows.</summary>
public static class CommandBarSettingsPage
{
    private static CommandBarService? Service => FeatureRuntime.Service<CommandBarService>();

    public static FrameworkElement Build() =>
        Ui.Page(CommandBarStrings.PageTitle, CommandBarStrings.SettingsCaption,
            MainGroup(),
            AppCenterGroup(),
            SourcesGroup(),
            FilesGroup(),
            LinksGroup(),
            KeyedListGroup(CommandBarStrings.RowShortcutsTitle, CommandBarStrings.RowShortcutsEmpty, CommandBarKeys.RowShortcuts, RowShortcutRows),
            KeyedListGroup(CommandBarStrings.NamedTitle, CommandBarStrings.NamedEmpty, CommandBarKeys.Aliases, AliasRows),
            KeyedListGroup(CommandBarStrings.PinnedTitle, CommandBarStrings.PinnedEmpty, CommandBarKeys.Pins, PinRows),
            HiddenGroup());

    /// <summary>A container that rebuilds itself whenever one of <paramref name="keys"/> changes.</summary>
    private static FrameworkElement Live(Func<FrameworkElement> build, params string[] keys)
    {
        var host = new ContentControl { Focusable = false, Content = build() };
        Ui.Track(host, () => Settings.Observe(() => host.Content = build(), keys));
        return host;
    }

    // ------------------------------------------------------------------ main

    private static FrameworkElement MainGroup()
    {
        var open = Ui.Button(CommandBarStrings.OpenButton, () => Service?.Show(), accent: true);
        var recenter = Ui.Button(CommandBarStrings.ResetPositionButton, CommandBarService.ResetPosition);
        void SyncRecenter() => recenter.IsEnabled = Settings.GetString(CommandBarKeys.Position, string.Empty).Length > 0;
        SyncRecenter();
        Ui.Track(recenter, () => Settings.Observe(SyncRecenter, CommandBarKeys.Position));

        var lockIcon = Ui.Icon(Glyphs.Lock, 12, "TextSecondary");
        lockIcon.VerticalAlignment = VerticalAlignment.Top;
        lockIcon.Margin = new Thickness(0, 2, 6, 0);
        var privacy = new DockPanel();
        DockPanel.SetDock(lockIcon, Dock.Left);
        privacy.Children.Add(lockIcon);
        privacy.Children.Add(Ui.Caption(CommandBarStrings.PrivacyNote, 12));

        var tryRow = new WrapPanel();
        var tryLabel = Ui.Text(CommandBarStrings.TryTheseLabel, 12, brush: "TextTertiary", wrap: false);
        tryLabel.Margin = new Thickness(0, 0, 6, 0);
        tryRow.Children.Add(tryLabel);
        var examples = new List<string> { "100 km to mi", "2+2*3", "volume 40", ":fire" };
        if (CommandBarSystem.ReadBattery() is not null) examples.Add("battery");
        foreach (var example in examples)
        {
            var chip = Ui.Badge(example);
            chip.Margin = new Thickness(0, 0, 5, 0);
            tryRow.Children.Add(chip);
        }
        var explanation = Ui.Stack(6, Ui.Caption(CommandBarStrings.PositionCaption, 12), privacy, tryRow);

        var skinTone = Live(() => CommandBarPreferences.IsEnabled(CommandBarSource.Emoji,
                CommandBarPreferences.DisabledSources(Settings.GetString(CommandBarKeys.DisabledSources, string.Empty)))
                ? Ui.Stack(4, Ui.Split(Ui.Text(CommandBarStrings.EmojiSkinToneLabel, 13.5, FontWeights.Medium), SkinTonePicker()),
                    Ui.Caption(CommandBarStrings.EmojiSkinToneCaption))
                : new Border(),
            CommandBarKeys.DisabledSources);

        var shortcut = Live(ShortcutRows, CommandBarKeys.Shortcut);

        return Ui.Group(null,
            Ui.Row(10, open, recenter),
            explanation,
            Ui.ToggleRow(CommandBarStrings.CompactModeToggle, CommandBarStrings.CompactModeCaption, CommandBarKeys.CompactMode, false),
            skinTone,
            shortcut);
    }

    private static FrameworkElement ShortcutRows()
    {
        var toggle = new CheckBox { IsChecked = CommandBarKeys.ShortcutEnabled };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        toggle.Click += (_, _) => CommandBarKeys.SetShortcutEnabled(toggle.IsChecked == true);
        var head = Ui.Split(Ui.Text(CommandBarStrings.ShortcutToggle, 13.5, FontWeights.Medium), toggle);
        if (!CommandBarKeys.ShortcutEnabled) return head;
        return Ui.Stack(10, head, Ui.ShortcutRow(CommandBarStrings.PageTitle, CommandBarKeys.Shortcut, CommandBarKeys.DefaultShortcut,
            "Defaults to Ctrl+Alt+Space: Alt+Space (upstream's choice) opens the window menu on Windows."));
    }

    private static FrameworkElement SkinTonePicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var current = CommandBarEmoji.ParseTone(Settings.GetString(CommandBarKeys.EmojiSkinTone, string.Empty));
        foreach (var tone in Enum.GetValues<CommandBarEmoji.SkinTone>())
        {
            // WPF draws emoji without colour, so each tone is shown as its colour.
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new System.Windows.Media.SolidColorBrush(ToneColor(tone)),
                StrokeThickness = 1,
            };
            swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CardBorder");
            var button = new RadioButton
            {
                GroupName = "commandBarSkinTone",
                IsChecked = tone == current,
                Content = swatch,
                ToolTip = ToneName(tone),
                Margin = new Thickness(0, 0, 2, 0),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            var value = tone;
            button.Checked += (_, _) => Settings.Set(CommandBarKeys.EmojiSkinTone, CommandBarEmoji.StorageValue(value));
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>The colour of a Fitzpatrick tone (the default is emoji yellow).</summary>
    public static System.Windows.Media.Color ToneColor(CommandBarEmoji.SkinTone tone) => tone switch
    {
        CommandBarEmoji.SkinTone.Light => System.Windows.Media.Color.FromRgb(0xF7, 0xDE, 0xCE),
        CommandBarEmoji.SkinTone.MediumLight => System.Windows.Media.Color.FromRgb(0xF3, 0xD2, 0xA2),
        CommandBarEmoji.SkinTone.Medium => System.Windows.Media.Color.FromRgb(0xD5, 0xAB, 0x88),
        CommandBarEmoji.SkinTone.MediumDark => System.Windows.Media.Color.FromRgb(0xAF, 0x7E, 0x57),
        CommandBarEmoji.SkinTone.Dark => System.Windows.Media.Color.FromRgb(0x7C, 0x53, 0x3E),
        _ => System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x3D),
    };

    public static string ToneName(CommandBarEmoji.SkinTone tone) => tone switch
    {
        CommandBarEmoji.SkinTone.Light => "Light skin tone",
        CommandBarEmoji.SkinTone.MediumLight => "Medium-light skin tone",
        CommandBarEmoji.SkinTone.Medium => "Medium skin tone",
        CommandBarEmoji.SkinTone.MediumDark => "Medium-dark skin tone",
        CommandBarEmoji.SkinTone.Dark => "Dark skin tone",
        _ => "Default",
    };

    // ------------------------------------------------------------------ app shortcuts

    private static FrameworkElement AppCenterGroup()
    {
        var button = Ui.Button(CommandBarStrings.AppCenterTitle, CommandBarAppShortcutsWindow.ShowWindow, glyph: CommandBarGlyphs.Apps);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Group(null, Ui.Stack(6, button, Ui.Caption(CommandBarStrings.AppCenterCaption)));
    }

    // ------------------------------------------------------------------ sources

    private static FrameworkElement SourcesGroup()
    {
        var rows = new List<UIElement>();
        foreach (var source in CommandBarPreferences.AllSources)
        {
            var toggle = new CheckBox();
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
            var s = source;
            void Sync() => toggle.IsChecked = CommandBarPreferences.IsEnabled(s,
                CommandBarPreferences.DisabledSources(Settings.GetString(CommandBarKeys.DisabledSources, string.Empty)));
            Sync();
            toggle.IsEnabled = !CommandBarPreferences.IsAlwaysOn(source);
            toggle.Click += (_, _) =>
            {
                var disabled = CommandBarPreferences.DisabledSources(Settings.GetString(CommandBarKeys.DisabledSources, string.Empty));
                if (toggle.IsChecked == true) disabled.Remove(s);
                else disabled.Add(s);
                Settings.Set(CommandBarKeys.DisabledSources, CommandBarPreferences.StorageValue(disabled));
            };
            Ui.Track(toggle, () => Settings.Observe(Sync, CommandBarKeys.DisabledSources));
            var icon = Ui.Icon(CommandBarPreferences.Glyph(source), 14);
            icon.Width = 22;
            rows.Add(Ui.Split(Ui.Row(8, icon, Ui.Text(CommandBarPreferences.Title(source), 13.5, wrap: false)), toggle));
        }
        rows.Add(Ui.Caption(CommandBarStrings.SourcesCaption, 12));
        return Ui.Group(CommandBarStrings.SourcesTitle, rows.ToArray());
    }

    // ------------------------------------------------------------------ files

    private static FrameworkElement FilesGroup() => Live(() =>
    {
        var scopes = CommandBarFileSearchSupport.DecodeList(Settings.GetString(CommandBarKeys.FileScopes, string.Empty));
        var ignores = CommandBarFileSearchSupport.DecodeList(Settings.GetString(CommandBarKeys.FileIgnores, string.Empty));
        var rows = new List<UIElement> { Ui.Caption(CommandBarStrings.FilesCaption, 12) };
        if (scopes.Count == 0) rows.Add(Ui.Caption(CommandBarStrings.FilesEmpty, 12));
        foreach (var scope in scopes)
        {
            var value = scope;
            rows.Add(ListRow(Glyphs.Folder, scope, null, () =>
                Settings.Set(CommandBarKeys.FileScopes, CommandBarFileSearchSupport.EncodeList(scopes.Where(s => s != value)))));
        }
        var add = Ui.Button(CommandBarStrings.FilesAddFolder, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
            if (dialog.ShowDialog() != true) return;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Settings.Set(CommandBarKeys.FileScopes, CommandBarFileSearchSupport.EncodeList(
                scopes.Concat(dialog.FolderNames.Select(f => CommandBarFileSearchSupport.WithTilde(f, home)))));
        }, glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(add);

        var chevron = Ui.Icon(ignores.Count > 0 ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        var moreHeader = Ui.Row(8, chevron, Ui.Text(CommandBarStrings.MoreOptions, 13, FontWeights.Medium));
        moreHeader.Background = System.Windows.Media.Brushes.Transparent;
        moreHeader.Cursor = Cursors.Hand;
        var options = Ui.Stack(8, Ui.Caption(CommandBarStrings.FilesIgnoreCaption, 12));
        foreach (var pattern in ignores)
        {
            var value = pattern;
            Ui.Append(options, 8, ListRow(Glyphs.Eye, pattern, null, () =>
                Settings.Set(CommandBarKeys.FileIgnores, CommandBarFileSearchSupport.EncodeList(ignores.Where(p => p != value)))));
        }
        var draft = new TextBox { ToolTip = CommandBarStrings.FilesIgnorePlaceholder, MinWidth = 220 };
        void AddIgnore()
        {
            var pattern = draft.Text.Trim();
            if (pattern.Length == 0) return;
            Settings.Set(CommandBarKeys.FileIgnores, CommandBarFileSearchSupport.EncodeList(ignores.Append(pattern)));
        }
        draft.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddIgnore();
        };
        Ui.Append(options, 8, Ui.Row(8, draft, Ui.Button(CommandBarStrings.FilesIgnoreAdd, AddIgnore)));
        options.Margin = new Thickness(18, 8, 0, 0);
        options.Visibility = ignores.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        moreHeader.MouseLeftButtonUp += (_, _) =>
        {
            var open = options.Visibility != Visibility.Visible;
            options.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        };
        rows.Add(Ui.Stack(0, moreHeader, options));
        return Ui.Group(CommandBarStrings.FilesTitle, rows.ToArray());
    }, CommandBarKeys.FileScopes, CommandBarKeys.FileIgnores);

    // ------------------------------------------------------------------ links

    private static FrameworkElement LinksGroup() => Live(() =>
    {
        var links = CommandBarLinks.Decode(Settings.GetString(CommandBarKeys.Links, string.Empty));
        var rows = new List<UIElement>();
        if (links.Count == 0) rows.Add(Ui.Caption(CommandBarStrings.LinksEmpty, 12));
        foreach (var link in links)
        {
            var glyph = link.Kind switch
            {
                CommandBarLinkKind.Place => Glyphs.Folder,
                CommandBarLinkKind.Script => CommandBarGlyphs.Script,
                _ => Glyphs.Link,
            };
            var current = link;
            var name = Ui.Text(link.Name, 13, FontWeights.SemiBold, wrap: false);
            var destination = Ui.Text(link.Destination, 12, brush: "TextSecondary", wrap: false);
            destination.TextTrimming = TextTrimming.CharacterEllipsis;
            destination.MaxWidth = 380;
            var icon = Ui.Icon(glyph, 13);
            icon.Width = 20;
            var left = Ui.Row(8, icon, name, destination);
            var buttons = Ui.Row(6,
                Ui.Button(CommandBarStrings.EditButton, () => CommandBarLinkEditor.Edit(current.Clone(), SaveLink)),
                Ui.Button(CommandBarStrings.RemoveButton, () => SaveAll(links.Where(l => l.Id != current.Id).ToList())));
            rows.Add(Ui.Split(left, buttons));
        }
        var add = Ui.Button(CommandBarStrings.LinkAddButton, () => CommandBarLinkEditor.Edit(new CommandBarLink(), SaveLink), glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(add);
        return Ui.Group(CommandBarStrings.LinksTitle, rows.ToArray());

        static void SaveLink(CommandBarLink saved)
        {
            var next = CommandBarLinks.Decode(Settings.GetString(CommandBarKeys.Links, string.Empty));
            var index = next.FindIndex(l => l.Id == saved.Id);
            if (index >= 0) next[index] = saved;
            else next.Add(saved);
            SaveAll(next);
        }

        static void SaveAll(List<CommandBarLink> next) => Settings.Set(CommandBarKeys.Links, CommandBarLinks.Encode(next));
    }, CommandBarKeys.Links);

    // ------------------------------------------------------------------ lists keyed by row

    private static FrameworkElement KeyedListGroup(string title, string empty, string key, Func<List<UIElement>> rows) => Live(() =>
    {
        var items = rows();
        if (items.Count == 0) items.Add(Ui.Caption(empty, 12));
        return Ui.Group(title, items.ToArray());
    }, key);

    private static List<UIElement> RowShortcutRows()
    {
        var shortcuts = CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty));
        var refused = Service?.RefusedRowShortcutKeys ?? [];
        return shortcuts.Select(p => (Key: p.Key, Title: Title(p.Key), Shortcut: p.Value))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(e =>
            {
                var key = e.Key;
                var left = Ui.Row(10, Ui.Badge(e.Shortcut.ToString(), "AccentSubtle", "Accent"), Ui.Text(e.Title, 13, brush: "TextSecondary", wrap: false));
                var right = Ui.Row(8,
                    refused.Contains(key) ? Ui.Text(CommandBarStrings.ShortcutUnavailable, 12, brush: "Orange", wrap: false) : null,
                    Ui.Button(CommandBarStrings.RemoveButton, () =>
                    {
                        var next = CommandBarRowShortcuts.Setting(null, key, CommandBarRowShortcuts.Decode(Settings.GetString(CommandBarKeys.RowShortcuts, string.Empty)));
                        Settings.Set(CommandBarKeys.RowShortcuts, CommandBarRowShortcuts.Encode(next));
                    }));
                return (UIElement)Ui.Split(left, right);
            }).ToList();
    }

    private static List<UIElement> AliasRows()
    {
        var aliases = CommandBarPreferences.DecodeAliases(Settings.GetString(CommandBarKeys.Aliases, string.Empty));
        return aliases.OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase).Select(p =>
        {
            var key = p.Key;
            var left = Ui.Row(10, Ui.Text(p.Value, 13, FontWeights.SemiBold, "Accent", wrap: false), Ui.Text(Title(key), 13, brush: "TextSecondary", wrap: false));
            return (UIElement)Ui.Split(left, Ui.Button(CommandBarStrings.RemoveButton, () =>
            {
                var next = CommandBarPreferences.DecodeAliases(Settings.GetString(CommandBarKeys.Aliases, string.Empty));
                next.Remove(key);
                Settings.Set(CommandBarKeys.Aliases, CommandBarPreferences.EncodeAliases(next));
            }));
        }).ToList();
    }

    private static List<UIElement> PinRows()
    {
        var pins = CommandBarPreferences.DecodePins(Settings.GetString(CommandBarKeys.Pins, string.Empty));
        return pins.Select(key => (UIElement)Ui.Split(Ui.Text(Title(key), 13, wrap: false), Ui.Button(CommandBarStrings.RemoveButton, () =>
            Settings.Set(CommandBarKeys.Pins, CommandBarPreferences.EncodePins(
                CommandBarPreferences.DecodePins(Settings.GetString(CommandBarKeys.Pins, string.Empty)).Where(k => k != key)))))).ToList();
    }

    private static FrameworkElement HiddenGroup() => Live(() =>
    {
        var hidden = CommandBarPreferences.DecodeHidden(Settings.GetString(CommandBarKeys.Hidden, string.Empty));
        var rows = hidden.OrderBy(k => k, StringComparer.Ordinal).Select(key => (UIElement)Ui.Split(Ui.Text(Title(key), 13, wrap: false),
            Ui.Button(CommandBarStrings.RemoveButton, () =>
            {
                var next = CommandBarPreferences.DecodeHidden(Settings.GetString(CommandBarKeys.Hidden, string.Empty));
                next.Remove(key);
                Settings.Set(CommandBarKeys.Hidden, CommandBarPreferences.EncodeHidden(next));
            }))).ToList();
        if (rows.Count == 0) rows.Add(Ui.Caption(CommandBarStrings.HiddenEmpty, 12));
        var forget = Ui.Button(CommandBarStrings.ForgetAllButton, () =>
        {
            if (Service is { } service) service.ForgetLearning();
            else
            {
                Settings.Remove(CommandBarKeys.Usage);
                Settings.Remove(CommandBarKeys.QueryHabits);
            }
        });
        forget.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(forget);
        return Ui.Group(CommandBarStrings.HiddenTitle, rows.ToArray());
    }, CommandBarKeys.Hidden);

    private static string Title(string key) => Service?.TitleFor(key) ?? key;

    private static FrameworkElement ListRow(string glyph, string text, string? detail, Action remove)
    {
        var icon = Ui.Icon(glyph, 12);
        icon.Width = 18;
        var label = Ui.Text(text, 13, wrap: false);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = 520;
        var left = Ui.Row(8, icon, label, detail is null ? null : Ui.Caption(detail, 12));
        return Ui.Split(left, Ui.Button(CommandBarStrings.RemoveButton, remove));
    }
}
