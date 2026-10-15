// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Features.Monitor.Logic;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>Physical memory, commit charge, file cache and page file use.</summary>
public sealed class MemoryReader : IDisposable
{
    private const string PageFileUsage = @"\Paging File(_Total)\% Usage";
    private readonly PdhQuery _pdh = new();
    private readonly bool _pageFile;

    public MemoryReader()
    {
        _pageFile = _pdh.Add(PageFileUsage);
    }

    public sealed record Reading(ulong Total, ulong Used, ulong Committed, ulong CommitLimit, ulong? Cached, ulong? SwapUsed,
        MemoryPressure Pressure);

    public Reading? Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return null;
        var total = status.ullTotalPhys;
        var used = total - Math.Min(total, status.ullAvailPhys);
        var commitLimit = status.ullTotalPageFile;
        var committed = commitLimit - Math.Min(commitLimit, status.ullAvailPageFile);

        ulong? cached = null;
        ulong? swapUsed = null;
        if (GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            var page = (ulong)perf.PageSize;
            cached = (ulong)perf.SystemCache * page;
        }
        if (_pageFile)
        {
            _pdh.Collect();
            var pageFileBytes = commitLimit > total ? commitLimit - total : 0;
            if (_pdh.ReadSingle(PageFileUsage) is { } percent && pageFileBytes > 0)
                swapUsed = (ulong)(pageFileBytes * Math.Clamp(percent, 0, 100) / 100);
        }
        var pressure = MemoryPressureClassifier.Classify(total, status.ullAvailPhys, committed, commitLimit);
        return new Reading(total, used, committed, commitLimit, cached, swapUsed, pressure);
    }

    public void Dispose() => _pdh.Dispose();
}
