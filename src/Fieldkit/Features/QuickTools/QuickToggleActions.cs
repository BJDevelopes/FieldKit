// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.Interop;
using Fieldkit.UI;
using static Fieldkit.Features.QuickTools.QuickToolsNative;

namespace Fieldkit.Features.QuickTools;

/// <summary>The system actions behind the Quick toggles (each one is safe to call from the UI thread).</summary>
public static class QuickToggleActions
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // ---- Dark mode -----------------------------------------------------------------

    public static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>Flips apps and system between light and dark and tells running apps.</summary>
    public static void ToggleDarkMode()
    {
        var light = IsDarkMode() ? 1 : 0;
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        }
        Task.Run(() => SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _));
    }

    // ---- Desktop icons -------------------------------------------------------------

    private static IntPtr FindDefView()
    {
        var progman = Native.FindWindow("Progman", null);
        var view = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        var found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (WindowHelper.GetClassName(hwnd) != "WorkerW") return true;
            var v = Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (v == IntPtr.Zero) return true;
            found = v;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static bool DesktopIconsHidden()
    {
        var view = FindDefView();
        if (view == IntPtr.Zero) return false;
        var list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        return list != IntPtr.Zero && !Native.IsWindowVisible(list);
    }

    /// <summary>Same command as desktop right-click → View → Show desktop icons (Explorer keeps HideIcons in sync).</summary>
    public static void ToggleDesktopIcons()
    {
        var view = FindDefView();
        if (view == IntPtr.Zero)
        {
            Notifier.Show("Quick toggles", "Could not complete.", warning: true);
            return;
        }
        Native.SendMessage(view, WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
    }

    // ---- Hidden files --------------------------------------------------------------

    public static bool HiddenFilesShown()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue("Hidden") is int v && v == 1;
    }

    public static void ToggleHiddenFiles()
    {
        var show = !HiddenFilesShown();
        using (var key = Registry.CurrentUser.CreateSubKey(AdvancedKey))
            key.SetValue("Hidden", show ? 1 : 2, RegistryValueKind.DWord);
        RefreshExplorer();
    }

    /// <summary>Makes open Explorer windows and the desktop pick up view-setting changes.</summary>
    private static void RefreshExplorer() => RunSta(() =>
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ShellState", SMTO_ABORTIFHUNG, 1000, out _);
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell is null) return;
        foreach (var window in shell.Windows())
        {
            try { window.Refresh(); }
            catch { }
        }
    });

    // ---- Recycle Bin ---------------------------------------------------------------

    public static (long Count, long Bytes) RecycleBinContents()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    public static void EmptyRecycleBin()
    {
        var (count, bytes) = RecycleBinContents();
        if (count == 0)
        {
            Notifier.Show("Recycle Bin", "The Recycle Bin is already empty.");
            return;
        }
        var what = $"{count} {(count == 1 ? "item" : "items")} ({FormatBytes(bytes)})";
        if (!EnergyUi.Confirm("Empty the Recycle Bin?", $"{what} will be removed. This cannot be undone.", "Empty the Recycle Bin")) return;
        Task.Run(() =>
        {
            var hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr != 0) Notifier.Show("Recycle Bin", "Could not complete.", warning: true);
        });
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1)
        {
            v /= 1000;
            u++;
        }
        return u == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[u]}";
    }

    // ---- Eject -------------------------------------------------------------------

    public static List<string> ExcludedDrives() => Settings.Get<List<string>?>(QuickTogglesCatalog.ExcludedDrivesKey, null) ?? [];

    /// <summary>External volumes the user has not excluded.</summary>
    public static List<DriveInfo> EjectableDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var excluded = ExcludedDrives();
        var result = new List<DriveInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var bus = d.DriveType == DriveType.Fixed ? BusType(d.Name[0]) : -1;
                if (!QuickToolsLogic.ShouldOfferEject(d.DriveType == DriveType.Removable, d.DriveType == DriveType.Fixed, bus,
                        string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase), d.Name, d.VolumeLabel, excluded))
                    continue;
                result.Add(d);
            }
            catch
            {
                // Drives can vanish while we look.
            }
        }
        return result;
    }

    public static void EjectAll() => RunSta(() =>
    {
        var drives = EjectableDrives();
        if (drives.Count == 0)
        {
            Notifier.Show("Eject all drives", "No external drive ready to eject.");
            return;
        }
        var type = Type.GetTypeFromProgID("Shell.Application");
        dynamic? shell = type is null ? null : Activator.CreateInstance(type);
        foreach (var d in drives)
        {
            try { shell?.NameSpace(17).ParseName(d.Name)?.InvokeVerb("Eject"); }
            catch (Exception ex) { Log.Warn($"Eject {d.Name} failed: {ex.Message}"); }
        }
        Thread.Sleep(2500);
        var left = drives.Where(d => Directory.Exists(d.Name)).Select(d => d.Name.TrimEnd('\\')).ToList();
        if (left.Count == 0) Notifier.Show("Eject all drives", drives.Count == 1 ? "1 drive ejected." : $"{drives.Count} drives ejected.");
        else Notifier.Show("Eject all drives", $"Could not eject {string.Join(", ", left)}", warning: true);
    });

    // ---- Lock, display, screen saver ---------------------------------------------

    /// <summary>Closes the panel, then runs the action after 0.15 s (like upstream).</summary>
    public static void AfterPanelCloses(Action action)
    {
        PanelHost.Hide();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Ui.Guard(action);
        };
        t.Start();
    }

    public static void LockScreen() => AfterPanelCloses(() => Native.LockWorkStation());

    public static void DisplayOff() => AfterPanelCloses(() =>
        Native.PostMessage(HWND_BROADCAST, Native.WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2)));

    public static void StartScreenSaver() => AfterPanelCloses(() =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var configured = key?.GetValue("SCRNSAVE.EXE") as string;
        var path = !string.IsNullOrWhiteSpace(configured) && File.Exists(Environment.ExpandEnvironmentVariables(configured))
            ? Environment.ExpandEnvironmentVariables(configured)
            : Path.Combine(Environment.SystemDirectory, "scrnsave.scr");
        Process.Start(new ProcessStartInfo(path, "/s") { UseShellExecute = false });
    });

    // ---- Sound -----------------------------------------------------------------------

    public static bool IsMuted()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    public static void ToggleMute()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mute toggle failed: {ex.Message}");
            Notifier.Show("Quick toggles", "Could not complete.", warning: true);
        }
    }

    // ---- Helpers -------------------------------------------------------------------

    private static void RunSta(Action action)
    {
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Quick toggle failed", ex); }
        }) { IsBackground = true, Name = "Fieldkit quick toggle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
