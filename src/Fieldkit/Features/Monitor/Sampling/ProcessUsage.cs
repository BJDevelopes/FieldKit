// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Fieldkit.Interop;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

public enum BreakdownKind { Cpu, Gpu, Memory, Energy }

/// <summary>One app in a breakdown list. CPU/GPU/energy: percent (0–100); memory: bytes.</summary>
public sealed record ProcessUsageRow(int Pid, string Name, string? Path, double Value);

/// <summary>
/// Top processes by CPU, GPU, private memory or "energy" (CPU + GPU share, like upstream),
/// read in one NtQuerySystemInformation call, so protected processes are included without
/// opening them. Only runs while a breakdown list is open.
/// </summary>
public static class ProcessUsage
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, (long Created, long Cpu)> Previous = [];
    private static readonly Dictionary<(int, long), (string Name, string? Path)> Names = [];
    private static long _previousAt;

    private sealed record Raw(int Pid, string Image, long Created, long CpuTime, ulong PrivateBytes);

    /// <summary>The top rows of a kind. The first CPU/energy call primes the deltas and returns nothing.</summary>
    public static List<ProcessUsageRow> Top(BreakdownKind kind, int limit, IReadOnlyDictionary<int, double>? gpuPerProcess = null)
    {
        lock (Gate)
        {
            var raw = Snapshot();
            var now = Stopwatch.GetTimestamp();
            var elapsed100ns = _previousAt == 0 ? 0 : (now - _previousAt) * 10_000_000.0 / Stopwatch.Frequency;
            var capacity = elapsed100ns * Environment.ProcessorCount;
            var cpu = new Dictionary<int, double>();
            foreach (var p in raw)
            {
                if (capacity > 0 && Previous.TryGetValue(p.Pid, out var prev) && prev.Created == p.Created && p.CpuTime >= prev.Cpu)
                    cpu[p.Pid] = Math.Clamp((p.CpuTime - prev.Cpu) / capacity * 100, 0, 100);
            }
            Previous.Clear();
            foreach (var p in raw) Previous[p.Pid] = (p.Created, p.CpuTime);
            _previousAt = now;

            IEnumerable<(Raw P, double Value)> scored = kind switch
            {
                BreakdownKind.Cpu => raw.Where(p => cpu.ContainsKey(p.Pid)).Select(p => (p, cpu[p.Pid])).Where(x => x.Item2 >= 0.05),
                BreakdownKind.Gpu => raw.Select(p => (p, gpuPerProcess?.GetValueOrDefault(p.Pid) ?? 0)).Where(x => x.Item2 >= 0.05),
                BreakdownKind.Memory => raw.Select(p => (p, (double)p.PrivateBytes)).Where(x => x.Item2 > 0),
                _ => raw.Select(p => (p, cpu.GetValueOrDefault(p.Pid) + (gpuPerProcess?.GetValueOrDefault(p.Pid) ?? 0)))
                    .Where(x => x.Item2 >= 2), // upstream keeps only meaningfully active apps
            };
            var top = scored.OrderByDescending(x => x.Value).Take(limit).ToList();
            var live = new HashSet<(int, long)>(raw.Select(p => (p.Pid, p.Created)));
            foreach (var key in Names.Keys.Where(k => !live.Contains(k)).ToList()) Names.Remove(key);
            return top.Select(x =>
            {
                var (name, path) = Describe(x.P);
                return new ProcessUsageRow(x.P.Pid, name, path, kind == BreakdownKind.Memory ? x.Value : Math.Min(100, x.Value));
            }).ToList();
        }
    }

    /// <summary>Forgets the CPU baseline (called when every list closes).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Previous.Clear();
            _previousAt = 0;
        }
    }

    private static (string Name, string? Path) Describe(Raw p)
    {
        if (Names.TryGetValue((p.Pid, p.Created), out var known)) return known;
        string? path = null;
        var name = p.Pid == 4 ? "System" : Path.GetFileNameWithoutExtension(p.Image);
        if (p.Pid > 4)
        {
            path = WindowHelper.GetProcessPath((uint)p.Pid);
            if (path.Length == 0) path = null;
            if (path is not null)
            {
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                }
                catch
                {
                    // keep the image name
                }
            }
        }
        if (string.IsNullOrEmpty(name)) name = $"pid {p.Pid}";
        Names[(p.Pid, p.Created)] = (name, path);
        return (name, path);
    }

    private static List<Raw> Snapshot()
    {
        var result = new List<Raw>();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    size = Math.Max(size * 2, needed + (64 << 10));
                    continue;
                }
                if (status != 0) return result;
                Parse(buffer, result);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    private static void Parse(IntPtr buffer, List<Raw> result)
    {
        var x64 = IntPtr.Size == 8;
        var offset = 0L;
        while (true)
        {
            var entry = new IntPtr(buffer.ToInt64() + offset);
            var next = Marshal.ReadInt32(entry, 0);
            var created = Marshal.ReadInt64(entry, 32);
            var cpu = Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48);
            var privateBytes = (ulong)Marshal.ReadInt64(entry, 8);
            var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
            var namePtr = Marshal.ReadIntPtr(entry, x64 ? 64 : 60);
            var pid = (int)Marshal.ReadIntPtr(entry, x64 ? 80 : 68).ToInt64();
            if (pid != 0)
            {
                var image = namePtr != IntPtr.Zero && nameLength > 0 ? Marshal.PtrToStringUni(namePtr, nameLength / 2) : string.Empty;
                result.Add(new Raw(pid, image, created, cpu, privateBytes));
            }
            if (next == 0) break;
            offset += next;
        }
    }
}
