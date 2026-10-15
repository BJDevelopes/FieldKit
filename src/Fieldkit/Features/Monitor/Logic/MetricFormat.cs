// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Monitor.Logic;

/// <summary>
/// Number formatting for the monitor (port of upstream MetricFormat). Sizes use the binary units
/// File Explorer and Task Manager use (1 GB = 1024³ bytes); network speeds in bits are decimal.
/// </summary>
public static class MetricFormat
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Scales bytes to the largest binary unit that keeps the value under 1024.</summary>
    public static (double Value, string Unit) Scale(double bytes)
    {
        var value = double.IsFinite(bytes) ? Math.Max(0, bytes) : 0;
        var index = 0;
        while (value >= 1024 && index < ByteUnits.Length - 1)
        {
            value /= 1024;
            index++;
        }
        // 1023.7 KB would print as "1024 KB": move up one unit instead.
        while (index < ByteUnits.Length - 1 && index > 0 && value >= 10 && Math.Round(value) >= 1024)
        {
            value /= 1024;
            index++;
        }
        return (value, ByteUnits[index]);
    }

    private static string Number(double value, string unit) =>
        unit == "B" ? value.ToString("0", C)
        : value < 10 ? value.ToString("0.0", C)
        : value.ToString("0", C);

    public static string Bytes(double bytes)
    {
        var (value, unit) = Scale(bytes);
        return $"{Number(value, unit)} {unit}";
    }

    /// <summary>Memory sizes like the upstream panel: "11.43 GB", "16 GB".</summary>
    public static string Memory(double bytes)
    {
        var (value, unit) = Scale(bytes);
        if (unit is "B" or "KB") return $"{value.ToString("0", C)} {unit}";
        return $"{value.ToString("0.##", C)} {unit}";
    }

    public static string BytesPerSec(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string BitsPerSec(double bytesPerSecond)
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) * 8 : 0;
        var index = 0;
        while (index < units.Length - 1 && Math.Round(value) >= 1000)
        {
            value /= 1000;
            index++;
        }
        if (index == 0) return $"{value.ToString("0", C)} {units[0]}";
        return $"{(value < 10 ? value.ToString("0.0", C) : value.ToString("0", C))} {units[index]}";
    }

    public static string NetworkRate(double bytesPerSecond, bool inBits) =>
        inBits ? BitsPerSec(bytesPerSecond) : BytesPerSec(bytesPerSecond);

    /// <summary>Short rate for a 16 px tray icon: "512B", "1.2M", "34K" (bytes) or "8.0Mb" (bits).</summary>
    public static string RateCompact(double bytesPerSecond, bool inBits)
    {
        if (inBits)
        {
            string[] bu = ["b", "K", "M", "G", "T"];
            var v = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) * 8 : 0;
            var i = 0;
            while (i < bu.Length - 1 && Math.Round(v) >= 1000)
            {
                v /= 1000;
                i++;
            }
            return CompactNumber(v, i) + bu[i];
        }
        string[] units = ["B", "K", "M", "G", "T"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        var index = 0;
        while (index < units.Length - 1 && Math.Round(value) >= 1024)
        {
            value /= 1024;
            index++;
        }
        return CompactNumber(value, index) + units[index];
    }

    private static string CompactNumber(double value, int unitIndex)
    {
        if (unitIndex == 0 || value >= 10) return Math.Round(value).ToString("0", C);
        var rounded = Math.Round(value * 10) / 10;
        return rounded >= 10 ? Math.Round(rounded).ToString("0", C) : rounded.ToString("0.0", C);
    }

    public static string Percent(double fraction) =>
        $"{Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * 100).ToString("0", C)}%";

    public static double ToDisplayTemperature(double celsius, bool fahrenheit) =>
        fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public static string Temperature(double celsius, bool fahrenheit) =>
        $"{ToDisplayTemperature(celsius, fahrenheit).ToString("0", C)} °{(fahrenheit ? "F" : "C")}";

    public static string TemperatureCompact(double celsius, bool fahrenheit) =>
        $"{ToDisplayTemperature(celsius, fahrenheit).ToString("0", C)}°";

    public static string Watts(double watts) =>
        Math.Abs(watts) < 10 ? $"{watts.ToString("0.0", C)} W" : $"{watts.ToString("0", C)} W";

    public static string WattsCompact(double watts) => $"{Math.Round(watts).ToString("0", C)}W";

    /// <summary>"1d 22h", "1h 11min", "7min" (upstream uptime).</summary>
    public static string Uptime(long seconds)
    {
        var total = Math.Max(0, seconds);
        var days = total / 86_400;
        var hours = total % 86_400 / 3_600;
        var minutes = total % 3_600 / 60;
        if (days > 0) return $"{days}d {hours}h";
        if (hours > 0) return $"{hours}h {minutes}min";
        return $"{minutes}min";
    }

    /// <summary>Battery time like upstream ("3h 5m"). Null when not meaningful.</summary>
    public static string? BatteryTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 7 * 24 * 3600) return null;
        var totalMinutes = Math.Max(1, (long)(seconds / 60));
        return $"{totalMinutes / 60}h {totalMinutes % 60}m";
    }

    /// <summary>A round axis ceiling at or above <paramref name="peak"/> (1, 2, 5, 10, 20, 50… × unitStep^n).</summary>
    public static double GraphCeiling(double peak, double unitStep)
    {
        for (var power = 0; power < 6; power++)
        {
            foreach (var step in new double[] { 1, 2, 5, 10, 20, 50, 100, 200, 500 })
            {
                var ceiling = step * Math.Pow(unitStep, power);
                if (ceiling >= peak) return ceiling;
            }
        }
        return peak;
    }

    public static double NetworkGraphCeiling(double peakBytesPerSecond, bool inBits) =>
        inBits ? GraphCeiling(peakBytesPerSecond * 8, 1000) / 8 : GraphCeiling(peakBytesPerSecond, 1024);

    /// <summary>Speed-test result: one decimal under 100 Mbps.</summary>
    public static string Mbps(double value) => value >= 100 ? value.ToString("0", C) : value.ToString("0.0", C);
}
