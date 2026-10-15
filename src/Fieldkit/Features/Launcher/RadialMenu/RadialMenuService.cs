// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// The radial menu's triggers. At rest it holds one global hotkey per wheel with a shortcut and,
/// only while some wheel is bound to a mouse button, the shared mouse hook. Switching the feature
/// off or uninstalling it releases all of that and closes any open wheel.
/// </summary>
public sealed class RadialMenuService : FeatureService
{
    private readonly List<IDisposable> _hotkeys = [];
    private IDisposable? _observer;
    private IDisposable? _mouseHook;
    private volatile RadialMouseTrigger[] _mouseTriggers = [];
    private volatile bool _swallowMiddle, _swallowBack, _swallowForward;

    public override string FeatureId => RadialCatalog.FeatureId;
    public override IReadOnlyList<string> EnableKeys => [RadialMenuStore.EnabledKey];
    public override bool EnabledByDefault => false;

    /// <summary>True when Windows refused one of the wheels' shortcuts (taken by another app).</summary>
    public static bool RegistrationFailed { get; private set; }

    /// <summary>Whether the mouse hook is installed (a wheel claims a mouse button).</summary>
    public static bool IsWatchingMouse { get; private set; }

    public static event Action? StateChanged;

    protected override void OnStart()
    {
        Sync();
        _observer = Settings.Observe(Sync, RadialMenuStore.ProfilesKey, RadialMenuStore.ShortcutKey);
    }

    protected override void OnStop()
    {
        _observer?.Dispose();
        _observer = null;
        ReleaseTriggers();
        RegistrationFailed = false;
        RadialMenuSession.Shutdown();
        StateChanged?.Invoke();
    }

    private void ReleaseTriggers()
    {
        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        _mouseHook?.Dispose();
        _mouseHook = null;
        _mouseTriggers = [];
        IsWatchingMouse = false;
    }

    private void Sync()
    {
        ReleaseTriggers();
        var profiles = RadialMenuStore.Load();
        var failed = false;
        var seen = new HashSet<Shortcut>();
        foreach (var profile in profiles)
        {
            var shortcut = Shortcut.Parse(profile.Shortcut);
            if (shortcut.IsEmpty || !seen.Add(shortcut)) continue;
            var id = profile.Id;
            var registration = Hotkeys.Register(shortcut, () => HotkeyPressed(id));
            if (registration is null) failed = true;
            else _hotkeys.Add(registration);
        }
        RegistrationFailed = failed;

        var triggers = RadialMenuSupport.MouseTriggers(profiles);
        _mouseTriggers = triggers.ToArray();
        if (triggers.Count > 0)
        {
            _mouseHook = InputHooks.AddMouseHandler(OnMouse, priority: 40);
            IsWatchingMouse = true;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Hotkeys never autorepeat, so a press during a session is the user asking to close.</summary>
    private static void HotkeyPressed(Guid profileId)
    {
        var profile = RadialMenuStore.Load().FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return;
        if (RadialMenuSession.IsActive)
        {
            var same = RadialMenuSession.ActiveProfileId == profileId;
            RadialMenuSession.End();
            if (same) return;
        }
        RadialMenuSession.Begin(profile, hold: true);
    }

    /// <summary>Settings' Try it: a sticky session, like a quick press of the shortcut (works while off).</summary>
    public static void Preview(RadialMenuProfile profile)
    {
        RadialMenuSession.End();
        RadialMenuSession.Begin(profile, hold: false);
    }

    // Hook thread: the claimed button is the wheel's alone, both halves of every click are consumed.
    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn) return false;
        var button = RadialMenuSupport.ButtonOf(e.Action, e.XButton);
        if (button == RadialMouseButton.Off) return false;
        var down = e.Action is MouseAction.MiddleDown or MouseAction.XDown;
        if (down)
        {
            var match = RadialMenuSupport.MatchMouseTrigger(_mouseTriggers, button, Shortcut.CurrentModifiers());
            if (match is not { } trigger) return false;
            SetSwallow(button, true);
            Ui.OnUi(() => TriggerDown(trigger));
            return true;
        }
        if (!GetSwallow(button)) return false;
        SetSwallow(button, false);
        Ui.OnUi(() => RadialMenuSession.ButtonReleased(button));
        return true;
    }

    private static void TriggerDown(RadialMouseTrigger trigger)
    {
        if (RadialMenuSession.IsActive)
        {
            // A press during a held session means nothing; otherwise it closes the wheel.
            if (!RadialMenuSession.InHoldPhase) RadialMenuSession.End();
            return;
        }
        var profile = RadialMenuStore.Load().FirstOrDefault(p => p.Id == trigger.ProfileId);
        if (profile is not null) RadialMenuSession.Begin(profile, hold: false, heldButton: trigger.Button);
    }

    private bool GetSwallow(RadialMouseButton b) => b switch
    {
        RadialMouseButton.Middle => _swallowMiddle,
        RadialMouseButton.Back => _swallowBack,
        _ => _swallowForward,
    };

    private void SetSwallow(RadialMouseButton b, bool value)
    {
        switch (b)
        {
            case RadialMouseButton.Middle: _swallowMiddle = value; break;
            case RadialMouseButton.Back: _swallowBack = value; break;
            default: _swallowForward = value; break;
        }
    }
}
