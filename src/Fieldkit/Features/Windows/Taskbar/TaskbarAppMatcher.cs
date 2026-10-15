// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.WindowTools;

/// <summary>A taskbar button as UI Automation reports it.</summary>
/// <param name="AppId">The app id after "Appid: " (an AppUserModelID, an exe path, or "{KNOWNFOLDER}\path").</param>
/// <param name="Name">Accessible name, e.g. "Google Chrome - 2 running windows pinned".</param>
public sealed record TaskbarButtonInfo(string AppId, string Name, System.Windows.Rect Bounds);

/// <summary>What identifies a window's app on the taskbar.</summary>
public sealed record WindowAppIdentity(string? WindowAppId, string? ProcessAppId, string ProcessPath, string DisplayName, string ClassName);

/// <summary>Pairs a taskbar button with the windows it stands for. Pure, for tests.</summary>
public static partial class TaskbarAppMatcher
{
    [GeneratedRegex(@"^(?<name>.*?)(?: - \d+ running windows?)?(?: pinned)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\{(?<guid>[0-9A-Fa-f\-]{36})\}\\(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KnownFolderPattern();

    /// <summary>"Google Chrome - 1 running window pinned" → "Google Chrome".</summary>
    public static string AppName(string buttonName)
    {
        var m = NamePattern().Match(buttonName.Trim());
        return m.Success ? m.Groups["name"].Value.Trim() : buttonName.Trim();
    }

    /// <summary>Running window count in a button name, 0 when none is mentioned.</summary>
    public static int RunningCount(string buttonName)
    {
        var m = Regex.Match(buttonName, @" - (\d+) running windows?");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>"Appid: Chrome" → "Chrome".</summary>
    public static string ParseAutomationId(string? automationId)
    {
        if (string.IsNullOrEmpty(automationId)) return string.Empty;
        const string prefix = "Appid:";
        return automationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? automationId[prefix.Length..].Trim()
            : automationId.Trim();
    }

    /// <summary>Turns "{6D809377-…}\Google\Chrome\chrome.exe" into a full path when the folder is known.</summary>
    public static string? ResolvePath(string appId, Func<Guid, string?> knownFolder)
    {
        if (appId.Length == 0) return null;
        var m = KnownFolderPattern().Match(appId);
        if (m.Success && Guid.TryParse(m.Groups["guid"].Value, out var guid))
        {
            var root = knownFolder(guid);
            return root is null ? null : Path.Combine(root, m.Groups["rest"].Value);
        }
        return Path.IsPathRooted(appId) ? appId : null;
    }

    public static bool Matches(TaskbarButtonInfo button, WindowAppIdentity window, Func<Guid, string?> knownFolder)
    {
        var id = button.AppId;
        if (id.Length > 0)
        {
            if (Same(id, window.WindowAppId) || Same(id, window.ProcessAppId)) return true;
            var path = ResolvePath(id, knownFolder);
            if (path is not null && window.WindowAppId is null && Same(path, window.ProcessPath)) return true;
            if (id.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(window.ProcessPath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                window.ClassName == "CabinetWClass")
                return true;
            // An explicit id that differs means the window is grouped under another button.
            if (window.WindowAppId is not null || window.ProcessAppId is not null) return false;
        }
        var name = AppName(button.Name);
        return name.Length > 0 && (Same(name, window.DisplayName) ||
                                   Same(name, Path.GetFileNameWithoutExtension(window.ProcessPath)));
    }

    private static bool Same(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
