// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// A tiny PDH query: English counter paths (so it works on every display language), wildcard
/// instances, formatted as doubles. Rate counters need two collections before they read.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.Ordinal);

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    /// <summary>Adds a counter path; false when the counter does not exist on this PC.</summary>
    public bool Add(string path)
    {
        if (_query == IntPtr.Zero) return false;
        if (_counters.ContainsKey(path)) return true;
        if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) != 0) return false;
        _counters[path] = counter;
        return true;
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// <summary>Every instance of a (wildcard) counter with a valid value.</summary>
    public List<(string Name, double Value)> Read(string path, bool noCap100 = true)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var counter)) return result;
        var format = PDH_FMT_DOUBLE | (noCap100 ? PDH_FMT_NOCAP100 : 0);
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, format, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, format, ref size, out var count, buffer);
            if (status != 0) return result;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(buffer + i * itemSize);
                // CStatus 0 = PDH_CSTATUS_VALID_DATA, 1 = PDH_CSTATUS_NEW_DATA.
                if (item.CStatus > 1) continue;
                var name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                result.Add((name, item.doubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>A single-instance counter value, or null.</summary>
    public double? ReadSingle(string path)
    {
        var values = Read(path);
        return values.Count > 0 ? values[0].Value : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        _counters.Clear();
    }
}
