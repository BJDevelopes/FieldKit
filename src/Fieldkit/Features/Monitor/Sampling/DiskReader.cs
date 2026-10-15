// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using static Fieldkit.Features.Monitor.Sampling.MonitorNative;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>Drive space (DriveInfo) and per-volume read/write rates (PDH LogicalDisk).</summary>
public sealed class DiskReader : IDisposable
{
    private const string ReadPath = @"\LogicalDisk(*)\Disk Read Bytes/sec";
    private const string WritePath = @"\LogicalDisk(*)\Disk Write Bytes/sec";
    private const string TotalReadPath = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string TotalWritePath = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";

    private static readonly Dictionary<string, bool> InternalCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly PdhQuery _pdh = new();
    private readonly bool _rates;
    private DateTime _lastAt = DateTime.UtcNow;

    public DiskReader()
    {
        _rates = _pdh.Add(ReadPath) & _pdh.Add(WritePath);
        _pdh.Add(TotalReadPath);
        _pdh.Add(TotalWritePath);
        _pdh.Collect();
    }

    // Totals are kept for the app session, across sampler pauses.
    public static ulong TotalRead { get; private set; }
    public static ulong TotalWritten { get; private set; }

    public (List<DriveReading> Drives, double? Read, double? Write) Read()
    {
        Dictionary<string, double> reads = new(StringComparer.OrdinalIgnoreCase), writes = new(StringComparer.OrdinalIgnoreCase);
        double? totalRead = null, totalWrite = null;
        var now = DateTime.UtcNow;
        if (_rates)
        {
            _pdh.Collect();
            foreach (var (n, v) in _pdh.Read(ReadPath)) reads[n] = v;
            foreach (var (n, v) in _pdh.Read(WritePath)) writes[n] = v;
            totalRead = _pdh.ReadSingle(TotalReadPath);
            totalWrite = _pdh.ReadSingle(TotalWritePath);
            var elapsed = (now - _lastAt).TotalSeconds;
            if (elapsed is > 0 and < 60)
            {
                TotalRead += (ulong)((totalRead ?? 0) * elapsed);
                TotalWritten += (ulong)((totalWrite ?? 0) * elapsed);
            }
        }
        _lastAt = now;

        var drives = new List<DriveReading>();
        foreach (var d in SafeDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var name = d.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? d.DriveType == DriveType.Removable ? "USB Drive" : "Local Disk"
                    : d.VolumeLabel;
                drives.Add(new DriveReading(name, label, d.Name, d.DriveFormat,
                    d.DriveType == DriveType.Fixed && IsInternal(name),
                    (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace,
                    reads.TryGetValue(name, out var r) ? r : null,
                    writes.TryGetValue(name, out var w) ? w : null));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (drives, totalRead, totalWrite);
    }

    private static DriveInfo[] SafeDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (IOException) { return []; }
    }

    /// <summary>False for USB, SD and FireWire volumes (queried once per drive, no admin needed).</summary>
    private static bool IsInternal(string drive)
    {
        lock (InternalCache)
        {
            if (InternalCache.TryGetValue(drive, out var cached)) return cached;
        }
        var result = true;
        try
        {
            using var handle = CreateFile($@"\\.\{drive}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
                var output = new byte[1024];
                if (StorageQuery(handle, query, output, out var returned) && returned >= 32)
                {
                    var removable = output[10] != 0;
                    var busType = BitConverter.ToInt32(output, 28);
                    result = !removable && busType is not (4 or 7 or 0xC or 0xD or 0xE); // 1394, USB, SD, MMC
                }
            }
        }
        catch
        {
            // keep "internal"
        }
        lock (InternalCache) InternalCache[drive] = result;
        return result;
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
        byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    private static bool StorageQuery(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] query, byte[] output, out int returned) =>
        DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out returned, IntPtr.Zero);

    public void Dispose() => _pdh.Dispose();
}
