// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// The one sampler behind every monitor surface. It runs only while somebody holds a lease
/// (a visible panel section, a tray readout, an alert, fan control) and reads only what the
/// leases ask for, on its own thread, at the chosen interval (1/2/5 s). It keeps 60 samples of
/// history per metric. Background-only leases read slow metrics less often, like upstream.
/// </summary>
public static class MonitorSampler
{
    public const int HistoryCapacity = 60;

    private static readonly object Gate = new();
    private static readonly List<Lease> Leases = [];
    private static RunControl? _run;

    /// <summary>Raised on the UI thread after each sample.</summary>
    public static event Action<MonitorSnapshot>? Updated;

    public static MonitorSnapshot Latest { get; private set; } = MonitorSnapshot.Empty;

    /// <summary>Per-process GPU use from the last sample (empty unless GPU is sampled).</summary>
    public static IReadOnlyDictionary<int, double> GpuPerProcess { get; private set; } = new Dictionary<int, double>();

    public static bool IsRunning
    {
        get { lock (Gate) return _run is not null; }
    }

    /// <summary>Starts sampling <paramref name="needs"/> until the returned lease is disposed.</summary>
    /// <param name="foreground">True for a visible panel (full rate for everything).</param>
    public static IDisposable Acquire(MonitorNeeds needs, bool foreground)
    {
        var lease = new Lease(needs, foreground);
        lock (Gate)
        {
            Leases.Add(lease);
            if (_run is null)
            {
                var run = new RunControl();
                _run = run;
                run.Thread = new Thread(() => Run(run)) { IsBackground = true, Name = "Fieldkit monitor sampler", Priority = ThreadPriority.BelowNormal };
                run.Thread.Start();
            }
            else
            {
                _run.Wake.Set(); // sample the new needs right away
            }
        }
        return lease;
    }

    /// <summary>Asks for a sample now (after a setting change).</summary>
    public static void Poke()
    {
        lock (Gate) _run?.Wake.Set();
    }

    private static void Release(Lease lease)
    {
        RunControl? stopping = null;
        lock (Gate)
        {
            if (!Leases.Remove(lease)) return;
            if (Leases.Count == 0 && _run is not null)
            {
                stopping = _run;
                _run = null;
                stopping.Stop = true;
                stopping.Wake.Set();
            }
        }
        // Let the thread finish its current sample and release its readers.
        stopping?.Thread?.Join(TimeSpan.FromSeconds(1.5));
    }

    private static (MonitorNeeds Needs, bool Foreground) Current()
    {
        lock (Gate)
        {
            var needs = MonitorNeeds.None;
            var fg = false;
            foreach (var l in Leases)
            {
                needs |= l.Needs;
                fg |= l.Foreground;
            }
            return (needs, fg);
        }
    }

    private static void Run(RunControl run)
    {
        var state = new SamplerState();
        try
        {
            var tick = 0;
            while (!run.Stop)
            {
                var (needs, _) = Current();
                var primed = Stopwatch.StartNew();
                var created = state.Prime(needs);
                // New rate counters need a short baseline (taken when they were created) before
                // their first reading, so the panel fills quickly; otherwise wait the interval.
                // An open panel refreshes every second so bars and graphs feel live; the "Update every"
                // setting paces background use (tray readouts, alerts) to keep idle cost low.
                var interval = Current().Foreground ? Math.Min(1, MonitorKeys.SanitizedInterval()) : MonitorKeys.SanitizedInterval();
                var wait = created || tick == 0
                    ? TimeSpan.FromMilliseconds(Math.Max(60, 300 - primed.ElapsedMilliseconds))
                    : TimeSpan.FromSeconds(interval);
                run.Wake.WaitOne(wait);
                if (run.Stop) break;
                var (current, foreground) = Current();
                var snapshot = state.Sample(current, foreground, tick++);
                Latest = snapshot;
                GpuPerProcess = state.GpuPerProcess;
                Ui.OnUi(() => Updated?.Invoke(snapshot));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Monitor sampler failed", ex);
        }
        finally
        {
            state.Dispose();
            lock (Gate)
            {
                if (_run == run) _run = null;
            }
        }
    }

    private sealed class RunControl
    {
        public AutoResetEvent Wake { get; } = new(false);
        public volatile bool Stop;
        public Thread? Thread;
    }

    private sealed class Lease(MonitorNeeds needs, bool foreground) : IDisposable
    {
        public MonitorNeeds Needs { get; } = needs;
        public bool Foreground { get; } = foreground;
        public void Dispose() => Release(this);
    }

    /// <summary>Readers and histories owned by the sampler thread.</summary>
    private sealed class SamplerState : IDisposable
    {
        private CpuReader? _cpu;
        private GpuReader? _gpu;
        private MemoryReader? _memory;
        private NetworkReader? _network;
        private DiskReader? _disk;
        private readonly History _cpuHistory = new(), _gpuHistory = new(), _memoryHistory = new(),
            _downHistory = new(), _upHistory = new(), _readHistory = new(), _writeHistory = new(),
            _batteryHistory = new(), _powerHistory = new();
        private MonitorSnapshot _last = MonitorSnapshot.Empty;
        private bool _usingSensors;
        private double? _lastGpuUsage;
        private List<DeviceReading>? _lastDevices;

        public IReadOnlyDictionary<int, double> GpuPerProcess { get; private set; } = new Dictionary<int, double>();

        /// <summary>Creates the readers the needs require; true when a rate reader was just created.</summary>
        public bool Prime(MonitorNeeds needs)
        {
            var created = false;
            if ((needs & (MonitorNeeds.Cpu | MonitorNeeds.CpuCores)) != 0 && _cpu is null) (_cpu, created) = (new CpuReader(), true);
            if (needs.HasFlag(MonitorNeeds.Gpu) && _gpu is null) (_gpu, created) = (new GpuReader(), true);
            if (needs.HasFlag(MonitorNeeds.Memory)) _memory ??= new MemoryReader();
            if (needs.HasFlag(MonitorNeeds.Network) && _network is null)
            {
                _network = new NetworkReader();
                _network.Read();
                created = true;
            }
            if (needs.HasFlag(MonitorNeeds.Disk) && _disk is null) (_disk, created) = (new DiskReader(), true);
            var sensors = (needs & (MonitorNeeds.Temperatures | MonitorNeeds.Fans)) != 0
                          && Settings.GetBool(MonitorKeys.HardwareSensors, false);
            if (sensors)
            {
                HardwareSensors.EnsureStarted();
                _usingSensors = true;
            }
            else if (_usingSensors)
            {
                _usingSensors = false;
                if (!Fans.FanController.IsControlling) HardwareSensors.Stop();
            }
            return created;
        }

        private static bool Due(int tick, int targetSeconds, bool foreground)
        {
            if (foreground) return true;
            var stride = Math.Max(1, (int)Math.Ceiling(targetSeconds / (double)MonitorKeys.SanitizedInterval()));
            return tick % stride == 0;
        }

        public MonitorSnapshot Sample(MonitorNeeds needs, bool fg, int tick)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var last = _last;

            double? cpu = last.CpuUsage, cpuReadAt = last.CpuReadAt;
            IReadOnlyList<double?> cores = [];
            if (_cpu is not null && (needs & (MonitorNeeds.Cpu | MonitorNeeds.CpuCores)) != 0)
            {
                var (total, perCore) = _cpu.Read(needs.HasFlag(MonitorNeeds.CpuCores));
                cpu = total;
                cpuReadAt = now;
                cores = perCore;
                if (total is { } t) _cpuHistory.Push(t);
            }

            double? gpu = last.GpuUsage;
            if (_gpu is not null && needs.HasFlag(MonitorNeeds.Gpu) && Due(tick, 10, fg))
            {
                gpu = _gpu.ReadUsage();
                GpuPerProcess = _gpu.PerProcess;
                if (gpu is { } g)
                {
                    // Upstream steadies the GPU line: rises are capped, falls are eased.
                    gpu = _lastGpuUsage is { } prev ? g > prev ? Math.Min(g, prev + 0.20) : prev * 0.35 + g * 0.65 : g;
                    _lastGpuUsage = gpu;
                    _gpuHistory.Push(gpu.Value);
                }
            }

            MemoryReader.Reading? mem = null;
            if (_memory is not null && needs.HasFlag(MonitorNeeds.Memory))
            {
                mem = _memory.Read();
                if (mem is not null)
                {
                    var committed = Settings.GetString(MonitorKeys.MemoryMetric, "used") == "committed";
                    _memoryHistory.Push(committed
                        ? (double)mem.Committed / Math.Max(1, mem.CommitLimit)
                        : (double)mem.Used / Math.Max(1, mem.Total));
                }
            }

            double? down = last.NetDown, up = last.NetUp;
            ulong? totalDown = last.NetTotalDown, totalUp = last.NetTotalUp;
            ulong? bootDown = last.NetSinceBootDown, bootUp = last.NetSinceBootUp;
            if (_network is not null && needs.HasFlag(MonitorNeeds.Network))
            {
                var n = _network.Read();
                (down, up, totalDown, totalUp, bootDown, bootUp) = (n.Down, n.Up, n.TotalDown, n.TotalUp, n.SinceBootDown, n.SinceBootUp);
                if (down is { } d && up is { } u)
                {
                    _downHistory.Push(d);
                    _upHistory.Push(u);
                }
            }

            var drives = last.Drives;
            double? diskRead = last.DiskRead, diskWrite = last.DiskWrite;
            if (_disk is not null && needs.HasFlag(MonitorNeeds.Disk) && Due(tick, 10, fg))
            {
                (drives, diskRead, diskWrite) = _disk.Read();
                if (diskRead is { } r && diskWrite is { } w)
                {
                    _readHistory.Push(r);
                    _writeHistory.Push(w);
                }
            }

            var power = last.Power;
            var batteryTemp = last.BatteryTemperature;
            var batteryTempAt = last.BatteryTemperatureReadAt;
            if (needs.HasFlag(MonitorNeeds.Power) && Due(tick, 15, fg))
            {
                (power, batteryTemp) = BatteryReader.Read();
                batteryTempAt = batteryTemp is null ? null : now;
                if (power.ChargePercent is { } c) _batteryHistory.Push(c / 100.0);
            }

            double? cpuTemp = last.CpuTemperature, cpuTempAt = last.CpuTemperatureReadAt, gpuTemp = last.GpuTemperature,
                boardTemp = last.MotherboardTemperature, cpuAvg = last.AverageCpuTemperature;
            var fans = last.Fans;
            var status = last.Sensors;
            if ((needs & (MonitorNeeds.Temperatures | MonitorNeeds.Fans)) != 0 && (Due(tick, 15, fg) || needs.HasFlag(MonitorNeeds.Fans)))
            {
                var hw = _usingSensors ? HardwareSensors.Read() : null;
                status = _usingSensors ? HardwareSensors.Status : SensorStatus.DriverOff;
                cpuTemp = hw?.CpuHottest;
                cpuAvg = hw?.CpuAverage;
                cpuTempAt = cpuTemp is null ? null : now;
                boardTemp = hw?.Motherboard;
                gpuTemp = hw?.Gpu ?? (needs.HasFlag(MonitorNeeds.Temperatures) ? GpuReader.ReadTemperature() : null);
                fans = hw?.Fans ?? [];
            }
            if (power?.SystemWatts is { } sw) _powerHistory.Push(sw);

            var devices = last.Devices;
            var devicesRead = last.DevicesRead;
            if (needs.HasFlag(MonitorNeeds.Devices)
                && DeviceReader.Cached(fg, TimeSpan.FromSeconds(fg ? 4 : 10)) is { } list
                && !ReferenceEquals(list, _lastDevices))
            {
                _lastDevices = list;
                devices = list;
                devicesRead = true;
            }

            var snapshot = new MonitorSnapshot
            {
                ReadAt = now,
                UptimeSeconds = (long)(MonitorNative.GetTickCount64() / 1000),
                CpuUsage = cpu,
                CpuReadAt = cpuReadAt,
                CoreUsage = cores,
                CoreGroups = _cpu?.Groups ?? [],
                CpuHistory = _cpuHistory.ToArray(),
                GpuUsage = gpu,
                GpuHistory = _gpuHistory.ToArray(),
                CpuTemperature = cpuTemp,
                CpuTemperatureReadAt = cpuTempAt,
                AverageCpuTemperature = cpuAvg,
                GpuTemperature = gpuTemp,
                MotherboardTemperature = boardTemp,
                BatteryTemperature = batteryTemp,
                BatteryTemperatureReadAt = batteryTempAt,
                Sensors = status,
                MemoryTotal = mem?.Total ?? last.MemoryTotal,
                MemoryUsed = mem?.Used ?? last.MemoryUsed,
                MemoryCommitted = mem?.Committed ?? last.MemoryCommitted,
                MemoryCommitLimit = mem?.CommitLimit ?? last.MemoryCommitLimit,
                MemoryCached = mem?.Cached ?? last.MemoryCached,
                MemorySwapUsed = mem?.SwapUsed ?? last.MemorySwapUsed,
                MemoryPressure = mem?.Pressure ?? last.MemoryPressure,
                MemoryHistory = _memoryHistory.ToArray(),
                NetDown = down,
                NetUp = up,
                NetTotalDown = totalDown,
                NetTotalUp = totalUp,
                NetSinceBootDown = bootDown,
                NetSinceBootUp = bootUp,
                NetDownHistory = _downHistory.ToArray(),
                NetUpHistory = _upHistory.ToArray(),
                Drives = drives,
                DiskRead = diskRead,
                DiskWrite = diskWrite,
                DiskTotalRead = DiskReader.TotalRead,
                DiskTotalWritten = DiskReader.TotalWritten,
                DiskReadHistory = _readHistory.ToArray(),
                DiskWriteHistory = _writeHistory.ToArray(),
                Power = power,
                BatteryHistory = _batteryHistory.ToArray(),
                SystemPowerHistory = _powerHistory.ToArray(),
                Fans = fans,
                Devices = devices,
                DevicesRead = devicesRead,
            };
            _last = snapshot;
            return snapshot;
        }

        public void Dispose()
        {
            _cpu?.Dispose();
            _gpu?.Dispose();
            _memory?.Dispose();
            _disk?.Dispose();
            if (_usingSensors && !Fans.FanController.IsControlling) HardwareSensors.Stop();
        }
    }

    private sealed class History
    {
        private readonly Queue<double> _values = new();

        public void Push(double value)
        {
            _values.Enqueue(double.IsFinite(value) ? value : 0);
            while (_values.Count > HistoryCapacity) _values.Dequeue();
        }

        public double[] ToArray() => _values.ToArray();
    }
}
