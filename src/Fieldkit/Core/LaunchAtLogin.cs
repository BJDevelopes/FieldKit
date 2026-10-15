// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;

namespace Fieldkit.Core;

/// <summary>Start with Windows via HKCU\...\Run (no elevation, per user).</summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInfo.Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(AppInfo.Name, $"\"{AppInfo.ExecutablePath}\" --background");
            else key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Changing launch at login failed", ex);
        }
    }
}
