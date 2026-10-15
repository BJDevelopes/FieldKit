// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Monitor.Logic;

/// <summary>Picks a readable name for a USB device and filters out infrastructure (hubs).</summary>
public static class DeviceNaming
{
    private static readonly string[] Generic =
    [
        "USB Composite Device", "USB Input Device", "HID-compliant device", "USB Mass Storage Device",
        "USB Device", "Unknown USB Device",
    ];

    /// <summary>The product string the device reports, unless it is a generic driver name.</summary>
    public static string? Choose(string? busReported, string? description)
    {
        var bus = Clean(busReported);
        var desc = Clean(description);
        if (bus is not null && !IsGeneric(bus)) return bus;
        if (desc is not null && !IsGeneric(desc)) return desc;
        return bus ?? desc;
    }

    public static bool IsHub(string? busReported, string? description) =>
        Contains(busReported, "hub") || Contains(description, "hub") || Contains(description, "host controller");

    private static bool IsGeneric(string name) => Generic.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? text, string word) =>
        text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
