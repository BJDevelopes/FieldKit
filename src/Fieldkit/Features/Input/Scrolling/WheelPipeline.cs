// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Scrolling;

/// <summary>
/// One mouse-hook handler for every wheel feature (scroll direction, sideways scrolling, linear
/// and smooth scrolling), so they compose in one place exactly like upstream, where the smooth
/// tap applies the inverter's flip and linear scrolling's cap itself.
///
/// What it relies on: WH_MOUSE_LL reports WM_MOUSEWHEEL / WM_MOUSEHWHEEL from wheel mice.
/// Precision touchpads deliver two-finger scrolling through Direct Manipulation / pointer input
/// and do not pass through this hook, so they keep their own Windows settings. Wheel events
/// injected by other software (legacy touchpad drivers, vendor mouse utilities, automation
/// tools) carry LLMHF_INJECTED and are left untouched; events we inject ourselves carry our tag.
///
/// The hook is installed only while one of the four services runs, and removed with the last.
/// </summary>
internal static class WheelPipeline
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Attached = new(StringComparer.Ordinal);
    private static IDisposable? _hook;
    private static IDisposable? _inject;
    private static Config _config = Config.Empty;

    // Hook-thread state.
    private static readonly PointerTargetResolver Resolver = new();
    private static double _linearCarryVertical;
    private static double _linearCarryHorizontal;

    // Glide state (hook thread adds, glide thread consumes) guarded by GlideGate.
    private static readonly object GlideGate = new();
    private static readonly SmoothScrollMath.Engine Engine = new();
    private static Modifiers _glideModifiers;
    // The top-level window the glide scrolls; the tail is dropped when the pointer leaves it (upstream #1851).
    private static IntPtr _glideTarget;
    private static int _glideResponse = SmoothScrollMath.DefaultResponse;
    private static int _glideCoast = SmoothScrollMath.DefaultCoast;
    private static Thread? _glideThread;
    private static readonly AutoResetEvent GlideSignal = new(false);
    private static volatile bool _glideStop;

    /// <summary>Starts (or keeps) the shared handler on behalf of a feature.</summary>
    public static void Attach(string featureId)
    {
        lock (Gate)
        {
            Attached.Add(featureId);
            Refresh();
            if (_hook is not null) return;
            Settings.Changed += OnSettingChanged;
            _inject = InjectQueue.Acquire();
            _hook = InputHooks.AddMouseHandler(Handle, priority: 60);
        }
    }

    public static void Detach(string featureId)
    {
        lock (Gate)
        {
            Attached.Remove(featureId);
            Refresh();
            if (featureId == "smoothScroll") StopGlide();
            if (Attached.Count > 0 || _hook is null) return;
            Settings.Changed -= OnSettingChanged;
            _hook.Dispose();
            _hook = null;
            StopGlide();
            _inject?.Dispose();
            _inject = null;
        }
    }

    private static void OnSettingChanged(string key)
    {
        if (key.StartsWith("scrollInverter.", StringComparison.Ordinal)
            || key.StartsWith("scrollHorizontal.", StringComparison.Ordinal)
            || key.StartsWith("linearScroll.", StringComparison.Ordinal)
            || key.StartsWith("smoothScroll.", StringComparison.Ordinal))
        {
            lock (Gate) Refresh();
        }
    }

    // Called under Gate.
    private static void Refresh()
    {
        bool On(string id) => Attached.Contains(id);
        var invertV = On("scrollInverter") && Settings.GetBool("scrollInverter.vertical");
        var invertH = On("scrollInverter") && Settings.GetBool("scrollInverter.horizontal");
        HorizontalScrollModifier? modifier = On("scrollHorizontal") && Settings.GetBool("scrollHorizontal.enabled")
            ? ScrollDirectionMath.ParseModifier(Settings.GetString("scrollHorizontal.modifier", "shift"))
            : null;
        var directionOn = invertV || invertH || modifier is not null;
        _config = new Config(
            invertV, invertH, modifier,
            directionOn ? AppExceptions.Snapshot("scrollInverter") : [],
            On("linearScroll") && Settings.GetBool("linearScroll.enabled"),
            LinearScrollMath.SanitizedLines(Settings.GetInt("linearScroll.lines", LinearScrollMath.DefaultLines)),
            AppExceptions.Snapshot("linearScroll"),
            On("smoothScroll") && Settings.GetBool("smoothScroll.enabled"),
            SmoothScrollMath.SanitizedStep(Settings.GetInt("smoothScroll.step", SmoothScrollMath.DefaultStep)),
            SmoothScrollMath.SanitizedResponse(Settings.GetInt("smoothScroll.response", SmoothScrollMath.DefaultResponse)),
            SmoothScrollMath.SanitizedCoast(Settings.GetInt("smoothScroll.coast", SmoothScrollMath.DefaultCoast)),
            AppExceptions.Snapshot("smoothScroll"),
            SystemWheelLines());
    }

    private static int SystemWheelLines()
    {
        var lines = 3;
        return Native.SystemParametersInfo(InputNative.SPI_GETWHEELSCROLLLINES, 0, ref lines, 0) ? lines : 3;
    }

    // ------------------------------------------------------------------ hook thread

    private static bool Handle(ref MouseEvent e)
    {
        if (e.Action is not (MouseAction.Wheel or MouseAction.HWheel)) return false;
        if (e.IsOwn || e.IsInjected || e.WheelDelta == 0) return false;
        var cfg = _config;
        var horizontal = e.Action == MouseAction.HWheel;
        var delta = e.WheelDelta;
        var held = HeldModifiers();
        var x = e.X;
        var y = e.Y;

        string? target = null;
        var resolved = false;
        bool Excluded(HashSet<string> list)
        {
            if (list.Count == 0) return false;
            if (!resolved)
            {
                target = Resolver.ProcessNameAt(x, y);
                resolved = true;
            }
            return AppExceptions.Matches(list, target);
        }

        var directionOn = (cfg.InvertVertical || cfg.InvertHorizontal || cfg.HorizontalModifier is not null)
                          && !Excluded(cfg.DirectionExceptions);
        var redirect = directionOn && ScrollDirectionMath.ShouldRedirect(horizontal, held, cfg.HorizontalModifier);
        if (directionOn)
            (horizontal, delta) = ScrollDirectionMath.Apply(horizontal, delta, redirect, cfg.InvertVertical, cfg.InvertHorizontal);

        var linear = cfg.Linear && !Excluded(cfg.LinearExceptions);

        // Ctrl+wheel keeps its native zoom unless Ctrl is the sideways key in use.
        var smooth = cfg.Smooth && !Excluded(cfg.SmoothExceptions)
                     && !(held.HasFlag(Modifiers.Ctrl) && !(redirect && cfg.HorizontalModifier == HorizontalScrollModifier.Ctrl));
        if (smooth)
        {
            var ticks = linear
                ? LinearScrollMath.TicksForLines(LinearScrollMath.Lines(LinearScrollMath.Ticks(delta), cfg.LinearLines), cfg.SystemLines)
                : SmoothScrollMath.Ticks(delta);
            var distance = ticks * cfg.Step;
            AddGlide(horizontal ? 0 : distance, horizontal ? distance : 0, held, WindowHelper.TopLevelAt(x, y), cfg);
            return true;
        }

        if (linear)
        {
            ref var carry = ref horizontal ? ref _linearCarryHorizontal : ref _linearCarryVertical;
            (delta, carry) = LinearScrollMath.Normalize(delta, cfg.LinearLines, cfg.SystemLines, carry);
        }

        var unchanged = horizontal == (e.Action == MouseAction.HWheel) && delta == e.WheelDelta;
        if (unchanged) return false;
        if (delta != 0) InjectQueue.Wheel(delta, horizontal);
        return true;
    }

    private static Modifiers HeldModifiers()
    {
        var m = Modifiers.None;
        if (InputHooks.IsKeyDown(InputNative.VK_SHIFT)) m |= Modifiers.Shift;
        if (InputHooks.IsKeyDown(InputNative.VK_CONTROL)) m |= Modifiers.Ctrl;
        if (InputHooks.IsKeyDown(InputNative.VK_MENU)) m |= Modifiers.Alt;
        if (InputHooks.IsKeyDown(InputNative.VK_LWIN) || InputHooks.IsKeyDown(InputNative.VK_RWIN)) m |= Modifiers.Win;
        return m;
    }

    // ------------------------------------------------------------------ glide

    private static void AddGlide(double vertical, double horizontal, Modifiers held, IntPtr target, Config cfg)
    {
        lock (GlideGate)
        {
            // Changing modifiers mid-glide changes what the app does with the wheel, and a notch over
            // another window starts a new scroll there: either way, drop the old tail.
            if (held != _glideModifiers || target != _glideTarget) Engine.Reset();
            _glideModifiers = held;
            _glideTarget = target;
            _glideResponse = cfg.Response;
            _glideCoast = cfg.Coast;
            Engine.Add(vertical, horizontal);
        }
        EnsureGlideThread();
        GlideSignal.Set();
    }

    private static void EnsureGlideThread()
    {
        if (_glideThread is not null) return;
        lock (GlideGate)
        {
            if (_glideThread is not null) return;
            _glideStop = false;
            _glideThread = new Thread(GlideLoop) { IsBackground = true, Name = "Fieldkit smooth scroll", Priority = ThreadPriority.AboveNormal };
            _glideThread.Start();
        }
    }

    private static void StopGlide()
    {
        Thread? thread;
        lock (GlideGate)
        {
            Engine.Reset();
            thread = _glideThread;
            _glideThread = null;
            _glideStop = true;
        }
        GlideSignal.Set();
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(500);
    }

    private static void GlideLoop()
    {
        var timer = InputNative.CreateWaitableTimerExW(IntPtr.Zero, null,
            InputNative.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, InputNative.TIMER_ALL_ACCESS);
        var clock = Stopwatch.StartNew();
        double carryV = 0, carryH = 0;
        try
        {
            while (!_glideStop)
            {
                GlideSignal.WaitOne();
                var last = clock.Elapsed.TotalSeconds - SmoothScrollMath.FrameIntervalSeconds;
                while (!_glideStop)
                {
                    var now = clock.Elapsed.TotalSeconds;
                    SmoothScrollMath.Frame frame;
                    lock (GlideGate)
                    {
                        if (!Engine.IsActive) break;
                        frame = Engine.Advance(now - last, _glideResponse, _glideCoast);
                    }
                    last = now;
                    var v = frame.Vertical * SmoothScrollMath.UnitsPerPixel;
                    var h = frame.Horizontal * SmoothScrollMath.UnitsPerPixel;
                    carryV = SmoothScrollMath.Carry(carryV, v);
                    carryH = SmoothScrollMath.Carry(carryH, h);
                    double outV, outH;
                    if (frame.Finished)
                    {
                        outV = SmoothScrollMath.FinalUnits(v, carryV);
                        outH = SmoothScrollMath.FinalUnits(h, carryH);
                        carryV = carryH = 0;
                    }
                    else
                    {
                        (outV, carryV) = SmoothScrollMath.WholeUnits(v, carryV);
                        (outH, carryH) = SmoothScrollMath.WholeUnits(h, carryH);
                    }
                    // The pointer moved to another window: the rest of this scroll must not land there.
                    if (Native.GetCursorPos(out var cursor) && WindowHelper.TopLevelAt(cursor.X, cursor.Y) != Volatile.Read(ref _glideTarget))
                    {
                        lock (GlideGate) Engine.Reset();
                        break;
                    }
                    if (outV != 0) InputSender.Wheel((int)outV);
                    if (outH != 0) InputSender.Wheel((int)outH, horizontal: true);
                    if (frame.Finished) break;
                    Wait(timer, SmoothScrollMath.FrameIntervalSeconds);
                }
                carryV = carryH = 0;
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
        }
    }

    private static void Wait(IntPtr timer, double seconds)
    {
        if (timer == IntPtr.Zero)
        {
            Thread.Sleep(Math.Max(1, (int)(seconds * 1000)));
            return;
        }
        var due = -(long)(seconds * 10_000_000); // relative, 100 ns units
        if (InputNative.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            InputNative.WaitForSingleObject(timer, 100);
        else
            Thread.Sleep(1);
    }

    private sealed record Config(
        bool InvertVertical,
        bool InvertHorizontal,
        HorizontalScrollModifier? HorizontalModifier,
        HashSet<string> DirectionExceptions,
        bool Linear,
        int LinearLines,
        HashSet<string> LinearExceptions,
        bool Smooth,
        int Step,
        int Response,
        int Coast,
        HashSet<string> SmoothExceptions,
        int SystemLines)
    {
        public static readonly Config Empty = new(false, false, null, [], false, 3, [], false, 40, 65, 0, [], 3);
    }
}
