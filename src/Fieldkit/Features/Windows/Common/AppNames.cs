// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Fieldkit.Core;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Apps are identified by their executable file name in lower case ("chrome.exe"), the
/// Windows counterpart of a bundle identifier. Display names come from the file's
/// version resource and are cached.
/// </summary>
public static class AppNames
{
    private static readonly ConcurrentDictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> KnownPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\...\Chrome.EXE" or "chrome" → "chrome.exe".</summary>
    public static string Key(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return string.Empty;
        var name = Path.GetFileName(pathOrName.Trim()).ToLowerInvariant();
        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.Ordinal)) name += ".exe";
        return name;
    }

    /// <summary>Remembers where an app lives so lists can show its icon and name later.</summary>
    public static void Remember(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        KnownPaths[Key(path)] = path;
    }

    public static string? PathFor(string key) => KnownPaths.TryGetValue(key, out var p) ? p : null;

    /// <summary>The app's friendly name: version-resource description, else the file name.</summary>
    public static string DisplayName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return DisplayNames.GetOrAdd(path, static p =>
        {
            try
            {
                if (File.Exists(p))
                {
                    var info = FileVersionInfo.GetVersionInfo(p);
                    var description = info.FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(description) && description.Length <= 60) return description;
                    var product = info.ProductName?.Trim();
                    if (!string.IsNullOrEmpty(product) && product.Length <= 60) return product;
                }
            }
            catch
            {
                // fall through
            }
            var name = Path.GetFileNameWithoutExtension(p);
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : p;
        });
    }

    /// <summary>Friendly name for a stored key, using a remembered path when there is one.</summary>
    public static string DisplayNameForKey(string key)
    {
        var path = PathFor(key);
        if (path is not null) return DisplayName(path);
        var name = Path.GetFileNameWithoutExtension(key);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : key;
    }

    /// <summary>Processes that belong to Windows itself and must never be quit or ignored by mistake.</summary>
    public static bool IsSystemProcess(string? path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var key = Key(path);
        if (key is "explorer.exe" or "applicationframehost.exe" or "searchhost.exe" or "startmenuexperiencehost.exe"
            or "shellexperiencehost.exe" or "textinputhost.exe" or "lockapp.exe" or "dwm.exe" or "csrss.exe"
            or "winlogon.exe" or "systemsettings.exe" or "shellhost.exe")
            return true;
        if (string.Equals(Path.GetFileNameWithoutExtension(key), AppInfo.Name, StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A list of app keys stored in one setting (a JSON string array).</summary>
public sealed class AppList(string settingKey)
{
    public string SettingKey { get; } = settingKey;

    public IReadOnlyList<string> Items => Settings.Get<List<string>>(SettingKey, []) is { } list
        ? list.Select(AppNames.Key).Where(k => k.Length > 0).Distinct().ToList()
        : [];

    public bool Contains(string? pathOrKey)
    {
        var key = AppNames.Key(pathOrKey);
        return key.Length > 0 && Items.Contains(key);
    }

    public void Add(string pathOrKey)
    {
        var key = AppNames.Key(pathOrKey);
        if (key.Length == 0) return;
        if (Path.IsPathRooted(pathOrKey)) AppNames.Remember(pathOrKey);
        var items = Items.ToList();
        if (items.Contains(key)) return;
        items.Add(key);
        Settings.Set(SettingKey, items);
    }

    public void Remove(string key)
    {
        var items = Items.Where(k => k != AppNames.Key(key)).ToList();
        Settings.Set(SettingKey, items);
    }
}
