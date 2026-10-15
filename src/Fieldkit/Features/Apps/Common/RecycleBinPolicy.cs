// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Fieldkit.Features.Apps.Common;

/// <summary>
/// Whether the Recycle Bin can actually keep an item. Asked to recycle something it cannot hold
/// (recycling turned off, item larger than the bin, removable or network drive), the shell offers
/// to delete it permanently — and with confirmations suppressed it just does. Every recycle in the
/// app goes through <see cref="RecycleBin.Send"/>, which refuses those items instead.
/// </summary>
public static class RecycleBinPolicy
{
    /// <summary>Windows' default bin size on a volume with no explicit setting (conservative).</summary>
    private const double DefaultCapacityShare = 0.05;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int length);

    public static bool CanHold(string path, long size)
    {
        var (max, nuke, driveBytes) = Read(path);
        return Fits(size, max, nuke, driveBytes);
    }

    /// <summary>Pure rule: false when recycling is off, else size must stay under 90 % of the bin.</summary>
    public static bool Fits(long size, long? maxCapacityMb, bool nukeOnDelete, long driveBytes)
    {
        if (nukeOnDelete) return false;
        var capacity = maxCapacityMb is { } mb
            ? mb * 1024L * 1024L
            : (long)(driveBytes * DefaultCapacityShare);
        if (capacity <= 0) return false;
        return size < capacity * 9 / 10;
    }

    /// <summary>
    /// The bin's configured size in MB for the path's volume (null when the default share applies),
    /// whether recycling is turned off, and the volume size. Removable and network drives have no
    /// Recycle Bin, so they report nuke = true.
    /// </summary>
    public static (long? MaxCapacityMb, bool NukeOnDelete, long DriveBytes) Read(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return (null, true, 0);
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed) return (null, true, 0);
            var driveBytes = drive.TotalSize;
            var sb = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(root, sb, sb.Capacity)) return (null, false, driveBytes);
            var volume = sb.ToString();
            var start = volume.IndexOf('{');
            var end = volume.IndexOf('}');
            if (start < 0 || end < start) return (null, false, driveBytes);
            var guid = volume[start..(end + 1)];
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
            if (key is null) return (null, false, driveBytes);
            var nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            long? max = key.GetValue("MaxCapacity") is int m ? m : null;
            return (max, nuke, driveBytes);
        }
        catch
        {
            // Unknown: refuse rather than risk a permanent delete.
            return (null, true, 0);
        }
    }
}
