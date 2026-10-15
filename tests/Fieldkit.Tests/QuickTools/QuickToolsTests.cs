// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.QuickTools;

namespace Fieldkit.Tests.QuickTools;

public class QuickToolsLogicTests
{
    [Fact]
    public void EjectOffersExternalVolumesOnly()
    {
        Assert.True(QuickToolsLogic.ShouldOfferEject(removable: true, fixedDisk: false, -1, isSystem: false, @"E:\", "STICK", []));
        Assert.True(QuickToolsLogic.ShouldOfferEject(false, true, busType: 7, false, @"F:\", "Backup", []));
        Assert.False(QuickToolsLogic.ShouldOfferEject(false, true, busType: 11, false, @"D:\", "Data", [])); // SATA
        Assert.False(QuickToolsLogic.ShouldOfferEject(true, false, -1, isSystem: true, @"C:\", "Windows", []));
    }

    [Theory]
    [InlineData("stick")]
    [InlineData("E:")]
    [InlineData(@"E:\")]
    [InlineData("e")]
    public void EjectExclusionsMatchLabelOrLetter(string exclusion) =>
        Assert.False(QuickToolsLogic.ShouldOfferEject(true, false, -1, false, @"E:\", "STICK", [exclusion]));

    [Fact]
    public void OrderSanitizing()
    {
        string[] defaults = ["a", "b", "c", "d"];
        Assert.Equal(defaults, QuickToolsLogic.SanitizedOrder("", defaults));
        Assert.Equal(["c", "a", "b", "d"], QuickToolsLogic.SanitizedOrder("c, x, a, c", defaults));
    }

    [Fact]
    public void MovesBeforeTarget()
    {
        Assert.Equal(["c", "a", "b"], QuickToolsLogic.Moved(["a", "b", "c"], "c", "a"));
        Assert.Equal(["b", "c", "a"], QuickToolsLogic.Moved(["a", "b", "c"], "a", null));
        Assert.Equal("a,b", QuickToolsLogic.JoinedSet(["b", "a", "b"]));
    }

    [Fact]
    public void GridNavigationDoesNotWrap()
    {
        // 3 columns, 7 tiles: rows [0 1 2] [3 4 5] [6]
        Assert.Equal(1, QuickToolsLogic.GridMove(0, 7, 3, 1, 0));
        Assert.Equal(2, QuickToolsLogic.GridMove(2, 7, 3, 1, 0)); // right edge
        Assert.Equal(3, QuickToolsLogic.GridMove(3, 7, 3, -1, 0)); // left edge
        Assert.Equal(6, QuickToolsLogic.GridMove(3, 7, 3, 0, 1));
        Assert.Equal(4, QuickToolsLogic.GridMove(4, 7, 3, 0, 1)); // no tile below
        Assert.Equal(1, QuickToolsLogic.GridMove(1, 7, 3, 0, -1)); // top
        Assert.Equal(6, QuickToolsLogic.GridMove(6, 7, 3, 1, 0)); // last tile
    }

    [Fact]
    public void DigitKeys()
    {
        Assert.Equal(0, QuickToolsLogic.DigitIndex(1, 5));
        Assert.Equal(-1, QuickToolsLogic.DigitIndex(6, 5));
        Assert.Equal(-1, QuickToolsLogic.DigitIndex(0, 5));
    }

    [Fact]
    public void FormatsBytes()
    {
        Assert.Equal("512 bytes", QuickToggleActions.FormatBytes(512));
        Assert.Equal("1.5 MB", QuickToggleActions.FormatBytes(1_500_000));
    }
}

public class CleaningUnlockCounterTests
{
    private const int Esc = 0x1B;

    private static bool Press(CleaningUnlockCounter c, int vk, long t)
    {
        var unlocked = c.Register(vk, true, t);
        c.Register(vk, false, t + 20);
        return unlocked;
    }

    [Fact]
    public void FivePressesUnlock()
    {
        var c = new CleaningUnlockCounter();
        for (var i = 0; i < 4; i++) Assert.False(Press(c, Esc, i * 1000));
        Assert.Equal(4, c.Progress);
        Assert.True(Press(c, Esc, 4000));
        Assert.Equal(0, c.Progress);
    }

    [Fact]
    public void SlowPressesRestart()
    {
        var c = new CleaningUnlockCounter();
        Press(c, Esc, 0);
        Press(c, Esc, 1000);
        Press(c, Esc, 8000); // more than 6 s after the previous one
        Assert.Equal(1, c.Progress);
    }

    [Fact]
    public void OtherKeysReset()
    {
        var c = new CleaningUnlockCounter();
        Press(c, Esc, 0);
        Press(c, Esc, 100);
        Press(c, 0x41, 200);
        Assert.Equal(0, c.Progress);
    }

    [Fact]
    public void AutoRepeatIsIgnored()
    {
        var c = new CleaningUnlockCounter();
        c.Register(Esc, true, 0);
        for (var t = 30; t < 1000; t += 30) c.Register(Esc, true, t); // held down
        Assert.Equal(1, c.Progress);
    }
}

public class ScratchpadTests
{
    [Fact]
    public void NamesAreCleaned()
    {
        Assert.Equal("My  notes".Replace("  ", " "), ScratchpadLogic.CleanName("  My \t notes "));
        Assert.Null(ScratchpadLogic.CleanName("   "));
        Assert.Equal(40, ScratchpadLogic.CleanName(new string('x', 60))!.Length);
    }

    [Fact]
    public void NextNameIsTheFirstFreeNumber()
    {
        Assert.Equal("Scratchpad 1", ScratchpadLogic.NextName([]));
        Assert.Equal("Scratchpad 2", ScratchpadLogic.NextName(["Scratchpad 1", "Ideas"]));
        var full = Enumerable.Range(1, 12).Select(i => $"Scratchpad {i}").ToList();
        Assert.Equal("Scratchpad 13", ScratchpadLogic.NextName(full));
    }

    [Fact]
    public void SanitizingRepairsTheDocument()
    {
        var doc = new ScratchpadDocument
        {
            Pads = Enumerable.Range(0, 14).Select(i => new ScratchPad { Id = i == 1 ? "0" : i.ToString(), Name = i == 2 ? " " : $"P{i}" }).ToList(),
            SelectedId = "missing",
        };
        var clean = ScratchpadLogic.Sanitized(doc);
        Assert.Equal(12, clean.Pads.Count);
        Assert.Equal(clean.Pads.Count, clean.Pads.Select(p => p.Id).Distinct().Count());
        Assert.All(clean.Pads, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.Equal(clean.Pads[0].Id, clean.SelectedId);
        Assert.Single(ScratchpadLogic.Sanitized(null).Pads);
    }

    [Fact]
    public void RetentionEmptiesStalePadsOnly()
    {
        var now = new DateTime(2026, 10, 14);
        var doc = new ScratchpadDocument
        {
            Pads =
            [
                new ScratchPad { Name = "old", Text = "x", ModifiedAt = now.AddDays(-2) },
                new ScratchPad { Name = "new", Text = "y", ModifiedAt = now.AddHours(-2) },
                new ScratchPad { Name = "undated", Text = "z" },
            ],
        };
        Assert.False(ScratchpadLogic.ApplyRetention(doc, ScratchpadRetention.Never, now));
        Assert.True(ScratchpadLogic.ApplyRetention(doc, ScratchpadRetention.Day, now));
        Assert.Equal("", doc.Pads[0].Text);
        Assert.Equal("old", doc.Pads[0].Name);
        Assert.Equal("y", doc.Pads[1].Text);
        Assert.Equal("z", doc.Pads[2].Text);
        Assert.Equal(ScratchpadRetention.Never, ScratchpadLogic.ParseRetention("garbage"));
    }

    [Fact]
    public void FileNamesAreSafe()
    {
        Assert.Equal("a-b-c 2026-10-14.txt", ScratchpadLogic.SuggestedFileName("a/b:c", new DateTime(2026, 10, 14)));
        Assert.Equal(13, ScratchpadLogic.ClampTextSize(double.NaN));
        Assert.Equal(22, ScratchpadLogic.ClampTextSize(40));
        Assert.Equal(1, ScratchpadLogic.SelectionAfterClose(3, 2));
    }

    [Theory]
    [InlineData(FormatMark.Bold, "hello world", 6, 5, "hello **world**", 8, 5)]
    [InlineData(FormatMark.Bold, "hello **world**", 8, 5, "hello world", 6, 5)]
    [InlineData(FormatMark.Bold, "hello **world**", 6, 9, "hello world", 6, 5)]
    [InlineData(FormatMark.Italic, "a word b", 2, 4, "a *word* b", 3, 4)]
    [InlineData(FormatMark.Italic, "a **word** b", 4, 4, "a ***word*** b", 5, 4)]
    [InlineData(FormatMark.Italic, "a ***word*** b", 5, 4, "a **word** b", 4, 4)]
    [InlineData(FormatMark.Strikethrough, "x", 0, 1, "~~x~~", 2, 1)]
    [InlineData(FormatMark.Code, "run it", 0, 0, "``run it", 1, 0)]
    [InlineData(FormatMark.Bold, "a  word  b", 1, 7, "a  **word**  b", 5, 4)]
    public void InlineMarksToggle(FormatMark mark, string text, int start, int length, string expected, int selStart, int selLength)
    {
        var edit = ScratchpadFormat.Apply(mark, text, start, length);
        Assert.Equal(expected, edit.Text);
        Assert.Equal(selStart, edit.SelectionStart);
        Assert.Equal(selLength, edit.SelectionLength);
    }

    [Fact]
    public void HeadingCycles()
    {
        var t = "Title";
        t = ScratchpadFormat.Apply(FormatMark.Heading, t, 0, 0).Text;
        Assert.Equal("# Title", t);
        t = ScratchpadFormat.Apply(FormatMark.Heading, t, 2, 0).Text;
        Assert.Equal("## Title", t);
        t = ScratchpadFormat.Apply(FormatMark.Heading, t, 2, 0).Text;
        Assert.Equal("### Title", t);
        t = ScratchpadFormat.Apply(FormatMark.Heading, t, 2, 0).Text;
        Assert.Equal("Title", t);
    }

    [Fact]
    public void ListMarksApplyToEveryTouchedLineAndToggleOff()
    {
        var text = "one\n\ntwo\n  three";
        var bulleted = ScratchpadFormat.Apply(FormatMark.Bullet, text, 0, text.Length).Text;
        Assert.Equal("- one\n\n- two\n  - three", bulleted);
        Assert.Equal(text, ScratchpadFormat.Apply(FormatMark.Bullet, bulleted, 0, bulleted.Length).Text);
        var numbered = ScratchpadFormat.Apply(FormatMark.Numbered, bulleted, 0, bulleted.Length).Text;
        Assert.Equal("1. one\n\n2. two\n  3. three", numbered);
        Assert.Equal("> a\n> b", ScratchpadFormat.Apply(FormatMark.Quote, "a\nb", 0, 3).Text);
    }

    [Fact]
    public void LinksWrapAndUnwrap()
    {
        var edit = ScratchpadFormat.Apply(FormatMark.Link, "see docs", 4, 4);
        Assert.Equal("see [docs](url)", edit.Text);
        Assert.Equal("url", edit.Text.Substring(edit.SelectionStart, edit.SelectionLength));
        Assert.Equal("see docs", ScratchpadFormat.Apply(FormatMark.Link, "see [docs](https://x.y)", 6, 0).Text);
        Assert.Equal("![a](b) [](url)", ScratchpadFormat.Apply(FormatMark.Link, "![a](b) ", 8, 0).Text);
    }
}

public class MarkdownTests
{
    [Fact]
    public void ParsesBlocks()
    {
        var blocks = MarkdownLite.Parse("# Title\n\npara one\nline two\n\n- a\n  - b\n1. x\n> quote\n---\n```\ncode\n```");
        Assert.Equal(
            [MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Ordered, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Code],
            blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("para one\nline two", blocks[1].Text);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal("code", blocks[7].Text);
    }

    [Fact]
    public void ParsesInlineStyles()
    {
        var spans = MarkdownLite.ParseInline("a **b** *c* ~~d~~ `e` [f](https://g)");
        Assert.Contains(spans, s => s.Text == "b" && s.Style == MdStyle.Bold);
        Assert.Contains(spans, s => s.Text == "c" && s.Style == MdStyle.Italic);
        Assert.Contains(spans, s => s.Text == "d" && s.Style == MdStyle.Strike);
        Assert.Contains(spans, s => s.Text == "e" && s.Style.HasFlag(MdStyle.Code));
        Assert.Contains(spans, s => s.Text == "f" && s.Url == "https://g");
    }

    [Fact]
    public void UnmatchedMarkersStayLiteral()
    {
        var spans = MarkdownLite.ParseInline("2 * 3 = 6");
        Assert.Equal("2 * 3 = 6", string.Concat(spans.Select(s => s.Text)));
        Assert.All(spans, s => Assert.Equal(MdStyle.None, s.Style));
    }
}

public class ProcessLogicTests
{
    private static ProcessRow Row(uint pid, string name, double cpu = 0, long mem = 0, string path = "", uint parent = 0, long created = 0) =>
        new(pid, parent, name, path, cpu, mem, created, false, [pid]);

    [Fact]
    public void ProtectsSystemProcesses()
    {
        Assert.True(ProcessLogic.IsProtected(4, "System", 999));
        Assert.True(ProcessLogic.IsProtected(500, "csrss.exe", 999));
        Assert.True(ProcessLogic.IsProtected(999, "notepad", 999));
        Assert.False(ProcessLogic.IsProtected(500, "notepad.exe", 999));
    }

    [Fact]
    public void SortsWithPidTieBreak()
    {
        var rows = new[] { Row(30, "b", 1), Row(10, "a", 5), Row(20, "c", 1) };
        Assert.Equal([10u, 20u, 30u], ProcessLogic.Sorted(rows, ProcessSort.Cpu, ascending: false).Select(r => r.Pid));
        Assert.Equal([10u, 30u, 20u], ProcessLogic.Sorted(rows, ProcessSort.Name, ascending: true).Select(r => r.Pid));
        Assert.True(ProcessLogic.NaturalAscending(ProcessSort.Name));
        Assert.False(ProcessLogic.NaturalAscending(ProcessSort.Memory));
    }

    [Fact]
    public void FilterMatchesNameOrExactPid()
    {
        var row = Row(1234, "Chrome");
        Assert.True(ProcessLogic.Matches(row, " chr "));
        Assert.True(ProcessLogic.Matches(row, "1234"));
        Assert.False(ProcessLogic.Matches(row, "123"));
    }

    [Fact]
    public void DescendantsAreBreadthFirstAndCycleSafe()
    {
        var parents = new Dictionary<uint, uint> { [2] = 1, [3] = 1, [4] = 2, [5] = 4, [1] = 5 };
        Assert.Equal([2u, 3u, 4u, 5u], ProcessLogic.Descendants(1, parents));
    }

    [Fact]
    public void GroupsByExecutable()
    {
        var rows = new[]
        {
            Row(10, "chrome", 1, 100, @"C:\c\chrome.exe", created: 5),
            Row(11, "chrome", 2, 200, @"C:\C\Chrome.exe", created: 3),
            Row(12, "notepad", 0, 50, @"C:\n.exe"),
        };
        var grouped = ProcessLogic.Grouped(rows);
        Assert.Equal(2, grouped.Count);
        var chrome = grouped.Single(r => r.Name == "chrome");
        Assert.Equal(11u, chrome.Pid); // oldest
        Assert.Equal(3, chrome.Cpu);
        Assert.Equal(300, chrome.Memory);
        Assert.Equal(2, chrome.Count);
    }

    [Fact]
    public void CpuPercentIsShareOfTheMachine()
    {
        Assert.Equal(50, ProcessLogic.CpuPercent(10_000_000, 10_000_000, 2));
        Assert.Equal(0, ProcessLogic.CpuPercent(5, 0, 4));
        Assert.Equal("1.5 MB", ProcessLogic.FormatMemory(1_572_864));
    }

    [Fact]
    public void SnapshotSeesThisProcess()
    {
        var samples = ProcessSnapshot.Take();
        Assert.Contains(samples, s => s.Pid == (uint)Environment.ProcessId);
        Assert.All(samples.Where(s => s.Pid != 0), s => Assert.False(string.IsNullOrEmpty(s.Name)));
    }
}

public class PortLogicTests
{
    private static PortEntry Entry(string host, int port, string proto = "TCP", uint pid = 100, string name = "node") =>
        new(proto, host, port, pid, name, 1);

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("[::]", true)]
    [InlineData("*", true)]
    [InlineData("127.0.0.1", false)]
    public void AllInterfaces(string host, bool expected) => Assert.Equal(expected, PortLogic.ListensOnAllInterfaces(host));

    [Fact]
    public void BrowserUrls()
    {
        Assert.Equal("http://127.0.0.1:3000", PortLogic.BrowserUrl(Entry("0.0.0.0", 3000)));
        Assert.Equal("http://[::1]:8080", PortLogic.BrowserUrl(Entry("::", 8080)));
        Assert.Equal("http://localhost:5173", PortLogic.BrowserUrl(Entry("*", 5173)));
        Assert.Equal("http://[fe80::1]:80", PortLogic.BrowserUrl(Entry("fe80::1", 80)));
        Assert.Null(PortLogic.BrowserUrl(Entry("0.0.0.0", 53, "UDP")));
    }

    [Fact]
    public void AddressesAndFilter()
    {
        Assert.Equal("[::]:443", Entry("::", 443).Address);
        Assert.True(PortLogic.Matches(Entry("127.0.0.1", 3000), "300"));
        Assert.True(PortLogic.Matches(Entry("127.0.0.1", 3000), "NODE"));
        Assert.True(PortLogic.Matches(Entry("127.0.0.1", 3000), "127.0.0.1:3000"));
        Assert.False(PortLogic.Matches(Entry("127.0.0.1", 3000), "python"));
    }

    [Fact]
    public void NormalizesAndSorts()
    {
        var list = PortLogic.Normalized([Entry("0.0.0.0", 8080), Entry("0.0.0.0", 80, name: "b"), Entry("0.0.0.0", 8080), Entry("::", 80, name: "a"), Entry("x", 0)]);
        Assert.Equal([80, 80, 8080], list.Select(e => e.Port));
        Assert.Equal("a", list[0].ProcessName);
    }

    [Fact]
    public void PortByteOrder() => Assert.Equal(3000, PortLogic.PortFromNetwork(0xB80B)); // 3000 = 0x0BB8

    [Fact]
    public void ReadsTheTablesWithoutThrowing()
    {
        var entries = PortTable.Read(includeUdp: true);
        Assert.All(entries, e => Assert.InRange(e.Port, 1, 65535));
    }
}

public class ToolPreviewTests
{
    /// <summary>Renders the tool windows with sample data (STA, theme loaded) so they can be reviewed as PNGs.</summary>
    [Fact]
    public void RendersToolWindowPreviews()
    {
        IReadOnlyList<string> files = [];
        var dir = Path.Combine(Environment.GetEnvironmentVariable("FIELDKIT_DATA_DIR") is { Length: > 0 } d ? d : Path.GetTempPath(), "previews");
        Fieldkit.Tests.Support.WpfTestHost.Run(() => files = ToolPreviews.RenderAll(dir));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 1000, f));
    }
}
