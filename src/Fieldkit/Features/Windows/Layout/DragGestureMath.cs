// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

public enum GestureState { Idle, Pending, Active }

/// <summary>What the mouse hook saw, reduced to what the decision needs.</summary>
public enum GestureInput { ButtonDownMatched, ButtonDownUnmatched, SameButtonDown, OtherButtonDown, DragWithinSlop, DragPastSlop, TrackedUp, OtherEvent }

/// <summary>What the hook does with the event.</summary>
public enum GestureDecision
{
    /// <summary>Let the event through untouched.</summary>
    PassThrough,
    /// <summary>Hold the press (swallow) while it may still become a drag.</summary>
    Arm,
    /// <summary>Keep holding: not moved enough yet.</summary>
    Hold,
    /// <summary>The press became a window drag.</summary>
    Promote,
    /// <summary>Drive the window with this movement.</summary>
    Apply,
    /// <summary>Last movement of the drag.</summary>
    Finish,
    /// <summary>It was only a click: give the held press back as a whole click.</summary>
    ReplayClick,
    /// <summary>Give the held press back and let this event through.</summary>
    FlushThenPass,
    /// <summary>Forget the stale press and judge this event afresh.</summary>
    Restart,
}

/// <summary>
/// The modifier-drag rules (port of upstream WindowGestureSupport) in screen coordinates
/// with Y down. A press is only held while it may still become a gesture, and a press that
/// never became one is always given back exactly once.
/// </summary>
public static class DragGestureMath
{
    /// <summary>How far the pointer may wander and still count as a click.</summary>
    public const double DragSlop = 6;

    public const Modifiers DefaultModifiers = Modifiers.Ctrl | Modifiers.Win;
    public const Modifiers MoveModifierMask = Modifiers.Ctrl | Modifiers.Alt | Modifiers.Win;

    public static bool ExceedsSlop(Point origin, Point now)
    {
        var dx = now.X - origin.X;
        var dy = now.Y - origin.Y;
        return Math.Sqrt(dx * dx + dy * dy) > DragSlop;
    }

    public static GestureDecision Decide(GestureState state, GestureInput input) => state switch
    {
        GestureState.Idle => input == GestureInput.ButtonDownMatched ? GestureDecision.Arm : GestureDecision.PassThrough,
        GestureState.Pending => input switch
        {
            GestureInput.DragPastSlop => GestureDecision.Promote,
            GestureInput.DragWithinSlop => GestureDecision.Hold,
            GestureInput.TrackedUp => GestureDecision.ReplayClick,
            GestureInput.SameButtonDown => GestureDecision.Restart,
            GestureInput.OtherButtonDown => GestureDecision.FlushThenPass,
            _ => GestureDecision.FlushThenPass,
        },
        _ => input switch
        {
            GestureInput.DragPastSlop or GestureInput.DragWithinSlop => GestureDecision.Apply,
            GestureInput.TrackedUp => GestureDecision.Finish,
            _ => GestureDecision.PassThrough,
        },
    };

    /// <summary>"ctrl+win" → modifiers; invalid or Shift-only values fall back to the default.</summary>
    public static Modifiers ParseModifiers(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultModifiers;
        var mods = Modifiers.None;
        foreach (var token in stored.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": mods |= Modifiers.Ctrl; break;
                case "alt": mods |= Modifiers.Alt; break;
                case "win": mods |= Modifiers.Win; break;
                case "shift": break;
                default: return DefaultModifiers;
            }
        }
        mods &= MoveModifierMask;
        return mods == Modifiers.None ? DefaultModifiers : mods;
    }

    public static string ModifiersStorage(Modifiers mods)
    {
        mods &= MoveModifierMask;
        if (mods == Modifiers.None) mods = DefaultModifiers;
        var parts = new List<string>();
        if (mods.HasFlag(Modifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(Modifiers.Win)) parts.Add("win");
        return string.Join("+", parts);
    }

    public static string ModifiersText(Modifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Modifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>
    /// Nine regions: corners resize two edges, sides one; the centre picks its nearest edge so
    /// resizing from anywhere always does something.
    /// </summary>
    public static ResizeEdges EdgesAt(Point point, Rect frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return ResizeEdges.None;
        var x = Math.Clamp(point.X - frame.Left, 0, frame.Width);
        var y = Math.Clamp(point.Y - frame.Top, 0, frame.Height);
        var edges = ResizeEdges.None;
        if (x < frame.Width / 3) edges |= ResizeEdges.Left;
        else if (x > frame.Width * 2 / 3) edges |= ResizeEdges.Right;
        if (y < frame.Height / 3) edges |= ResizeEdges.Top;
        else if (y > frame.Height * 2 / 3) edges |= ResizeEdges.Bottom;
        if (edges != ResizeEdges.None) return edges;
        var candidates = new (double Distance, ResizeEdges Edge)[]
        {
            (x, ResizeEdges.Left), (frame.Width - x, ResizeEdges.Right), (y, ResizeEdges.Top), (frame.Height - y, ResizeEdges.Bottom),
        };
        return candidates.MinBy(c => c.Distance).Edge;
    }

    public static Rect Moved(Rect original, Point start, Point now) =>
        new(original.X + now.X - start.X, original.Y + now.Y - start.Y, original.Width, original.Height);

    /// <summary>Resizes the chosen edges keeping the opposite ones fixed, with a safety minimum.</summary>
    public static Rect Resized(Rect original, Point start, Point now, ResizeEdges edges, double minWidth = 120, double minHeight = 80)
    {
        var dx = now.X - start.X;
        var dy = now.Y - start.Y;
        double x = original.X, y = original.Y, w = original.Width, h = original.Height;
        if (edges.HasFlag(ResizeEdges.Left))
        {
            w = Math.Max(minWidth, original.Width - dx);
            x = original.Right - w;
        }
        else if (edges.HasFlag(ResizeEdges.Right))
        {
            w = Math.Max(minWidth, original.Width + dx);
        }
        if (edges.HasFlag(ResizeEdges.Top))
        {
            h = Math.Max(minHeight, original.Height - dy);
            y = original.Bottom - h;
        }
        else if (edges.HasFlag(ResizeEdges.Bottom))
        {
            h = Math.Max(minHeight, original.Height + dy);
        }
        return new Rect(x, y, w, h);
    }
}
