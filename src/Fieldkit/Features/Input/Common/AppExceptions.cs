// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Input.Common;

/// <summary>
/// "Apps to leave alone" (upstream MouseAppExceptions): each feature keeps its OWN list, stored
/// as process names at <c>&lt;featureId&gt;.exceptions</c>, so excepting an app from the glide does
/// not also silence its side buttons. Mouse features check the app under the pointer; the
/// super key checks whether a listed app is running at all, like upstream.
/// </summary>
public static class AppExceptions
{
    public static string Key(string featureId) => $"{featureId}.exceptions";

    public static List<string> Get(string featureId) =>
        Settings.Get<List<string>?>(Key(featureId), null) ?? [];

    public static void Add(string featureId, string nameOrPath)
    {
        var name = Normalize(nameOrPath);
        if (name.Length == 0) return;
        var list = Get(featureId);
        if (list.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(name);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        Settings.Set(Key(featureId), list);
    }

    public static void Remove(string featureId, string name)
    {
        var list = Get(featureId);
        if (list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) > 0)
            Settings.Set(Key(featureId), list);
    }

    /// <summary>A hook-thread-friendly snapshot of a list.</summary>
    public static HashSet<string> Snapshot(string featureId) =>
        new(Get(featureId).Select(Normalize).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\Apps\Blender.EXE", "blender.exe" and "Blender" all become "blender".</summary>
    public static string Normalize(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return string.Empty;
        var trimmed = nameOrPath.Trim().Trim('"');
        var file = Path.GetFileName(trimmed);
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return file.Trim().ToLowerInvariant();
    }

    public static bool Matches(IReadOnlySet<string> list, string? processName) =>
        list.Count > 0 && !string.IsNullOrEmpty(processName) && list.Contains(processName);
}

/// <summary>
/// Which app the pointer is over, cheap enough for the hook thread: the answer is reused while
/// the pointer stays on the same top-level window for half a second (upstream measured the same
/// trade-off), and process names are cached per process id. One instance per thread.
/// </summary>
public sealed class PointerTargetResolver
{
    private const long LifetimeMs = 500;
    private readonly Dictionary<uint, string> _names = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private IntPtr _lastRoot;
    private string? _lastName;
    private long _resolvedAt = long.MinValue;

    /// <summary>Lower-case process name (no extension) of the window under the point, or null for our own windows / nothing.</summary>
    public string? ProcessNameAt(int x, int y)
    {
        var now = Environment.TickCount64;
        var hwnd = Native.WindowFromPoint(new Native.POINT(x, y));
        var root = hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == _lastRoot && now - _resolvedAt < LifetimeMs) return _lastName;
        _lastRoot = root;
        _resolvedAt = now;
        _lastName = root == IntPtr.Zero ? null : NameOf(root);
        return _lastName;
    }

    /// <summary>Lower-case process name of a window (cached per process id).</summary>
    public string? NameOf(IntPtr hwnd)
    {
        var pid = WindowHelper.GetProcessId(hwnd);
        if (pid == 0 || pid == _ownPid) return null;
        if (_names.TryGetValue(pid, out var cached)) return cached;
        if (_names.Count > 256) _names.Clear();
        var name = AppExceptions.Normalize(WindowHelper.GetProcessPath(pid));
        _names[pid] = name;
        return name;
    }
}
