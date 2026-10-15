// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Monitor.Logic;

/// <summary>A reading that can sit in the notification area (upstream MenuBarMetric), in default order.</summary>
public enum ReadoutMetric
{
    Cpu, CpuTemperature, Gpu, GpuTemperature, Memory,
    Battery, BatteryTime, BatteryTemperature,
    Network, DiskUsage, DiskActivity, ConnectedDevices, Power, FanSpeed,
}

public enum ReadoutTint { Default, Green, Yellow, Red }

/// <param name="Label">Short label drawn above the value when the reading has its own icon ("CPU").</param>
/// <param name="Value">Short value ("42%", "47°").</param>
/// <param name="Value2">Second value for two-line readings (network ↓/↑, disk read/write).</param>
public sealed record ReadoutValue(ReadoutMetric Metric, string Label, string Value, string Tooltip,
    string? Value2 = null, ReadoutTint Tint = ReadoutTint.Default);

/// <summary>One notification-area icon: up to two short lines.</summary>
public sealed record TrayIconContent(string Key, string Line1, string? Line2, ReadoutTint Tint, string Tooltip,
    IReadOnlyList<ReadoutMetric> Metrics);

/// <summary>
/// Packs readings into 16 px notification-area icons. Each icon holds two short lines, so:
/// <list type="bullet">
/// <item>Separate on: every reading gets its own icon (label over value). With "combine usage
/// and temperature", CPU/GPU usage and temperature share one icon.</item>
/// <item>Separate off (default, like upstream's single menu bar item): readings are packed two
/// per icon without labels, so the fewest icons are used.</item>
/// </list>
/// Two-line readings (network, disk activity) and tinted readings always get their own icon.
/// </summary>
public static class TrayLayout
{
    public static ReadoutMetric? TemperaturePartner(ReadoutMetric m) => m switch
    {
        ReadoutMetric.Cpu => ReadoutMetric.CpuTemperature,
        ReadoutMetric.Gpu => ReadoutMetric.GpuTemperature,
        _ => null,
    };

    public static List<TrayIconContent> Layout(IReadOnlyList<ReadoutValue> values, bool separate, bool combineTemperatures)
    {
        var icons = new List<TrayIconContent>();
        var used = new HashSet<ReadoutMetric>();
        ReadoutValue? pending = null;

        void Add(string line1, string? line2, ReadoutTint tint, params ReadoutValue[] parts) =>
            icons.Add(new TrayIconContent(
                string.Join("+", parts.Select(p => p.Metric)),
                line1, line2, tint,
                string.Join("\n", parts.Select(p => p.Tooltip)),
                parts.Select(p => p.Metric).ToList()));

        void Flush()
        {
            if (pending is null) return;
            Add(pending.Label, pending.Value, pending.Tint, pending);
            pending = null;
        }

        foreach (var v in values)
        {
            if (!used.Add(v.Metric)) continue;
            if (v.Value2 is not null)
            {
                if (!separate) Flush();
                Add(v.Value, v.Value2, v.Tint, v);
                continue;
            }
            if (separate)
            {
                if (combineTemperatures && TemperaturePartner(v.Metric) is { } partnerId
                    && values.FirstOrDefault(x => x.Metric == partnerId) is { } partner && used.Add(partner.Metric))
                {
                    Add(v.Value, partner.Value, v.Tint, v, partner);
                }
                else if (combineTemperatures && IsTemperatureOfPresentUsage(v.Metric, values))
                {
                    used.Remove(v.Metric); // drawn later, with its usage reading
                }
                else
                {
                    Add(v.Label, v.Value, v.Tint, v);
                }
                continue;
            }
            if (v.Tint != ReadoutTint.Default)
            {
                Flush();
                Add(v.Label, v.Value, v.Tint, v);
                continue;
            }
            if (pending is null)
            {
                pending = v;
            }
            else
            {
                Add(pending.Value, v.Value, ReadoutTint.Default, pending, v);
                pending = null;
            }
        }
        Flush();
        return icons;
    }

    private static bool IsTemperatureOfPresentUsage(ReadoutMetric m, IReadOnlyList<ReadoutValue> values) => m switch
    {
        ReadoutMetric.CpuTemperature => values.Any(v => v.Metric == ReadoutMetric.Cpu),
        ReadoutMetric.GpuTemperature => values.Any(v => v.Metric == ReadoutMetric.Gpu),
        _ => false,
    };
}
