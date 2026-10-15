// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;
using static Fieldkit.Features.Energy.EnergyNative;

namespace Fieldkit.Features.Energy;

/// <summary>What the lid action was before we changed it (persisted for crash recovery).</summary>
public sealed record LidRestoreRecord(Guid Scheme, uint Ac, uint Dc);

/// <summary>
/// "Keep working with the laptop lid closed": sets the active power scheme's lid-close
/// action to "Do nothing" (AC and DC) and puts the previous values back afterwards.
/// The previous values are saved BEFORE the change so a crash is repaired at next launch.
/// </summary>
public static class LidCloseOverride
{
    public const string RestoreKey = "keepAwake.restore.lid";

    private const uint DoNothing = 0;

    public static bool IsApplied => Settings.Has(RestoreKey);

    /// <summary>Applies the override. Returns false (and changes nothing) on failure.</summary>
    public static bool Apply()
    {
        if (IsApplied) return true;
        try
        {
            if (!TryGetActiveScheme(out var scheme)) return false;
            var sub = GUID_SYSTEM_BUTTON_SUBGROUP;
            var setting = GUID_LIDCLOSE_ACTION;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) != 0) return false;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) != 0) return false;
            Settings.Set(RestoreKey, new LidRestoreRecord(scheme, ac, dc));
            Settings.Flush();
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, DoNothing) != 0 ||
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, DoNothing) != 0 ||
                PowerSetActiveScheme(IntPtr.Zero, ref scheme) != 0)
            {
                Restore();
                return false;
            }
            Log.Info($"Lid close action set to Do nothing (was AC {ac}, DC {dc})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Changing the lid close action failed", ex);
            Restore();
            return false;
        }
    }

    /// <summary>Puts the saved lid action back (no-op when nothing is saved).</summary>
    public static bool Restore()
    {
        var record = Settings.Get<LidRestoreRecord?>(RestoreKey, null);
        if (record is null)
        {
            Settings.Remove(RestoreKey);
            return true;
        }
        try
        {
            var scheme = record.Scheme;
            var sub = GUID_SYSTEM_BUTTON_SUBGROUP;
            var setting = GUID_LIDCLOSE_ACTION;
            var ok = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, record.Ac) == 0;
            ok &= PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, record.Dc) == 0;
            if (TryGetActiveScheme(out var active) && active == scheme) ok &= PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
            if (ok)
            {
                Settings.Remove(RestoreKey);
                Settings.Flush();
                Log.Info($"Lid close action restored (AC {record.Ac}, DC {record.Dc})");
            }
            else
            {
                Log.Warn("Restoring the lid close action failed; will retry");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Restoring the lid close action failed", ex);
            return false;
        }
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return false;
        try
        {
            scheme = Marshal.PtrToStructure<Guid>(ptr);
            return true;
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <summary>Asks Windows to sleep (used when a session ends while the lid is already closed).</summary>
    public static void RequestSleep()
    {
        try
        {
            EnableShutdownPrivilege();
            if (!SetSuspendState(false, false, false)) Log.Warn($"SetSuspendState failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Error("Requesting sleep failed", ex);
        }
    }
}
