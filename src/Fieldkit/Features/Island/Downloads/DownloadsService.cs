// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Downloads;

/// <summary>
/// Watches one folder (the user's Downloads unless another is chosen) for browsers' partial files and the
/// files that arrive (port of upstream <c>NotchDownloadService</c>). Only the folder's direct children are read.
/// </summary>
public sealed class DownloadsService : IslandExtensionService
{
    private readonly DispatcherTimer _debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clearFinished = new(DispatcherPriority.Background) { Interval = DownloadTracker.FinishedLinger };
    private readonly List<DownloadItem> _finished = [];
    private FileSystemWatcher? _watcher;
    private IDisposable? _folderObserver;
    private DownloadScan _scan = new([], []);
    private bool _scanning;

    public override string FeatureId => "notchDownloads";
    protected override string EnableKey => IslandKeys.DownloadsEnabled;

    public IReadOnlyList<DownloadItem> Items { get; private set; } = [];
    public bool FolderUnavailable { get; private set; }

    public static string DefaultFolder
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static string Folder
    {
        get
        {
            var chosen = Settings.GetString(IslandKeys.DownloadsFolder, string.Empty);
            return chosen.Length > 0 ? chosen : DefaultFolder;
        }
    }

    /// <summary>An item downloading now (the closed island's activity).</summary>
    public DownloadItem? Active => Items.FirstOrDefault(i => i.Active && !i.Completed);

    public DownloadsService()
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ScanAsync(); };
        _refresh.Tick += (_, _) => { if (_scan.Partials.Count > 0) _ = ScanAsync(); };
        _clearFinished.Tick += (_, _) => { _clearFinished.Stop(); _finished.Clear(); Rebuild(); };
    }

    protected override void OnStart()
    {
        IslandHub.Downloads = this;
        Watch();
        _folderObserver = Settings.Observe(Watch, IslandKeys.DownloadsFolder);
        _refresh.Start();
    }

    protected override void OnStop()
    {
        _folderObserver?.Dispose();
        _folderObserver = null;
        _watcher?.Dispose();
        _watcher = null;
        _debounce.Stop();
        _refresh.Stop();
        _clearFinished.Stop();
        _finished.Clear();
        Items = [];
        if (IslandHub.Downloads == this) IslandHub.Downloads = null;
        IslandHub.RaiseChanged();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        _scan = new DownloadScan([], []);
        var folder = Folder;
        FolderUnavailable = !Directory.Exists(folder);
        if (FolderUnavailable)
        {
            Rebuild();
            return;
        }
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            void Kick(object? s, EventArgs e) => Ui.OnUi(() => { _debounce.Stop(); _debounce.Start(); });
            _watcher.Created += Kick;
            _watcher.Deleted += Kick;
            _watcher.Renamed += Kick;
            _watcher.Changed += Kick;
            _watcher.Error += (_, _) => Ui.OnUi(() => { FolderUnavailable = !Directory.Exists(folder); Rebuild(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Watching downloads failed: {ex.Message}");
            FolderUnavailable = true;
        }
        _ = ScanAsync(initial: true);
    }

    private async Task ScanAsync(bool initial = false)
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var folder = Folder;
            var before = _scan.Partials;
            DownloadScan scan;
            (List<PartialFile> Completed, List<PartialFile> Failed) changes;
            try
            {
                (scan, changes) = await Task.Run(() =>
                {
                    var s = DownloadTracker.Scan(folder, FileIdentity);
                    return (s, DownloadTracker.Compare(before, s.Partials, p => File.Exists(p) ? FileIdentity(p) : null));
                });
            }
            catch (Exception)
            {
                FolderUnavailable = !Directory.Exists(folder);
                Rebuild();
                return;
            }
            if (!IsRunning) return;
            _scan = scan;
            FolderUnavailable = false;
            if (!initial)
            {
                foreach (var done in changes.Completed)
                {
                    var name = Path.GetFileName(done.ExpectedPath);
                    var item = new DownloadItem(done.ExpectedPath, name, done.Size, 1, true, false, DateTime.Now);
                    if (_finished.Any(f => string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase))) continue;
                    _finished.Insert(0, item);
                    if (_finished.Count > DownloadTracker.MaximumFinished) _finished.RemoveAt(_finished.Count - 1);
                    IslandHub.Post(new IslandNotice(IslandNoticeEvent.Download, "Download complete", name, Glyphs.Download, OpenSection: "downloads"), toastFallback: false);
                    IslandHub.React(MascotEvent.DownloadFinished);
                    _clearFinished.Stop();
                    _clearFinished.Start();
                }
                if (changes.Failed.Count > 0) IslandHub.React(MascotEvent.DownloadFailed);
            }
            Rebuild();
        }
        finally
        {
            _scanning = false;
        }
    }

    private void Rebuild()
    {
        Items = DownloadTracker.Items(_scan, _finished, DateTime.Now);
        IslandHub.RaiseChanged();
    }

    /// <summary>Volume serial + file index: survives a rename in place.</summary>
    private static long FileIdentity(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return 0;
            return (long)(((ulong)info.FileIndexHigh << 32 | info.FileIndexLow) ^ ((ulong)info.VolumeSerialNumber << 48));
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
