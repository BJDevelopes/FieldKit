// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Features.WindowTools;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Windows;

public class EdgeSnapTests
{
    private static readonly SnapScreen Main = new(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1032));
    private static readonly SnapScreen Right = new(new Rect(1920, 0, 1920, 1080), new Rect(1920, 0, 1920, 1080));
    private static readonly HashSet<EdgeZone> All = Enum.GetValues<EdgeZone>().ToHashSet();

    private static SnapTarget? At(double x, double y, Vector velocity = default, ISet<EdgeZone>? zones = null, EdgeSnapLayout? layout = null) =>
        EdgeSnap.Target(new Point(x, y), [Main, Right], velocity, zones ?? All, layout ?? new EdgeSnapLayout(),
            (a, work) => LayoutGeometry.RectFor(a, work, work));

    [Fact]
    public void FindsEdgesAndCorners()
    {
        Assert.Equal(EdgeZone.Top, At(960, 0)?.Zone);
        Assert.Equal(LayoutAction.Maximize, At(960, 0)?.Action);
        Assert.Equal(EdgeZone.TopLeft, At(5, 0)?.Zone);
        Assert.Equal(EdgeZone.Left, At(0, 500)?.Zone);
        Assert.Equal(EdgeZone.BottomLeft, At(0, 1075)?.Zone);
        Assert.Equal(EdgeZone.Bottom, At(960, 1079)?.Zone);
        Assert.Null(At(960, 500));
    }

    [Fact]
    public void SeamsCountOnlyWhenThepointerSlowsDown()
    {
        Assert.Equal(EdgeZone.Right, At(1915, 500, new Vector(0, 0))?.Zone);
        Assert.Null(At(1915, 500, new Vector(2000, 0)));
        // The far right edge of the right display is a wall: any speed counts.
        Assert.Equal(EdgeZone.Right, At(3839, 500, new Vector(5000, 0))?.Zone);
    }

    [Fact]
    public void DisabledZonesAndTargetFrames()
    {
        var zones = All.ToHashSet();
        zones.Remove(EdgeZone.Top);
        Assert.Null(At(960, 0, zones: zones));
        var left = At(0, 500);
        Assert.Equal(new Rect(0, 0, 960, 1032), left?.Frame);
        Assert.Equal("top,left", EdgeSnap.DisabledZonesStorage([EdgeZone.Left, EdgeZone.Top]));
        Assert.Equal([EdgeZone.Top, EdgeZone.Left], EdgeSnap.DisabledZones("top,left,bogus").OrderBy(z => z));
    }

    [Fact]
    public void SplitEdgesPickTheAreaAlongTheEdge()
    {
        var layout = new EdgeSnapLayout();
        layout.SetPartCount(EdgeZone.Top, 3);
        Assert.Equal("top=leftThird+centerThird+rightThird", layout.Storage);
        Assert.Equal(LayoutAction.LeftThird, At(400, 0, layout: layout)?.Action);
        Assert.Equal(LayoutAction.CenterThird, At(960, 0, layout: layout)?.Action);
        Assert.Equal(LayoutAction.RightThird, At(1600, 0, layout: layout)?.Action);
        layout.SetAction(EdgeZone.Top, 1, LayoutAction.Maximize);
        var parsed = EdgeSnapLayout.Parse(layout.Storage);
        Assert.Equal([LayoutAction.LeftThird, LayoutAction.Maximize, LayoutAction.RightThird], parsed.Actions(EdgeZone.Top));
        layout.SetPartCount(EdgeZone.Top, 1);
        Assert.Equal(string.Empty, layout.Storage);
        // Bad entries keep the default.
        Assert.Equal([LayoutAction.LeftHalf], EdgeSnapLayout.Parse("left=center,right=nope").Actions(EdgeZone.Left));
        Assert.Equal([LayoutAction.TopLeft], EdgeSnapLayout.Parse("topLeft=leftHalf+rightHalf").Actions(EdgeZone.TopLeft));
    }

    [Fact]
    public void ResizeIsTellingApartFromMove()
    {
        var start = new Rect(100, 100, 800, 600);
        Assert.True(EdgeSnap.IsResize(start, new Rect(100, 100, 900, 600)));       // right edge dragged
        Assert.True(EdgeSnap.IsResize(start, new Rect(50, 50, 850, 650)));        // top-left corner dragged
        Assert.False(EdgeSnap.IsResize(start, new Rect(300, 200, 800, 600)));     // moved
        Assert.False(EdgeSnap.IsResize(start, new Rect(500, 20, 1000, 700)));     // restored from a tile while moving
    }
}

public class DragGestureTests
{
    [Fact]
    public void ClickIsGivenBackDragIsPromoted()
    {
        Assert.Equal(GestureDecision.Arm, DragGestureMath.Decide(GestureState.Idle, GestureInput.ButtonDownMatched));
        Assert.Equal(GestureDecision.PassThrough, DragGestureMath.Decide(GestureState.Idle, GestureInput.ButtonDownUnmatched));
        Assert.Equal(GestureDecision.Hold, DragGestureMath.Decide(GestureState.Pending, GestureInput.DragWithinSlop));
        Assert.Equal(GestureDecision.Promote, DragGestureMath.Decide(GestureState.Pending, GestureInput.DragPastSlop));
        Assert.Equal(GestureDecision.ReplayClick, DragGestureMath.Decide(GestureState.Pending, GestureInput.TrackedUp));
        Assert.Equal(GestureDecision.FlushThenPass, DragGestureMath.Decide(GestureState.Pending, GestureInput.OtherButtonDown));
        Assert.Equal(GestureDecision.Restart, DragGestureMath.Decide(GestureState.Pending, GestureInput.SameButtonDown));
        Assert.Equal(GestureDecision.Apply, DragGestureMath.Decide(GestureState.Active, GestureInput.DragPastSlop));
        Assert.Equal(GestureDecision.Finish, DragGestureMath.Decide(GestureState.Active, GestureInput.TrackedUp));
        Assert.True(DragGestureMath.ExceedsSlop(new Point(0, 0), new Point(5, 5)));
        Assert.False(DragGestureMath.ExceedsSlop(new Point(0, 0), new Point(3, 3)));
    }

    [Fact]
    public void ModifiersNeedAPrimaryKey()
    {
        Assert.Equal(Modifiers.Ctrl | Modifiers.Win, DragGestureMath.ParseModifiers(null));
        Assert.Equal(Modifiers.Alt, DragGestureMath.ParseModifiers("alt"));
        Assert.Equal(DragGestureMath.DefaultModifiers, DragGestureMath.ParseModifiers("shift"));
        Assert.Equal(DragGestureMath.DefaultModifiers, DragGestureMath.ParseModifiers("hyper"));
        Assert.Equal("ctrl+alt", DragGestureMath.ModifiersStorage(Modifiers.Ctrl | Modifiers.Alt | Modifiers.Shift));
        Assert.Equal("Ctrl+Shift+Win", DragGestureMath.ModifiersText(Modifiers.Ctrl | Modifiers.Win | Modifiers.Shift));
    }

    [Fact]
    public void NineRegionsChooseEdges()
    {
        var frame = new Rect(0, 0, 900, 600);
        Assert.Equal(ResizeEdges.Left | ResizeEdges.Top, DragGestureMath.EdgesAt(new Point(10, 10), frame));
        Assert.Equal(ResizeEdges.Right, DragGestureMath.EdgesAt(new Point(890, 300), frame));
        Assert.Equal(ResizeEdges.Bottom, DragGestureMath.EdgesAt(new Point(450, 590), frame));
        Assert.Equal(ResizeEdges.Top, DragGestureMath.EdgesAt(new Point(450, 201), frame));
    }

    [Fact]
    public void ResizeKeepsTheOppositeEdgeAndAMinimum()
    {
        var original = new Rect(100, 100, 800, 600);
        var r = DragGestureMath.Resized(original, new Point(100, 100), new Point(150, 80), ResizeEdges.Left | ResizeEdges.Top);
        Assert.Equal(new Rect(150, 80, 750, 620), r);
        var tiny = DragGestureMath.Resized(original, new Point(900, 700), new Point(0, 0), ResizeEdges.Right | ResizeEdges.Bottom);
        Assert.Equal(new Size(120, 80), tiny.Size);
        Assert.Equal(new Rect(110, 120, 800, 600), DragGestureMath.Moved(original, new Point(5, 5), new Point(15, 25)));
    }
}

public class TaskbarTests
{
    private static string? Folder(Guid id) =>
        id == new Guid("6D809377-6AF0-444B-8957-A3773F02200E") ? @"C:\Program Files" : null;

    [Theory]
    [InlineData("Google Chrome - 1 running window pinned", "Google Chrome", 1)]
    [InlineData("File Explorer - 3 running windows", "File Explorer", 3)]
    [InlineData("Spotify pinned", "Spotify", 0)]
    [InlineData("Notepad", "Notepad", 0)]
    public void ParsesButtonNames(string name, string app, int count)
    {
        Assert.Equal(app, TaskbarAppMatcher.AppName(name));
        Assert.Equal(count, TaskbarAppMatcher.RunningCount(name));
    }

    [Fact]
    public void ParsesAppIdsAndKnownFolders()
    {
        Assert.Equal("Chrome", TaskbarAppMatcher.ParseAutomationId("Appid: Chrome"));
        Assert.Equal(@"D:\TigerVNC\vncviewer.exe", TaskbarAppMatcher.ParseAutomationId(@"Appid: D:\TigerVNC\vncviewer.exe"));
        Assert.Equal(@"C:\Program Files\Google\Chrome\chrome.exe",
            TaskbarAppMatcher.ResolvePath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Google\Chrome\chrome.exe", Folder));
        Assert.Null(TaskbarAppMatcher.ResolvePath("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", Folder));
    }

    [Fact]
    public void MatchesWindowsToButtons()
    {
        var chrome = new TaskbarButtonInfo("Chrome", "Google Chrome - 2 running windows pinned", Rect.Empty);
        Assert.True(TaskbarAppMatcher.Matches(chrome,
            new WindowAppIdentity("Chrome", null, @"C:\Program Files\Google\Chrome\chrome.exe", "Google Chrome", "Chrome_WidgetWin_1"), Folder));
        Assert.False(TaskbarAppMatcher.Matches(chrome,
            new WindowAppIdentity("Chrome.Profile2", null, @"C:\Program Files\Google\Chrome\chrome.exe", "Google Chrome", "Chrome_WidgetWin_1"), Folder));

        var packaged = new TaskbarButtonInfo("Claude_pzs8sxrjxfjjc!Claude", "Claude - 1 running window pinned", Rect.Empty);
        Assert.True(TaskbarAppMatcher.Matches(packaged, new WindowAppIdentity(null, "Claude_pzs8sxrjxfjjc!Claude", @"C:\x\claude.exe", "Claude", "X"), Folder));

        var byPath = new TaskbarButtonInfo(@"D:\TigerVNC\vncviewer.exe", "TigerVNC", Rect.Empty);
        Assert.True(TaskbarAppMatcher.Matches(byPath, new WindowAppIdentity(null, null, @"d:\tigervnc\VNCVIEWER.exe", "VNC Viewer", "X"), Folder));

        var explorer = new TaskbarButtonInfo("Microsoft.Windows.Explorer", "File Explorer - 1 running window", Rect.Empty);
        Assert.True(TaskbarAppMatcher.Matches(explorer, new WindowAppIdentity(null, null, @"C:\Windows\explorer.exe", "Windows Explorer", "CabinetWClass"), Folder));

        // An app id only known to the process (Squirrel apps): fall back to the name.
        var discord = new TaskbarButtonInfo("com.squirrel.Discord.Discord", "Discord - 1 running window", Rect.Empty);
        Assert.True(TaskbarAppMatcher.Matches(discord, new WindowAppIdentity(null, null, @"C:\Users\u\AppData\Local\Discord\app-1\Discord.exe", "Discord", "X"), Folder));
        Assert.False(TaskbarAppMatcher.Matches(discord, new WindowAppIdentity(null, null, @"C:\x\slack.exe", "Slack", "X"), Folder));
    }

    [Fact]
    public void ClickDecision()
    {
        TaskbarClickAction Act(bool front, bool unmin, bool min, bool minimize = true, bool cycle = false, int count = 1, bool owns = false,
            bool mods = false, bool full = false) =>
            TaskbarClickLogic.Action(front, unmin, min, full, mods, minimize, cycle, count, owns);

        Assert.Equal(TaskbarClickAction.Minimize, Act(front: true, unmin: true, min: false));
        Assert.Equal(TaskbarClickAction.PassThrough, Act(front: false, unmin: true, min: false));
        Assert.Equal(TaskbarClickAction.Restore, Act(front: false, unmin: false, min: true, owns: true));
        Assert.Equal(TaskbarClickAction.PassThrough, Act(front: false, unmin: false, min: true, owns: false));
        Assert.Equal(TaskbarClickAction.CycleWindows, Act(front: true, unmin: true, min: false, cycle: true, count: 2));
        Assert.Equal(TaskbarClickAction.Minimize, Act(front: true, unmin: true, min: false, cycle: true, count: 1));
        Assert.Equal(TaskbarClickAction.PassThrough, Act(front: true, unmin: true, min: false, mods: true));
        Assert.Equal(TaskbarClickAction.PassThrough, Act(front: true, unmin: true, min: false, full: true));
        Assert.Equal(TaskbarClickAction.PassThrough, Act(front: true, unmin: true, min: false, minimize: false));
    }
}

public class AutoQuitTests
{
    [Fact]
    public void QuitsOnlyAppsThatHadAWindowAndPassTheScope()
    {
        IReadOnlyCollection<string> exceptions = ["zoom.exe"];
        IReadOnlyCollection<string> listed = ["discord.exe"];
        bool Quit(string key, bool system = false, bool had = true, int remaining = 0, AutoQuitScope scope = AutoQuitScope.AllExceptExceptions) =>
            AutoQuitLogic.ShouldQuit(key, system, had, remaining, scope, exceptions, listed);

        Assert.True(Quit("discord.exe"));
        Assert.False(Quit("zoom.exe"));
        Assert.False(Quit("discord.exe", remaining: 1));
        Assert.False(Quit("discord.exe", had: false));
        Assert.False(Quit("explorer.exe", system: true));
        Assert.True(Quit("discord.exe", scope: AutoQuitScope.OnlyListed));
        Assert.False(Quit("steam.exe", scope: AutoQuitScope.OnlyListed));
        Assert.Equal(AutoQuitScope.OnlyListed, AutoQuitLogic.ParseScope("listed"));
        Assert.Equal(AutoQuitScope.AllExceptExceptions, AutoQuitLogic.ParseScope(null));
    }

    [Fact]
    public void AppKeysAndSystemProcesses()
    {
        Assert.Equal("chrome.exe", AppNames.Key(@"C:\Program Files\Google\Chrome\Application\Chrome.EXE"));
        Assert.Equal("notepad.exe", AppNames.Key("notepad"));
        Assert.Equal(string.Empty, AppNames.Key(" "));
        Assert.True(AppNames.IsSystemProcess(@"C:\Windows\explorer.exe"));
        Assert.True(AppNames.IsSystemProcess(string.Empty));
        Assert.False(AppNames.IsSystemProcess(@"C:\Users\u\AppData\Local\Discord\Discord.exe"));
    }
}

public class ThumbnailAndEnumerationTests
{
    [Fact]
    public void AspectFitNeverUpscales()
    {
        Assert.Equal(new Rect(10, 35, 200, 100), ThumbnailMath.AspectFit(1600, 800, new Rect(10, 10, 200, 150)));
        Assert.Equal(new Rect(60, 35, 100, 100), ThumbnailMath.AspectFit(100, 100, new Rect(10, 10, 200, 150)));
    }

    [Fact]
    public void EnumeratesWindowsReadOnly()
    {
        // Read-only: lists windows without touching them.
        var windows = WindowEnumerator.List(includeOtherDesktops: true);
        Assert.All(windows, w => Assert.NotEqual(IntPtr.Zero, w.Handle));
        Assert.DoesNotContain(windows, w => w.ProcessId == (uint)Environment.ProcessId);
    }
}
