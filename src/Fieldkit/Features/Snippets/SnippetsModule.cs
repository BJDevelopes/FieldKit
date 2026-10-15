// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.UI;

namespace Fieldkit.Features.Snippets;

/// <summary>Text snippets: typed triggers, the quick snippet menu and the snippet library editor.</summary>
public sealed class SnippetsModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(TextSnippetService.Id, Parity.Partial,
            "Typed triggers (right away / after a delimiter, ignore case), date/time/clipboard variables with the date builder, " +
            "{{cursor}}, sound, folders, the searchable quick menu and import/export. Password boxes are detected through UI Automation " +
            "and classic edit controls, not system-wide like macOS secure input.");

        // Typing only needs the hook while some snippet is enabled: re-check when the library changes.
        SnippetStore.Changed += () => Ui.OnUi(() => FeatureRuntime.Reevaluate(TextSnippetService.Id));

        r.Service(TextSnippetService.Id, () => new TextSnippetService());
        r.Service(TextSnippetService.Id, () => new SnippetLibraryService());
        r.SettingsPage(new SettingsPageDef("snippets", SettingsCategory.Files, "Text snippets", Glyphs.Note, 30,
            [TextSnippetService.Id], BuildPage,
            ["snippet", "expand", "trigger", "abbreviation", "text expansion", "library", "date", "template"]));
        r.Control(new ControlEntry(TextSnippetService.Id, ControlGroup.MouseKeyboard, "Text snippets",
            "Type a trigger anywhere and it becomes its text. Everything stays on this PC.",
            Glyphs.Note, TextSnippetService.EnabledKey, false, Order: 60));
        r.Action(new ToolAction("textSnippets.library", TextSnippetService.Id, "Quick snippet menu", Glyphs.Note,
            SnippetLibraryWindow.Toggle, SnippetLibraryService.ShortcutKey, SnippetLibraryService.DefaultShortcut));
        r.Action(new ToolAction("textSnippets.manage", TextSnippetService.Id, "Manage snippets", Glyphs.Note,
            () => PanelHost.OpenSettings("snippets")));
    }

    // ------------------------------------------------------------------ page

    private static FrameworkElement BuildPage()
    {
        var soundBox = new ComboBox { MinWidth = 220 };
        var names = SnippetSounds.Names();
        var stored = Settings.GetString(TextSnippetService.SoundNameKey, SnippetSounds.DefaultName);
        foreach (var name in names) soundBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        var resolved = SnippetSounds.ResolvedName(stored, names);
        if (resolved != stored) soundBox.Items.Add(new ComboBoxItem { Content = "Sound unavailable", Tag = stored });
        soundBox.SelectedItem = soundBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == stored)
                                ?? soundBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        soundBox.SelectionChanged += (_, _) =>
        {
            if (soundBox.SelectedItem is not ComboBoxItem item) return;
            Settings.Set(TextSnippetService.SoundNameKey, (string)item.Tag);
            if (SnippetSounds.Resolve((string)item.Tag, names) is { } path) SnippetSounds.Play(path);
        };
        var soundRow = Ui.Split(Ui.Text("Sound", 13.5, FontWeights.Medium), soundBox);
        var soundToggle = Ui.ToggleRow("Play a sound when a typed trigger expands",
            "A short system sound plays each time a typed trigger expands.", TextSnippetService.SoundEnabledKey, false);
        void SyncSound()
        {
            var on = Settings.GetBool(TextSnippetService.EnabledKey);
            soundToggle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            soundRow.Visibility = on && Settings.GetBool(TextSnippetService.SoundEnabledKey) ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncSound();
        Ui.Track(soundRow, () => Settings.Observe(SyncSound, TextSnippetService.EnabledKey, TextSnippetService.SoundEnabledKey));
        // Rows that hide themselves share one group row, so no empty divider is left behind.
        var main = Ui.Group(null,
            Ui.Stack(14,
                Ui.ToggleRow("Expand snippets while typing", "Type a trigger anywhere and it becomes its text. Everything stays on this PC.",
                    TextSnippetService.EnabledKey, false),
                soundToggle,
                soundRow));

        var libraryShortcut = Ui.ShortcutRow("Shortcut", SnippetLibraryService.ShortcutKey, SnippetLibraryService.DefaultShortcut);
        void SyncLibrary() => libraryShortcut.Visibility = Settings.GetBool(SnippetLibraryService.EnabledKey) ? Visibility.Visible : Visibility.Collapsed;
        SyncLibrary();
        Ui.Track(libraryShortcut, () => Settings.Observe(SyncLibrary, SnippetLibraryService.EnabledKey));
        var library = Ui.Group("Quick snippet menu",
            Ui.Stack(14,
                Ui.ToggleRow("Open snippets from a menu",
                    "The shortcut opens a searchable menu. Picking a snippet types it right where your cursor is.",
                    SnippetLibraryService.EnabledKey, false),
                libraryShortcut));

        var privacy = Ui.Group("Where snippets expand",
            ExcludedAppsList(),
            Ui.Caption("Snippets never expand in password boxes Windows can recognize, in Fieldkit's own windows, or while the snippet menu is open."));

        return Ui.Page("Text snippets", "Short triggers expand into full text.", main, library, privacy, SnippetListGroup());
    }

    private static FrameworkElement ExcludedAppsList()
    {
        var list = Ui.Stack(6);
        var add = Ui.Button("Add an app…", () => { }, glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => ClipboardPage.ShowAddMenu(add, exe => TextSnippetService.ExcludedApps = [.. TextSnippetService.ExcludedApps, exe]);
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var app in TextSnippetService.ExcludedApps)
            {
                var name = app;
                var remove = Ui.IconButton(Glyphs.Remove, "Remove", () =>
                    TextSnippetService.ExcludedApps = TextSnippetService.ExcludedApps.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList(), 11);
                Ui.Append(list, 6, Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Grid, 13, "TextSecondary"), Ui.Text(name, 13, wrap: false)), remove));
            }
        }
        Rebuild();
        Ui.Track(list, () => Settings.Observe(Rebuild, TextSnippetService.ExcludedAppsKey));
        return Ui.Stack(6, Ui.Text("Apps to skip", 13.5, FontWeights.Medium), Ui.Caption("Triggers typed in these apps stay as typed."), list, add);
    }

    private static FrameworkElement SnippetListGroup()
    {
        var rows = new StackPanel();
        var addButton = Ui.Button("Add snippet", () => Edit(null), glyph: Glyphs.Add);
        var import = Ui.Button("Import…", Import);
        var export = Ui.Button("Export…", Export);
        var actions = new DockPanel { LastChildFill = false };
        var io = Ui.Row(8, import, export);
        DockPanel.SetDock(io, Dock.Right);
        actions.Children.Add(addButton);
        actions.Children.Add(io);
        void Rebuild()
        {
            rows.Children.Clear();
            var all = SnippetStore.All;
            export.IsEnabled = all.Count > 0;
            if (all.Count == 0) rows.Children.Add(Ui.Caption("No snippets yet. Add the first one."));
            var first = true;
            foreach (var snippet in all)
            {
                if (!first) rows.Children.Add(Ui.Divider(6));
                first = false;
                rows.Children.Add(SnippetRow(snippet));
            }
        }
        Rebuild();
        void OnChanged() => Ui.OnUi(Rebuild);
        Ui.Track(rows, () =>
        {
            SnippetStore.Changed += OnChanged;
            return new ActionDisposable(() => SnippetStore.Changed -= OnChanged);
        });
        return Ui.Group("Snippets",
            rows,
            actions,
            Ui.Stack(2,
                Ui.Caption(SnippetEditor.VariablesHint, 12),
                Ui.Caption(SnippetEditor.VariablesCaption, 12),
                Ui.Caption("A format after a colon picks how they look, like {{date:yyyy-MM-dd}}. A -tz(...) part sets the timezone, like {{date-tz(America/New_York):yyyy-MM-dd}}.", 12)));
    }

    private static FrameworkElement SnippetRow(TextSnippet snippet)
    {
        var name = Ui.Text(snippet.DisplayName, 13.5, FontWeights.Medium, wrap: false);
        var trigger = Ui.Badge(snippet.Trigger);
        ((TextBlock)trigger.Child).FontFamily = new FontFamily("Cascadia Mono, Consolas");
        var folder = snippet.Folder.Length > 0
            ? Ui.Row(4, Ui.Icon(Glyphs.Folder, 11, "TextSecondary"), Ui.Text(snippet.Folder, 11.5, brush: "TextSecondary", wrap: false))
            : null;
        var preview = Ui.Text(SnippetEngine.Preview(snippet.Replacement), 12, brush: "TextSecondary", wrap: false);
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        var left = Ui.Stack(2, Ui.Row(6, name, trigger, folder), preview);
        var edit = Ui.Clickable(left, () => Edit(snippet), "Edit snippet");
        edit.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var mode = Ui.Text(snippet.Expansion == SnippetExpansion.Immediate ? "Right away" : "After space, Tab or Enter", 11, brush: "TextTertiary", wrap: false);
        var toggle = new CheckBox { IsChecked = snippet.Enabled };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchSmall");
        toggle.Click += (_, _) => SnippetStore.SetEnabled(snippet.Id, toggle.IsChecked == true);
        return Ui.Split(edit, Ui.Row(10, mode, toggle));
    }

    private static Window? Owner() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    private static void Edit(TextSnippet? existing)
    {
        var all = SnippetStore.All;
        var snippet = existing ?? new TextSnippet();
        SnippetEditor.Show(Owner(), snippet, existing is null, all.Where(s => s.Id != snippet.Id).ToList(),
            SnippetStore.Upsert,
            existing is null ? null : () => SnippetStore.Remove(existing.Id));
    }

    private static void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Snippets (*.json)|*.json|All files (*.*)|*.*", Title = "Import snippets" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var parsed = SnippetStore.Parse(File.ReadAllText(dialog.FileName));
            if (parsed is null)
            {
                MessageBox.Show("That file is not a snippets file.", "Import snippets", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var (result, imported) = SnippetStore.Merge(SnippetStore.All, parsed);
            SnippetStore.Save(result);
            var skipped = parsed.Count - imported;
            Notifier.Show("Text snippets", skipped > 0
                ? $"Imported {imported} snippets; {skipped} skipped because their trigger is already used."
                : $"Imported {imported} snippets.");
        }
        catch (Exception ex)
        {
            Log.Error("Importing snippets failed", ex);
            MessageBox.Show($"Could not import: {ex.Message}", "Import snippets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Snippets (*.json)|*.json", FileName = "Snippets.json", Title = "Export snippets" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, SnippetStore.Serialize(SnippetStore.All));
        }
        catch (Exception ex)
        {
            Log.Error("Exporting snippets failed", ex);
            MessageBox.Show($"Could not export: {ex.Message}", "Export snippets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
