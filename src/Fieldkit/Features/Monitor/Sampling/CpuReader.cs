// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// CPU load. Total and per-core values come from PDH "Processor Information\% Processor
/// Utility" (what Task Manager shows), with GetSystemTimes as a fallback for the total.
/// </summary>
public sealed class CpuReader : IDisposable
{
    private const string UtilityPath = @"\Processor Information(*)\% Processor Utility";
    private const string TimePath = @"\Processor Information(*)\% Processor Time";

    private readonly PdhQuery _pdh = new();
    private readonly string? _path;
    private long _idle, _kernel, _user;
    private bool _primed;

    public CpuReader()
    {
        _path = _pdh.Add(UtilityPath) ? UtilityPath : _pdh.Add(TimePath) ? TimePath : null;
        _pdh.Collect();
        _primed = GetSystemTimes(out _idle, out _kernel, out _user);
        Groups = CpuTopology.Groups();
    }

    public IReadOnlyList<Logic.CoreGroup> Groups { get; }

    public (double? Total, IReadOnlyList<double?> Cores) Read(bool cores)
    {
        double? total = null;
        var perCore = new List<double?>();
        if (_path is not null)
        {
            _pdh.Collect();
            var values = _pdh.Read(_path);
            var parsed = new List<(int Group, int Number, double Value)>();
            foreach (var (name, value) in values)
            {
                if (name == "_Total") total = Math.Clamp(value / 100, 0, 1);
                else if (TryParseInstance(name, out var g, out var n)) parsed.Add((g, n, value));
            }
            if (cores)
                perCore.AddRange(parsed.OrderBy(p => p.Group).ThenBy(p => p.Number).Select(p => (double?)Math.Clamp(p.Value / 100, 0, 1)));
        }

        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (total is null && _primed)
            {
                var idleDelta = idle - _idle;
                var busy = kernel - _kernel + (user - _user); // kernel time includes idle
                if (busy > 0) total = Math.Clamp(1 - (double)idleDelta / busy, 0, 1);
            }
            (_idle, _kernel, _user, _primed) = (idle, kernel, user, true);
        }
        return (total, perCore);
    }

    /// <summary>"0,3" → group 0, number 3. "0,_Total" is skipped.</summary>
    public static bool TryParseInstance(string name, out int group, out int number)
    {
        group = number = 0;
        var comma = name.IndexOf(',');
        return comma > 0
               && int.TryParse(name.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
               && int.TryParse(name.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    public void Dispose() => _pdh.Dispose();
}

/// <summary>Hybrid CPU topology: logical processors grouped by efficiency class.</summary>
internal static class CpuTopology
{
    public static IReadOnlyList<Logic.CoreGroup> Groups()
    {
        try
        {
            var classes = EfficiencyClasses();
            return Logic.CoreGrouping.Group(classes);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Efficiency class per logical processor, ordered by (group, number).</summary>
    private static List<int> EfficiencyClasses()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return [];
            var logical = new List<(int Group, int Number, int Class)>();
            var offset = 0;
            while (offset < length)
            {
                var entry = buffer + offset;
                var size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break;
                var efficiencyClass = Marshal.ReadByte(entry, 9);
                var groupCount = Marshal.ReadInt16(entry, 30);
                for (var g = 0; g < groupCount; g++)
                {
                    var affinity = entry + 32 + g * 16; // GROUP_AFFINITY: KAFFINITY Mask; WORD Group; WORD Reserved[3]
                    var mask = (ulong)Marshal.ReadInt64(affinity);
                    var group = Marshal.ReadInt16(affinity, 8);
                    for (var bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0) logical.Add((group, bit, efficiencyClass));
                    }
                }
                offset += size;
            }
            return logical.OrderBy(l => l.Group).ThenBy(l => l.Number).Select(l => l.Class).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
