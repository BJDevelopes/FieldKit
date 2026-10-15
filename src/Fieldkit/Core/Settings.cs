// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Fieldkit.Core;

/// <summary>
/// The app's preference store (the Windows counterpart of UserDefaults): a flat
/// key → JSON value map persisted to %APPDATA%\Fieldkit\settings.json.
///
/// Keys are dotted, feature-scoped strings such as <c>mixer.hideInactive</c>.
/// Writes are saved after a short debounce and atomically (temp file + replace).
/// <see cref="Changed"/> fires on the thread that wrote; use <see cref="Observe"/>
/// to be called back on the UI thread instead.
/// </summary>
public static class Settings
{
    private static readonly object Gate = new();
    private static readonly string FilePath = AppInfo.DataPath("settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, JsonNode?> _values = Load();
    private static Timer? _saveTimer;

    /// <summary>Raised with the key after any Set/Remove.</summary>
    public static event Action<string>? Changed;

    /// <summary>Keys that must never leave the machine through settings export.</summary>
    public static readonly HashSet<string> MachineSpecificPrefixes = new(StringComparer.Ordinal)
    {
        "window.", "runtime.", "keepAwake.restore", "mouse.restore",
        "extraBrightness.restore", "bluetoothSleep.restorePending",
        "micMute.typingMutedDevices", "micMute.mutedDevices",
    };

    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            if (!_values.TryGetValue(key, out var node) || node is null) return fallback;
            try { return node.Deserialize<T>() ?? fallback; }
            catch { return fallback; }
        }
    }

    public static bool GetBool(string key, bool fallback = false) => Get(key, fallback);
    public static int GetInt(string key, int fallback = 0) => Get(key, fallback);
    public static double GetDouble(string key, double fallback = 0) => Get(key, fallback);
    public static string GetString(string key, string fallback = "") => Get(key, fallback) ?? fallback;

    public static bool Has(string key)
    {
        lock (Gate) return _values.ContainsKey(key);
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            var node = JsonSerializer.SerializeToNode(value);
            if (_values.TryGetValue(key, out var existing) && JsonNode.DeepEquals(existing, node)) return;
            _values[key] = node;
            ScheduleSave();
        }
        RaiseChanged(key);
    }

    public static void Remove(string key)
    {
        bool removed;
        lock (Gate)
        {
            removed = _values.Remove(key);
            if (removed) ScheduleSave();
        }
        if (removed) RaiseChanged(key);
    }

    /// <summary>
    /// Calls <paramref name="onChange"/> on the UI thread whenever any of <paramref name="keys"/> change.
    /// Returns a token that unsubscribes when disposed.
    /// </summary>
    public static IDisposable Observe(Action onChange, params string[] keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        void Handler(string key)
        {
            if (set.Count > 0 && !set.Contains(key)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) onChange();
            else dispatcher.BeginInvoke(onChange);
        }
        Changed += Handler;
        return new Unsubscriber(() => Changed -= Handler);
    }

    /// <summary>Observe every key that starts with <paramref name="prefix"/>.</summary>
    public static IDisposable ObservePrefix(string prefix, Action onChange)
    {
        void Handler(string key)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) onChange();
            else dispatcher.BeginInvoke(onChange);
        }
        Changed += Handler;
        return new Unsubscriber(() => Changed -= Handler);
    }

    public static string ExportJson()
    {
        lock (Gate)
        {
            var obj = new JsonObject();
            foreach (var (k, v) in _values)
            {
                if (MachineSpecificPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) continue;
                obj[k] = v?.DeepClone();
            }
            return obj.ToJsonString(JsonOptions);
        }
    }

    public static void ImportJson(string json)
    {
        var parsed = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Not a settings file.");
        var changed = new List<string>();
        lock (Gate)
        {
            foreach (var (k, v) in parsed)
            {
                if (MachineSpecificPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) continue;
                _values[k] = v?.DeepClone();
                changed.Add(k);
            }
            ScheduleSave();
        }
        foreach (var k in changed) RaiseChanged(k);
    }

    /// <summary>Writes pending changes immediately (call on exit).</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            SaveNow();
        }
    }

    private static void RaiseChanged(string key)
    {
        try { Changed?.Invoke(key); }
        catch (Exception ex) { Log.Error($"Settings observer for '{key}' failed", ex); }
    }

    private static void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => { lock (Gate) SaveNow(); }, null, 400, Timeout.Infinite);
    }

    private static void SaveNow()
    {
        try
        {
            var obj = new JsonObject();
            foreach (var (k, v) in _values.OrderBy(p => p.Key, StringComparer.Ordinal)) obj[k] = v?.DeepClone();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, obj.ToJsonString(JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    private static Dictionary<string, JsonNode?> Load()
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject obj)
            {
                foreach (var (k, v) in obj) result[k] = v?.DeepClone();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading settings failed; starting fresh", ex);
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
        }
        return result;
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
