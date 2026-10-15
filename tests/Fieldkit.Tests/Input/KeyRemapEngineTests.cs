// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Input.Remap;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Input;

public class KeyRemapEngineTests
{
    private const int CapsLock = 0x14, Esc = 0x1B, K = 0x4B, J = 0x4A, Left = 0x25, A = 0x41;
    private const int LShift = 0xA0, LCtrl = 0xA2, LAlt = 0xA4, Mask = 0xE8;

    private static KeyRemapEngine Engine(params KeyRemapRule[] rules) => new() { Rules = rules };

    private static KeyRemapRule Rule(string from, string to, string app = "") => new(Shortcut.Parse(from), Shortcut.Parse(to), app);

    private static (bool Swallow, List<KeyOut> Out) Press(KeyRemapEngine engine, int vk, bool down, string? app = null, bool canStart = true)
    {
        var output = new List<KeyOut>();
        var swallow = engine.Handle(vk, down, () => app, canStart, output);
        return (swallow, output);
    }

    private static KeyOut D(int vk) => new(vk, true);
    private static KeyOut U(int vk) => new(vk, false);

    [Fact]
    public void APlainKeySwapsAndRepeats()
    {
        var engine = Engine(Rule("CapsLock", "Esc"));
        var down = Press(engine, CapsLock, true);
        Assert.True(down.Swallow);
        Assert.Equal([D(Esc)], down.Out);
        Assert.Equal([D(Esc)], Press(engine, CapsLock, true).Out); // auto-repeat
        var up = Press(engine, CapsLock, false);
        Assert.True(up.Swallow);
        Assert.Equal([U(Esc)], up.Out);
        Assert.False(engine.IsActive);
    }

    [Fact]
    public void APlainKeyRemapKeepsHeldModifiers()
    {
        var engine = Engine(Rule("CapsLock", "Esc"));
        Assert.False(Press(engine, LShift, true).Swallow);
        Assert.Equal([D(Esc)], Press(engine, CapsLock, true).Out); // apps see Shift+Esc
        Assert.Equal([U(Esc)], Press(engine, CapsLock, false).Out);
    }

    [Fact]
    public void AShortcutAddsOnlyTheModifiersItNeeds()
    {
        var engine = Engine(Rule("Ctrl+K", "Ctrl+Shift+Esc"));
        Press(engine, LCtrl, true);
        var down = Press(engine, K, true);
        Assert.True(down.Swallow);
        Assert.Equal([D(LShift), D(Esc)], down.Out);
        Assert.Equal([U(Esc), U(LShift)], Press(engine, K, false).Out);
    }

    [Fact]
    public void AltIsLiftedBehindAMaskAndPressedAgainAfterwards()
    {
        var engine = Engine(Rule("Alt+J", "Left"));
        Press(engine, LAlt, true);
        Assert.Equal([D(Mask), U(Mask), U(LAlt), D(Left)], Press(engine, J, true).Out);
        Assert.Equal([U(Left), D(LAlt), D(Mask), U(Mask)], Press(engine, J, false).Out);
        // Still holding Alt: pressing J again remaps again.
        Assert.Equal(D(Left), Press(engine, J, true).Out[^1]);
    }

    [Fact]
    public void AModifierLetGoEarlyIsNotPressedAgain()
    {
        var engine = Engine(Rule("Alt+J", "Left"));
        Press(engine, LAlt, true);
        Press(engine, J, true);
        Assert.True(Press(engine, LAlt, false).Swallow); // already up for apps
        Assert.Equal([U(Left)], Press(engine, J, false).Out);
    }

    [Fact]
    public void ShortcutsNeedExactlyTheirModifiers()
    {
        var engine = Engine(Rule("Ctrl+K", "Esc"));
        Assert.False(Press(engine, K, true).Swallow);
        Press(engine, K, false);
        Press(engine, LCtrl, true);
        Press(engine, LShift, true);
        Assert.False(Press(engine, K, true).Swallow); // Ctrl+Shift+K is not Ctrl+K
    }

    [Fact]
    public void AnAppRuleOnlyAppliesInThatApp()
    {
        var engine = Engine(Rule("CapsLock", "Esc", "Chrome.exe"));
        Assert.False(Press(engine, CapsLock, true, app: "notepad").Swallow);
        Assert.True(Press(engine, CapsLock, true, app: "chrome").Swallow);
    }

    [Fact]
    public void TheForegroundAppIsOnlyLookedUpForRemappedKeys()
    {
        var engine = Engine(Rule("CapsLock", "Esc", "chrome"));
        var asked = 0;
        engine.Handle(A, true, () => { asked++; return "chrome"; }, true, []);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void NothingNewStartsWhileFieldkitIsInFront()
    {
        var engine = Engine(Rule("CapsLock", "Esc"));
        Assert.False(Press(engine, CapsLock, true, canStart: false).Swallow);
    }

    [Fact]
    public void AShortcutRemapWinsOverAPlainOne()
    {
        var engine = Engine(Rule("K", "A"), Rule("Ctrl+K", "Ctrl+J"));
        Press(engine, LCtrl, true);
        Assert.Equal([D(J)], Press(engine, K, true).Out);
    }

    [Theory]
    [InlineData("", "Esc")]
    [InlineData("Esc", "Esc")]
    [InlineData("CapsLock", "")]
    public void IncompleteOrPointlessRulesAreIgnored(string from, string to) =>
        Assert.False(Rule(from, to).IsValid);

    [Fact]
    public void SwitchingOffLetsGoOfHeldKeys()
    {
        var engine = Engine(Rule("Ctrl+K", "Ctrl+Shift+Esc"));
        Press(engine, LCtrl, true);
        Press(engine, K, true);
        var output = new List<KeyOut>();
        engine.ReleaseAll(output);
        Assert.Equal([U(Esc), U(LShift)], output);
        Assert.False(engine.IsActive);
    }

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome.EXE", "Chrome")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    public void AppNamesAreNormalised(string value, string expected) =>
        Assert.Equal(expected, KeyRemapEngine.AppName(value));
}
