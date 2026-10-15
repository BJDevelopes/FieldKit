// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Features.Monitor.Logic;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// GPU utilisation from PDH "GPU Engine" counters (Task Manager's source) and GPU temperature
/// from the WDDM 2.4+ adapter performance data (D3DKMT), which needs neither admin nor a driver.
/// </summary>
public sealed class GpuReader : IDisposable
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private readonly PdhQuery _pdh = new();
    private readonly bool _available;

    public GpuReader()
    {
        _available = _pdh.Add(EnginePath);
        _pdh.Collect();
    }

    /// <summary>Last per-process utilisation (0–100), for the GPU/energy app lists.</summary>
    public IReadOnlyDictionary<int, double> PerProcess { get; private set; } = new Dictionary<int, double>();

    public double? ReadUsage()
    {
        if (!_available) return null;
        _pdh.Collect();
        var samples = _pdh.Read(EnginePath);
        PerProcess = GpuEngines.PerProcess(samples);
        return GpuEngines.Total(samples) / 100;
    }

    /// <summary>The hottest adapter's temperature in °C, or null when no driver reports one.</summary>
    public static double? ReadTemperature()
    {
        double? hottest = null;
        foreach (var t in AdapterTemperatures())
            hottest = Math.Max(hottest ?? t, t);
        return hottest;
    }

    private static List<double> AdapterTemperatures()
    {
        var result = new List<double>();
        var enumArgs = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0) return result;
        var infoSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        var buffer = Marshal.AllocHGlobal(infoSize * (int)enumArgs.NumAdapters);
        try
        {
            enumArgs.pAdapters = buffer;
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return result;
            for (var i = 0; i < enumArgs.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * infoSize);
                try
                {
                    if (QueryTemperature(info.hAdapter) is { } t) result.Add(t);
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static double? QueryTemperature(uint adapter)
    {
        var size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA(), data, false);
            var query = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = adapter,
                Type = KMTQAITYPE_ADAPTERPERFDATA,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
            var perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(data);
            var celsius = perf.Temperature / 10.0;
            return celsius is > 1 and < 125 ? celsius : null;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public void Dispose() => _pdh.Dispose();
}
