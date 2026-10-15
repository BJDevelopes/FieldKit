// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Fieldkit.Features.Monitor.Sampling;
using Xunit.Abstractions;

namespace Fieldkit.Tests.Monitor;

/// <summary>
/// Read-only queries against this PC (PDH, D3DKMT, battery IOCTLs, interface counters, SetupAPI).
/// They must not throw and must return plausible values; timings are written to the test output.
/// </summary>
public class ReaderSmokeTests(ITestOutputHelper output)
{
    private T Time<T>(string name, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        var result = f();
        output.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms → {result}");
        return result;
    }

    [Fact]
    public void CpuReaderReadsTotalAndCores()
    {
        using var cpu = Time("CpuReader()", () => new CpuReader());
        Thread.Sleep(600);
        var (total, cores) = Time("CpuReader.Read", () => cpu.Read(true));
        Assert.NotNull(total);
        Assert.InRange(total!.Value, 0, 1);
        Assert.Equal(Environment.ProcessorCount, cores.Count);
        output.WriteLine($"groups: {string.Join(", ", cpu.Groups.Select(g => $"{g.Name}×{g.Indices.Count}"))}");
    }

    [Fact]
    public void GpuReaderDoesNotThrow()
    {
        using var gpu = Time("GpuReader()", () => new GpuReader());
        Thread.Sleep(600);
        var usage = Time("GpuReader.ReadUsage", gpu.ReadUsage);
        if (usage is { } u) Assert.InRange(u, 0, 1);
        var temp = Time("GpuReader.ReadTemperature", GpuReader.ReadTemperature);
        if (temp is { } t) Assert.InRange(t, 1, 125);
    }

    [Fact]
    public void MemoryReaderIsConsistent()
    {
        using var memory = new MemoryReader();
        var r = Time("MemoryReader.Read", memory.Read);
        Assert.NotNull(r);
        Assert.True(r!.Used <= r.Total);
        Assert.True(r.Committed <= r.CommitLimit);
    }

    [Fact]
    public void NetworkReaderReportsRates()
    {
        var net = new NetworkReader();
        net.Read();
        Thread.Sleep(300);
        var r = Time("NetworkReader.Read", net.Read);
        Assert.NotNull(r.Down);
        Time("LocalAddresses", () => string.Join(",", NetworkReader.LocalAddresses()));
    }

    [Fact]
    public void DiskReaderListsSystemDrive()
    {
        using var disk = new DiskReader();
        Thread.Sleep(300);
        var (drives, _, _) = Time("DiskReader.Read", disk.Read);
        Assert.Contains(drives, d => Path.GetPathRoot(Environment.SystemDirectory)!.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var d in drives) output.WriteLine($"{d.Name} {d.Label} {d.FileSystem} internal={d.IsInternal} {d.FreeBytes}/{d.TotalBytes}");
    }

    [Fact]
    public void BatteryReaderDoesNotThrow()
    {
        var (power, temp) = Time("BatteryReader.Read", BatteryReader.Read);
        output.WriteLine($"{power} temp={temp}");
        if (power.ChargePercent is { } c) Assert.InRange(c, 0, 100);
    }

    [Fact]
    public void DeviceReaderDoesNotThrow()
    {
        var devices = Time("DeviceReader.Read", () => DeviceReader.Read(true));
        foreach (var d in devices) output.WriteLine($"{d.Kind} {d.Name} battery={d.BatteryPercent} class={d.Category}");
    }

    [Fact]
    public void ProcessUsageListsTopApps()
    {
        ProcessUsage.Top(BreakdownKind.Cpu, 5);
        Thread.Sleep(300);
        var rows = Time("ProcessUsage.Top(Memory)", () => ProcessUsage.Top(BreakdownKind.Memory, 5));
        Assert.NotEmpty(rows);
        foreach (var r in rows) output.WriteLine($"{r.Pid} {r.Name} {r.Value}");
    }
}
