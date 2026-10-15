// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Input.Acceleration;
using Fieldkit.Features.Input.Buttons;
using Fieldkit.Features.Input.Common;
using Fieldkit.Features.Input.Debounce;
using Fieldkit.Features.Input.Focus;
using Fieldkit.Features.Input.QuitProtection;
using Fieldkit.Features.Input.SuperKey;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Input;

public class FocusFollowsMouseStateTests
{
    [Fact]
    public void WaitsForTheDelayAfterTheLastMove()
    {
        var s = new FocusFollowsMouseState();
        s.RecordMovement(10, 10, 0);
        Assert.Null(s.NextEvaluation(200, 250));
        s.RecordMovement(20, 20, 200);
        Assert.Null(s.NextEvaluation(400, 250));
        var e = s.NextEvaluation(460, 250);
        Assert.NotNull(e);
        Assert.Equal(20, e!.Value.X);
        s.FinishEvaluation(e.Value, true);
        Assert.False(s.HasPendingEvaluation);
    }

    [Fact]
    public void DelayIsClamped()
    {
        var s = new FocusFollowsMouseState();
        s.RecordMovement(0, 0, 0);
        Assert.NotNull(s.NextEvaluation(100, 1)); // clamped up to the 100 ms minimum
    }

    [Fact]
    public void MovingInsideTheSameWindowKeepsTheTimer()
    {
        var s = new FocusFollowsMouseState();
        var w = new IntPtr(42);
        s.RecordMovement(0, 0, 0, w);
        s.RecordMovement(5, 5, 200, w);
        Assert.NotNull(s.NextEvaluation(260, 250));
    }

    [Fact]
    public void FailedAttemptRetriesWhenThePointerMovedMeanwhile()
    {
        var s = new FocusFollowsMouseState();
        var w = new IntPtr(1);
        s.RecordMovement(0, 0, 0, w);
        var e = s.NextEvaluation(300, 250)!.Value;
        s.RecordMovement(3, 3, 310, w);
        s.FinishEvaluation(e, false);
        Assert.True(s.HasPendingEvaluation);
    }

    [Fact]
    public void ShellWindowsAreNeverFocused()
    {
        Assert.True(FocusFollowsMouseRules.IsShellClass("Shell_TrayWnd"));
        Assert.True(FocusFollowsMouseRules.IsShellClass("#32768"));
        Assert.True(FocusFollowsMouseRules.IsShellClass("Progman"));
        Assert.False(FocusFollowsMouseRules.IsShellClass("Chrome_WidgetWin_1"));
    }
}

public class DebounceTests
{
    [Fact]
    public void HealthyClicksPass()
    {
        var s = new ClickDebounceState();
        Assert.False(s.ShouldSuppress(0, ClickPhase.Down, 1000, 25));
        Assert.False(s.ShouldSuppress(0, ClickPhase.Up, 1080, 25));
        Assert.False(s.ShouldSuppress(0, ClickPhase.Down, 1200, 25)); // a real double click
        Assert.False(s.ShouldSuppress(0, ClickPhase.Up, 1280, 25));
    }

    [Fact]
    public void BounceIsDroppedWithItsOwnRelease()
    {
        var s = new ClickDebounceState();
        Assert.False(s.ShouldSuppress(1, ClickPhase.Down, 1000, 25));
        Assert.False(s.ShouldSuppress(1, ClickPhase.Up, 1050, 25));
        Assert.True(s.ShouldSuppress(1, ClickPhase.Down, 1060, 25));
        Assert.True(s.ShouldSuppress(1, ClickPhase.Up, 1065, 25));
        Assert.False(s.ShouldSuppress(1, ClickPhase.Down, 1200, 25));
    }

    [Fact]
    public void AcceptedReleaseIsNeverDropped()
    {
        var s = new ClickDebounceState();
        Assert.False(s.ShouldSuppress(2, ClickPhase.Down, 0, 25));
        Assert.True(s.ShouldSuppress(2, ClickPhase.Down, 3, 25)); // duplicate down while held
        Assert.False(s.ShouldSuppress(2, ClickPhase.Up, 50, 25));
        Assert.False(s.ShouldSuppress(2, ClickPhase.Up, 60, 25)); // unmatched up passes
    }

    [Fact]
    public void ButtonsAreIndependent()
    {
        var s = new ClickDebounceState();
        s.ShouldSuppress(0, ClickPhase.Down, 0, 25);
        s.ShouldSuppress(0, ClickPhase.Up, 10, 25);
        Assert.False(s.ShouldSuppress(1, ClickPhase.Down, 15, 25));
    }

    [Fact]
    public void KeyChatterIsDropped()
    {
        var s = new KeyDebounceState();
        Assert.False(s.ShouldSuppress('A', true, 1000, 30));
        Assert.False(s.ShouldSuppress('A', false, 1040, 30));
        Assert.True(s.ShouldSuppress('A', true, 1050, 30));
        Assert.True(s.ShouldSuppress('A', false, 1055, 30));
        Assert.False(s.ShouldSuppress('A', true, 1200, 30));
    }

    [Fact]
    public void AutoRepeatPassesEvenAfterADroppedBounce()
    {
        var s = new KeyDebounceState();
        s.ShouldSuppress('B', true, 0, 30);
        s.ShouldSuppress('B', false, 10, 30);
        Assert.True(s.ShouldSuppress('B', true, 15, 30));
        Assert.False(s.ShouldSuppress('B', true, 500, 30)); // repeat: the key really is held
        Assert.False(s.ShouldSuppress('B', true, 533, 30));
        Assert.False(s.ShouldSuppress('B', false, 600, 30));
    }

    [Fact]
    public void ZeroWindowDisablesAKeyAndOtherKeysBreakTheChain()
    {
        var s = new KeyDebounceState();
        s.ShouldSuppress('C', true, 0, 0);
        s.ShouldSuppress('C', false, 5, 0);
        Assert.False(s.ShouldSuppress('C', true, 6, 0));

        var t = new KeyDebounceState();
        t.ShouldSuppress('D', true, 0, 50);
        t.ShouldSuppress('D', false, 10, 50);
        t.ShouldSuppress('E', true, 12, 50);
        Assert.False(t.ShouldSuppress('D', true, 20, 50)); // "ded" typed fast is not chatter
    }

    [Fact]
    public void KeyWindowsRoundTrip()
    {
        var windows = KeyWindowCodec.Decode("65:20, 66:900, x:1, 300:5");
        Assert.Equal(2, windows.Count);
        Assert.Equal(500, windows[66]);
        Assert.Equal("65:20,66:500", KeyWindowCodec.Encode(windows));
        Assert.Equal(25, ClickDebounceState.SanitizedWindow(3));
        Assert.Equal(40, ClickDebounceState.SanitizedWindow(40));
    }
}

public class SuperKeyTests
{
    [Fact]
    public void TapAloneRunsTheSoloAction()
    {
        var s = new SuperKeyState();
        s.TriggerDown(false, 0);
        Assert.Equal(SuperKeyDecision.SoloTap, s.TriggerUp(100));
    }

    [Fact]
    public void LongHoldAloneIsASoloHold()
    {
        var s = new SuperKeyState();
        s.TriggerDown(false, 0);
        s.TriggerDown(false, 300); // auto-repeat
        Assert.Equal(SuperKeyDecision.SoloHold, s.TriggerUp(700));
        Assert.True(s.Repeated);
    }

    [Fact]
    public void AnotherKeyAddsModifiersAndCancelsTheSoloAction()
    {
        var s = new SuperKeyState();
        s.TriggerDown(false, 0);
        Assert.Equal(SuperKeyDecision.AddModifiers, s.OtherKey());
        Assert.Equal(SuperKeyDecision.Swallow, s.TriggerUp(100));
        Assert.Equal(SuperKeyDecision.Pass, s.OtherKey());
    }

    [Fact]
    public void ModifierHeldAtPressMeansNoSoloAction()
    {
        var s = new SuperKeyState();
        s.TriggerDown(true, 0);
        Assert.Equal(SuperKeyDecision.Swallow, s.TriggerUp(100));
        s.TriggerDown(false, 200);
        s.OtherModifier();
        Assert.Equal(SuperKeyDecision.Swallow, s.TriggerUp(300));
    }

    [Theory]
    [InlineData("ctrl+alt+shift+win", Modifiers.Ctrl | Modifiers.Alt | Modifiers.Shift | Modifiers.Win)]
    [InlineData("control+option", Modifiers.Ctrl | Modifiers.Alt)]
    [InlineData("shift", SuperKeyMath.DefaultModifiers)] // Shift alone is not a shortcut modifier
    [InlineData("bogus", SuperKeyMath.DefaultModifiers)]
    [InlineData(null, SuperKeyMath.DefaultModifiers)]
    public void ModifiersParse(string? stored, Modifiers expected) =>
        Assert.Equal(expected, SuperKeyMath.ParseModifiers(stored));

    [Fact]
    public void CodecsRoundTrip()
    {
        foreach (var source in SuperKeyMath.Sources)
            Assert.Equal(source, SuperKeyMath.ParseSource(SuperKeyMath.StorageValue(source)));
        Assert.Equal("ctrl+win", SuperKeyMath.StorageValue(Modifiers.Win | Modifiers.Ctrl));
        Assert.Equal("Ctrl+Alt+Shift+Win", SuperKeyMath.Display(SuperKeyMath.DefaultModifiers));
        Assert.Equal(SuperKeySource.RightWin, SuperKeyMath.ParseSource("rightCommand"));
        Assert.Equal(0x14, SuperKeyMath.VirtualKey(SuperKeySource.CapsLock));
    }

    [Fact]
    public void SoloEffects()
    {
        Assert.Equal(SuperKeySoloAction.Escape, SuperKeyMath.SoloEffect(SuperKeySoloAction.Escape, false, false));
        Assert.Equal(SuperKeySoloAction.None, SuperKeyMath.SoloEffect(SuperKeySoloAction.Escape, true, true));
        Assert.Equal(SuperKeySoloAction.CapsLock, SuperKeyMath.SoloEffect(SuperKeySoloAction.InputSource, true, true));
        Assert.Equal(SuperKeySoloAction.InputSource, SuperKeyMath.SoloEffect(SuperKeySoloAction.InputSource, false, false));
    }
}

public class QuitProtectionTests
{
    [Fact]
    public void OnlyTheExactCombinationsMatch()
    {
        Assert.Equal(ProtectedAction.Quit, QuitProtectionMath.MatchBase(0x73, Modifiers.Alt)!.Value.Action);
        Assert.Equal(ProtectedAction.Quit, QuitProtectionMath.MatchBase('Q', Modifiers.Ctrl)!.Value.Action);
        Assert.Equal(ProtectedAction.Close, QuitProtectionMath.MatchBase('W', Modifiers.Ctrl)!.Value.Action);
        Assert.Equal(ProtectedAction.Close, QuitProtectionMath.MatchBase(0x73, Modifiers.Ctrl)!.Value.Action);
        Assert.Null(QuitProtectionMath.MatchBase('W', Modifiers.Ctrl | Modifiers.Shift));
        Assert.Null(QuitProtectionMath.MatchBase('Q', Modifiers.None));
        Assert.Null(QuitProtectionMath.MatchBase(0x73, Modifiers.Alt | Modifiers.Ctrl));
    }

    [Fact]
    public void ExtraModifierFallsBackWhenTheShortcutAlreadyUsesIt()
    {
        var altF4 = QuitProtectionMath.Combos[0];
        Assert.Equal(Modifiers.Shift, QuitProtectionMath.EffectiveExtra(altF4, Modifiers.Alt));
        Assert.Equal(Modifiers.Ctrl, QuitProtectionMath.EffectiveExtra(altF4, Modifiers.Ctrl));
        var match = QuitProtectionMath.MatchExtra(0x73, Modifiers.Alt | Modifiers.Ctrl, _ => Modifiers.Ctrl);
        Assert.Equal(altF4, match);
        Assert.Null(QuitProtectionMath.MatchExtra('W', Modifiers.Ctrl, _ => Modifiers.Shift));
        Assert.Equal(ProtectedAction.Close, QuitProtectionMath.MatchExtra('W', Modifiers.Ctrl | Modifiers.Shift, _ => Modifiers.Shift)!.Value.Action);
    }

    [Fact]
    public void ScopeRules()
    {
        var apps = new HashSet<string> { "notepad" };
        Assert.True(QuitProtectionMath.ScopeAllows(AppScope.All, "code", apps));
        Assert.True(QuitProtectionMath.ScopeAllows(AppScope.SelectedOnly, "notepad", apps));
        Assert.False(QuitProtectionMath.ScopeAllows(AppScope.SelectedOnly, "code", apps));
        Assert.False(QuitProtectionMath.ScopeAllows(AppScope.AllExceptSelected, "notepad", apps));
        Assert.True(QuitProtectionMath.ScopeAllows(AppScope.AllExceptSelected, null, apps));
    }

    [Fact]
    public void DoublePressIntervalAndTitles()
    {
        Assert.True(QuitProtectionMath.IsWithinDoublePress(1000, 1500, 600));
        Assert.False(QuitProtectionMath.IsWithinDoublePress(1000, 1700, 600));
        Assert.False(QuitProtectionMath.IsWithinDoublePress(1000, 900, 600));
        Assert.Equal(2000, QuitProtectionMath.SanitizedHold(9000));
        var ctrlW = QuitProtectionMath.Combos[2];
        Assert.Equal("Hold Ctrl+W to close", QuitProtectionMath.HoldTitle(ctrlW));
        Assert.Equal("Press Ctrl+W again to close", QuitProtectionMath.DoubleTitle(ctrlW));
        Assert.Equal("Use Ctrl+Shift+W to close", QuitProtectionMath.ExtraTitle(ctrlW, Modifiers.Shift));
        Assert.Equal("Hold Alt+F4 to quit", QuitProtectionMath.HoldTitle(QuitProtectionMath.Combos[0]));
    }
}

public class MouseButtonTests
{
    [Fact]
    public void TiltFiresOncePerBurstAndDirection()
    {
        var gate = new SideWheelGestureGate();
        Assert.True(gate.ShouldFire(MouseInputs.WheelLeft, 0));
        Assert.False(gate.ShouldFire(MouseInputs.WheelLeft, 100));
        Assert.True(gate.ShouldFire(MouseInputs.WheelRight, 150));
        Assert.False(gate.ShouldFire(MouseInputs.WheelLeft, 300)); // still the same burst
        Assert.True(gate.ShouldFire(MouseInputs.WheelLeft, 700));  // quiet gap: new push
    }

    [Fact]
    public void ShortDragStaysAClick()
    {
        var t = new DesktopGestureTracker(0, 0);
        Assert.Null(t.Advance(100, 50, 10));
        Assert.False(t.DidFire);
    }

    [Fact]
    public void HorizontalDragSwitchesDesktopsWithCooldown()
    {
        var t = new DesktopGestureTracker(0, 0);
        Assert.Equal(DesktopGesture.DesktopRight, t.Advance(230, 0, 0));
        Assert.Null(t.Advance(500, 0, 100));                  // cooldown: banked
        Assert.Equal(DesktopGesture.DesktopRight, t.Advance(520, 0, 400));
        Assert.Null(t.Advance(520, -400, 500));               // axis locked to horizontal
        Assert.True(t.DidFire);
    }

    [Fact]
    public void VerticalDragOpensTaskViewOnce()
    {
        var t = new DesktopGestureTracker(0, 0);
        Assert.Equal(DesktopGesture.TaskView, t.Advance(0, -160, 0));
        Assert.Null(t.Advance(0, -400, 1000));
        var down = new DesktopGestureTracker(0, 0);
        Assert.Equal(DesktopGesture.ShowDesktop, down.Advance(10, 200, 0));
    }

    [Fact]
    public void FollowingTheDragSwapsOnlyDesktopDirections()
    {
        Assert.Equal(DesktopGesture.DesktopLeft, DesktopGestureTracker.Resolved(DesktopGesture.DesktopRight, true));
        Assert.Equal(DesktopGesture.TaskView, DesktopGestureTracker.Resolved(DesktopGesture.TaskView, true));
        Assert.Equal(DesktopGesture.DesktopRight, DesktopGestureTracker.Resolved(DesktopGesture.DesktopRight, false));
    }

    [Fact]
    public void InputsMap()
    {
        Assert.Equal(MouseInputs.Middle, MouseInputs.ButtonOf(MouseAction.MiddleDown, 0));
        Assert.Equal(MouseInputs.Forward, MouseInputs.ButtonOf(MouseAction.XUp, 2));
        Assert.Equal(MouseInputs.Back, MouseInputs.ButtonOf(MouseAction.XDown, 1));
        Assert.Null(MouseInputs.ButtonOf(MouseAction.LeftDown, 0));
        Assert.Equal(MouseInputs.WheelRight, MouseInputs.WheelDirection(120));
        Assert.Equal(MouseInputs.WheelLeft, MouseInputs.WheelDirection(-8));
        Assert.False(MouseInputs.CanHold(MouseInputs.WheelLeft));
        Assert.True(MouseInputs.CanHold(MouseInputs.Middle));
    }
}

public class ExceptionAndAccelerationTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Blender\Blender.EXE", "blender")]
    [InlineData("blender.exe", "blender")]
    [InlineData("  Photoshop ", "photoshop")]
    [InlineData("\"C:\\a b\\game.exe\"", "game")]
    [InlineData("", "")]
    public void NamesNormalize(string input, string expected) => Assert.Equal(expected, AppExceptions.Normalize(input));

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var list = new HashSet<string>(["blender"], StringComparer.OrdinalIgnoreCase);
        Assert.True(AppExceptions.Matches(list, "Blender"));
        Assert.False(AppExceptions.Matches(list, null));
        Assert.False(AppExceptions.Matches(new HashSet<string>(), "blender"));
    }

    [Fact]
    public void AccelerationTripleRoundTrips()
    {
        Assert.True(MouseAccelerationMath.IsAccelerated([6, 10, 1]));
        Assert.False(MouseAccelerationMath.IsAccelerated(MouseAccelerationMath.Linear()));
        Assert.Equal(new[] { 6, 10, 1 }, MouseAccelerationMath.Decode(MouseAccelerationMath.Encode([6, 10, 1])));
        Assert.Null(MouseAccelerationMath.Decode("1,2"));
        Assert.Null(MouseAccelerationMath.Decode(null));
    }
}
