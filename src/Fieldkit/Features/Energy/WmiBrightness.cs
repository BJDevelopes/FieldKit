// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;
using Fieldkit.Core;

namespace Fieldkit.Features.Energy;

/// <summary>Laptop panel brightness through WMI (root\WMI WmiMonitorBrightness*). Blocking: call off the UI thread.</summary>
public static class WmiBrightness
{
    private const string Scope = @"root\WMI";

    /// <summary>Instance name → current percent for every active WMI-controllable panel.</summary>
    public static Dictionary<string, int> Read()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT InstanceName, CurrentBrightness, Active FROM WmiMonitorBrightness");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    if (obj["Active"] is bool active && !active) continue;
                    if (obj["InstanceName"] is string name && obj["CurrentBrightness"] is byte level) result[name] = level;
                }
            }
        }
        catch (ManagementException)
        {
            // No WMI-controllable panel (desktop PCs): not an error.
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI brightness read failed: {ex.Message}");
        }
        return result;
    }

    public static bool Write(string instanceName, int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    if (obj["InstanceName"] is not string name || !string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase)) continue;
                    obj.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI brightness write failed: {ex.Message}");
        }
        return false;
    }
}
