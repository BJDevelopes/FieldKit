// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Launcher.CommandBar;
using Fieldkit.Interop;
using Fieldkit.Tests.Apps;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarControllerTests
{
    public CommandBarControllerTests() => CommandBarSystem.Beep = () => { };

    private sealed class FakeHost : ICommandBarHost
    {
        public int Hidden;
        public string? OpenedPage;
        public int ShortcutChanges;
        public bool Suspended;
        public IntPtr PreviousWindow => IntPtr.Zero;
        public void Hide() => Hidden++;
        public void OpenSettings(string pageId) => OpenedPage = pageId;
        public void RowShortcutsChanged() => ShortcutChanges++;
        public void SuspendHotkeys(bool suspend) => Suspended = suspend;
    }

    private static CommandBarEntry Row(string id, string title, string subtitle = "Tools", Action<int?>? run = null) =>
        new() { Id = id, Title = title, Subtitle = subtitle, Run = run ?? (_ => { }) };

    private static (CommandBarController C, FakeHost Host, List<string> Ran) Make(CommandBarPrefs? prefs = null, bool compact = false)
    {
        var host = new FakeHost();
        var ran = new List<string>();
        prefs ??= new CommandBarPrefs { Compact = compact };
        var c = new CommandBarController(host, prefs) { AnswerFactory = CommandBarCatalog.Answer, OpenUrlFactory = CommandBarCatalog.OpenUrl };
        var catalog = new List<CommandBarEntry>
        {
            Row("action.screenshot", "Take a screenshot", "Screenshot", _ => ran.Add("screenshot")),
            Row("action.clipboard", "Open clipboard history", "Clipboard history", _ => ran.Add("clipboard")),
            new() { Id = "action.volume", Title = "Volume", Subtitle = "Sound", NumericRange = (0, 100), Run = v => ran.Add("volume " + v) },
            new() { Id = "action.power.restart", Title = "Restart", Subtitle = "General", ConfirmationPrompt = "Restart the PC?", Run = _ => ran.Add("restart") },
            new() { Id = "answer.battery", Title = "Battery", AnswerValue = "80%", CountsUsage = false, Run = _ => ran.Add("battery") },
            Row("settings.general", "General", "Settings"),
            Row(CommandBarPreferences.EmojiBrowserRowId, "Emoji", "Command Bar") with { KeepsBarOpen = true, Run = _ => c.SetCategory(CommandBarSource.Emoji) },
        };
        var apps = Enumerable.Range(0, 8).Select(i => Row("app.chrome" + i, "Chrome " + i, "App", _ => ran.Add("chrome"))).ToList();
        apps.Add(Row("app.notepad", "Notepad", "App", _ => ran.Add("notepad")));
        var quit = new List<CommandBarEntry> { new() { Id = "quit.notepad.exe", Title = "Quit Notepad", ConfirmationPrompt = "Quit Notepad?" } };
        var emoji = new List<CommandBarEntry>
        {
            new() { Id = "emoji.🔥", Title = "fire", MatchTitle = "fire", Keywords = "hot flame", IconKind = CommandBarIconKind.Emoji, Glyph = "🔥" },
            new() { Id = "emoji.👍", Title = "thumbs up", MatchTitle = "thumbs up", Keywords = "yes", IconKind = CommandBarIconKind.Emoji, Glyph = "👍" },
        };
        c.SetData(prefs, catalog, apps, [], quit, CommandBarCatalog.WindowsSettingsEntries(), emoji);
        return (c, host, ran);
    }

    [Fact]
    public void EmptyBarShowsChipsAndSuggestions()
    {
        var (c, _, _) = Make();
        Assert.True(c.IsShowingSuggestions);
        Assert.Contains(CommandBarSource.Apps, c.CategoryChips);
        Assert.Contains(CommandBarSource.Emoji, c.CategoryChips);
        Assert.DoesNotContain(CommandBarSource.Windows, c.CategoryChips); // no windows were given
        Assert.NotEmpty(c.SectionTitles);
    }

    [Fact]
    public void CompactModeStartsCollapsedAndDownPeeks()
    {
        var (c, _, _) = Make(compact: true);
        Assert.True(c.IsCompactHome);
        Assert.Empty(c.Rows);
        Assert.True(c.PeekHome());
        Assert.False(c.IsCompactHome);
        Assert.NotEmpty(c.Rows);
        c.StepBack(); // puts the peek away
        Assert.True(c.IsCompactHome);
    }

    [Fact]
    public void TypingRanksAndCapsEachKind()
    {
        var (c, _, _) = Make();
        c.SetQuery("chrome");
        Assert.Equal(5, c.Rows.Count(r => r.Id.StartsWith("app.", StringComparison.Ordinal)));
        Assert.Equal(0, c.SelectedIndex);
    }

    [Fact]
    public void ACalculatorAnswerLeads()
    {
        var (c, _, _) = Make();
        c.SetQuery("2+2*3");
        Assert.Equal("math.result", c.Rows[0].Id);
        Assert.Equal("8", c.Rows[0].Title);
        c.CompleteSelection();
        Assert.Equal("8", c.Query);
    }

    [Fact]
    public void FactsOnlyShowWhenAskedByName()
    {
        var (c, _, _) = Make();
        c.SetQuery("ba");
        Assert.DoesNotContain(c.Rows, r => r.Id == "answer.battery");
        c.SetQuery("batt");
        Assert.Contains(c.Rows, r => r.Id == "answer.battery");
    }

    [Fact]
    public void QuitRowsJoinOnlyWhenAskedFor()
    {
        var (c, _, _) = Make();
        c.SetQuery("notepad");
        Assert.DoesNotContain(c.Rows, r => r.Id.StartsWith("quit.", StringComparison.Ordinal));
        c.SetQuery("quit notepad");
        Assert.Contains(c.Rows, r => r.Id == "quit.notepad.exe");
    }

    [Fact]
    public void EmojiNeedTheColon()
    {
        var (c, _, _) = Make();
        c.SetQuery("fire");
        Assert.DoesNotContain(c.Rows, r => r.Id.StartsWith("emoji.", StringComparison.Ordinal));
        c.SetQuery(":fire");
        Assert.Equal("emoji.🔥", c.Rows[0].Id);
        c.SetQuery(":hot");
        Assert.Equal("emoji.🔥", c.Rows[0].Id);
    }

    [Fact]
    public void AliasesOutrankTitles()
    {
        var prefs = new CommandBarPrefs { Aliases = new(StringComparer.Ordinal) { ["app.notepad"] = "chr" } };
        var (c, _, _) = Make(prefs);
        c.SetQuery("chr");
        Assert.Equal("app.notepad", c.Rows[0].Id);
    }

    [Fact]
    public void HiddenRowsAndDisabledSourcesStayOut()
    {
        var prefs = new CommandBarPrefs { Hidden = ["app.notepad"], Disabled = [CommandBarSource.WindowsSettings] };
        var (c, _, _) = Make(prefs);
        c.SetQuery("notepad");
        Assert.DoesNotContain(c.Rows, r => r.Id == "app.notepad");
        c.SetQuery("display");
        Assert.DoesNotContain(c.Rows, r => r.Id.StartsWith("winsettings.", StringComparison.Ordinal));
    }

    [Fact]
    public void EnterRunsAndHides()
    {
        var (c, host, ran) = Make();
        c.SetQuery("screenshot");
        c.RunSelected();
        Assert.Equal(["screenshot"], ran);
        Assert.Equal(1, host.Hidden);
    }

    [Fact]
    public void NumericCommandsTakeATrailingNumberOrAsk()
    {
        var (c, host, ran) = Make();
        c.SetQuery("volume 40");
        Assert.Equal("action.volume", c.Rows[0].Id);
        c.RunSelected();
        Assert.Equal(["volume 40"], ran);

        c.SetQuery("volume");
        c.RunSelected();
        Assert.Equal(CommandBarMode.Argument, c.Mode);
        Assert.Equal(string.Empty, c.Query);
        c.SetQuery("150");
        c.RunSelected();
        Assert.Equal("volume 100", ran[^1]);
        Assert.Equal(2, host.Hidden);
    }

    [Fact]
    public void DestructiveCommandsConfirmAndEscCancels()
    {
        var (c, host, ran) = Make();
        c.SetQuery("restart");
        c.RunSelected();
        Assert.Equal(CommandBarMode.Confirm, c.Mode);
        Assert.Empty(ran);
        c.StepBack();
        Assert.Equal(CommandBarMode.Search, c.Mode);
        Assert.Equal("restart", c.Query);
        c.RunSelected();
        c.RunSelected();
        Assert.Equal(["restart"], ran);
        Assert.Equal(1, host.Hidden);
    }

    [Fact]
    public void TypingTakesAConfirmationDown()
    {
        var (c, _, ran) = Make();
        c.SetQuery("restart");
        c.RunSelected();
        c.SetQuery("restartx");
        Assert.Equal(CommandBarMode.Search, c.Mode);
        Assert.Empty(ran);
    }

    [Fact]
    public void EscClearsThenLeavesTheCategoryThenCloses()
    {
        var (c, host, _) = Make();
        c.SetCategory(CommandBarSource.Apps);
        c.SetQuery("chrome");
        c.StepBack();
        Assert.Equal(string.Empty, c.Query);
        Assert.Equal(CommandBarSource.Apps, c.ActiveCategory);
        c.StepBack();
        Assert.Null(c.ActiveCategory);
        Assert.Equal(0, host.Hidden);
        c.StepBack();
        Assert.Equal(1, host.Hidden);
    }

    [Fact]
    public void ArrowsWalkTheChipsOnlyWhileEmpty()
    {
        var (c, _, _) = Make();
        Assert.True(c.MoveCategory(1));
        Assert.Equal(c.CategoryChips[0], c.ActiveCategory);
        Assert.True(c.MoveCategory(-1));
        Assert.Null(c.ActiveCategory);
        c.SetQuery("x");
        Assert.False(c.MoveCategory(1));
    }

    [Fact]
    public void SelectionWrapsAround()
    {
        var (c, _, _) = Make();
        c.SetQuery("chrome");
        c.MoveSelection(-1);
        Assert.Equal(c.Rows.Count - 1, c.SelectedIndex);
        c.MoveSelection(1);
        Assert.Equal(0, c.SelectedIndex);
    }

    [Fact]
    public void ActionsPinNameAndHide()
    {
        var (c, _, _) = Make();
        c.SetQuery("notepad");
        Assert.True(c.CanOpenActions);
        c.OpenActions();
        Assert.Equal(CommandBarMode.Actions, c.Mode);
        var actions = c.ActionRows();
        Assert.Contains(actions, a => a.Id == "pin");
        Assert.Contains(actions, a => a.Id == "name");
        Assert.Contains(actions, a => a.Id == "hide");
        actions.Single(a => a.Id == "pin").Run();
        Assert.Contains("app.notepad", c.Prefs.Pins);
        Assert.Equal(CommandBarMode.Search, c.Mode);
        Assert.Equal("notepad", c.Query);

        c.OpenActions();
        c.ActionRows().Single(a => a.Id == "name").Run();
        Assert.Equal(CommandBarMode.Naming, c.Mode);
        c.SetQuery("np");
        c.RunSelected();
        Assert.Equal("np", c.Prefs.Aliases["app.notepad"]);
        Assert.Equal(CommandBarMode.Search, c.Mode);
    }

    [Fact]
    public void NamingRefusesANameAlreadyTaken()
    {
        var prefs = new CommandBarPrefs { Aliases = new(StringComparer.Ordinal) { ["app.chrome0"] = "browser" } };
        var (c, _, _) = Make(prefs);
        c.SetQuery("notepad");
        c.OpenActions();
        c.ActionRows().Single(a => a.Id == "name").Run();
        c.SetQuery("browser");
        c.RunSelected();
        Assert.Equal(CommandBarMode.Naming, c.Mode);
        Assert.NotNull(c.AliasWarning);
        Assert.False(c.Prefs.Aliases.ContainsKey("app.notepad"));
    }

    [Fact]
    public void CapturesARowShortcut()
    {
        var (c, host, _) = Make();
        c.SetQuery("notepad");
        c.OpenActions();
        c.ActionRows().Single(a => a.Id == "shortcut").Run();
        Assert.Equal(CommandBarMode.CapturingShortcut, c.Mode);
        Assert.True(host.Suspended);
        c.HandleCaptureKey(0x4E, Modifiers.None); // a bare key is refused
        Assert.Equal(CommandBarMode.CapturingShortcut, c.Mode);
        c.HandleCaptureKey(0x4E, Modifiers.Ctrl | Modifiers.Alt);
        Assert.Equal(CommandBarMode.Actions, c.Mode);
        Assert.False(host.Suspended);
        Assert.Equal("Ctrl+Alt+N", c.Prefs.RowShortcuts["app.notepad"].ToString());
        Assert.Equal(1, host.ShortcutChanges);
    }

    [Fact]
    public void AnswersAndUnindexedRowsHaveNoActions()
    {
        var (c, _, _) = Make();
        c.SetQuery("2+2");
        Assert.False(c.CanOpenActions);
    }

    [Fact]
    public void BrowseRowsKeepTheBarOpen()
    {
        var (c, host, _) = Make();
        c.SetQuery("emoji");
        c.Run(c.Rows.First(r => r.Id == CommandBarPreferences.EmojiBrowserRowId));
        Assert.Equal(0, host.Hidden);
        Assert.Equal(CommandBarSource.Emoji, c.ActiveCategory);
    }

    [Fact]
    public void LearnsWhichRowFollowsWhichLetters()
    {
        var (c, _, _) = Make();
        c.SetQuery("ch");
        var second = c.Rows[1];
        c.Run(second);
        c.Reset();
        c.SetQuery("ch");
        Assert.Equal(second.Id, c.Rows[0].Id);
    }

    [Fact]
    public void HighlightsOnlyInSearch()
    {
        var (c, _, _) = Make();
        c.SetQuery("note");
        var notepad = c.Rows.First(r => r.Id == "app.notepad");
        Assert.Equal([0, 1, 2, 3], c.HighlightOffsets(notepad).OrderBy(i => i));
    }

    [Fact]
    public void RendersTheSettingsPageOffscreen()
    {
        var path = StaRunner.PreviewPath("commandbar-settings.png");
        StaRunner.Run(() => PreviewRenderer.Render(CommandBarSettingsPage.Build(), path, 900, 2600));
        Assert.True(new FileInfo(path).Length > 5000);
    }
}
