// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Mascot;

public enum MascotMood { Idle, Happy, Wink, Love, Sleepy, Determined, Surprised, Searching, Thinking, Confused, Alert }

public enum MascotEye { Open, Closed, Squint, Wide, Happy, Heart, Sleepy, Determined }

public enum MascotReaction { Celebrate, Love, Surprised, Flash, Confused, WakeUp, Yawn, Perk, Bounce, Hush, Ready, Groove }

/// <summary>Things that happen around the island and make the companion react.</summary>
public enum MascotEvent
{
    DownloadFinished, DownloadFailed, FilesDropped, AgentFinished, AgentLimitRenewed, AgentStarted,
    TimerStarted, FocusDone, BreakDone, TimerDone, MusicStarted, CalendarEventBegins,
    WatchChanged, WatchSettled, WatchClosed, ChargerPlugged, FullyCharged, BatteryLow, Unlocked,
}

/// <summary>The companion's palette (light top → shade bottom, sRGB 0..1).</summary>
public sealed record MascotPalette(string Id, string Name, (double R, double G, double B) Light, (double R, double G, double B) Shade);

/// <summary>
/// The companion's model (port of upstream <c>NotchMascotSupport</c>): looks, moods, which event makes which
/// reaction, and the gate that keeps reactions from piling up.
/// </summary>
public static class MascotLogic
{
    public static readonly MascotPalette[] Palettes =
    [
        new("pearl", "Pearl", (.99, .98, .96), (.84, .85, .90)),
        new("lemon", "Lemon", (1, .96, .64), (.97, .83, .30)),
        new("peach", "Peach", (1, .86, .74), (.98, .64, .50)),
        new("rose", "Rose", (1, .80, .87), (.95, .54, .68)),
        new("lilac", "Lilac", (.88, .83, 1), (.69, .59, .96)),
        new("sky", "Sky", (.77, .90, 1), (.43, .67, .97)),
        new("mint", "Mint", (.72, .97, .86), (.40, .83, .66)),
    ];

    public static readonly (string Id, string Name)[] Shapes = [("ball", "Ball"), ("egg", "Egg"), ("squircle", "Rounded square"), ("pill", "Pill")];

    public static MascotPalette Palette(string id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

    public static (MascotEye Left, MascotEye Right, double GazeX, double GazeY, double Tilt) Face(MascotMood mood) => mood switch
    {
        MascotMood.Happy => (MascotEye.Happy, MascotEye.Happy, 0, -.02, 0),
        MascotMood.Wink => (MascotEye.Open, MascotEye.Happy, 0, 0, -.08),
        MascotMood.Love => (MascotEye.Heart, MascotEye.Heart, 0, 0, 0),
        MascotMood.Sleepy => (MascotEye.Sleepy, MascotEye.Sleepy, 0, .03, .05),
        MascotMood.Determined => (MascotEye.Determined, MascotEye.Determined, 0, 0, 0),
        MascotMood.Surprised => (MascotEye.Wide, MascotEye.Wide, 0, -.02, 0),
        MascotMood.Searching => (MascotEye.Open, MascotEye.Open, .05, 0, 0),
        MascotMood.Thinking => (MascotEye.Squint, MascotEye.Open, .06, -.06, -.1),
        MascotMood.Confused => (MascotEye.Open, MascotEye.Squint, -.03, .01, .14),
        MascotMood.Alert => (MascotEye.Wide, MascotEye.Wide, 0, 0, 0),
        _ => (MascotEye.Open, MascotEye.Open, 0, 0, 0),
    };

    /// <summary>How long a reaction plays, in seconds.</summary>
    public static double Length(MascotReaction r) => r switch
    {
        MascotReaction.Love => 1.4,
        MascotReaction.Surprised => 0.9,
        MascotReaction.Flash => 0.5,
        MascotReaction.Confused or MascotReaction.Yawn => 1.2,
        MascotReaction.WakeUp or MascotReaction.Groove => 1.6,
        _ => 1.0,
    };

    /// <summary>The face a reaction shows.</summary>
    public static MascotMood Mood(MascotReaction r) => r switch
    {
        MascotReaction.Celebrate or MascotReaction.Groove or MascotReaction.Bounce => MascotMood.Happy,
        MascotReaction.Love => MascotMood.Love,
        MascotReaction.Surprised => MascotMood.Surprised,
        MascotReaction.Confused => MascotMood.Confused,
        MascotReaction.Yawn or MascotReaction.Hush => MascotMood.Sleepy,
        MascotReaction.WakeUp => MascotMood.Happy,
        MascotReaction.Perk => MascotMood.Alert,
        MascotReaction.Ready => MascotMood.Determined,
        _ => MascotMood.Idle,
    };

    /// <summary>Upstream's event → reaction mapping.</summary>
    public static MascotReaction? ReactionFor(MascotEvent e) => e switch
    {
        MascotEvent.DownloadFinished or MascotEvent.FilesDropped or MascotEvent.AgentFinished or MascotEvent.AgentLimitRenewed
            or MascotEvent.FocusDone or MascotEvent.WatchSettled or MascotEvent.FullyCharged => MascotReaction.Celebrate,
        MascotEvent.DownloadFailed or MascotEvent.WatchClosed => MascotReaction.Confused,
        MascotEvent.AgentStarted or MascotEvent.TimerStarted or MascotEvent.BreakDone => MascotReaction.Ready,
        MascotEvent.TimerDone => MascotReaction.Surprised,
        MascotEvent.MusicStarted => MascotReaction.Groove,
        MascotEvent.CalendarEventBegins => MascotReaction.Bounce,
        MascotEvent.WatchChanged => MascotReaction.Perk,
        MascotEvent.ChargerPlugged => MascotReaction.Love,
        MascotEvent.BatteryLow => MascotReaction.Yawn,
        MascotEvent.Unlocked => MascotReaction.WakeUp,
        _ => null,
    };

    /// <summary>Vertical offset (fraction of a hop) of a reaction at <paramref name="t"/> seconds.</summary>
    public static double Hop(MascotReaction r, double t)
    {
        static double Arc(double t, double start, double length, double height) =>
            t < start || t > start + length ? 0 : height * Math.Sin(Math.PI * (t - start) / length);
        return r switch
        {
            MascotReaction.Celebrate => Arc(t, 0, 0.36, 1) + Arc(t, 0.4, 0.22, 0.3),
            MascotReaction.Surprised => Arc(t, 0, 0.35, 0.7),
            MascotReaction.Perk => Arc(t, 0, 0.3, 0.55),
            MascotReaction.Bounce => Arc(t, 0, 0.45, 0.6) + Arc(t, 0.5, 0.45, 0.42),
            MascotReaction.Groove => Math.Max(0, Math.Sin(t * Math.PI * 2 / 0.4)) * 0.45 * (t < 1.6 ? 1 : 0),
            _ => 0,
        };
    }
}

/// <summary>At least 2.5 s between reactions and never the same one twice within 30 s.</summary>
public sealed class MascotReactionGate
{
    public const double Spacing = 2.5;
    public const double Repeat = 30;

    private double _last = double.NegativeInfinity;
    private readonly Dictionary<MascotReaction, double> _lastOf = [];

    public bool TryPass(MascotReaction reaction, double now)
    {
        if (now - _last < Spacing) return false;
        if (_lastOf.TryGetValue(reaction, out var t) && now - t < Repeat) return false;
        _last = now;
        _lastOf[reaction] = now;
        return true;
    }
}
