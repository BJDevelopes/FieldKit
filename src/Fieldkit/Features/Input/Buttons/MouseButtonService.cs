// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Buttons;

/// <summary>
/// Mouse button shortcuts and the virtual-desktop drag (upstream MouseButtonShortcutService).
/// A mapped button's whole click belongs to its shortcut: the press and its release are both
/// swallowed. The drag button's press is only held back: a release without travel hands the
/// click back to the app. Apps on the exception list keep their buttons untouched.
/// </summary>
internal sealed class MouseButtonService : FeatureService
{
    public override string FeatureId => "mouseButtonShortcuts";
    public override IReadOnlyList<string> EnableKeys => ["mouseButtonShortcuts.enabled", "mouseButtonShortcuts.spacesEnabled"];
    public override bool EnabledByDefault => false;

    /// <summary>Set by the Settings capture row; the next button or tilt is reported instead of acting.</summary>
    public static volatile bool Capturing;

    /// <summary>Raised on the UI thread with the input id seen while <see cref="Capturing"/>.</summary>
    public static event Action<string>? Captured;

    private IDisposable? _hook;
    private IDisposable? _inject;
    private IDisposable? _observer;
    private volatile Config _config = Config.Empty;
    private readonly PointerTargetResolver _resolver = new();
    private readonly SideWheelGestureGate _gate = new();
    private readonly bool[] _consumed = new bool[MouseInputs.All.Length];
    private string? _heldButton;
    private DesktopGestureTracker? _tracker;
    private double _heldScale = 1;

    protected override void OnStart()
    {
        Refresh();
        _observer = Settings.ObservePrefix("mouseButtonShortcuts.", Refresh);
        _inject = InjectQueue.Acquire();
        _hook = InputHooks.AddMouseHandler(OnMouse, priority: 40);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        // A press still held back is owed to the app before the hook disappears.
        if (_heldButton is { } held && _tracker is { DidFire: false }) Replay(held);
        _heldButton = null;
        _tracker = null;
        Array.Clear(_consumed);
        _gate.Reset();
        _observer?.Dispose();
        _observer = null;
        _inject?.Dispose();
        _inject = null;
    }

    private void Refresh()
    {
        var enabled = Settings.GetBool("mouseButtonShortcuts.enabled");
        var shortcuts = new Shortcut[MouseInputs.All.Length];
        if (enabled)
        {
            foreach (var id in MouseButtonSettings.Buttons())
                shortcuts[MouseInputs.Index(id)] = Shortcut.FromSetting(MouseInputs.ShortcutKey(id));
        }
        var spaces = Settings.GetBool("mouseButtonShortcuts.spacesEnabled")
            ? MouseButtonSettings.SpacesButton() : null;
        // A button with a live shortcut keeps it; the drag never doubles up on it.
        if (spaces is not null && !shortcuts[MouseInputs.Index(spaces)].IsEmpty) spaces = null;
        _config = new Config(shortcuts, spaces, Settings.GetBool("mouseButtonShortcuts.spacesFollowsDrag"),
            AppExceptions.Snapshot(FeatureId));
    }

    // ------------------------------------------------------------------ hook thread

    private bool OnMouse(ref MouseEvent e)
    {
        if (e.IsOwn) return false;
        var cfg = _config;
        switch (e.Action)
        {
            case MouseAction.MiddleDown or MouseAction.XDown:
                return OnDown(MouseInputs.ButtonOf(e.Action, e.XButton)!, e.X, e.Y, cfg);
            case MouseAction.MiddleUp or MouseAction.XUp:
                return OnUp(MouseInputs.ButtonOf(e.Action, e.XButton)!);
            case MouseAction.Move:
                if (_tracker is not null) OnDrag(e.X, e.Y, e.Time, cfg);
                return false;
            case MouseAction.HWheel:
                return OnTilt(e.WheelDelta, e.X, e.Y, e.Time, e.IsInjected, cfg);
            default:
                return false;
        }
    }

    private bool OnDown(string id, int x, int y, Config cfg)
    {
        var index = MouseInputs.Index(id);
        if (Capturing)
        {
            Report(id);
            _consumed[index] = true;
            return true;
        }
        if (_heldButton is not null) return false;
        if (cfg.Exceptions.Count > 0 && AppExceptions.Matches(cfg.Exceptions, _resolver.ProcessNameAt(x, y))) return false;
        if (cfg.SpacesButton == id)
        {
            _heldButton = id;
            _heldScale = WindowHelper.GetScaleAt(x, y);
            _tracker = new DesktopGestureTracker(x / _heldScale, y / _heldScale);
            return true;
        }
        var shortcut = cfg.Shortcuts[index];
        if (shortcut.IsEmpty) return false;
        _consumed[index] = true;
        InjectQueue.Post(InputSender.AsRemap(KeyCombo.Press(shortcut)));
        return true;
    }

    private bool OnUp(string id)
    {
        if (_heldButton == id)
        {
            var fired = _tracker?.DidFire == true;
            _heldButton = null;
            _tracker = null;
            // A press that never travelled was only a click: give it back whole.
            if (!fired) Replay(id);
            return true;
        }
        var index = MouseInputs.Index(id);
        if (!_consumed[index]) return false;
        _consumed[index] = false;
        return true;
    }

    private void OnDrag(int x, int y, uint time, Config cfg)
    {
        if (_tracker?.Advance(x / _heldScale, y / _heldScale, time) is not { } gesture) return;
        var (mods, key) = DesktopGestureTracker.Resolved(gesture, cfg.FollowsDrag) switch
        {
            DesktopGesture.DesktopLeft => (Modifiers.Ctrl | Modifiers.Win, InputNative.VK_LEFT),
            DesktopGesture.DesktopRight => (Modifiers.Ctrl | Modifiers.Win, InputNative.VK_RIGHT),
            DesktopGesture.TaskView => (Modifiers.Win, InputNative.VK_TAB),
            _ => (Modifiers.Win, 'D'),
        };
        InjectQueue.Post(KeyCombo.Press(mods, key));
    }

    private bool OnTilt(int delta, int x, int y, uint time, bool injected, Config cfg)
    {
        if (injected || MouseInputs.WheelDirection(delta) is not { } direction) return false;
        if (Capturing)
        {
            if (_gate.ShouldFire(direction, time)) Report(direction);
            return true;
        }
        var shortcut = cfg.Shortcuts[MouseInputs.Index(direction)];
        if (shortcut.IsEmpty) return false;
        if (cfg.Exceptions.Count > 0 && AppExceptions.Matches(cfg.Exceptions, _resolver.ProcessNameAt(x, y))) return false;
        if (_gate.ShouldFire(direction, time)) InjectQueue.Post(InputSender.AsRemap(KeyCombo.Press(shortcut)));
        return true;
    }

    private static void Replay(string id)
    {
        var (down, up, data) = id switch
        {
            MouseInputs.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP, 0),
            MouseInputs.Forward => (Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON2),
            _ => (Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON1),
        };
        InjectQueue.Post(InputSender.MouseButton(down, data), InputSender.MouseButton(up, data));
    }

    private static void Report(string id) => Ui.OnUi(() => Captured?.Invoke(id));

    private sealed record Config(Shortcut[] Shortcuts, string? SpacesButton, bool FollowsDrag, HashSet<string> Exceptions)
    {
        public static readonly Config Empty = new(new Shortcut[MouseInputs.All.Length], null, false, []);
    }
}

/// <summary>Reads the persisted button list (shared by the service and the Settings section).</summary>
internal static class MouseButtonSettings
{
    public const string ButtonsKey = "mouseButtonShortcuts.buttons";
    public const string SpacesButtonKey = "mouseButtonShortcuts.spacesButton";

    public static List<string> Buttons() =>
        (Settings.Get<List<string>?>(ButtonsKey, null) ?? []).Where(MouseInputs.CanMap).Distinct().ToList();

    public static void SetButtons(IEnumerable<string> ids) =>
        Settings.Set(ButtonsKey, ids.Where(MouseInputs.CanMap).Distinct().OrderBy(MouseInputs.Index).ToList());

    public static string? SpacesButton()
    {
        var id = Settings.GetString(SpacesButtonKey);
        return MouseInputs.CanHold(id) ? id : null;
    }
}
