// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Files;

/// <summary>What File Explorer shows in one window: its folder and the selected items.</summary>
public sealed record ExplorerLocation(string Folder, IReadOnlyList<string> Selection);

/// <summary>
/// Reads File Explorer windows through the Shell.Application automation object (the counterpart of
/// upstream's Finder AppleScript bridge). Every call runs on its own short-lived STA thread with a
/// timeout, so a busy Explorer can never stall the UI or an input hook.
/// </summary>
public static class ExplorerShell
{
    private static readonly HashSet<string> ExplorerClasses = new(StringComparer.Ordinal) { "CabinetWClass", "ExploreWClass" };
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW" };

    /// <summary>Whether <paramref name="hwnd"/> is a File Explorer folder window (or the desktop).</summary>
    public static bool IsExplorerWindow(IntPtr hwnd, bool includeDesktop = true)
    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = WindowHelper.GetClassName(hwnd);
        return ExplorerClasses.Contains(cls) || (includeDesktop && DesktopClasses.Contains(cls));
    }

    public static bool IsDesktop(IntPtr hwnd) => hwnd != IntPtr.Zero && DesktopClasses.Contains(WindowHelper.GetClassName(hwnd));

    /// <summary>The folder and selection of the Explorer window <paramref name="hwnd"/>, or null.</summary>
    public static Task<ExplorerLocation?> QueryAsync(IntPtr hwnd, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<ExplorerLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var title = WindowHelper.GetTitle(hwnd);
        var desktop = IsDesktop(hwnd);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Query(hwnd, title, desktop)); }
            catch (Exception ex)
            {
                Log.Warn($"Reading the Explorer window failed: {ex.Message}");
                tcs.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "Fieldkit Explorer query",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WithTimeout(tcs.Task, timeout ?? TimeSpan.FromSeconds(3));
    }

    private static async Task<ExplorerLocation?> WithTimeout(Task<ExplorerLocation?> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == task ? await task.ConfigureAwait(false) : null;
    }

    private static ExplorerLocation? Query(IntPtr hwnd, string title, bool desktop)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return null;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            if (desktop)
            {
                var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                // The desktop's selection is not exposed through Shell.Application windows.
                return new ExplorerLocation(path, []);
            }
            dynamic windows = shell.Windows();
            var candidates = new List<dynamic>();
            int count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = null;
                try { window = windows.Item(i); }
                catch { /* closing window */ }
                if (window is null) continue;
                long handle;
                try { handle = (long)window.HWND; }
                catch { continue; }
                if (new IntPtr(handle) == hwnd) candidates.Add(window);
            }
            if (candidates.Count == 0) return null;
            // Windows 11 tabs share one top-level window; the title names the active tab.
            var chosen = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(c => SafeString(() => (string)c.LocationName) == title) ?? candidates[0];
            dynamic document = chosen.Document;
            string folder = document.Folder.Self.Path;
            var selection = new List<string>();
            dynamic items = document.SelectedItems();
            int n = items.Count;
            for (var i = 0; i < n; i++)
            {
                try
                {
                    string p = items.Item(i).Path;
                    if (!string.IsNullOrEmpty(p)) selection.Add(p);
                }
                catch
                {
                    // virtual items have no path
                }
            }
            return Directory.Exists(folder) ? new ExplorerLocation(folder, selection) : null;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? SafeString(Func<string> get)
    {
        try { return get(); }
        catch { return null; }
    }

    /// <summary>A path that does not exist yet: "name.png", then "name 2.png", "name 3.png", …</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            path = Path.Combine(folder, $"{stem} {i}{ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
        return Path.Combine(folder, $"{stem} {Guid.NewGuid():N}{ext}");
    }
}
