// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Scrolling;

/// <summary>
/// Pure maths for smooth wheel scrolling (port of upstream SmoothScrollSupport).
///
/// Each wheel notch adds a distance to a per-axis budget; frames consume the budget with an
/// exponential ease so the glide has the same shape whatever the frame cadence. Distances are
/// in "pixels" like upstream (the step setting is the distance of one notch) and converted to
/// Windows wheel units at emission: the default step of 40 equals one 120-unit notch, which is
/// exactly what Windows would have scrolled on its own.
/// </summary>
public static class SmoothScrollMath
{
    public const int WheelDelta = 120;

    /// <summary>Wakeup cadence of the glide thread; elapsed time drives the motion.</summary>
    public const double FrameIntervalSeconds = 1.0 / 120.0;

    /// <summary>A long stall must not dump an entire old tail in one jump.</summary>
    public const double MaximumFrameIntervalSeconds = 1.0 / 20.0;

    /// <summary>Leftovers smaller than this (pixels) are flushed in one final frame.</summary>
    public const double FinishThreshold = 1.0;

    public const int StepMin = 20, StepMax = 100, DefaultStep = 40;
    public const int ResponseMin = 0, ResponseMax = 100, DefaultResponse = 65;
    public const int CoastMin = 0, CoastMax = 100, DefaultCoast = 0;

    private const double SlowResponseTime = 0.16;
    private const double FastResponseTime = 0.04;
    private const double FullCoastStretch = 3.0;
    private const double MinimumGlideSpeed = 60.0;

    /// <summary>Wheel units per glide pixel (120 units per default step).</summary>
    public const double UnitsPerPixel = (double)WheelDelta / DefaultStep;

    public static int SanitizedStep(int value) => value == 0 ? DefaultStep : Math.Clamp(value, StepMin, StepMax);
    public static int SanitizedResponse(int value) => Math.Clamp(value, ResponseMin, ResponseMax);
    public static int SanitizedCoast(int value) => Math.Clamp(value, CoastMin, CoastMax);

    /// <summary>Notches carried by one wheel event (fractions for high-resolution wheels).</summary>
    public static double Ticks(int wheelDelta) => wheelDelta / (double)WheelDelta;

    /// <summary>
    /// One at the start of a notch, growing toward the full stretch as the glide nears its end.
    /// Without a known glide the plain curve applies.
    /// </summary>
    public static double CoastStretch(int coast, double remaining, double glide)
    {
        if (!double.IsFinite(glide) || glide <= remaining || remaining < 0) return 1;
        var normalized = (SanitizedCoast(coast) - CoastMin) / (double)(CoastMax - CoastMin);
        var travelled = 1 - remaining / glide;
        return 1 + normalized * (FullCoastStretch - 1) * travelled * travelled;
    }

    /// <summary>
    /// Distance to emit this frame. Exponential decay composes: two short intervals leave the
    /// same remainder as one interval of their combined length.
    /// </summary>
    public static double FrameDelta(double remaining, double elapsed, int response, int coast = 0, double glide = 0)
    {
        if (!double.IsFinite(remaining) || remaining == 0 || !double.IsFinite(elapsed) || elapsed <= 0) return 0;
        var magnitude = Math.Abs(remaining);
        var stretch = CoastStretch(coast, magnitude, glide);
        if (magnitude <= FinishThreshold / stretch) return remaining;
        var clamped = Math.Min(elapsed, MaximumFrameIntervalSeconds);
        var normalizedResponse = (SanitizedResponse(response) - ResponseMin) / (double)(ResponseMax - ResponseMin);
        var responseTime = (SlowResponseTime - normalizedResponse * (SlowResponseTime - FastResponseTime)) * stretch;
        var eased = magnitude * (1 - Math.Exp(-clamped / responseTime));
        var emitted = Math.Min(magnitude, Math.Max(eased, MinimumGlideSpeed / stretch * clamped));
        return remaining < 0 ? -emitted : emitted;
    }

    /// <summary>Splits a distance into whole units to post now and a fraction to carry.</summary>
    public static (double Whole, double Carry) WholeUnits(double distance, double carry)
    {
        var total = distance + carry;
        if (!double.IsFinite(total)) return (0, 0);
        var whole = Math.Truncate(total);
        return (whole, total - whole);
    }

    /// <summary>The last frame rounds its leftover out: there is no next frame to spend it in.</summary>
    public static double FinalUnits(double distance, double carry)
    {
        var total = distance + carry;
        return double.IsFinite(total) ? Math.Round(total, MidpointRounding.AwayFromZero) : 0;
    }

    /// <summary>A reversal drops the old fraction so the new direction's first unit is not eaten.</summary>
    public static double Carry(double current, double continuing) => DirectionsOppose(current, continuing) ? 0 : current;

    public static bool DirectionsOppose(double a, double b) => a != 0 && b != 0 && (a < 0) != (b < 0);

    public readonly record struct Frame(double Vertical, double Horizontal, bool Finished);

    /// <summary>Per-axis distance budget. Not thread-safe: callers lock around it.</summary>
    public sealed class Engine
    {
        public double RemainingVertical { get; private set; }
        public double RemainingHorizontal { get; private set; }
        private double _glideVertical;
        private double _glideHorizontal;

        public bool IsActive => RemainingVertical != 0 || RemainingHorizontal != 0;

        /// <summary>Adds pixel distances. A reversal replaces the old tail on that axis.</summary>
        public void Add(double vertical, double horizontal)
        {
            RemainingVertical = AddTo(vertical, RemainingVertical, ref _glideVertical);
            RemainingHorizontal = AddTo(horizontal, RemainingHorizontal, ref _glideHorizontal);
        }

        public Frame Advance(double elapsed, int response, int coast = 0)
        {
            var v = FrameDelta(RemainingVertical, elapsed, response, coast, _glideVertical);
            var h = FrameDelta(RemainingHorizontal, elapsed, response, coast, _glideHorizontal);
            RemainingVertical -= v;
            RemainingHorizontal -= h;
            return new Frame(v, h, !IsActive);
        }

        public void Reset()
        {
            RemainingVertical = RemainingHorizontal = 0;
            _glideVertical = _glideHorizontal = 0;
        }

        private static double AddTo(double distance, double remaining, ref double glide)
        {
            if (!double.IsFinite(distance) || distance == 0) return remaining;
            remaining = DirectionsOppose(distance, remaining) ? distance : remaining + distance;
            glide = Math.Abs(remaining);
            return remaining;
        }
    }
}

/// <summary>
/// Linear scrolling (port of upstream ScrollWheelSupport linear helpers): every notch is worth
/// the same distance however fast the wheel spins. Vendor drivers report a fast spin as one
/// event of 240, 360… units; any event worth at least one notch counts as exactly one notch,
/// while a high-resolution wheel's fraction stays a fraction.
/// </summary>
public static class LinearScrollMath
{
    public const int LinesMin = 1, LinesMax = 10, DefaultLines = 3;

    public static int SanitizedLines(int value) => value == 0 ? DefaultLines : Math.Clamp(value, LinesMin, LinesMax);

    /// <summary>Notches of one event: whole events are one notch, fractions stay fractions.</summary>
    public static double Ticks(int wheelDelta)
    {
        if (wheelDelta == 0) return 0;
        if (Math.Abs(wheelDelta) >= SmoothScrollMath.WheelDelta) return Math.Sign(wheelDelta);
        return wheelDelta / (double)SmoothScrollMath.WheelDelta;
    }

    /// <summary>Lines an event scrolls under linear scrolling: at most one notch, multiplied out.</summary>
    public static double Lines(double ticks, int linesPerNotch)
    {
        if (!double.IsFinite(ticks) || ticks == 0) return 0;
        var lines = Math.Min(Math.Abs(ticks), 1) * SanitizedLines(linesPerNotch);
        return ticks < 0 ? -lines : lines;
    }

    /// <summary>
    /// Windows wheel units that scroll <paramref name="lines"/> lines given the system's lines per
    /// notch (SPI_GETWHEELSCROLLLINES; page scrolling or 0 counts as the default 3).
    /// </summary>
    public static double UnitsForLines(double lines, int systemLinesPerNotch)
    {
        var system = systemLinesPerNotch is > 0 and <= 100 ? systemLinesPerNotch : DefaultLines;
        return lines / system * SmoothScrollMath.WheelDelta;
    }

    /// <summary>Notches (for the glide) that <paramref name="lines"/> lines are worth.</summary>
    public static double TicksForLines(double lines, int systemLinesPerNotch) =>
        UnitsForLines(lines, systemLinesPerNotch) / SmoothScrollMath.WheelDelta;

    /// <summary>
    /// The delta to post for one raw event, plus the fraction to carry. A reversal drops the carry.
    /// </summary>
    public static (int Delta, double Carry) Normalize(int wheelDelta, int linesPerNotch, int systemLinesPerNotch, double carry)
    {
        var units = UnitsForLines(Lines(Ticks(wheelDelta), linesPerNotch), systemLinesPerNotch);
        var kept = SmoothScrollMath.Carry(carry, units);
        var (whole, rest) = SmoothScrollMath.WholeUnits(units, kept);
        return ((int)whole, rest);
    }
}

/// <summary>The key that turns the vertical wheel sideways (upstream ScrollHorizontalModifier).</summary>
public enum HorizontalScrollModifier { Shift, Alt, Ctrl, Win }

/// <summary>Direction rules shared by the raw and the smoothed wheel.</summary>
public static class ScrollDirectionMath
{
    public static HorizontalScrollModifier ParseModifier(string? raw) => raw?.ToLowerInvariant() switch
    {
        "alt" or "option" => HorizontalScrollModifier.Alt,
        "ctrl" or "control" => HorizontalScrollModifier.Ctrl,
        "win" or "command" => HorizontalScrollModifier.Win,
        _ => HorizontalScrollModifier.Shift,
    };

    public static string StorageValue(HorizontalScrollModifier m) => m switch
    {
        HorizontalScrollModifier.Alt => "alt",
        HorizontalScrollModifier.Ctrl => "ctrl",
        HorizontalScrollModifier.Win => "win",
        _ => "shift",
    };

    public static Modifiers ToModifiers(HorizontalScrollModifier m) => m switch
    {
        HorizontalScrollModifier.Alt => Modifiers.Alt,
        HorizontalScrollModifier.Ctrl => Modifiers.Ctrl,
        HorizontalScrollModifier.Win => Modifiers.Win,
        _ => Modifiers.Shift,
    };

    /// <summary>Only the selected modifier, held alone, redirects; other combinations keep their meaning.</summary>
    public static bool ShouldRedirect(bool isHorizontal, Modifiers held, HorizontalScrollModifier? wanted) =>
        !isHorizontal && wanted is { } w && held == ToModifiers(w);

    /// <summary>
    /// Resolves the axis and signed delta to post. A redirected notch scrolls right when the wheel
    /// turns down (the way Shift+wheel already behaves in Windows apps), and inversion follows the
    /// axis the user actually sees move.
    /// </summary>
    public static (bool Horizontal, int Delta) Apply(bool isHorizontal, int delta, bool redirect, bool invertVertical, bool invertHorizontal)
    {
        if (redirect && !isHorizontal)
        {
            isHorizontal = true;
            delta = -delta;
        }
        if (isHorizontal ? invertHorizontal : invertVertical) delta = -delta;
        return (isHorizontal, delta);
    }
}
