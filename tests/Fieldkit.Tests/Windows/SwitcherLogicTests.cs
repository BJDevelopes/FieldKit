// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.WindowTools;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Windows;

public class SwitcherLogicTests
{
    private static SwitcherItem W(int handle, string app, string title, bool minimized = false, Rect? frame = null) =>
        new(new IntPtr(handle), title, (uint)app.Length, $@"C:\Apps\{app}.exe", app, minimized, false, frame ?? new Rect(0, 0, 100, 100));

    [Fact]
    public void OrdersByMostRecentUseThenZOrder()
    {
        var z = new[] { W(1, "a", "one"), W(2, "b", "two"), W(3, "c", "three"), W(4, "d", "four") };
        var ordered = SwitcherLogic.Order(z, [new IntPtr(3), new IntPtr(1)], MinimizedPlacement.Normal);
        Assert.Equal([3, 1, 2, 4], ordered.Select(i => (int)i.Handle));
    }

    [Fact]
    public void MinimizedPlacementOptions()
    {
        var z = new[] { W(1, "a", "one", minimized: true), W(2, "b", "two"), W(3, "c", "three") };
        Assert.Equal([1, 2, 3], SwitcherLogic.Order(z, [], MinimizedPlacement.Normal).Select(i => (int)i.Handle));
        Assert.Equal([2, 3, 1], SwitcherLogic.Order(z, [], MinimizedPlacement.End).Select(i => (int)i.Handle));
        Assert.Equal([2, 3], SwitcherLogic.Order(z, [], MinimizedPlacement.Hidden).Select(i => (int)i.Handle));
        Assert.Equal(MinimizedPlacement.End, SwitcherLogic.ParsePlacement("end"));
        Assert.Equal(MinimizedPlacement.Normal, SwitcherLogic.ParsePlacement("garbage"));
    }

    [Fact]
    public void RulesHideAppsAndDecideWindowlessEntries()
    {
        var rules = new Dictionary<string, SwitcherAppRule>
        {
            ["a.exe"] = SwitcherAppRule.Hidden,
            ["b.exe"] = SwitcherAppRule.ShowWithoutWindows,
            ["c.exe"] = SwitcherAppRule.WindowsOnly,
        };
        var items = SwitcherLogic.ApplyRules([W(1, "a", "x"), W(2, "b", "y")], rules);
        Assert.Single(items);
        Assert.True(SwitcherLogic.ShowsWindowlessApp("b.exe", WindowlessApps.Off, rules));
        Assert.False(SwitcherLogic.ShowsWindowlessApp("c.exe", WindowlessApps.All, rules));
        Assert.True(SwitcherLogic.ShowsWindowlessApp("explorer.exe", WindowlessApps.Explorer, rules));
        Assert.False(SwitcherLogic.ShowsWindowlessApp("zoom.exe", WindowlessApps.Explorer, rules));
        Assert.True(SwitcherLogic.ShowsWindowlessApp("zoom.exe", WindowlessApps.All, rules));
        Assert.Equal(WindowlessApps.Explorer, SwitcherLogic.ParseWindowless(null));
        Assert.Equal(SwitcherAppRule.Hidden, SwitcherLogic.ParseRule(SwitcherLogic.RuleId(SwitcherAppRule.Hidden)));
    }

    [Fact]
    public void MergesAndGroupsByApp()
    {
        var items = new[] { W(1, "a", "a1"), W(2, "b", "b1"), W(3, "a", "a2"), W(4, "c", "c1"), W(5, "b", "b2") };
        Assert.Equal([1, 2, 4], SwitcherLogic.MergeByApp(items).Select(i => (int)i.Handle));
        var groups = SwitcherLogic.GroupByApp(items);
        Assert.Equal(3, groups.Count);
        Assert.Equal([0, 2], groups[0].ItemIndices);
        Assert.Equal([1, 4], groups[1].ItemIndices);
        Assert.Equal(2, SwitcherLogic.NextOfSameApp(items, 0, 1));
        Assert.Equal(0, SwitcherLogic.NextOfSameApp(items, 2, 1));
        Assert.Equal(3, SwitcherLogic.NextOfSameApp(items, 3, 1));
    }

    [Theory]
    [InlineData("Stocks", true)]
    [InlineData("stocks chrome", true)]
    [InlineData("STO", true)]
    [InlineData("gc", true)]
    [InlineData("tab news", false)]
    [InlineData("", true)]
    [InlineData("café", true)]
    public void SearchMatchesTitleAndAppName(string query, bool expected)
    {
        var item = new SwitcherItem(new IntPtr(1), "Stocks — Cafe", 1, @"C:\chrome.exe", "Google Chrome", false, false, Rect.Empty);
        Assert.Equal(expected, SwitcherLogic.Matches(item, query));
    }

    [Fact]
    public void LettersActOnTheSelection()
    {
        Assert.Equal(SwitcherLetterAction.CloseWindow, SwitcherLogic.LetterAction('W', false));
        Assert.Equal(SwitcherLetterAction.QuitApp, SwitcherLogic.LetterAction('q', false));
        Assert.Equal(SwitcherLetterAction.MinimizeWindow, SwitcherLogic.LetterAction('m', false));
        Assert.Null(SwitcherLogic.LetterAction('s', false));
        Assert.Equal(SwitcherLetterAction.PinSearch, SwitcherLogic.LetterAction('s', true));
        Assert.Null(SwitcherLogic.LetterAction('x', true));
    }

    [Fact]
    public void SelectionMovement()
    {
        Assert.Equal(1, SwitcherLogic.InitialSelection(5, false));
        Assert.Equal(4, SwitcherLogic.InitialSelection(5, true));
        Assert.Equal(0, SwitcherLogic.InitialSelection(1, true));
        Assert.Equal(0, SwitcherLogic.Advance(4, 1, 5));
        Assert.Equal(4, SwitcherLogic.Advance(0, -1, 5));
        Assert.Equal(4, SwitcherLogic.Advance(4, 1, 5, wrap: false));
        Assert.Equal(7, SwitcherLogic.MoveRow(3, 1, 4, 10));
        Assert.Equal(8, SwitcherLogic.MoveRow(8, 1, 4, 10));
        Assert.Equal(8, SwitcherLogic.MoveRow(8, -3, 4, 10));
    }

    [Fact]
    public void ColumnsFitTheScreenAndTheCap()
    {
        Assert.Equal(5, SwitcherLogic.Columns(10, 1500, 298, null));
        Assert.Equal(3, SwitcherLogic.Columns(10, 1500, 298, 3));
        Assert.Equal(2, SwitcherLogic.Columns(2, 1500, 298, null));
        Assert.Null(SwitcherLogic.SanitizedMaxColumns(1));
        Assert.Equal(8, SwitcherLogic.SanitizedMaxColumns(8));
        Assert.Equal(1.8, SwitcherLogic.PreviewScale("xlarge"));
        Assert.Equal(500, SwitcherLogic.SanitizedDelay(900));
    }

    [Fact]
    public void CurrentDisplayFilter()
    {
        var display = new Rect(0, 0, 1920, 1080);
        var items = new[]
        {
            W(1, "a", "on", frame: new Rect(100, 100, 800, 600)),
            W(2, "b", "off", frame: new Rect(2000, 100, 800, 600)),
            W(3, "c", "mostly off", frame: new Rect(1800, 100, 800, 600)),
        };
        Assert.Equal([1], SwitcherLogic.OnDisplay(items, display).Select(i => (int)i.Handle));
    }

    [Fact]
    public void ShortcutMatchingAllowsShiftForBackwards()
    {
        var altTab = Shortcut.Parse("Alt+Tab");
        Assert.True(SwitcherLogic.MatchesShortcut(altTab, 0x09, Modifiers.Alt));
        Assert.True(SwitcherLogic.MatchesShortcut(altTab, 0x09, Modifiers.Alt | Modifiers.Shift));
        Assert.False(SwitcherLogic.MatchesShortcut(altTab, 0x09, Modifiers.Alt | Modifiers.Ctrl));
        Assert.False(SwitcherLogic.MatchesShortcut(altTab, 0x09, Modifiers.None));
        Assert.False(SwitcherLogic.MatchesShortcut(altTab, 0x41, Modifiers.Alt));
        Assert.True(SwitcherService.IsSystemCombo(altTab));
        Assert.False(SwitcherService.IsSystemCombo(Shortcut.Parse("Alt+`")));
        Assert.Equal("Alt+`", Shortcut.Parse(SwitcherService.DefaultWindowShortcut).ToString());
    }
}
