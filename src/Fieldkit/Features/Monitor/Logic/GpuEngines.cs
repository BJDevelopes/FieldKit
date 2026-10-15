// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Monitor.Logic;

/// <summary>
/// Turns the PDH "GPU Engine(*)\Utilization Percentage" instances into the numbers Task Manager
/// shows: a process's share of one engine is summed with every other process on that engine,
/// and the GPU's utilisation is its busiest engine.
/// </summary>
public static partial class GpuEngines
{
    public readonly record struct Instance(int Pid, string Adapter, string Engine, string EngineType);

    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<luid>0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.*)$")]
    private static partial Regex InstancePattern();

    public static Instance? Parse(string instanceName)
    {
        var m = InstancePattern().Match(instanceName);
        if (!m.Success) return null;
        var luid = m.Groups["luid"].Value.ToLowerInvariant();
        var phys = m.Groups["phys"].Value;
        return new Instance(
            int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture),
            luid,
            $"{luid}_{phys}_{m.Groups["eng"].Value}",
            m.Groups["type"].Value);
    }

    /// <summary>Total utilisation (0–100): the busiest engine across every adapter.</summary>
    public static double Total(IEnumerable<(string Name, double Value)> samples)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in samples)
        {
            if (Parse(name) is not { } inst || !double.IsFinite(value) || value <= 0) continue;
            perEngine[inst.Engine] = perEngine.GetValueOrDefault(inst.Engine) + value;
        }
        return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>Per-process utilisation (0–100): each process's busiest engine.</summary>
    public static Dictionary<int, double> PerProcess(IEnumerable<(string Name, double Value)> samples)
    {
        var perPidEngine = new Dictionary<(int, string), double>();
        foreach (var (name, value) in samples)
        {
            if (Parse(name) is not { } inst || !double.IsFinite(value) || value <= 0) continue;
            perPidEngine[(inst.Pid, inst.Engine)] = perPidEngine.GetValueOrDefault((inst.Pid, inst.Engine)) + value;
        }
        var result = new Dictionary<int, double>();
        foreach (var ((pid, _), value) in perPidEngine)
            result[pid] = Math.Clamp(Math.Max(result.GetValueOrDefault(pid), value), 0, 100);
        return result;
    }
}
