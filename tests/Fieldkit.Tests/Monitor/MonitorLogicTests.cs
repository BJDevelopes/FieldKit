// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;

namespace Fieldkit.Tests.Monitor;

public class MetricFormatTests
{
    public MetricFormatTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(454L * 1024 * 1024, "454 MB")]
    [InlineData(1023.7 * 1024, "1.0 MB")]
    public void Bytes(double bytes, string expected) => Assert.Equal(expected, MetricFormat.Bytes(bytes));

    [Fact]
    public void MemoryUsesTwoDecimals()
    {
        Assert.Equal("16 GB", MetricFormat.Memory(16L * 1024 * 1024 * 1024));
        Assert.Equal("11.43 GB", MetricFormat.Memory(11.43 * 1024 * 1024 * 1024));
    }

    [Theory]
    [InlineData(0, "0 bps")]
    [InlineData(125_000, "1.0 Mbps")]
    [InlineData(86_375_000, "691 Mbps")]
    public void Bits(double bytesPerSecond, string expected) => Assert.Equal(expected, MetricFormat.BitsPerSec(bytesPerSecond));

    [Theory]
    [InlineData(512, false, "512B")]
    [InlineData(1536, false, "1.5K")]
    [InlineData(2.7 * 1024 * 1024, false, "2.7M")]
    [InlineData(125_000, true, "1.0M")]
    public void CompactRates(double rate, bool bits, string expected) => Assert.Equal(expected, MetricFormat.RateCompact(rate, bits));

    [Theory]
    [InlineData(0, "0min")]
    [InlineData(4260, "1h 11min")]
    [InlineData(165_600, "1d 22h")]
    public void Uptime(long seconds, string expected) => Assert.Equal(expected, MetricFormat.Uptime(seconds));

    [Fact]
    public void TemperaturesAndWatts()
    {
        Assert.Equal("42 °C", MetricFormat.Temperature(42.4, false));
        Assert.Equal("108 °F", MetricFormat.Temperature(42.4, true));
        Assert.Equal("47°", MetricFormat.TemperatureCompact(46.6, false));
        Assert.Equal("6.0 W", MetricFormat.Watts(6));
        Assert.Equal("12 W", MetricFormat.Watts(12.2));
        Assert.Equal("3h 5m", MetricFormat.BatteryTime(3 * 3600 + 5 * 60));
        Assert.Null(MetricFormat.BatteryTime(-1));
        Assert.Equal("80%", MetricFormat.Percent(0.8));
        Assert.Equal("100%", MetricFormat.Percent(1.4));
    }

    [Theory]
    [InlineData(0.7, 1024, 1)]
    [InlineData(3, 1024, 5)]
    [InlineData(1500, 1024, 2048)]
    [InlineData(7.7, 1000, 10)]
    public void GraphCeiling(double peak, double unit, double expected) => Assert.Equal(expected, MetricFormat.GraphCeiling(peak, unit));
}

public class MonitorPolicyTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;

    [Theory]
    [InlineData(16, 8, 10, 32, MemoryPressure.Normal)]
    [InlineData(16, 2, 10, 32, MemoryPressure.Warning)]
    [InlineData(16, 0.5, 10, 32, MemoryPressure.Critical)]
    [InlineData(16, 8, 28, 32, MemoryPressure.Warning)]
    [InlineData(16, 8, 31, 32, MemoryPressure.Critical)]
    public void MemoryPressureLevels(double total, double avail, double commit, double limit, MemoryPressure expected) =>
        Assert.Equal(expected, MemoryPressureClassifier.Classify((ulong)(total * Gb), (ulong)(avail * Gb), (ulong)(commit * Gb), (ulong)(limit * Gb)));

    [Fact]
    public void UnknownWithoutMemory() => Assert.Equal(MemoryPressure.Unknown, MemoryPressureClassifier.Classify(0, 0, 0, 0));

    [Fact]
    public void GateWaitsTwelveSecondsOfDistinctReadings()
    {
        var gate = new SustainedAlertGate();
        Assert.False(gate.ShouldAlert(0.95, 0.9, 100));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 106));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 106)); // same reading served again
        Assert.True(gate.ShouldAlert(0.95, 0.9, 112));
    }

    [Fact]
    public void GateResetsWhenTheReadingDrops()
    {
        var gate = new SustainedAlertGate();
        gate.ShouldAlert(0.95, 0.9, 100);
        Assert.False(gate.ShouldAlert(0.5, 0.9, 105));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 110));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 120));
        Assert.True(gate.ShouldAlert(0.95, 0.9, 122));
        Assert.False(gate.ShouldAlert(null, 0.9, 130));
    }

    [Fact]
    public void ThresholdsAreSanitized()
    {
        Assert.Equal(90, AlertPolicy.CpuThreshold(0));
        Assert.Equal(75, AlertPolicy.CpuThreshold(75));
        Assert.Equal(90, AlertPolicy.CpuTemperatureThreshold(200));
        Assert.Equal(10, AlertPolicy.DiskFreePercent(2));
        Assert.Equal(15, AlertPolicy.BatteryPercent(99));
        Assert.Equal(40, AlertPolicy.BatteryTemperatureThreshold(10));
        Assert.Equal(15, AlertPolicy.Cooldown(7));
        Assert.Equal(60, AlertPolicy.Cooldown(60));
    }

    [Fact]
    public void LowDiskIgnoresSmallDrives()
    {
        var drives = new[]
        {
            ("USB", 8_000_000_000UL, 100_000UL),
            ("C:", 500_000_000_000UL, 20_000_000_000UL),
        };
        Assert.Equal(("C:", 10), AlertPolicy.LowDisk(drives, 10));
        Assert.Null(AlertPolicy.LowDisk(drives, 4));
    }

    [Fact]
    public void LowBatteryOnlyOnBattery()
    {
        Assert.Equal(12, AlertPolicy.LowBattery(true, false, false, 12, 15));
        Assert.Null(AlertPolicy.LowBattery(true, true, true, 12, 15));
        Assert.Null(AlertPolicy.LowBattery(true, false, true, 12, 15));
        Assert.Null(AlertPolicy.LowBattery(false, false, false, 12, 15));
        Assert.Null(AlertPolicy.LowBattery(true, false, false, 40, 15));
    }

    [Fact]
    public void Cooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.True(AlertPolicy.CooledDown(null, now, 15));
        Assert.False(AlertPolicy.CooledDown(now.AddMinutes(-5), now, 15));
        Assert.True(AlertPolicy.CooledDown(now.AddMinutes(-15), now, 15));
    }
}

public class GpuEngineTests
{
    private const string A = "luid_0x00000000_0x0000D1B4_phys_0";

    [Fact]
    public void ParsesInstanceNames()
    {
        var inst = GpuEngines.Parse($"pid_1234_{A}_eng_3_engtype_VideoDecode");
        Assert.NotNull(inst);
        Assert.Equal(1234, inst!.Value.Pid);
        Assert.Equal("VideoDecode", inst.Value.EngineType);
        Assert.Null(GpuEngines.Parse("_Total"));
    }

    [Fact]
    public void TotalIsTheBusiestEngineSummedOverProcesses()
    {
        var samples = new List<(string, double)>
        {
            ($"pid_1_{A}_eng_0_engtype_3D", 20),
            ($"pid_2_{A}_eng_0_engtype_3D", 25),
            ($"pid_2_{A}_eng_1_engtype_Copy", 30),
            ($"pid_3_{A}_eng_4_engtype_VideoEncode", 10),
        };
        Assert.Equal(45, GpuEngines.Total(samples));
        var per = GpuEngines.PerProcess(samples);
        Assert.Equal(20, per[1]);
        Assert.Equal(30, per[2]);
        Assert.Equal(10, per[3]);
    }

    [Fact]
    public void TotalIsCapped() =>
        Assert.Equal(100, GpuEngines.Total([($"pid_1_{A}_eng_0_engtype_3D", 70), ($"pid_2_{A}_eng_0_engtype_3D", 60)]));
}

public class FanPolicyTests
{
    private static readonly FanCurvePoint[] Points = [new(50, 0), new(70, 100)];

    [Theory]
    [InlineData(40, 0)]
    [InlineData(50, 0)]
    [InlineData(51, 5)]
    [InlineData(60, 50)]
    [InlineData(61, 55)]
    [InlineData(90, 100)]
    public void Interpolates(double temperature, int level) => Assert.Equal(level, FanPolicy.InterpolatedLevel(Points, temperature));

    [Fact]
    public void ValidatesCurves()
    {
        Assert.True(FanPolicy.ValidCurve(FanPolicy.DefaultCurve));
        Assert.False(FanPolicy.ValidCurve(new FanCurve(FanTemperatureSource.HottestCpu, [new(50, 0)])));
        Assert.False(FanPolicy.ValidCurve(new FanCurve(FanTemperatureSource.HottestCpu, [new(50, 50), new(70, 40)])));
        Assert.False(FanPolicy.ValidCurve(new FanCurve(FanTemperatureSource.HottestCpu, [new(50, 0), new(50, 40)])));
        Assert.False(FanPolicy.ValidCurve(new FanCurve(FanTemperatureSource.HottestCpu, [new(50, 3), new(70, 40)])));
        Assert.False(FanPolicy.ValidCurves([FanPolicy.DefaultCurve, FanPolicy.DefaultCurve]));
    }

    [Fact]
    public void CurveLevelNeedsItsSensorAndHoldsWhileCooling()
    {
        var curves = new[] { FanPolicy.DefaultCurve };
        Assert.Null(FanPolicy.CurveLevel(curves, new Dictionary<FanTemperatureSource, double>()));
        var temps = new Dictionary<FanTemperatureSource, double> { [FanTemperatureSource.HottestCpu] = 60 };
        Assert.Equal(50, FanPolicy.CurveLevel(curves, temps));
        // Cooling by 1 °C keeps the previous level thanks to the 2 °C hysteresis.
        temps[FanTemperatureSource.HottestCpu] = 59;
        Assert.Equal(50, FanPolicy.CurveLevel(curves, temps, previousLevel: 50));
        temps[FanTemperatureSource.HottestCpu] = 55;
        Assert.Equal(35, FanPolicy.CurveLevel(curves, temps, previousLevel: 50));
    }

    [Fact]
    public void NextPointSplitsTheWidestGap()
    {
        Assert.Equal(new FanCurvePoint(60, 50), FanPolicy.NextPoint(Points));
        // No gap wider than 1 °C: extend past the hottest point.
        Assert.Equal(new FanCurvePoint(61, 100), FanPolicy.NextPoint([new(50, 0), new(51, 100)]));
    }

    [Fact]
    public void CurvesRoundTripThroughJson()
    {
        var json = FanPolicy.EncodeCurves([FanPolicy.DefaultCurve]);
        var back = FanPolicy.DecodeCurves(json);
        Assert.NotNull(back);
        Assert.Equal(FanTemperatureSource.HottestCpu, back![0].Sensor);
        Assert.Equal(FanPolicy.DefaultCurve.Points, back[0].Points);
        Assert.Null(FanPolicy.DecodeCurves("not json"));
    }

    [Fact]
    public void Levels()
    {
        Assert.Equal(55, FanPolicy.SteppedLevel(52));
        Assert.Equal(100, FanPolicy.SteppedLevel(140));
        Assert.Equal(0, FanPolicy.SteppedLevel(-3));
        Assert.Equal(60f, FanPolicy.DutyFor(50, 20, 100));
        Assert.Equal(100f, FanPolicy.DutyFor(100, float.NaN, float.NaN));
    }
}

public class TrayLayoutTests
{
    private static ReadoutValue V(ReadoutMetric m, string label, string value, string? value2 = null, ReadoutTint tint = ReadoutTint.Default) =>
        new(m, label, value, $"{label}: {value}", value2, tint);

    [Fact]
    public void PackedModePairsReadings()
    {
        var icons = TrayLayout.Layout([
            V(ReadoutMetric.Cpu, "CPU", "12%"),
            V(ReadoutMetric.CpuTemperature, "CPU", "47°"),
            V(ReadoutMetric.Memory, "RAM", "78%"),
        ], separate: false, combineTemperatures: true);
        Assert.Equal(2, icons.Count);
        Assert.Equal(("12%", "47°"), (icons[0].Line1, icons[0].Line2));
        Assert.Equal(("RAM", "78%"), (icons[1].Line1, icons[1].Line2));
    }

    [Fact]
    public void SeparateModeCombinesUsageAndTemperature()
    {
        var values = new[]
        {
            V(ReadoutMetric.CpuTemperature, "CPU", "47°"),
            V(ReadoutMetric.Cpu, "CPU", "12%"),
            V(ReadoutMetric.Gpu, "GPU", "65%"),
        };
        var combined = TrayLayout.Layout(values, separate: true, combineTemperatures: true);
        Assert.Equal(2, combined.Count);
        Assert.Equal(("12%", "47°"), (combined[0].Line1, combined[0].Line2));
        Assert.Equal(("GPU", "65%"), (combined[1].Line1, combined[1].Line2));

        var apart = TrayLayout.Layout(values, separate: true, combineTemperatures: false);
        Assert.Equal(3, apart.Count);
        Assert.All(apart, i => Assert.Single(i.Metrics));
    }

    [Fact]
    public void TwoLineAndTintedReadingsGetTheirOwnIcon()
    {
        var icons = TrayLayout.Layout([
            V(ReadoutMetric.Cpu, "CPU", "12%"),
            V(ReadoutMetric.Network, "NET", "↓1M", "↑2K"),
            V(ReadoutMetric.Memory, "RAM", "78%", tint: ReadoutTint.Green),
            V(ReadoutMetric.Battery, "BAT", "80%"),
        ], separate: false, combineTemperatures: true);
        Assert.Equal(4, icons.Count);
        Assert.Equal(("CPU", "12%"), (icons[0].Line1, icons[0].Line2));
        Assert.Equal(("↓1M", "↑2K"), (icons[1].Line1, icons[1].Line2));
        Assert.Equal(ReadoutTint.Green, icons[2].Tint);
        Assert.Equal(("BAT", "80%"), (icons[3].Line1, icons[3].Line2));
        Assert.Equal(icons.Count, icons.Select(i => i.Key).Distinct().Count());
    }
}

public class SamplingHelperTests
{
    [Fact]
    public void CoreGroupsSplitHybridCpus()
    {
        var groups = CoreGrouping.Group([1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Performance", groups[0].Name);
        Assert.Equal(4, groups[0].Indices.Count);
        Assert.Equal("Efficiency", groups[1].Name);
        Assert.Single(CoreGrouping.Group([0, 0, 0, 0]));
        Assert.Empty(CoreGrouping.Group([]));
    }

    [Theory]
    [InlineData("0,3", true, 0, 3)]
    [InlineData("1,12", true, 1, 12)]
    [InlineData("0,_Total", false, 0, 0)]
    [InlineData("_Total", false, 0, 0)]
    public void ProcessorInstances(string name, bool ok, int group, int number)
    {
        Assert.Equal(ok, CpuReader.TryParseInstance(name, out var g, out var n));
        if (ok) Assert.Equal((group, number), (g, n));
    }

    [Fact]
    public void DeviceNames()
    {
        Assert.Equal("USB Receiver", DeviceNaming.Choose("USB Receiver", "USB Composite Device"));
        Assert.Equal("Logitech Webcam", DeviceNaming.Choose("USB Composite Device", "Logitech Webcam"));
        Assert.Equal("USB Composite Device", DeviceNaming.Choose(null, "USB Composite Device"));
        Assert.True(DeviceNaming.IsHub("USB2.0 Hub", "Generic USB Hub"));
        Assert.False(DeviceNaming.IsHub("USB Receiver", "USB Input Device"));
    }
}
