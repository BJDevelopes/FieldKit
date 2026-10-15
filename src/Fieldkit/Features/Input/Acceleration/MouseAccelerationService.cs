// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Acceleration;

/// <summary>The SPI_GETMOUSE triple and what "acceleration off" means for it.</summary>
public static class MouseAccelerationMath
{
    /// <summary>"Enhance pointer precision" is the third value of the SPI_GETMOUSE triple.</summary>
    public static bool IsAccelerated(int[] triple) => triple.Length == 3 && triple[2] != 0;

    /// <summary>Thresholds zero and acceleration off: a linear pointer.</summary>
    public static int[] Linear() => [0, 0, 0];

    public static string Encode(int[] triple) => string.Join(",", triple);

    public static int[]? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        if (parts.Length != 3) return null;
        var result = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], out result[i])) return null;
        }
        return result;
    }
}

/// <summary>
/// Turns off "Enhance pointer precision" (upstream: disable mouse acceleration). The previous
/// triple is saved (machine-specific <c>mouse.restore…</c> key, never exported) before the first
/// change and written back when the feature is switched off, uninstalled or the app exits. The
/// change is made for this session only (no SPIF_UPDATEINIFILE), so even a crash cannot make it
/// permanent: signing out restores the user's own setting, and the saved value is restored the
/// next time the app starts.
/// </summary>
internal sealed class MouseAccelerationService : FeatureService
{
    internal const string RestoreKey = "mouse.restoreAcceleration";

    public override string FeatureId => "mouseAcceleration";
    public override IReadOnlyList<string> EnableKeys => ["mouseAcceleration.enabled"];
    public override bool EnabledByDefault => false;

    /// <summary>Also runs (briefly) to put back a value left behind by a crash.</summary>
    public override bool ShouldRun() => base.ShouldRun() || Settings.Has(RestoreKey);

    protected override void OnStart()
    {
        if (!Settings.GetBool("mouseAcceleration.enabled"))
        {
            // Started only to recover from an earlier unclean exit.
            Restore();
            Ui.OnUi(() => FeatureRuntime.Reevaluate(FeatureId));
            return;
        }
        var current = Read();
        if (current is null) return;
        if (!Settings.Has(RestoreKey)) Settings.Set(RestoreKey, MouseAccelerationMath.Encode(current));
        if (MouseAccelerationMath.IsAccelerated(current) || current[0] != 0 || current[1] != 0)
            Write(MouseAccelerationMath.Linear());
    }

    protected override void OnStop() => Restore();

    private static void Restore()
    {
        var saved = MouseAccelerationMath.Decode(Settings.GetString(RestoreKey));
        if (saved is not null) Write(saved);
        Settings.Remove(RestoreKey);
    }

    private static int[]? Read()
    {
        var triple = new int[3];
        return Native.SystemParametersInfo(Native.SPI_GETMOUSE, 0, triple, 0) ? triple : null;
    }

    private static void Write(int[] triple)
    {
        if (!Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, triple, Native.SPIF_SENDCHANGE))
            Log.Warn("SPI_SETMOUSE failed");
    }
}
