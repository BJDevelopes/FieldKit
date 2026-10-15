// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Debounce;

/// <summary>
/// Extra click filter (upstream MouseClickDebounceService): drops the switch bounce of worn
/// left/right/middle buttons. Normal clicks pass immediately; nothing is ever delayed.
/// </summary>
internal sealed class MouseClickDebounceService : FeatureService
{
    public override string FeatureId => "mouseClickDebounce";
    public override IReadOnlyList<string> EnableKeys => ["mouseClickDebounce.enabled"];
    public override bool EnabledByDefault => false;

    private readonly ClickDebounceState _state = new();
    private IDisposable? _hook;
    private IDisposable? _observer;
    private volatile int _windowMs = ClickDebounceState.DefaultWindowMs;

    protected override void OnStart()
    {
        void Read() => _windowMs = ClickDebounceState.SanitizedWindow(Settings.GetInt("mouseClickDebounce.windowMs", ClickDebounceState.DefaultWindowMs));
        Read();
        _observer = Settings.Observe(Read, "mouseClickDebounce.windowMs");
        _state.Reset();
        _hook = InputHooks.AddMouseHandler(OnMouse, priority: 10);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        _observer?.Dispose();
        _observer = null;
    }

    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn || e.IsInjected) return false;
        var (button, phase) = e.Action switch
        {
            MouseAction.LeftDown => (0, ClickPhase.Down),
            MouseAction.LeftUp => (0, ClickPhase.Up),
            MouseAction.RightDown => (1, ClickPhase.Down),
            MouseAction.RightUp => (1, ClickPhase.Up),
            MouseAction.MiddleDown => (2, ClickPhase.Down),
            MouseAction.MiddleUp => (2, ClickPhase.Up),
            _ => (-1, ClickPhase.Up),
        };
        return button >= 0 && _state.ShouldSuppress(button, phase, e.Time, _windowMs);
    }
}

/// <summary>
/// Key debounce (upstream KeyboardDebounceService): drops a second press of the same key that
/// arrives within the window after its release, with per-key overrides. Never delays a key.
/// </summary>
internal sealed class KeyboardDebounceService : FeatureService
{
    public override string FeatureId => "keyboardDebounce";
    public override IReadOnlyList<string> EnableKeys => ["keyboardDebounce.enabled"];
    public override bool EnabledByDefault => false;

    private readonly KeyDebounceState _state = new();
    private readonly int[] _windows = new int[256];
    private IDisposable? _hook;
    private IDisposable? _observer;

    protected override void OnStart()
    {
        Read();
        _observer = Settings.Observe(Read, "keyboardDebounce.windowMs", "keyboardDebounce.keyWindows");
        _state.Reset();
        _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 10);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        _observer?.Dispose();
        _observer = null;
    }

    private void Read()
    {
        var global = KeyDebounceState.SanitizedWindow(Settings.GetInt("keyboardDebounce.windowMs", KeyDebounceState.DefaultWindowMs));
        var overrides = KeyWindowCodec.Decode(Settings.GetString("keyboardDebounce.keyWindows"));
        for (var vk = 0; vk < 256; vk++)
            Volatile.Write(ref _windows[vk], overrides.TryGetValue(vk, out var ms) ? ms : global);
    }

    private bool OnKey(ref KeyEvent e)
    {
        if (e.IsOwn || e.IsInjected) return false;
        var vk = e.VirtualKey & 0xFF;
        return _state.ShouldSuppress(vk, e.IsDown, e.Time, Volatile.Read(ref _windows[vk]));
    }
}
