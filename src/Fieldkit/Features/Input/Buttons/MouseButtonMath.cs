// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Buttons;

/// <summary>
/// The inputs a shortcut can live on. The low-level hook reports the middle button, the two
/// side buttons (XBUTTON1/2) and the tilt wheel; buttons beyond those only reach Windows through
/// vendor software, which usually remaps them to keys already.
/// </summary>
public static class MouseInputs
{
    public const string Middle = "middle";
    public const string Back = "x1";
    public const string Forward = "x2";
    public const string WheelLeft = "wheelLeft";
    public const string WheelRight = "wheelRight";

    public static readonly string[] All = [Middle, Back, Forward, WheelLeft, WheelRight];

    public static bool CanMap(string id) => Array.IndexOf(All, id) >= 0;

    /// <summary>A drag needs a button that can be held: never a wheel direction.</summary>
    public static bool CanHold(string id) => id is Middle or Back or Forward;

    public static int Index(string id) => Array.IndexOf(All, id);

    public static string Name(string id) => id switch
    {
        Middle => "Middle button",
        Back => "Back side button",
        Forward => "Forward side button",
        WheelLeft => "Side wheel left",
        WheelRight => "Side wheel right",
        _ => id,
    };

    public static string ShortcutKey(string id) => $"mouseButtonShortcuts.shortcut.{id}";

    /// <summary>Button id of a hook event, or null for other events.</summary>
    public static string? ButtonOf(MouseAction action, int xButton) => action switch
    {
        MouseAction.MiddleDown or MouseAction.MiddleUp => Middle,
        MouseAction.XDown or MouseAction.XUp => xButton == 2 ? Forward : Back,
        _ => null,
    };

    /// <summary>Tilt direction of a horizontal wheel event (positive = right on Windows).</summary>
    public static string? WheelDirection(int horizontalDelta) =>
        horizontalDelta == 0 ? null : horizontalDelta > 0 ? WheelRight : WheelLeft;
}

/// <summary>
/// A tilt wheel can emit several packets for one push. Fire once per direction per burst; a
/// quiet gap begins a new push (port of upstream SideWheelGestureGate). Times in milliseconds.
/// </summary>
public sealed class SideWheelGestureGate
{
    public const long QuietMs = 250;
    private long? _last;
    private int _fired;

    public bool ShouldFire(string direction, long timeMs)
    {
        var bit = direction switch { MouseInputs.WheelLeft => 1, MouseInputs.WheelRight => 2, _ => 0 };
        if (bit == 0) return false;
        if (_last is { } last && timeMs >= last && timeMs - last <= QuietMs)
        {
            _last = timeMs;
        }
        else
        {
            _last = timeMs;
            _fired = 0;
        }
        if ((_fired & bit) != 0) return false;
        _fired |= bit;
        return true;
    }

    public void Reset()
    {
        _last = null;
        _fired = 0;
    }
}

/// <summary>What a held-button drag asked for (upstream MouseSpacesGestureSupport.Action).</summary>
public enum DesktopGesture { DesktopLeft, DesktopRight, TaskView, ShowDesktop }

/// <summary>
/// The virtual desktop drag (port of upstream MouseSpacesGestureSupport.Tracker): horizontal
/// travel switches desktops, vertical travel opens Task View (up) or shows the desktop (down).
/// Distances are device-independent pixels; the first axis to cross its threshold owns the press.
/// </summary>
public sealed class DesktopGestureTracker(double originX, double originY)
{
    public const double DesktopStep = 220;
    public const double OverviewStep = 150;
    public const double RepeatCooldownMs = 350;
    public const double BankedTravelLimit = DesktopStep;

    private enum Axis { None, Horizontal, Vertical }

    private Axis _axis = Axis.None;
    private double _lastX = originX, _lastY = originY;
    private double _accX, _accY;
    private double? _lastFiredAt;

    /// <summary>True once the press has done something; its release belongs to the gesture.</summary>
    public bool DidFire { get; private set; }

    public static DesktopGesture Resolved(DesktopGesture gesture, bool followsDrag) => !followsDrag ? gesture : gesture switch
    {
        DesktopGesture.DesktopLeft => DesktopGesture.DesktopRight,
        DesktopGesture.DesktopRight => DesktopGesture.DesktopLeft,
        _ => gesture,
    };

    public DesktopGesture? Advance(double x, double y, double nowMs)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
        _accX += x - _lastX;
        _accY += y - _lastY;
        _lastX = x;
        _lastY = y;

        var axis = _axis != Axis.None ? _axis : CommittedAxis();
        switch (axis)
        {
            case Axis.Vertical:
                _axis = Axis.Vertical;
                if (DidFire) return null; // an overview is a toggle: once per press
                DidFire = true;
                return _accY < 0 ? DesktopGesture.TaskView : DesktopGesture.ShowDesktop;
            case Axis.Horizontal:
                _axis = Axis.Horizontal;
                if (_lastFiredAt is { } fired && nowMs - fired < RepeatCooldownMs)
                {
                    _accX = Math.Clamp(_accX, -BankedTravelLimit, BankedTravelLimit);
                    return null;
                }
                if (Math.Abs(_accX) < DesktopStep) return null;
                var left = _accX < 0;
                _accX += left ? DesktopStep : -DesktopStep;
                _lastFiredAt = nowMs;
                DidFire = true;
                return left ? DesktopGesture.DesktopLeft : DesktopGesture.DesktopRight;
            default:
                return null;
        }
    }

    private Axis CommittedAxis()
    {
        var h = Math.Abs(_accX) / DesktopStep;
        var v = Math.Abs(_accY) / OverviewStep;
        if (h >= 1 && h >= v) return Axis.Horizontal;
        if (v >= 1) return Axis.Vertical;
        return Axis.None;
    }
}
