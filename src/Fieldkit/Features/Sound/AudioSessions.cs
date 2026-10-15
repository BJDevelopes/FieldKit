// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Sound;

/// <summary>One WASAPI session (an app's audio stream on one output).</summary>
internal sealed class MixerSession(IAudioSessionControl2 control, uint pid, string instanceId, bool isActive)
{
    public IAudioSessionControl2 Control { get; } = control;
    public uint ProcessId { get; } = pid;
    public string InstanceId { get; } = instanceId;
    public bool IsActive { get; } = isActive;

    public ISimpleAudioVolume Volume => (ISimpleAudioVolume)Control;

    public float Peak()
    {
        try
        {
            return ((IAudioMeterInformation)Control).GetPeakValue(out var peak) == 0 ? peak : 0f;
        }
        catch
        {
            return 0f;
        }
    }
}

/// <summary>A mixer row: every session of one app (grouped by executable), across all outputs.</summary>
internal sealed class MixerApp(string id, string name, string? exePath, bool isSystem)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string? ExePath { get; } = exePath;
    public bool IsSystem { get; } = isSystem;
    public List<MixerSession> Sessions { get; } = [];

    public IEnumerable<uint> ProcessIds => Sessions.Select(s => s.ProcessId).Where(p => p != 0).Distinct();

    /// <summary>Whether any of its streams is running (Windows' "active" session state).</summary>
    public bool IsActive => Sessions.Any(s => s.IsActive);

    /// <summary>Identifies the set of sessions, so the view only rebuilds when it changes.</summary>
    public string Signature => string.Join("|", Sessions.Select(s => s.InstanceId).Order(StringComparer.Ordinal));

    public float Volume
    {
        get
        {
            foreach (var s in Sessions)
            {
                try
                {
                    if (s.Volume.GetMasterVolume(out var level) == 0) return level;
                }
                catch
                {
                    // session went away; try the next
                }
            }
            return 1f;
        }
    }

    public bool Muted
    {
        get
        {
            try
            {
                return Sessions.Count > 0 && Sessions.All(s => s.Volume.GetMute(out var mute) == 0 && mute);
            }
            catch
            {
                return false;
            }
        }
    }

    public float Peak() => Sessions.Count == 0 ? 0f : Sessions.Max(s => s.Peak());

    public void SetVolume(float level)
    {
        foreach (var s in Sessions)
        {
            try { s.Volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), IntPtr.Zero); }
            catch (Exception ex) { Log.Warn($"Setting {Name} volume failed: {ex.Message}"); }
        }
    }

    public void SetMute(bool mute)
    {
        foreach (var s in Sessions)
        {
            try { s.Volume.SetMute(mute, IntPtr.Zero); }
            catch (Exception ex) { Log.Warn($"Muting {Name} failed: {ex.Message}"); }
        }
    }
}

/// <summary>
/// The apps holding audio sessions right now, on every active output. Owns the session
/// COM objects: dispose it (the mixer swaps snapshots every second) to release them.
/// </summary>
internal sealed class MixerSnapshot : IDisposable
{
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public List<MixerApp> Apps { get; } = [];

    /// <summary>Changes whenever an app or one of its sessions appears or goes away.</summary>
    public string Signature => string.Join(";", Apps.Select(a => a.Id + "=" + a.Signature));

    public const string SystemSoundsId = "#system";

    public static MixerSnapshot Capture()
    {
        var snapshot = new MixerSnapshot();
        var byId = new Dictionary<string, MixerApp>(StringComparer.Ordinal);
        var ownPid = (uint)Environment.ProcessId;
        var seen = new HashSet<uint>();
        foreach (var deviceId in SessionManagerCache.RenderDeviceIds())
        {
            foreach (var session in EnumerateSessions(deviceId))
            {
                var (control, pid, instance, state, isSystem) = session;
                string? path = null;
                string id = SystemSoundsId;
                if (!isSystem)
                {
                    seen.Add(pid);
                    // Sessions outlive their process for a while; a pid that no longer exists is skipped.
                    path = SessionManagerCache.ProcessPath(pid);
                    string? processName = null;
                    if (string.IsNullOrEmpty(path))
                    {
                        using var process = WindowHelper.TryGetProcess(pid);
                        processName = process?.ProcessName;
                    }
                    id = SoundLogic.AppId(path, processName);
                }
                if (state == CoreAudioNative.AudioSessionStateExpired || pid == ownPid || id.Length == 0)
                {
                    CoreAudioNative.Release(control);
                    continue;
                }
                if (!byId.TryGetValue(id, out var app))
                {
                    app = new MixerApp(id, isSystem ? "System sounds" : DisplayName(path, control, id), path, isSystem);
                    byId[id] = app;
                    snapshot.Apps.Add(app);
                }
                app.Sessions.Add(new MixerSession(control, pid, instance, state == CoreAudioNative.AudioSessionStateActive));
            }
        }
        SessionManagerCache.ForgetProcessesExcept(seen);
        return snapshot;
    }

    private static IEnumerable<(IAudioSessionControl2 Control, uint Pid, string Instance, int State, bool IsSystem)> EnumerateSessions(string deviceId)
    {
        var found = new List<(IAudioSessionControl2, uint, string, int, bool)>();
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (SessionManagerCache.Manager(deviceId) is not { } manager) return found;
            if (manager.GetSessionEnumerator(out sessions) != 0) return found;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0 || control is null) continue;
                try
                {
                    control.GetState(out var state);
                    control.GetProcessId(out var pid);
                    control.GetSessionInstanceIdentifier(out var instance);
                    var isSystem = control.IsSystemSoundsSession() == 0;
                    found.Add((control, pid, instance ?? $"{deviceId}/{i}", state, isSystem));
                }
                catch
                {
                    CoreAudioNative.Release(control);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Enumerating audio sessions failed", ex);
        }
        finally
        {
            CoreAudioNative.Release(sessions);
        }
        return found;
    }

    /// <summary>The executable's description ("Google Chrome"), else the session's name, else the process name.</summary>
    private static string DisplayName(string? path, IAudioSessionControl2 control, string id)
    {
        if (!string.IsNullOrEmpty(path))
        {
            return NameCache.GetOrAdd(path, p =>
            {
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(description)) return description;
                }
                catch
                {
                    // fall through to the file name
                }
                return Path.GetFileNameWithoutExtension(p);
            });
        }
        try
        {
            if (control.GetDisplayName(out var name) == 0 && !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) return name;
        }
        catch
        {
            // fall through
        }
        return id;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in Apps.SelectMany(a => a.Sessions)) CoreAudioNative.Release(session.Control);
        Apps.Clear();
    }
}

/// <summary>
/// The mixer re-reads its sessions every second; activating each device's session manager and
/// re-listing devices each time made that refresh take ~100 ms on the UI thread (a visible hitch).
/// Devices and their managers are kept until Windows reports an audio device change; process
/// paths are kept while their sessions exist. UI thread only.
/// </summary>
internal static class SessionManagerCache
{
    private static List<string>? _renderIds;
    private static readonly Dictionary<string, IAudioSessionManager2> Managers = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, string> Paths = [];
    private static IDisposable? _watch;

    public static IReadOnlyList<string> RenderDeviceIds()
    {
        _watch ??= AudioDeviceWatcher.Subscribe(Invalidate);
        return _renderIds ??= AudioEndpoints.List(EDataFlow.Render).Select(d => d.Id).ToList();
    }

    public static IAudioSessionManager2? Manager(string deviceId)
    {
        if (Managers.TryGetValue(deviceId, out var cached)) return cached;
        IMMDevice? device = null;
        try
        {
            if (AudioEndpoints.Enumerator.GetDevice(deviceId, out device) != 0) return null;
            var iid = CoreAudioNative.IID_IAudioSessionManager2;
            if (device.Activate(ref iid, CoreAudioNative.CLSCTX_ALL, IntPtr.Zero, out var manager) != 0) return null;
            var typed = (IAudioSessionManager2)manager;
            Managers[deviceId] = typed;
            return typed;
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening the session manager of {deviceId} failed: {ex.Message}");
            return null;
        }
        finally
        {
            CoreAudioNative.Release(device);
        }
    }

    public static string ProcessPath(uint pid)
    {
        if (Paths.TryGetValue(pid, out var path)) return path;
        path = WindowHelper.GetProcessPath(pid);
        // An empty path (process gone or protected) is not cached, so a later read can still succeed.
        if (path.Length > 0) Paths[pid] = path;
        return path;
    }

    /// <summary>Drops cached paths of processes that no longer hold a session (ids get reused).</summary>
    public static void ForgetProcessesExcept(HashSet<uint> alive)
    {
        foreach (var pid in Paths.Keys.Where(p => !alive.Contains(p)).ToList()) Paths.Remove(pid);
    }

    private static void Invalidate()
    {
        foreach (var manager in Managers.Values) CoreAudioNative.Release(manager);
        Managers.Clear();
        _renderIds = null;
    }
}
