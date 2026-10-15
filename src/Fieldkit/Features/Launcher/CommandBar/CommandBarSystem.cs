// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The things a person tries in a launcher on day one (port of upstream CommandBarExtras): sleep,
/// restart, Wi-Fi, volume, the folders everyone has, facts about this PC; plus the shell plumbing
/// rows use (open, reveal, copy, type at the caret). Nothing here runs while the bar is closed.
/// </summary>
public static class CommandBarSystem
{
    /// <summary>The refusal sound (a beep, like upstream). Replaceable so tests stay silent.</summary>
    public static Action Beep { get; set; } = () => System.Media.SystemSounds.Beep.Play();

    public enum PowerAction { Sleep, Restart, ShutDown, LogOut }

    public static void Run(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.Sleep:
                    System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
                    break;
                case PowerAction.Restart:
                    Start("shutdown.exe", "/r /t 0");
                    break;
                case PowerAction.ShutDown:
                    Start("shutdown.exe", "/s /t 0");
                    break;
                case PowerAction.LogOut:
                    Start("shutdown.exe", "/l");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar power action {action} failed: {ex.Message}");
            CommandBarSystem.Beep();
        }
    }

    private static void Start(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();

    // ------------------------------------------------------------------ Wi-Fi

    /// <summary>Last known Wi-Fi radio state, read off the UI thread when the bar opens; null when there is no Wi-Fi.</summary>
    public static bool? CachedWifiOn { get; private set; }

    public static async Task RefreshWifiAsync()
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
            CachedWifiOn = wifi is null ? null : wifi.State == Windows.Devices.Radios.RadioState.On;
        }
        catch (Exception)
        {
            CachedWifiOn = null;
        }
    }

    public static async void SetWifi(bool on)
    {
        try
        {
            var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
            if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed || wifi is null)
            {
                CommandBarSystem.Beep();
                return;
            }
            await wifi.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            CachedWifiOn = on;
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: switching Wi-Fi failed: {ex.Message}");
            CommandBarSystem.Beep();
        }
    }

    public static void ClearCaches()
    {
        CachedWifiOn = null;
    }

    // ------------------------------------------------------------------ volume

    /// <summary>The default output's volume (0..100) and mute state, or null without an output.</summary>
    public static (int Volume, bool Muted)? ReadVolume()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var v = device.AudioEndpointVolume;
            return ((int)Math.Round(v.MasterVolumeLevelScalar * 100), v.Mute);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void SetVolume(int percent)
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
            if (percent > 0) device.AudioEndpointVolume.Mute = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: setting volume failed: {ex.Message}");
        }
    }

    public static void SetMute(bool mute)
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            device.AudioEndpointVolume.Mute = mute;
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: muting failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ facts

    public static (int Percent, bool Charging, bool OnPower)? ReadBattery()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return null;
        // 128 = no system battery, 255 = unknown.
        if ((status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255 || status.BatteryLifePercent > 100) return null;
        return (status.BatteryLifePercent, (status.BatteryFlag & 8) != 0, status.ACLineStatus == 1);
    }

    public static (ulong Used, ulong Total)? ReadMemory()
    {
        var m = new CommandBarNative.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CommandBarNative.MEMORYSTATUSEX>() };
        if (!CommandBarNative.GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return null;
        return (m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }

    public static (long Free, long Total)? ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            return drive.IsReady && drive.TotalSize > 0 ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The folders every PC has (a fixed set of destinations, never a search of anyone's files).</summary>
    public static List<(string Name, string Path)> StandardFolders()
    {
        var list = new List<(string, string)>();
        void Add(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add((name, path));
        }
        Add("Downloads", CommandBarNative.KnownFolder(CommandBarNative.FolderDownloads));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        return list;
    }

    // ------------------------------------------------------------------ shell plumbing

    /// <summary>Opens a file, folder, URL or URI with its default handler.</summary>
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: opening {target} failed: {ex.Message}");
            CommandBarSystem.Beep();
            return false;
        }
    }

    /// <summary>Shows a file or folder selected in File Explorer.</summary>
    public static void Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CommandBarSystem.Beep();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: revealing {path} failed: {ex.Message}");
        }
    }

    public static bool Copy(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }

    /// <summary>Types text into the window that had focus before the bar opened, once the keys that
    /// summoned it are released (otherwise every character would land as a shortcut).</summary>
    public static void TypeInto(IntPtr target, string text)
    {
        if (target != IntPtr.Zero && Native.IsWindow(target)) WindowHelper.Activate(target);
        var started = Environment.TickCount64;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var held = Shortcut.CurrentModifiers() != Modifiers.None;
            if (held && Environment.TickCount64 - started < 1500) return;
            timer.Stop();
            if (held)
            {
                CommandBarSystem.Beep();
                return;
            }
            InputSender.TypeText(text);
        };
        timer.Start();
    }
}
