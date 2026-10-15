// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Windows;

namespace Fieldkit.Core;

/// <summary>
/// Discovers modules, owns the registry and keeps every feature service in the
/// state its settings ask for: running while installed and enabled, stopped otherwise.
/// </summary>
public static class FeatureRuntime
{
    private static readonly List<ServiceSlot> Slots = [];
    private static bool _started;

    public static ModuleRegistry Registry { get; } = new();

    /// <summary>Raised on the UI thread after a feature is installed or uninstalled.</summary>
    public static event Action? AvailabilityChanged;

    private static volatile bool _initialized;
    private static readonly object InitializeLock = new();

    public static void Initialize()
    {
        if (_initialized) return;
        // A second thread (the tests share this registry) waits here until registration is complete, instead of
        // seeing a half-filled registry; the same thread re-entering gets through.
        lock (InitializeLock) Register();
    }

    private static bool _registering;

    private static void Register()
    {
        if (_initialized || _registering) return;
        _registering = true;
        var moduleTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IFeatureModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in moduleTypes)
        {
            try
            {
                var module = (IFeatureModule)Activator.CreateInstance(type)!;
                module.Register(Registry);
            }
            catch (Exception ex)
            {
                Log.Error($"Registering module {type.Name} failed", ex);
            }
        }

        foreach (var (featureId, factory) in Registry.ServiceFactories)
            Slots.Add(new ServiceSlot(featureId, factory));

        Log.Info($"Registered {Slots.Count} services, {Registry.PanelSections.Count} panel sections, " +
                 $"{Registry.SettingsPages.Count} settings pages");
        _initialized = true;
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var slot in Slots) slot.Evaluate();
        Settings.Changed += OnSettingChanged;
    }

    public static void Shutdown()
    {
        Settings.Changed -= OnSettingChanged;
        foreach (var slot in Slots) slot.Stop();
    }

    /// <summary>The running (or cached) service instance for a feature, if any.</summary>
    public static T? Service<T>() where T : FeatureService =>
        Slots.Select(s => s.Instance).OfType<T>().FirstOrDefault();

    /// <summary>Re-checks <see cref="FeatureService.ShouldRun"/> for a feature's services
    /// (for services whose run condition depends on more than their enable keys).</summary>
    public static void Reevaluate(string featureId)
    {
        foreach (var slot in Slots.Where(s => s.FeatureId == featureId)) slot.Evaluate();
    }

    /// <summary>Installs/uninstalls features as a batch (Features hub, bundles).</summary>
    public static void SetAvailability(IEnumerable<string> featureIds, bool available)
    {
        foreach (var id in featureIds) FeatureCatalog.Get(id).SetAvailable(available);
    }

    private static void OnSettingChanged(string key)
    {
        void Apply()
        {
            var availability = key.StartsWith("feature.", StringComparison.Ordinal) && key.EndsWith(".available", StringComparison.Ordinal);
            foreach (var slot in Slots)
            {
                if (availability || slot.Watches(key)) slot.Evaluate();
            }
            if (availability) AvailabilityChanged?.Invoke();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }

    private sealed class ServiceSlot(string featureId, Func<FeatureService> factory)
    {
        private HashSet<string>? _keys;

        public string FeatureId => featureId;

        public FeatureService? Instance { get; private set; }

        public bool Watches(string key)
        {
            // Never construct services of uninstalled features just to answer this.
            if (Instance is null && !FeatureCatalog.IsAvailable(featureId)) return false;
            EnsureInstance();
            _keys ??= new HashSet<string>(Instance!.EnableKeys, StringComparer.Ordinal);
            return _keys.Contains(key);
        }

        public void Evaluate()
        {
            try
            {
                var available = FeatureCatalog.IsAvailable(featureId);
                if (!available)
                {
                    Stop();
                    return;
                }
                EnsureInstance();
                if (Instance!.ShouldRun()) Instance.StartInternal();
                else Instance.StopInternal();
            }
            catch (Exception ex)
            {
                Log.Error($"Evaluating service for {featureId} failed", ex);
            }
        }

        public void Stop() => Instance?.StopInternal();

        private void EnsureInstance()
        {
            if (Instance is not null) return;
            Instance = factory();
            if (Instance.FeatureId != featureId)
                Log.Warn($"Service {Instance.GetType().Name} reports {Instance.FeatureId} but was registered for {featureId}");
        }
    }
}
