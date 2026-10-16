// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;

namespace Fieldkit.Core;

/// <summary>
/// "Always run as administrator": when it is on, a Fieldkit that starts without administrator rights asks Windows for
/// them (the usual permission prompt) and restarts elevated. Windows does not let a normal app see keys and mouse input
/// aimed at an administrator window, so this is what makes the input features work there. Declining the prompt just
/// leaves Fieldkit running as a normal user.
/// </summary>
/// <remarks>
/// It always asks each time rather than using a scheduled task or a compatibility flag that would start Fieldkit elevated
/// without asking: an elevated task or exe in a folder the user can write to would let any program running as the user
/// become administrator, which is what the permission prompt exists to prevent.
/// </remarks>
public static class ElevationPolicy
{
    public const string AlwaysKey = "general.alwaysAdmin";
    public const string RestartSwitch = "--elevated-restart";

    public static bool AlwaysAdmin => Settings.GetBool(AlwaysKey, false);

    /// <summary>
    /// Whether this start should ask for administrator rights: the switch is on, Fieldkit is not elevated yet, and this
    /// is not already the restart that was asked for (which would loop if it was declined).
    /// </summary>
    public static bool ShouldElevateAtStartup(bool alwaysAdmin, bool isElevated, IEnumerable<string> args) =>
        alwaysAdmin && !isElevated && !args.Contains(RestartSwitch, StringComparer.OrdinalIgnoreCase);

    /// <summary>The arguments of the elevated copy: what this start was given (a link or action, --background) plus the restart switch.</summary>
    public static string RestartArguments(IEnumerable<string> args)
    {
        var parts = new List<string> { RestartSwitch };
        parts.AddRange(args.Where(a => !string.Equals(a, RestartSwitch, StringComparison.OrdinalIgnoreCase)).Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>Starts an elevated copy. False when the permission prompt was declined or the copy could not start.</summary>
    public static bool TryRestartElevated(string executable, IEnumerable<string> args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable, RestartArguments(args)) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Administrator permission was declined; Fieldkit keeps running as a normal user");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Restarting as administrator failed: {ex.Message}");
            return false;
        }
    }
}
