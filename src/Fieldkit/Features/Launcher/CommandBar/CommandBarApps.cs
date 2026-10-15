// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>One app as the Start menu's "All apps" list shows it.</summary>
/// <param name="Id">The AppsFolder parsing id: an AUMID for packaged/registered apps, or a path.</param>
/// <param name="ExePath">The executable when known (for the running dot and "put away when in front").</param>
/// <param name="ShortcutPath">The Start menu shortcut that launches it, when there is one.</param>
public sealed record CommandBarApp(string Id, string Name, string? ExePath, string? ShortcutPath)
{
    public bool IsPackagedOrRegistered => !Id.Contains(":\\", StringComparison.Ordinal);
}

/// <summary>
/// Reads the installed apps: the shell's AppsFolder (Start menu desktop shortcuts plus Store/packaged
/// apps, each with its AUMID) merged with the Start menu .lnk files (user + common) for executable
/// targets. Read-only. Must run on an STA thread (COM shell objects).
/// </summary>
public static class CommandBarApps
{
    /// <summary>Things the Start menu lists that are not apps.</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".chm", ".pdf", ".htm", ".html", ".txt", ".rtf", ".md", ".url", ".xml", ".ini", ".log", ".hlp", ".doc", ".docx", ".website",
    };

    /// <summary>Whether a Start menu entry is worth a row (no uninstallers, help files or read-mes).</summary>
    public static bool IsApp(string name, string id)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Contains(":\\", StringComparison.Ordinal) && DocumentExtensions.Contains(Path.GetExtension(id))) return false;
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static List<CommandBarApp> Load()
    {
        var shortcuts = StartMenuShortcuts();
        var byName = new Dictionary<string, (string Lnk, string? Target)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shortcuts) byName.TryAdd(Path.GetFileNameWithoutExtension(s.Lnk), s);

        var apps = new List<CommandBarApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (dynamic item in folder.Items())
                    {
                        string name = item.Name ?? string.Empty;
                        string id = item.Path ?? string.Empty;
                        if (id.Length == 0 || !IsApp(name, id) || !seen.Add(name + "|" + id)) continue;
                        byName.TryGetValue(name, out var lnk);
                        var exe = id.Contains(":\\", StringComparison.Ordinal) ? id : lnk.Target;
                        apps.Add(new CommandBarApp(id, name, IsExecutable(exe) ? exe : null, lnk.Lnk));
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: AppsFolder enumeration failed, using Start menu shortcuts: {ex.Message}");
        }

        if (apps.Count == 0)
        {
            foreach (var (lnk, target) in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                var id = target ?? lnk;
                if (!IsApp(name, id) || !seen.Add(name)) continue;
                apps.Add(new CommandBarApp(lnk, name, IsExecutable(target) ? target : null, lnk));
            }
        }
        var own = Environment.ProcessPath;
        return apps.Where(a => own is null || !string.Equals(a.ExePath, own, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsExecutable(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static List<(string Lnk, string? Target)> StartMenuShortcuts()
    {
        var result = new List<(string, string?)>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", options))
                    result.Add((lnk, CommandBarNative.ShortcutTarget(lnk)));
            }
            catch (Exception ex)
            {
                Log.Warn($"Command Bar: reading {root} failed: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Opens the app: its Start menu shortcut, else its executable, else its AUMID through the shell.</summary>
    public static void Launch(CommandBarApp app)
    {
        if (app.ShortcutPath is { } lnk && File.Exists(lnk))
        {
            Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (app.Id.Contains(":\\", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(app.Id) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.Id) ?? string.Empty })?.Dispose();
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Id) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Executable paths of processes that own a switchable window right now.</summary>
    public static HashSet<string> RunningPaths(IEnumerable<WindowInfo> windows) =>
        windows.Select(w => w.ProcessPath).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>An app's combination: put it away when it is in front, bring it forward when it runs,
    /// launch it otherwise.</summary>
    public static void ToggleOrLaunch(CommandBarApp app)
    {
        if (app.ExePath is { } exe)
        {
            var foreground = Native.GetForegroundWindow();
            var fgPath = WindowHelper.GetProcessPath(WindowHelper.GetProcessId(foreground));
            if (CommandBarRowShortcuts.HidesAppInFront(string.Equals(fgPath, exe, StringComparison.OrdinalIgnoreCase)))
            {
                Native.ShowWindow(foreground, Native.SW_MINIMIZE);
                return;
            }
            var window = WindowHelper.GetAltTabWindows().FirstOrDefault(w => string.Equals(w.ProcessPath, exe, StringComparison.OrdinalIgnoreCase));
            if (window is not null)
            {
                WindowHelper.Activate(window.Handle);
                return;
            }
        }
        Launch(app);
    }
}
