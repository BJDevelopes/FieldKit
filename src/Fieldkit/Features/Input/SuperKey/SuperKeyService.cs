// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.SuperKey;

/// <summary>
/// The super key (upstream SuperKeyService). Windows needs no key remapping table: the keyboard
/// hook swallows the chosen key itself, so Caps Lock never toggles (its light and state stay
/// exactly as they were) and the right-side modifier never reaches apps. When another key is
/// pressed while it is held, the chosen modifiers are pressed first and that key is replayed
/// after them; they are released with the super key. A tap on its own runs the solo action.
///
/// While an app on the exception list is running (even in the background) the key works
/// normally, like upstream. The process check runs every 2 s and only while the list is not empty.
/// </summary>
internal sealed class SuperKeyService : FeatureService
{
    public override string FeatureId => "superKey";
    public override IReadOnlyList<string> EnableKeys => ["superKey.enabled"];
    public override bool EnabledByDefault => false;

    /// <summary>True while an excepted app is running (shown on the settings page).</summary>
    public bool IsPaused => _paused;

    public event Action? StateChanged;

    private readonly SuperKeyState _state = new();
    private IDisposable? _hook;
    private IDisposable? _inject;
    private IDisposable? _observer;
    private DispatcherTimer? _pauseTimer;
    private volatile bool _paused;
    private volatile int _sourceVk = 0x14;
    private volatile SuperKeySource _source;
    private volatile int _modifiers = (int)SuperKeyMath.DefaultModifiers;
    private volatile SuperKeySoloAction _solo;
    private int[] _injectedModifiers = []; // hook thread only
    private HashSet<string> _exceptions = [];

    protected override void OnStart()
    {
        Read();
        _observer = Settings.ObservePrefix("superKey.", Read);
        _inject = InjectQueue.Acquire();
        _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 20);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        ReleaseInjected();
        _state.Reset();
        _pauseTimer?.Stop();
        _pauseTimer = null;
        _observer?.Dispose();
        _observer = null;
        _inject?.Dispose();
        _inject = null;
        _paused = false;
    }

    private void Read()
    {
        _source = SuperKeyMath.ParseSource(Settings.GetString("superKey.source", "capsLock"));
        _sourceVk = SuperKeyMath.VirtualKey(_source);
        _modifiers = (int)SuperKeyMath.ParseModifiers(Settings.GetString("superKey.modifiers", SuperKeyMath.StorageValue(SuperKeyMath.DefaultModifiers)));
        _solo = SuperKeyMath.ParseSolo(Settings.GetString("superKey.soloAction", "none"));
        _exceptions = AppExceptions.Snapshot(FeatureId);
        if (_exceptions.Count > 0)
        {
            if (_pauseTimer is null)
            {
                _pauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _pauseTimer.Tick += (_, _) => CheckPaused();
                _pauseTimer.Start();
            }
            CheckPaused();
        }
        else
        {
            _pauseTimer?.Stop();
            _pauseTimer = null;
            SetPaused(false);
        }
    }

    private void CheckPaused()
    {
        var running = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!running && _exceptions.Contains(process.ProcessName.ToLowerInvariant())) running = true;
            }
        }
        SetPaused(running);
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ hook thread

    private bool OnKey(ref KeyEvent e)
    {
        if (e.IsOwn) return false;
        var vk = e.VirtualKey;
        if (vk == _sourceVk)
        {
            if (_paused && !_state.IsHeld) return false;
            if (e.IsDown)
            {
                _state.TriggerDown(HasPrimaryModifiersHeld(), e.Time);
                return true;
            }
            var decision = _state.TriggerUp(e.Time);
            var repeated = _state.Repeated;
            ReleaseInjected();
            if (decision is SuperKeyDecision.SoloTap or SuperKeyDecision.SoloHold)
                RunSolo(SuperKeyMath.SoloEffect(_solo, decision == SuperKeyDecision.SoloHold, repeated));
            return true;
        }

        // Right Alt on AltGr layouts arrives with a synthetic Left Ctrl (scan code 0x21D): it
        // belongs to the super key too.
        if (_source == SuperKeySource.RightAlt && vk == InputNative.VK_LCONTROL && (e.ScanCode & 0x200) != 0)
            return _state.IsHeld || e.IsDown;

        if (!_state.IsHeld) return false;
        if (Shortcut.IsModifierKey(vk))
        {
            _state.OtherModifier();
            return false;
        }
        if (!e.IsDown) return false;
        _state.OtherKey();
        if (_injectedModifiers.Length > 0) return false; // modifiers already down: the key passes as is

        _injectedModifiers = KeyCombo.ModifierKeys((Modifiers)_modifiers);
        var inputs = new Native.INPUT[_injectedModifiers.Length + 1];
        for (var i = 0; i < _injectedModifiers.Length; i++) inputs[i] = InputSender.Key(_injectedModifiers[i], true);
        inputs[^1] = InputSender.Key(vk, true);
        InjectQueue.Post(inputs);
        return true;
    }

    private static bool HasPrimaryModifiersHeld() =>
        InputHooks.IsKeyDown(InputNative.VK_CONTROL) || InputHooks.IsKeyDown(InputNative.VK_MENU)
        || InputHooks.IsKeyDown(InputNative.VK_LWIN) || InputHooks.IsKeyDown(InputNative.VK_RWIN);

    private void ReleaseInjected()
    {
        var mods = _injectedModifiers;
        if (mods.Length == 0) return;
        _injectedModifiers = [];
        var list = new List<Native.INPUT>(mods.Length + 2);
        // Releasing Win alone after other keys never opens Start, but a masking key makes sure.
        if (Array.IndexOf(mods, InputNative.VK_LWIN) >= 0)
        {
            list.Add(InputSender.Key(InputNative.VK_MASK, true));
            list.Add(InputSender.Key(InputNative.VK_MASK, false));
        }
        for (var i = mods.Length - 1; i >= 0; i--) list.Add(InputSender.Key(mods[i], false));
        InjectQueue.Post(list.ToArray());
    }

    private static void RunSolo(SuperKeySoloAction action)
    {
        switch (action)
        {
            case SuperKeySoloAction.CapsLock:
                InjectQueue.Post(InputSender.Key(InputNative.VK_CAPITAL, true), InputSender.Key(InputNative.VK_CAPITAL, false));
                break;
            case SuperKeySoloAction.Escape:
                InjectQueue.Post(InputSender.Key(InputNative.VK_ESCAPE, true), InputSender.Key(InputNative.VK_ESCAPE, false));
                break;
            case SuperKeySoloAction.InputSource:
                InjectQueue.Post(KeyCombo.Press(Modifiers.Win, InputNative.VK_SPACE));
                break;
        }
    }
}
