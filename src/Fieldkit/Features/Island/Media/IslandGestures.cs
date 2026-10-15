// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Media;

public enum IslandGestureAction { None, Open, Close, NextTrack, PreviousTrack }

/// <summary>
/// The island's scroll gestures (port of upstream <c>NotchGestureSupport</c>): scroll down opens,
/// scroll up over the header closes, a horizontal swipe over the music changes tracks.
/// One action per sequence; a pause of more than 0.35 s starts a new sequence.
/// Deltas are in upstream points: a notched wheel step counts as 24, positive y = toward the user.
/// </summary>
public sealed class IslandGestureTracker
{
    public const double SequenceGap = 0.35;
    public const double LockTravel = 4;
    public const double Dominance = 1.5;
    public const double HorizontalThreshold = 40;
    public const double VerticalThreshold = 16;
    public const double WheelNotch = 24;

    private double _x;
    private double _y;
    private double _lastTime = double.NegativeInfinity;
    private int _axis; // 0 none, 1 horizontal, 2 vertical
    private bool _fired;

    /// <summary>Feeds one event. <paramref name="time"/> in seconds; <paramref name="precise"/> for touchpads.</summary>
    public IslandGestureAction Feed(double dx, double dy, double time, bool precise, bool expanded,
        bool verticalAllowed, bool horizontalAllowed)
    {
        if (time - _lastTime > SequenceGap || time < _lastTime) Reset();
        _lastTime = time;
        if (_fired) return IslandGestureAction.None;
        _x += dx;
        _y += dy;
        if (_axis == 0)
        {
            var ax = Math.Abs(_x);
            var ay = Math.Abs(_y);
            if (Math.Max(ax, ay) < LockTravel) return IslandGestureAction.None;
            if (ax >= ay * Dominance && precise) _axis = 1;
            else if (ay >= ax * Dominance) _axis = 2;
            else return IslandGestureAction.None;
        }
        if (_axis == 1)
        {
            if (!horizontalAllowed || Math.Abs(_x) < HorizontalThreshold) return IslandGestureAction.None;
            _fired = true;
            return _x < 0 ? IslandGestureAction.NextTrack : IslandGestureAction.PreviousTrack;
        }
        if (!verticalAllowed || Math.Abs(_y) < VerticalThreshold) return IslandGestureAction.None;
        if (_y > 0 && !expanded) { _fired = true; return IslandGestureAction.Open; }
        if (_y < 0 && expanded) { _fired = true; return IslandGestureAction.Close; }
        return IslandGestureAction.None;
    }

    public void Reset()
    {
        _x = _y = 0;
        _axis = 0;
        _fired = false;
    }
}
