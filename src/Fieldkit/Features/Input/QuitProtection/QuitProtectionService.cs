// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.QuitProtection;

/// <summary>
/// Quit and close protection (upstream QuitProtectionService). Alt+F4 / Ctrl+Q and Ctrl+W /
/// Ctrl+F4 are held back until the chosen confirmation: holding them, pressing them twice, or
/// adding an extra modifier. The confirmed key is then replayed while the user's own modifiers
/// are still down, so the app receives the ordinary shortcut. Every other key passes untouched
/// and ordinary typing costs one comparison.
/// </summary>
internal sealed class QuitProtectionService : FeatureService
{
    public override string FeatureId => "quitWindowProtection";
    public override IReadOnlyList<string> EnableKeys => ["quitWindowProtection.quit.enabled", "quitWindowProtection.close.enabled"];
    public override bool EnabledByDefault => false;

    private readonly object _gate = new();
    private IDisposable? _hook;
    private IDisposable? _inject;
    private IDisposable? _observer;
    private volatile QuitProtectionConfig _quit = QuitProtectionConfig.Off;
    private volatile QuitProtectionConfig _close = QuitProtectionConfig.Off;
    private QuitProtectionHud? _hud;
    private DispatcherTimer? _timer;

    // Guarded by _gate (hook thread and UI thread).
    private Pending? _pending;
    private int _swallowVk;
    private ulong _generation;

    private sealed record Pending(ProtectedCombo Combo, ConfirmationMode Mode, long StartedMs, ulong Generation)
    {
        public bool KeyDown { get; set; } = true;
    }

    protected override void OnStart()
    {
        Read();
        _observer = Settings.ObservePrefix("quitWindowProtection.", Read);
        _inject = InjectQueue.Acquire();
        _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 30);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        lock (_gate)
        {
            _pending = null;
            _swallowVk = 0;
            _generation++;
        }
        _timer?.Stop();
        _timer = null;
        _hud?.Close();
        _hud = null;
        _observer?.Dispose();
        _observer = null;
        _inject?.Dispose();
        _inject = null;
    }

    private void Read()
    {
        _quit = ReadConfig(ProtectedAction.Quit);
        _close = ReadConfig(ProtectedAction.Close);
    }

    internal static QuitProtectionConfig ReadConfig(ProtectedAction action)
    {
        var p = QuitProtectionMath.Prefix(action);
        return new QuitProtectionConfig(
            Settings.GetBool($"{p}.enabled"),
            QuitProtectionMath.ParseMode(Settings.GetString($"{p}.mode", "hold")),
            QuitProtectionMath.SanitizedHold(Settings.GetInt($"{p}.holdMs", QuitProtectionMath.DefaultHoldMs)),
            QuitProtectionMath.SanitizedDouble(Settings.GetInt($"{p}.doubleMs", QuitProtectionMath.DefaultDoubleMs)),
            QuitProtectionMath.ParseExtraModifier(Settings.GetString($"{p}.extraModifier", "shift")),
            QuitProtectionMath.ParseScope(Settings.GetString($"{p}.scope", "all")),
            AppExceptions.Snapshot(p),
            Settings.GetBool($"{p}.feedback", true));
    }

    private QuitProtectionConfig ConfigFor(ProtectedAction a) => a == ProtectedAction.Quit ? _quit : _close;

    // ------------------------------------------------------------------ hook thread

    private bool OnKey(ref KeyEvent e)
    {
        if (e.IsOwn) return false;
        var vk = e.VirtualKey;
        lock (_gate)
        {
            if (_swallowVk == vk)
            {
                if (!e.IsDown) _swallowVk = 0;
                return true;
            }
            if (_pending is { } pending)
            {
                if (e.IsDown && vk == InputNative.VK_ESCAPE)
                {
                    CancelLocked();
                    return true;
                }
                if (vk == pending.Combo.Key)
                {
                    if (!e.IsDown)
                    {
                        pending.KeyDown = false;
                        if (pending.Mode != ConfirmationMode.DoublePress) CancelLocked();
                        return true;
                    }
                    if (pending.KeyDown) return true; // auto-repeat while held
                }
                else if (Shortcut.IsModifierKey(vk))
                {
                    // Letting go of the shortcut's own modifier ends a hold.
                    if (!e.IsDown && pending.Mode == ConfirmationMode.Hold) CancelLocked();
                    return false;
                }
                else if (e.IsDown)
                {
                    CancelLocked();
                }
            }

            if (!e.IsDown || !IsProtectedKey(vk)) return false;
            var held = Shortcut.CurrentModifiers();

            // Extra-modifier confirmation first: it contains the base combination.
            if (QuitProtectionMath.MatchExtra(vk, held, a => ConfigFor(a).ExtraModifier) is { } extraCombo)
            {
                var cfg = ConfigFor(extraCombo.Action);
                if (cfg.Enabled && cfg.Mode == ConfirmationMode.ExtraModifier && InScope(cfg))
                {
                    CancelLocked();
                    _swallowVk = vk;
                    ConfirmExtra(extraCombo, QuitProtectionMath.EffectiveExtra(extraCombo, cfg.ExtraModifier));
                    return true;
                }
            }

            if (QuitProtectionMath.MatchBase(vk, held) is not { } combo) return false;
            var config = ConfigFor(combo.Action);
            if (!config.Enabled || !InScope(config)) return false;

            if (config.Mode == ConfirmationMode.DoublePress && _pending is { Mode: ConfirmationMode.DoublePress } first
                && first.Combo == combo && QuitProtectionMath.IsWithinDoublePress(first.StartedMs, e.Time, config.DoublePressMs))
            {
                CancelLocked();
                _swallowVk = vk;
                Replay(combo);
                return true;
            }

            Begin(combo, config, e.Time);
            return true;
        }
    }

    private static bool IsProtectedKey(int vk) => vk is 0x73 or 'Q' or 'W';

    private static bool InScope(QuitProtectionConfig cfg) =>
        cfg.Scope == AppScope.All || QuitProtectionMath.ScopeAllows(cfg.Scope, WindowHelper.ForegroundProcessName(), cfg.Apps);

    // Under _gate.
    private void Begin(ProtectedCombo combo, QuitProtectionConfig cfg, long timeMs)
    {
        var generation = ++_generation;
        _pending = new Pending(combo, cfg.Mode, timeMs, generation);
        MaskAlt(combo.Base);
        var (title, delay) = cfg.Mode switch
        {
            ConfirmationMode.Hold => (QuitProtectionMath.HoldTitle(combo), cfg.HoldMs),
            ConfirmationMode.DoublePress => (QuitProtectionMath.DoubleTitle(combo), cfg.DoublePressMs + 100),
            _ => (QuitProtectionMath.ExtraTitle(combo, cfg.ExtraModifier), QuitProtectionMath.ExtraModifierExpiryMs),
        };
        var mode = cfg.Mode;
        var feedback = cfg.ShowFeedback;
        Ui.OnUi(() =>
        {
            lock (_gate)
            {
                if (_generation != generation || !IsRunning) return;
            }
            if (feedback)
            {
                _hud ??= new QuitProtectionHud();
                _hud.ShowMessage(title, "Esc cancels", mode == ConfirmationMode.Hold ? TimeSpan.FromMilliseconds(delay) : null);
            }
            _timer?.Stop();
            _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(delay) };
            _timer.Tick += (_, _) =>
            {
                _timer?.Stop();
                OnTimer(generation);
            };
            _timer.Start();
        });
    }

    // UI thread: a hold completed, or a double press / extra modifier prompt expired.
    private void OnTimer(ulong generation)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Generation != generation) return;
            if (pending.Mode == ConfirmationMode.Hold && pending.KeyDown)
            {
                _swallowVk = pending.Combo.Key;
                Replay(pending.Combo);
            }
            CancelLocked();
        }
    }

    // Under _gate.
    private void CancelLocked()
    {
        if (_pending is null) return;
        _pending = null;
        var generation = ++_generation;
        Ui.OnUi(() =>
        {
            lock (_gate)
            {
                if (_generation != generation) return;
            }
            _timer?.Stop();
            _hud?.HideMessage();
        });
    }

    /// <summary>Replays the key while the user's own modifiers are still down.</summary>
    private static void Replay(ProtectedCombo combo) =>
        InjectQueue.Post(InputSender.Key(combo.Key, true), InputSender.Key(combo.Key, false));

    /// <summary>Lifts the extra modifier, then replays the key so the app sees the plain shortcut.</summary>
    private static void ConfirmExtra(ProtectedCombo combo, Modifiers extra)
    {
        var list = new List<Native.INPUT>();
        foreach (var vk in ExtraKeys(extra))
        {
            if (InputHooks.IsKeyDown(vk)) list.Add(InputSender.Key(vk, false));
        }
        list.Add(InputSender.Key(combo.Key, true));
        list.Add(InputSender.Key(combo.Key, false));
        InjectQueue.Post(list.ToArray());
    }

    private static int[] ExtraKeys(Modifiers m) => m switch
    {
        Modifiers.Alt => [InputNative.VK_LMENU, InputNative.VK_RMENU],
        Modifiers.Ctrl => [InputNative.VK_LCONTROL, InputNative.VK_RCONTROL],
        _ => [InputNative.VK_LSHIFT, InputNative.VK_RSHIFT],
    };

    /// <summary>A swallowed key under Alt would leave a bare Alt tap, which opens the app's menu bar.</summary>
    private static void MaskAlt(Modifiers held)
    {
        if (held.HasFlag(Modifiers.Alt))
            InjectQueue.Post(InputSender.Key(InputNative.VK_MASK, true), InputSender.Key(InputNative.VK_MASK, false));
    }
}
