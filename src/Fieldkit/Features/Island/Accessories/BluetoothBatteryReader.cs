// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Windows.Devices.Enumeration;

namespace Fieldkit.Features.Island.Accessories;

/// <summary>A connected accessory and its battery level.</summary>
public sealed record AccessoryBattery(string Id, string Name, int Percent, AccessoryKind Kind);

public enum AccessoryKind { Other, Headphones, Keyboard, Mouse, Controller, Phone, Speaker }

/// <summary>
/// Reads battery levels Windows already knows about: the PnP property Windows fills in for Bluetooth
/// hands-free/LE devices (DEVPKEY_Bluetooth_Battery), the same value Settings → Bluetooth shows.
/// Nothing connects to the devices; it only reads the device store.
/// </summary>
public static class BluetoothBatteryReader
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string CategoryKey = "System.Devices.Category";

    public static async Task<List<AccessoryBattery>> ReadAsync()
    {
        var result = new List<AccessoryBattery>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                [BatteryKey, ContainerKey, CategoryKey],
                DeviceInformationKind.Device);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices)
            {
                if (!d.Properties.TryGetValue(BatteryKey, out var raw) || raw is null) continue;
                var percent = Convert.ToInt32(raw);
                if (percent is < 0 or > 100) continue;
                var container = d.Properties.TryGetValue(ContainerKey, out var c) && c is not null ? c.ToString()! : d.Id;
                if (!seen.Add(container)) continue;
                var category = d.Properties.TryGetValue(CategoryKey, out var cat) && cat is string[] cats ? string.Join(" ", cats) : string.Empty;
                result.Add(new AccessoryBattery(container, CleanName(d.Name), percent, AccessoryClassifier.Classify(d.Name, category)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading accessory batteries failed: {ex.Message}");
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Strips the "Hands-Free AG" / "Avrcp Transport" suffixes Windows adds to profile nodes.</summary>
    public static string CleanName(string name)
    {
        foreach (var suffix in new[] { " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo", " LE" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length].Trim();
        }
        return name.Trim();
    }
}

/// <summary>Guesses an icon for an accessory from its name and category (testable).</summary>
public static class AccessoryClassifier
{
    public static AccessoryKind Classify(string name, string category)
    {
        var text = (name + " " + category).ToLowerInvariant();
        if (Has(text, "airpods", "buds", "headphone", "headset", "earphone", "wh-", "wf-", "beats", "audio.headphone", "hands-free")) return AccessoryKind.Headphones;
        if (Has(text, "keyboard", "keys", "input.keyboard")) return AccessoryKind.Keyboard;
        if (Has(text, "mouse", "trackpad", "mx master", "input.mouse")) return AccessoryKind.Mouse;
        if (Has(text, "controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con", "input.gaming")) return AccessoryKind.Controller;
        if (Has(text, "phone", "iphone", "pixel", "galaxy s")) return AccessoryKind.Phone;
        if (Has(text, "speaker", "soundlink", "jbl", "boom", "audio.speaker")) return AccessoryKind.Speaker;
        return AccessoryKind.Other;
    }

    private static bool Has(string text, params string[] needles) => needles.Any(text.Contains);
}
