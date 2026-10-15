// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Fieldkit.Interop;
using static Fieldkit.Features.QuickTools.QuickToolsNative;

namespace Fieldkit.Features.QuickTools;

/// <summary>One raw process from a system snapshot.</summary>
public sealed record ProcessSample(uint Pid, uint ParentPid, string Name, long CreateTime, long CpuTime, long Memory);

/// <summary>A row of the process list (possibly a group of related processes).</summary>
public sealed record ProcessRow(uint Pid, uint ParentPid, string Name, string Path, double Cpu, long Memory, long CreateTime, bool Protected,
    IReadOnlyList<uint> Members)
{
    public int Count => Members.Count;
}

public enum ProcessSort { Cpu, Memory, Name, Pid }

/// <summary>Pure process-list rules (upstream KillProcessSupport), unit-tested.</summary>
public static class ProcessLogic
{
    public const int DescendantLimit = 4096;

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "system idle process", "registry", "memory compression", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "dwm", "fontdrvhost", "svchost", "fieldkit",
    };

    /// <summary>PIDs 0 and 4, this app, and the processes Windows needs to keep running.</summary>
    public static bool IsProtected(uint pid, string name, uint ownPid)
    {
        if (pid <= 4 || pid == ownPid) return true;
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return ProtectedNames.Contains(bare);
    }

    /// <summary>CPU % of the whole machine between two samples (Task Manager convention).</summary>
    public static double CpuPercent(long cpuDelta100ns, long elapsed100ns, int processors) =>
        elapsed100ns <= 0 || processors <= 0 || cpuDelta100ns <= 0 ? 0 : Math.Min(100, cpuDelta100ns * 100.0 / elapsed100ns / processors);

    public static bool Matches(ProcessRow row, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || row.Pid.ToString() == q;
    }

    /// <summary>Default directions: name ascending, numbers descending.</summary>
    public static bool NaturalAscending(ProcessSort sort) => sort == ProcessSort.Name;

    public static List<ProcessRow> Sorted(IEnumerable<ProcessRow> rows, ProcessSort sort, bool ascending)
    {
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var c = sort switch
            {
                ProcessSort.Cpu => a.Cpu.CompareTo(b.Cpu),
                ProcessSort.Memory => a.Memory.CompareTo(b.Memory),
                ProcessSort.Pid => a.Pid.CompareTo(b.Pid),
                _ => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            };
            if (!ascending) c = -c;
            return c != 0 ? c : a.Pid.CompareTo(b.Pid); // ties: lower PID first
        });
        return list;
    }

    /// <summary>Breadth-first descendants of <paramref name="root"/> (each PID once, capped).</summary>
    public static List<uint> Descendants(uint root, IReadOnlyDictionary<uint, uint> parentOf)
    {
        var children = new Dictionary<uint, List<uint>>();
        foreach (var (pid, parent) in parentOf)
        {
            if (pid == parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(pid);
        }
        var result = new List<uint>();
        var seen = new HashSet<uint> { root };
        var queue = new Queue<uint>();
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < DescendantLimit)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var kids)) continue;
            foreach (var kid in kids.OrderBy(k => k))
            {
                if (!seen.Add(kid)) continue;
                result.Add(kid);
                queue.Enqueue(kid);
                if (result.Count >= DescendantLimit) break;
            }
        }
        return result;
    }

    /// <summary>
    /// Folds processes with the same executable into one row (summing CPU and memory). The row
    /// keeps the oldest member's PID and parent.
    /// </summary>
    public static List<ProcessRow> Grouped(IEnumerable<ProcessRow> rows)
    {
        var result = new List<ProcessRow>();
        foreach (var group in rows.GroupBy(r => r.Path.Length > 0 ? r.Path.ToLowerInvariant() : "name:" + r.Name.ToLowerInvariant()))
        {
            var members = group.OrderBy(r => r.CreateTime).ThenBy(r => r.Pid).ToList();
            if (members.Count == 1)
            {
                result.Add(members[0]);
                continue;
            }
            var head = members[0];
            result.Add(head with
            {
                Cpu = members.Sum(m => m.Cpu),
                Memory = members.Sum(m => m.Memory),
                Protected = members.Any(m => m.Protected),
                Members = members.SelectMany(m => m.Members).ToList(),
            });
        }
        return result;
    }

    public static string FormatMemory(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>Reads every process in one system call (works without opening each process).</summary>
public static class ProcessSnapshot
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static List<ProcessSample> Take()
    {
        if (!Environment.Is64BitProcess) return FromProcessApi();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (1 << 16));
                    continue;
                }
                if (status != 0) return FromProcessApi();
                return Parse(buffer, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return FromProcessApi();
    }

    // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset 0, WorkingSetPrivateSize 8, CreateTime 32, UserTime 40,
    // KernelTime 48, ImageName (UNICODE_STRING) 56 [Length 56, Buffer 64], UniqueProcessId 80, InheritedFromUniqueProcessId 88.
    private static List<ProcessSample> Parse(IntPtr buffer, int size)
    {
        var list = new List<ProcessSample>();
        var offset = 0;
        while (offset + 96 <= size)
        {
            var entry = buffer + offset;
            var next = Marshal.ReadInt32(entry, 0);
            var privateWs = Marshal.ReadInt64(entry, 8);
            var create = Marshal.ReadInt64(entry, 32);
            var user = Marshal.ReadInt64(entry, 40);
            var kernel = Marshal.ReadInt64(entry, 48);
            var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
            var namePtr = Marshal.ReadIntPtr(entry, 64);
            var pid = (uint)Marshal.ReadInt64(entry, 80);
            var parent = (uint)Marshal.ReadInt64(entry, 88);
            var name = namePtr == IntPtr.Zero || nameLength == 0
                ? (pid == 0 ? "System Idle Process" : "System")
                : Marshal.PtrToStringUni(namePtr, nameLength / 2);
            list.Add(new ProcessSample(pid, parent, name, create, user + kernel, privateWs));
            if (next == 0) break;
            offset += next;
        }
        return list;
    }

    private static List<ProcessSample> FromProcessApi()
    {
        var list = new List<ProcessSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                long cpu = 0, create = 0;
                try { cpu = p.TotalProcessorTime.Ticks; create = p.StartTime.ToFileTimeUtc(); }
                catch { }
                list.Add(new ProcessSample((uint)p.Id, 0, p.ProcessName + ".exe", create, cpu, p.WorkingSet64));
            }
        }
        return list;
    }

    private static readonly Dictionary<(uint, long), string> PathCache = [];

    /// <summary>Full image path, cached per process identity (empty when inaccessible).</summary>
    public static string PathOf(uint pid, long createTime)
    {
        lock (PathCache)
        {
            if (PathCache.TryGetValue((pid, createTime), out var cached)) return cached;
            if (PathCache.Count > 4000) PathCache.Clear();
            var path = pid <= 4 ? string.Empty : WindowHelper.GetProcessPath(pid);
            PathCache[(pid, createTime)] = path;
            return path;
        }
    }
}

/// <summary>Turns snapshots into rows with CPU usage between refreshes.</summary>
public sealed class ProcessSampler
{
    private Dictionary<(uint, long), long> _previous = [];
    private long _previousTime;

    public List<ProcessRow> Sample()
    {
        var now = DateTime.UtcNow.ToFileTimeUtc();
        var samples = ProcessSnapshot.Take();
        var own = (uint)Environment.ProcessId;
        var elapsed = _previousTime == 0 ? 0 : now - _previousTime;
        var current = new Dictionary<(uint, long), long>();
        var rows = new List<ProcessRow>(samples.Count);
        foreach (var s in samples)
        {
            if (s.Pid == 0) continue;
            current[(s.Pid, s.CreateTime)] = s.CpuTime;
            var cpu = _previous.TryGetValue((s.Pid, s.CreateTime), out var before)
                ? ProcessLogic.CpuPercent(s.CpuTime - before, elapsed, Environment.ProcessorCount)
                : 0;
            var path = ProcessSnapshot.PathOf(s.Pid, s.CreateTime);
            var name = Path.GetFileNameWithoutExtension(s.Name);
            rows.Add(new ProcessRow(s.Pid, s.ParentPid, name, path, cpu, s.Memory, s.CreateTime,
                ProcessLogic.IsProtected(s.Pid, s.Name, own), [s.Pid]));
        }
        _previous = current;
        _previousTime = now;
        return rows;
    }
}

/// <summary>Ending processes: graceful close through their windows, or terminate. Every kill checks identity first.</summary>
public static class ProcessKiller
{
    /// <summary>Visible top-level windows owned by a process.</summary>
    public static List<IntPtr> WindowsOf(uint pid)
    {
        var result = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && WindowHelper.GetProcessId(hwnd) == pid && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero)
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsSameProcess(uint pid, long createTime) =>
        ProcessSnapshot.Take().Any(s => s.Pid == pid && s.CreateTime == createTime);

    /// <summary>Asks the process to close (WM_CLOSE to its windows). False when it has no window.</summary>
    public static bool CloseGracefully(uint pid)
    {
        var windows = WindowsOf(pid);
        foreach (var hwnd in windows) WindowHelper.Close(hwnd);
        return windows.Count > 0;
    }

    /// <summary>TerminateProcess after checking the PID still belongs to the same process.</summary>
    public static bool Terminate(uint pid, long createTime)
    {
        if (!IsSameProcess(pid, createTime)) return true; // already gone
        var handle = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return TerminateProcess(handle, 1); }
        finally { CloseHandle(handle); }
    }

    public static async Task<bool> WaitForExit(uint pid, long createTime, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!IsSameProcess(pid, createTime)) return true;
            await Task.Delay(250);
        }
        return !IsSameProcess(pid, createTime);
    }
}
