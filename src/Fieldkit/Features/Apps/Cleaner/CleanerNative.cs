// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>A file's identity on disk (volume serial + file index), the counterpart of upstream's inode check.</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index)
{
    public override string ToString() => $"{Volume:X8}:{Index:X16}";
}

/// <summary>Win32 calls the cleaner needs that the shared Interop layer does not have.</summary>
internal static class CleanerNative
{
    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Screenshots known folder (Pictures\Screenshots, possibly redirected to OneDrive).</summary>
    public static string? ScreenshotsFolder() => KnownFolder(FolderIdScreenshots);

    /// <summary>The Downloads known folder (may be moved by the user).</summary>
    public static string? DownloadsFolder() => KnownFolder(FolderIdDownloads);

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------- identity

    // FILETIME is two DWORDs: Pack = 4 keeps the 64-bit fields at their native (4-byte aligned) offsets.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    private const uint FileShareAll = 0x7; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000; // directories
    private const uint FlagOpenReparsePoint = 0x00200000; // never follow links

    /// <summary>Identity of a file or folder without following links; null when it cannot be read.</summary>
    public static FileIdentity? Identity(string path)
    {
        try
        {
            using var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), 0, FileShareAll, IntPtr.Zero, OpenExisting,
                FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether a path is a symbolic link or junction (never followed, never cleaned).</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    // ------------------------------------------------------------- recycle bin

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    /// <summary>Size and item count of the Recycle Bin across all drives.</summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.SizeBytes, info.NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin on every drive (permanent).</summary>
    public static bool EmptyRecycleBin() =>
        SHEmptyRecycleBinW(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound) is 0 or unchecked((int)0x8000FFFF);

    // --------------------------------------------------------------- shortcuts

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    private const uint SlgpRawPath = 0x4;

    /// <summary>The raw target path of a .lnk (environment variables expanded), without resolving
    /// or searching for a moved target. Null for shell-namespace links (no file target).</summary>
    public static string? ShortcutTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, SlgpRawPath);
            var target = sb.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
        }
    }
}
