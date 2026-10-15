// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Island.Accessories;

/// <summary>
/// Warns once per low-battery episode (port of upstream <c>NotchAccessorySupport</c>): the first reading of a
/// device is a silent baseline, a drop to 20% or below warns, and only 25% or more re-arms it.
/// Also announces devices that appear after the baseline as "Connected".
/// </summary>
public sealed class LowBatteryTracker
{
    public const int LowThreshold = 20;
    public const int RecoveryThreshold = 25;
    public const int MaximumTracked = 128;

    private readonly Dictionary<string, bool> _low = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private HashSet<string>? _connected;

    public sealed record Alert(AccessoryBattery Device, bool Connected);

    /// <summary>Feeds a full reading; returns the alerts it raises.</summary>
    public List<Alert> Observe(IReadOnlyList<AccessoryBattery> devices)
    {
        var alerts = new List<Alert>();
        var ids = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (_connected is null) _connected = ids;
        else
        {
            foreach (var d in devices.Where(d => !_connected.Contains(d.Id))) alerts.Add(new Alert(d, true));
            _connected = ids;
        }
        foreach (var d in devices)
        {
            if (!_low.TryGetValue(d.Id, out var wasLow))
            {
                _low[d.Id] = d.Percent <= LowThreshold;
                _order.Enqueue(d.Id);
                while (_order.Count > MaximumTracked) _low.Remove(_order.Dequeue());
                continue;
            }
            if (d.Percent >= RecoveryThreshold) _low[d.Id] = false;
            else if (d.Percent <= LowThreshold && !wasLow)
            {
                _low[d.Id] = true;
                alerts.Add(new Alert(d, false));
            }
        }
        return alerts;
    }
}
