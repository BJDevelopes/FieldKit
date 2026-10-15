// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;

namespace Fieldkit.Features.Sound;

/// <summary>The stored priority lists (shared by the service and the editor).</summary>
internal static class PriorityLists
{
    public static string IdsKey(EDataFlow flow) => flow == EDataFlow.Render ? SoundKeys.PriorityOutputIds : SoundKeys.PriorityInputIds;

    public static string EnabledKey(EDataFlow flow) => flow == EDataFlow.Render ? SoundKeys.PriorityOutputEnabled : SoundKeys.PriorityInputEnabled;

    public static bool IsEnabled(EDataFlow flow) => Settings.GetBool(EnabledKey(flow), true);

    public static List<string> Load(EDataFlow flow) => SoundKeys.GetList(IdsKey(flow));

    public static PriorityTier Tier(string id, IReadOnlyList<AudioDevice> devices) =>
        devices.FirstOrDefault(d => d.Id == id)?.IsVirtual == true ? PriorityTier.Virtual : PriorityTier.Hardware;

    /// <summary>
    /// The complete list as the editor shows it: the stored order (disconnected devices keep
    /// their place) plus connected devices it has not ranked yet. An empty list starts as the
    /// device in use, then hardware, then virtual devices.
    /// </summary>
    public static List<string> ForDisplay(EDataFlow flow, IReadOnlyList<AudioDevice> devices)
    {
        var stored = Load(flow);
        var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
        if (stored.Count == 0) return SoundLogic.InitialPriorityList(devices.Select(d => d.Id), current, id => Tier(id, devices));
        var list = stored;
        foreach (var device in devices.Where(d => !stored.Contains(d.Id)))
            list = SoundLogic.PlaceNewPriorityDevice(device.Id, list, isCurrent: false, id => Tier(id, devices));
        return list;
    }

    /// <summary>Saves a list and refreshes the last-known names of its devices.</summary>
    public static void Save(EDataFlow flow, IReadOnlyList<string> ids, IEnumerable<AudioDevice> devices)
    {
        SoundKeys.SetList(IdsKey(flow), ids);
        UpdateNames(devices);
    }

    /// <summary>Remembers device names so disconnected entries still read well; prunes unlisted ones.</summary>
    public static void UpdateNames(IEnumerable<AudioDevice> devices)
    {
        var listed = Load(EDataFlow.Render).Concat(Load(EDataFlow.Capture)).ToHashSet(StringComparer.Ordinal);
        var names = SoundKeys.GetMap(SoundKeys.PriorityNames);
        var updated = names.Where(p => listed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var d in devices.Where(d => listed.Contains(d.Id))) updated[d.Id] = d.Name;
        if (updated.Count != names.Count || updated.Any(p => !names.TryGetValue(p.Key, out var n) || n != p.Value))
            Settings.Set(SoundKeys.PriorityNames, updated);
    }

    public static string DisplayName(string id, IEnumerable<AudioDevice> devices) =>
        devices.FirstOrDefault(d => d.Id == id)?.Name
        ?? (SoundKeys.GetMap(SoundKeys.PriorityNames).TryGetValue(id, out var name) ? name : id);
}

/// <summary>
/// Automatically selects the highest-priority connected output and microphone (upstream
/// AudioPriorityService). It acts when a device arrives or leaves, or when the lists or
/// switches change; a manual choice stays in effect until hardware changes. A device the
/// lists have never seen gets its place after a short delay, so headphones Windows just
/// switched to are not taken back while unranked.
/// </summary>
internal sealed class AudioPriorityService : FeatureService
{
    private static readonly TimeSpan PlacementDelay = TimeSpan.FromSeconds(2);

    private readonly List<IDisposable> _subscriptions = [];
    private DispatcherTimer? _placement;
    private HashSet<string>? _settledOutputs;
    private HashSet<string>? _settledInputs;
    private readonly HashSet<string> _unplacedOutputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unplacedInputs = new(StringComparer.Ordinal);
    private bool _writing;

    public override string FeatureId => "audioPriority";
    public override IReadOnlyList<string> EnableKeys => [SoundKeys.PriorityOutputEnabled, SoundKeys.PriorityInputEnabled];
    public override bool EnabledByDefault => true;

    protected override void OnStart()
    {
        var outputs = AudioEndpoints.List(EDataFlow.Render);
        var inputs = AudioEndpoints.List(EDataFlow.Capture);
        _settledOutputs = outputs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        _settledInputs = inputs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Merge(EDataFlow.Render, outputs);
        Merge(EDataFlow.Capture, inputs);
        _subscriptions.Add(AudioDeviceWatcher.Subscribe(OnDevicesChanged));
        // Editing a list or switching a direction on applies it right away.
        _subscriptions.Add(Settings.Observe(() => OnPreferenceChanged(EDataFlow.Render), SoundKeys.PriorityOutputIds, SoundKeys.PriorityOutputEnabled));
        _subscriptions.Add(Settings.Observe(() => OnPreferenceChanged(EDataFlow.Capture), SoundKeys.PriorityInputIds, SoundKeys.PriorityInputEnabled));
    }

    protected override void OnStop()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _placement?.Stop();
        _placement = null;
        _settledOutputs = _settledInputs = null;
        _unplacedOutputs.Clear();
        _unplacedInputs.Clear();
    }

    private void OnPreferenceChanged(EDataFlow flow)
    {
        if (_writing || !IsRunning) return;
        Enforce(flow, AudioEndpoints.List(flow));
    }

    private void OnDevicesChanged()
    {
        var outputs = AudioEndpoints.List(EDataFlow.Render);
        var inputs = AudioEndpoints.List(EDataFlow.Capture);
        var outputIds = outputs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var inputIds = inputs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var enforceOutput = SoundLogic.AvailabilityChanged(_settledOutputs, outputIds);
        var enforceInput = SoundLogic.AvailabilityChanged(_settledInputs, inputIds);
        _settledOutputs = outputIds;
        _settledInputs = inputIds;
        Merge(EDataFlow.Render, outputs);
        Merge(EDataFlow.Capture, inputs);
        if (enforceOutput) Enforce(EDataFlow.Render, outputs);
        if (enforceInput) Enforce(EDataFlow.Capture, inputs);
    }

    /// <summary>Keeps the stored list complete: seeds an empty one, queues unseen devices for placement.</summary>
    private void Merge(EDataFlow flow, List<AudioDevice> devices)
    {
        var stored = PriorityLists.Load(flow);
        _writing = true;
        try
        {
            if (stored.Count == 0 && devices.Count > 0)
            {
                PriorityLists.Save(flow, PriorityLists.ForDisplay(flow, devices), devices);
                return;
            }
            var unplaced = flow == EDataFlow.Render ? _unplacedOutputs : _unplacedInputs;
            var unseen = devices.Where(d => !stored.Contains(d.Id)).Select(d => d.Id).ToList();
            if (unseen.Count > 0)
            {
                unplaced.UnionWith(unseen);
                SchedulePlacement();
            }
            PriorityLists.UpdateNames(devices);
        }
        finally
        {
            _writing = false;
        }
    }

    private void SchedulePlacement()
    {
        if (_placement is null)
        {
            _placement = new DispatcherTimer { Interval = PlacementDelay };
            _placement.Tick += (_, _) =>
            {
                _placement?.Stop();
                PlaceNewDevices();
            };
        }
        _placement.Stop();
        _placement.Start();
    }

    /// <summary>Each newly seen, still connected device joins: first when in use, else below other hardware.</summary>
    private void PlaceNewDevices()
    {
        if (!IsRunning) return;
        _writing = true;
        try
        {
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                var unplaced = flow == EDataFlow.Render ? _unplacedOutputs : _unplacedInputs;
                var devices = AudioEndpoints.List(flow);
                var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
                var list = PriorityLists.Load(flow);
                foreach (var id in unplaced.Order(StringComparer.Ordinal).Where(id => devices.Any(d => d.Id == id)))
                    list = SoundLogic.PlaceNewPriorityDevice(id, list, id == current, x => PriorityLists.Tier(x, devices));
                unplaced.Clear();
                PriorityLists.Save(flow, list, devices);
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private void Enforce(EDataFlow flow, List<AudioDevice> devices)
    {
        if (!PriorityLists.IsEnabled(flow)) return;
        var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
        // Windows just moved to a device the list has not ranked yet: leave that choice alone until it has its place.
        var unplaced = flow == EDataFlow.Render ? _unplacedOutputs : _unplacedInputs;
        if (current is not null && unplaced.Contains(current)) return;
        var available = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var target = SoundLogic.FirstAvailablePriority(PriorityLists.ForDisplay(flow, devices), available);
        if (target is null || target == current) return;
        if (AudioEndpoints.SetDefault(target))
            Log.Info($"Audio priority selected {devices.First(d => d.Id == target).Name}");
    }
}
