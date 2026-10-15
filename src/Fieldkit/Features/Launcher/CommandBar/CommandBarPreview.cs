// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Renders the floating bar offscreen with realistic sample data (the bar cannot be reached by
/// <c>--render-ui</c>). Used by STA unit tests; touches nothing on the system but read-only icons.
/// </summary>
public static class CommandBarPreview
{
    private sealed class PreviewHost : ICommandBarHost
    {
        public IntPtr PreviousWindow => IntPtr.Zero;
        public void Hide() { }
        public void OpenSettings(string pageId) { }
        public void RowShortcutsChanged() { }
        public void SuspendHotkeys(bool suspend) { }
    }

    /// <summary>The bar opened with nothing typed: chips, examples, pins, suggestions and areas.</summary>
    public static void RenderHome(string path, bool dark) => Render(path, dark, null, null);

    /// <summary>The bar with a query typed (apps, a file, a window, a setting).</summary>
    public static void RenderSearch(string path, bool dark, string query = "note") => Render(path, dark, query, null);

    /// <summary>The bar answering a sum.</summary>
    public static void RenderCalculator(string path, bool dark) => Render(path, dark, "(2+2)*3^2", null);

    /// <summary>The actions list (Ctrl+K) of an app row.</summary>
    public static void RenderActions(string path, bool dark) => Render(path, dark, "notepad", c => c.OpenActions());

    /// <summary>A destructive command waiting for Enter.</summary>
    public static void RenderConfirm(string path, bool dark) => Render(path, dark, "restart", c =>
    {
        if (c.SelectedEntry is { } e) c.Run(e);
    });

    private static void Render(string path, bool dark, string? query, Action<CommandBarController>? then)
    {
        var host = new PreviewHost();
        var prefs = new CommandBarPrefs
        {
            Pins = ["app.notepad"],
            Aliases = new(StringComparer.Ordinal) { ["app.notepad"] = "np" },
            Usage = new(StringComparer.Ordinal)
            {
                ["action.screenshot"] = new CommandBarUse(9, 0),
                ["action.clipboard"] = new CommandBarUse(5, 0),
            },
            RowShortcuts = new(StringComparer.Ordinal) { ["app.notepad"] = Interop.Shortcut.Parse("Ctrl+Alt+N") },
            Persist = false,
        };
        var controller = new CommandBarController(host, prefs)
        {
            AnswerFactory = CommandBarCatalog.Answer,
            OpenUrlFactory = CommandBarCatalog.OpenUrl,
            Curated = ["action.screenshot", "action.clipboard", "action.keepAwake", "action.ocr", "action.colorPicker"],
        };
        controller.ExtraActions = entry => entry.Id == "app.notepad"
            ? [new CommandBarRowAction("quitApp", string.Format(CommandBarStrings.QuitFormat, "Notepad"), CommandBarGlyphs.QuitApp, () => { })]
            : [];
        // Files are found asynchronously in the real bar; a typed preview shows one as already found.
        var catalog = SampleCatalog();
        if (query is not null) catalog.Add(SampleFile());
        controller.SetData(prefs, catalog, SampleApps(), SampleWindows(), [], CommandBarCatalog.WindowsSettingsEntries(),
            CommandBarCatalog.EmojiEntries(CommandBarEmoji.SkinTone.None, host));
        if (query is not null) controller.SetQuery(query);
        then?.Invoke(controller);

        var view = new CommandBarView(controller, () => "Ctrl+Alt+Space")
        {
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        view.Field.Text = controller.Query;
        view.Rebuild();
        var frame = new Border { Padding = new Thickness(24), Child = view };
        frame.Measure(new Size(CommandBarView.BarWidth + 48, double.PositiveInfinity));
        var height = Math.Ceiling(frame.DesiredSize.Height) + 8;
        PreviewRenderer.Render(frame, path, CommandBarView.BarWidth + 48, Math.Max(160, height), dark);
    }

    private static CommandBarEntry Action(string id, string title, string area, string glyph, string? shortcut = null) => new()
    {
        Id = "action." + id,
        Title = title,
        Subtitle = area,
        Glyph = glyph,
        ShortcutText = shortcut,
    };

    private static List<CommandBarEntry> SampleCatalog()
    {
        var list = new List<CommandBarEntry>
        {
            Action("screenshot", "Take a screenshot", "Screenshot", Glyphs.Camera, "Ctrl+Shift+4"),
            Action("clipboard", "Open clipboard history", "Clipboard history", Glyphs.Clipboard, "Ctrl+Alt+V"),
            Action("keepAwake", "Toggle keep awake", "Keep awake", Glyphs.Moon),
            Action("ocr", "Copy text from screen", "Tools", Glyphs.TextScan),
            Action("colorPicker", "Pick a color", "Color picker", Glyphs.Eyedropper),
            Action("snippets", "Open snippet library", "Text snippets", Glyphs.Note),
            new CommandBarEntry { Id = "toggle.quick.darkMode", Title = "Turn off Dark mode", Subtitle = CommandBarStrings.QuickTogglesTitle, Glyph = Glyphs.Moon, IsActive = true },
            new CommandBarEntry { Id = "toggle.scroll", Title = "Turn on Scroll direction", Subtitle = "Mouse and keyboard", Glyph = Glyphs.ArrowsUpDown },
            new CommandBarEntry { Id = "action.power.restart", Title = CommandBarStrings.PowerRestart, Subtitle = CommandBarStrings.GeneralTitle, Glyph = CommandBarGlyphs.Restart, ConfirmationPrompt = CommandBarStrings.PowerRestartConfirm },
            new CommandBarEntry { Id = "action.power.sleep", Title = CommandBarStrings.PowerSleep, Subtitle = CommandBarStrings.GeneralTitle, Glyph = CommandBarGlyphs.Moon },
            new CommandBarEntry { Id = "action.volume", Title = "Volume", Subtitle = "Sound", Glyph = Glyphs.Volume, AnswerValue = "40%", NumericRange = (0, 100) },
            new CommandBarEntry { Id = "answer.battery", Title = CommandBarStrings.AnswerBatteryLabel, Subtitle = CommandBarStrings.AnswerBatteryPlugged, Glyph = CommandBarGlyphs.BatteryCharging, AnswerValue = "80%", CountsUsage = false },
            new CommandBarEntry { Id = "settings.commandBar", Title = CommandBarStrings.PageTitle, Subtitle = CommandBarStrings.SettingsTitle, Glyph = Glyphs.Command },
            new CommandBarEntry { Id = "settings.notes", Title = "Scratchpad notes", Subtitle = CommandBarStrings.SettingsTitle, Glyph = Glyphs.Note, Keywords = "notes" },
            new CommandBarEntry { Id = CommandBarPreferences.EmojiBrowserRowId, Title = CommandBarStrings.SourceEmoji, Subtitle = CommandBarStrings.PageTitle, Glyph = CommandBarGlyphs.Smile },
        };
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        list.Add(new CommandBarEntry
        {
            Id = "folder." + documents,
            Title = "Documents",
            Subtitle = CommandBarStrings.KindFolder,
            IconKind = CommandBarIconKind.Shell,
            IconPath = documents,
            Glyph = Glyphs.Folder,
            RevealPath = documents,
        });
        list.AddRange(CommandBarCatalog.DateAnswers(System.Globalization.CultureInfo.GetCultureInfo("en-US"), new DateTime(2026, 10, 14, 15, 30, 0)));
        return list;
    }

    private static CommandBarEntry App(string id, string name, string exe, bool running = false)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
        if (!File.Exists(path)) path = Path.Combine(Environment.SystemDirectory, exe);
        return new CommandBarEntry
        {
            Id = "app." + id,
            Title = name,
            Subtitle = CommandBarStrings.KindApp,
            IconKind = CommandBarIconKind.Shell,
            IconPath = path,
            Glyph = CommandBarGlyphs.Apps,
            IsActive = running,
            RevealPath = path,
        };
    }

    private static List<CommandBarEntry> SampleApps() =>
    [
        App("notepad", "Notepad", "notepad.exe", running: true),
        App("calc", "Calculator", "calc.exe"),
        App("explorer", "File Explorer", "explorer.exe", running: true),
        App("paint", "Paint", "mspaint.exe"),
        App("taskmgr", "Task Manager", "Taskmgr.exe"),
        new CommandBarEntry { Id = "app.stickynotes", Title = "Sticky Notes", Subtitle = CommandBarStrings.KindApp, Glyph = Glyphs.Note },
    ];

    private static List<CommandBarEntry> SampleWindows() =>
    [
        new CommandBarEntry
        {
            Id = "window.1",
            Title = "Meeting notes.txt - Notepad",
            Subtitle = "Notepad",
            Keywords = "Window Notepad",
            IconKind = CommandBarIconKind.Shell,
            IconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
            CountsUsage = false,
        },
    ];

    private static CommandBarEntry SampleFile()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return CommandBarCatalog.FileEntry(Path.Combine(documents, "Notes", "Release notes.md")) with
        {
            IconKind = CommandBarIconKind.Glyph,
            Glyph = CommandBarGlyphs.Document,
        };
    }
}
