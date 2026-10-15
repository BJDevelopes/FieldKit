// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Accessories;

/// <summary>
/// Accessory alerts (port of upstream <c>NotchAccessoryService</c>): reads the battery levels Windows keeps for
/// Bluetooth devices once a minute, announces newly connected ones and warns once when one falls to 20%.
/// Queued notices are shown one every 4.1 s and expire after 30 s.
/// </summary>
public sealed class AccessoriesService : IslandExtensionService
{
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _present = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4.1) };
    private readonly Queue<(IslandNotice Notice, DateTime Expires)> _queue = new();
    private LowBatteryTracker _tracker = new();
    private bool _reading;

    public override string FeatureId => "notchAccessories";
    protected override string EnableKey => IslandKeys.AccessoriesEnabled;

    public IReadOnlyList<AccessoryBattery> Devices { get; private set; } = [];

    public AccessoriesService()
    {
        _poll.Tick += (_, _) => _ = ReadAsync();
        _present.Tick += (_, _) => PresentNext();
    }

    protected override void OnStart()
    {
        IslandHub.Accessories = this;
        _tracker = new LowBatteryTracker();
        _poll.Start();
        _present.Start();
        _ = ReadAsync();
    }

    protected override void OnStop()
    {
        _poll.Stop();
        _present.Stop();
        _queue.Clear();
        Devices = [];
        if (IslandHub.Accessories == this) IslandHub.Accessories = null;
    }

    private async Task ReadAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var devices = await BluetoothBatteryReader.ReadAsync();
            if (!IsRunning) return;
            Devices = devices;
            foreach (var alert in _tracker.Observe(devices))
            {
                var d = alert.Device;
                var notice = alert.Connected
                    ? new IslandNotice(IslandNoticeEvent.Accessory, "Connected", d.Name, AccessoryGlyph(d.Kind))
                    : new IslandNotice(IslandNoticeEvent.Accessory, d.Name, $"Low battery · {d.Percent}%", AccessoryGlyph(d.Kind), "Red", d.Percent / 100.0);
                // A fresh reading of 25% or more drops a queued low-battery notice.
                if (_queue.Count < 8) _queue.Enqueue((notice, DateTime.UtcNow.AddSeconds(30)));
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private void PresentNext()
    {
        while (_queue.Count > 0)
        {
            var (notice, expires) = _queue.Dequeue();
            if (expires < DateTime.UtcNow) continue;
            var recovered = notice.Level is not null && Devices.Any(d => d.Name == notice.Title && d.Percent >= LowBatteryTracker.RecoveryThreshold);
            if (recovered) continue;
            IslandHub.Post(notice);
            return;
        }
    }

    public static string AccessoryGlyph(AccessoryKind kind) => kind switch
    {
        AccessoryKind.Headphones => Glyphs.Headphones,
        AccessoryKind.Keyboard => Glyphs.Keyboard,
        AccessoryKind.Mouse => Glyphs.Mouse,
        AccessoryKind.Controller => "\uE7FC",
        AccessoryKind.Speaker => Glyphs.Speaker,
        _ => Glyphs.Bluetooth,
    };
}
